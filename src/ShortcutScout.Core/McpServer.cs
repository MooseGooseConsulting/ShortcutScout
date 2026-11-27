using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShortcutScout.Core;

/// <summary>
/// Minimal MCP (stdio, JSON-RPC 2.0) handler. Pure/testable: <see cref="Handle"/> maps one
/// request line to one response line, or to nothing when the message is a notification.
/// </summary>
public sealed class McpServer
{
    // JSON-RPC 2.0 error codes.
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    /// <summary>
    /// Protocol revisions this server can speak, newest last. It only offers tools with text
    /// results, which is the same wire shape in all of them.
    /// </summary>
    private static readonly string[] SupportedVersions = { "2024-11-05", "2025-03-26", "2025-06-18" };

    private readonly ConfigStore _store;

    // Relaxed encoder keeps keys readable for agents ("Ctrl+Z" with a literal plus, not a unicode escape).
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly JsonSerializerOptions PrettyOut = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>A tool-level failure: reported inside the result so the agent can read it and retry.</summary>
    private sealed class ToolException : Exception
    {
        public ToolException(string message) : base(message) { }
    }

    /// <summary>A protocol-level failure, carrying its JSON-RPC code.</summary>
    private sealed class RpcException : Exception
    {
        public int Code { get; }
        public RpcException(int code, string message) : base(message) => Code = code;
    }

    public McpServer(ConfigStore store) => _store = store;

