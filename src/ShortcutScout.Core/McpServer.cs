using System.Text.Encodings.Web;
using System.Text.Json;

namespace ShortcutScout.Core;

/// <summary>Minimal MCP (stdio, JSON-RPC 2.0) handler. Pure/testable: Handle() maps one request line to one response line.</summary>
public sealed class McpServer
{
    private readonly ConfigStore _store;
    // Relaxed encoder keeps keys readable for agents (Ctrl+Z, not Ctrl\u002BZ).
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly JsonSerializerOptions PrettyOut = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public McpServer(ConfigStore store) => _store = store;

    public string Handle(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";
        var method = root.TryGetProperty("method", out var m) ? (m.GetString() ?? "") : "";
        string resultPayload;
        try { resultPayload = Dispatch(method, root); }
        catch (Exception ex) { return $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-32000,\"message\":{JsonSerializer.Serialize(ex.Message, JsonOut)}}}}}"; }
        return $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{resultPayload}}}";
    }

    private string Dispatch(string method, JsonElement root)
    {
        switch (method)
        {
            case "initialize":
                return "{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"shortcutscout\",\"version\":\"1.0\"}}";
            case "notifications/initialized": return "{}";
            case "tools/list":
                return "{\"tools\":[" +
                    Tool("list_apps", "List configured apps", "{}") + "," +
                    Tool("list_shortcuts", "List shortcuts for an app", "{\"type\":\"object\",\"properties\":{\"app\":{\"type\":\"string\"}},\"required\":[\"app\"]}") + "," +
                    Tool("add_shortcut", "Add a shortcut (keys, action, optional discovery/category)", "{\"type\":\"object\",\"properties\":{\"app\":{\"type\":\"string\"},\"keys\":{\"type\":\"string\"},\"action\":{\"type\":\"string\"},\"discovery\":{\"type\":\"boolean\"},\"category\":{\"type\":\"string\"}},\"required\":[\"app\",\"keys\",\"action\"]}") + "," +
                    Tool("remove_shortcut", "Remove shortcut by keys", "{\"type\":\"object\",\"properties\":{\"app\":{\"type\":\"string\"},\"keys\":{\"type\":\"string\"}},\"required\":[\"app\",\"keys\"]}") + "," +
                    Tool("reload_config", "Reload config files from disk", "{}") +
                    "]}";
            case "tools/call": return CallTool(root);
            default: throw new Exception("unknown method " + method);
        }
    }

    private string CallTool(JsonElement root)
    {
        var p = root.TryGetProperty("params", out var pr) ? pr : default;
        var name = p.TryGetProperty("name", out var n) ? n.GetString() : "";
        var args = p.TryGetProperty("arguments", out var a) ? a : default;
        string Str(string k) => args.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
        object data = name switch
        {
            "list_apps" => _store.Config.Apps.Select(x => new { id = x.Id, name = x.Name, count = x.Shortcuts.Count }).ToArray(),
            "list_shortcuts" => (_store.Config.Apps.FirstOrDefault(x => x.Id == Str("app"))?.Shortcuts
                ?? throw new Exception("app not found: " + Str("app"))).ToArray(),
            "add_shortcut" => new { ok = _store.AddShortcut(Str("app"), new Shortcut(Str("keys"), Str("action"),
                args.TryGetProperty("discovery", out var d) && d.GetBoolean(),
                args.TryGetProperty("category", out var c) ? c.GetString() ?? "General" : "General")) },
            "remove_shortcut" => new { ok = _store.RemoveShortcut(Str("app"), Str("keys")) },
            "reload_config" => RunReload(),
            _ => throw new Exception("unknown tool " + name),
        };
        var text = JsonSerializer.Serialize(data, PrettyOut);
        return "{\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text, JsonOut) + "}]}";
    }

    private object RunReload() { _store.Reload(); return new { ok = true, apps = _store.Config.Apps.Count }; }
    private static string Tool(string name, string desc, string schema) =>
        $"{{\"name\":\"{name}\",\"description\":\"{desc}\",\"inputSchema\":{schema}}}";
}