    /// <summary>
    /// Handle one line. Returns the response line, or <c>null</c> when nothing may be sent:
    /// a JSON-RPC notification (no <c>id</c>) never gets a reply, not even an error. Never throws
    /// — the stdio loop has no per-line catch, so an escaped exception would end the session.
    /// </summary>
    public string? Handle(string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException ex) { return Error("null", ParseError, "parse error: " + ex.Message); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return HandleOne(root);

            // A batch. Protocol revision 2025-03-26, which this server still negotiates, obliges
            // a server to accept them: each entry is answered on its own, the replies go back
            // as one array, and a batch of nothing but notifications gets no reply at all.
            if (root.GetArrayLength() == 0) return Error("null", InvalidRequest, "empty batch");
            var replies = root.EnumerateArray().Select(HandleOne).Where(r => r != null).ToList();
            return replies.Count == 0 ? null : "[" + string.Join(",", replies) + "]";
        }
    }

    private string? HandleOne(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
            return Error("null", InvalidRequest, "expected a JSON-RPC request object");

        // An id we could not echo back as a valid response id is answered with null instead.
        var hasId = message.TryGetProperty("id", out var idEl);
        if (hasId && idEl.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
            return Error("null", InvalidRequest, "id must be a string or a number");
        var id = hasId ? idEl.GetRawText() : "null";

        // Only a well-formed request without an id is a notification. An object with no usable
        // method is just invalid, and is told so (with a null id) rather than silently dropped.
        if (!message.TryGetProperty("method", out var m) || m.ValueKind != JsonValueKind.String)
            return Error(id, InvalidRequest, "missing method");

        string method;
        try { method = m.GetString() ?? ""; }
        catch (InvalidOperationException)
        {
            // The parser accepts an unpaired surrogate escape; decoding it is what fails.
            return Error(id, InvalidRequest, "method is not valid text");
        }

        // Nothing a client notifies us of needs acting on (initialized, cancelled, ...).
        if (!hasId) return null;

        try
        {
            var result = Dispatch(method, message);
            return $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result.ToJsonString(JsonOut)}}}";
        }
        catch (RpcException ex) { return Error(id, ex.Code, ex.Message); }
        catch (Exception ex) { return Error(id, InternalError, ex.Message); }
    }

    private static string Error(string rawId, int code, string message) =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":{rawId},\"error\":{{\"code\":{code},\"message\":{JsonSerializer.Serialize(message, JsonOut)}}}}}";

    private JsonObject Dispatch(string method, JsonElement root)
    {
        switch (method)
        {
            case "initialize":
                return new JsonObject
                {
                    ["protocolVersion"] = NegotiateVersion(root),
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "shortcutscout", ["version"] = "1.0" },
                };
            case "ping":
                return new JsonObject();
            case "tools/list":
                return new JsonObject
                {
                    ["tools"] = new JsonArray(
                        Tool("list_apps", "List configured apps with their shortcut counts"),
                        Tool("list_shortcuts", "List shortcuts for an app",
                            required: new[] { "app" }, ("app", "string")),
                        Tool("add_shortcut", "Add a shortcut (keys, action, optional discovery/category). Creates the app if it does not exist.",
                            required: new[] { "app", "keys", "action" },
                            ("app", "string"), ("keys", "string"), ("action", "string"), ("discovery", "boolean"), ("category", "string")),
                        Tool("remove_shortcut", "Remove every shortcut with these keys from an app",
                            required: new[] { "app", "keys" }, ("app", "string"), ("keys", "string")),
                        Tool("reload_config", "Reload config files from disk and report any that failed to parse")),
                };
            case "tools/call":
                return CallTool(root);
            default:
                throw new RpcException(MethodNotFound, "unknown method " + method);
        }
    }

    /// <summary>Echo the client's revision when we speak it; otherwise offer our newest.</summary>
    private static string NegotiateVersion(JsonElement root)
    {
        if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String
            && SupportedVersions.Contains(v.GetString()))
            return v.GetString()!;
        return SupportedVersions[^1];
    }

    private JsonObject CallTool(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object)
            throw new RpcException(InvalidParams, "tools/call needs params");
        var name = p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
        var args = p.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;

        string Str(string key)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var v)) return "";
            return v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : throw new ToolException($"'{key}' must be a string");
        }
        string Required(string key) =>
            Str(key) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ToolException($"'{key}' is required");
        bool Flag(string key)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var v)) return false;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new ToolException($"'{key}' must be true or false"),
            };
        }

        object data;
        try
        {
            data = name switch
            {
                "list_apps" => _store.Config.Apps.Select(x => new { id = x.Id, name = x.Name, count = x.Shortcuts.Count }).ToArray(),
                "list_shortcuts" => (_store.Config.Apps.FirstOrDefault(x => x.Id == Required("app"))?.Shortcuts
                    ?? throw new ToolException("app not found: " + Str("app"))).ToArray(),
                "add_shortcut" => new
                {
                    ok = _store.AddShortcut(Required("app"), new Shortcut(
                        Required("keys"), Required("action"), Flag("discovery"),
                        Str("category") is { Length: > 0 } category ? category : "General"))
                },
                "remove_shortcut" => new { ok = _store.RemoveShortcut(Required("app"), Required("keys")) },
                "reload_config" => RunReload(),
                _ => throw new RpcException(InvalidParams, "unknown tool " + name),
            };
        }
        catch (Exception ex) when (ex is ToolException or ArgumentException or IOException
                                       or UnauthorizedAccessException or JsonException)
        {
            // The call was well-formed but could not be carried out. MCP reports that inside
            // the result (isError) so the model sees the reason, rather than as a protocol error.
            return TextResult(ex.Message, isError: true);
        }

        return TextResult(JsonSerializer.Serialize(data, PrettyOut), isError: false);
    }

    private static JsonObject TextResult(string text, bool isError)
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        };
        if (isError) result["isError"] = true;
        return result;
    }

    private object RunReload()
    {
        _store.Reload();
        return new { ok = _store.LoadErrors.Count == 0, apps = _store.Config.Apps.Count, errors = _store.LoadErrors };
    }

    /// <summary>
    /// <c>inputSchema</c> must be a JSON Schema of type <c>object</c> even for a tool with no
    /// arguments — a bare <c>{}</c> is rejected by clients that validate tool definitions.
    /// </summary>
    private static JsonNode Tool(string name, string description, string[]? required = null,
        params (string Name, string Type)[] properties)
    {
        var props = new JsonObject();
        foreach (var (propName, type) in properties) props[propName] = new JsonObject { ["type"] = type };

        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required is { Length: > 0 })
            schema["required"] = new JsonArray(required.Select(r => (JsonNode)r!).ToArray());

        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
    }
}
