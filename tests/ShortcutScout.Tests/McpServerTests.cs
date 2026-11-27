using System.Text.Json;
using ShortcutScout.Core;

namespace ShortcutScout.Tests;

/// <summary>Protocol conformance of the MCP handler: what a strict client would trip over.</summary>
[TestClass]
public class McpServerTests
{
    private string _dir = "";
    private McpServer _server = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ss-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "apps"));
        File.WriteAllText(Path.Combine(_dir, "apps", "devin.json"),
            """{"id":"devin","name":"Devin","shortcuts":[{"keys":"Ctrl+K","action":"Palette → open","discovery":true}]}""");
        _server = new McpServer(new ConfigStore(_dir));
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, true);

    private JsonElement Send(string line)
    {
        var response = _server.Handle(line);
        Assert.IsNotNull(response, "a request with an id must be answered");
        Assert.IsFalse(response.Contains('\n'), "stdio framing is one message per line");
        return JsonDocument.Parse(response).RootElement.Clone();
    }

    private JsonElement Call(string tool, string argumentsJson = "{}") =>
        Send("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"TOOL","arguments":ARGS}}"""
            .Replace("TOOL", tool).Replace("ARGS", argumentsJson));

    private static string Text(JsonElement response) =>
        response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    private static bool IsToolError(JsonElement response) =>
        response.GetProperty("result").TryGetProperty("isError", out var e) && e.GetBoolean();

    [DataTestMethod]
    [DataRow("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")]
    [DataRow("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":3}}""")]
    [DataRow("""{"jsonrpc":"2.0","method":"some/unknown-notification"}""")]
    public void Notifications_GetNoReplyAtAll(string line) =>
        Assert.IsNull(_server.Handle(line), "JSON-RPC forbids answering a message that has no id");

    [TestMethod]
    public void UnparseableInput_IsAParseError_NotAnException()
    {
        var response = Send("{ this is not json");

        Assert.AreEqual(McpServer.ParseError, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, response.GetProperty("id").ValueKind);
    }

    [DataTestMethod]
    [DataRow("[]")]
    [DataRow("42")]
    [DataRow("""{"jsonrpc":"2.0","id":1}""")]
    public void MalformedRequests_AreInvalidRequest(string line) =>
        Assert.AreEqual(McpServer.InvalidRequest, Send(line).GetProperty("error").GetProperty("code").GetInt32());

    [TestMethod]
    public void UnknownMethod_IsMethodNotFound_AndEchoesTheId()
    {
        var response = Send("""{"jsonrpc":"2.0","id":"abc","method":"nope/nope"}""");

        Assert.AreEqual(McpServer.MethodNotFound, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.AreEqual("abc", response.GetProperty("id").GetString(), "string ids round-trip as strings");
    }

    [TestMethod]
    public void Ping_IsAnsweredWithAnEmptyResult()
    {
        var response = Send("""{"jsonrpc":"2.0","id":5,"method":"ping"}""");
        Assert.AreEqual(0, response.GetProperty("result").EnumerateObject().Count());
    }

    [TestMethod]
    public void Initialize_EchoesASupportedProtocolVersion_AndOffersItsOwnOtherwise()
    {
        string Negotiated(string requested) =>
            Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"VERSION","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}"""
                    .Replace("VERSION", requested))
                .GetProperty("result").GetProperty("protocolVersion").GetString()!;

        Assert.AreEqual("2024-11-05", Negotiated("2024-11-05"));
        Assert.AreEqual("2025-06-18", Negotiated("2025-06-18"));
        Assert.AreEqual("2025-06-18", Negotiated("2099-01-01"), "an unknown revision gets the newest we speak");
    }

    [TestMethod]
    public void EveryTool_AdvertisesAnObjectInputSchema()
    {
        var tools = Send("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""")
            .GetProperty("result").GetProperty("tools").EnumerateArray().ToList();

        Assert.AreEqual(5, tools.Count);
        foreach (var tool in tools)
        {
            var schema = tool.GetProperty("inputSchema");
            Assert.AreEqual("object", schema.GetProperty("type").GetString(),
                $"{tool.GetProperty("name").GetString()}: a bare {{}} schema is rejected by validating clients");

            if (!schema.TryGetProperty("required", out var required)) continue;
            foreach (var name in required.EnumerateArray())
                Assert.IsTrue(schema.GetProperty("properties").TryGetProperty(name.GetString()!, out _));
        }
    }

    [TestMethod]
    public void ToolsWithoutArguments_WorkWhenArgumentsAreOmitted()
    {
        var response = Send("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_apps"}}""");

        Assert.IsFalse(IsToolError(response));
        StringAssert.Contains(Text(response), "devin");
    }

    [TestMethod]
    public void NonAsciiText_IsEmittedReadably()
    {
        var response = _server.Handle("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"list_shortcuts","arguments":{"app":"devin"}}}""")!;

        StringAssert.Contains(response, "Ctrl+K");
        StringAssert.Contains(response, "Palette → open");
    }

    [TestMethod]
    public void AToolThatCannotDoItsJob_ReportsIsError_NotAProtocolError()
    {
        var missing = Call("list_shortcuts", """{"app":"nosuchapp"}""");
        Assert.IsTrue(IsToolError(missing));
        StringAssert.Contains(Text(missing), "nosuchapp");
        Assert.IsFalse(missing.TryGetProperty("error", out _));

        Assert.IsTrue(IsToolError(Call("add_shortcut", """{"app":"devin","keys":"","action":"x"}""")), "blank keys");
        Assert.IsTrue(IsToolError(Call("add_shortcut", """{"app":"devin","keys":"Ctrl+1"}""")), "missing action");
        Assert.IsTrue(IsToolError(Call("add_shortcut", """{"app":"devin","keys":"Ctrl+1","action":"x","discovery":"yes"}""")), "wrong type");
        Assert.IsTrue(IsToolError(Call("add_shortcut", """{"app":17,"keys":"Ctrl+1","action":"x"}""")), "wrong type");
    }

    [TestMethod]
    public void AnAppIdThatEscapesTheConfigFolder_IsRefused()
    {
        var response = Call("add_shortcut", """{"app":"../../escaped","keys":"Ctrl+1","action":"x"}""");

        Assert.IsTrue(IsToolError(response));
        Assert.IsFalse(File.Exists(Path.Combine(_dir, "..", "escaped.json")));
        Assert.AreEqual(1, Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public void UnknownTool_IsInvalidParams()
    {
        var response = Call("format_disk");
        Assert.AreEqual(McpServer.InvalidParams, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public void ReloadConfig_ReportsFilesThatFailedToParse()
    {
        File.WriteAllText(Path.Combine(_dir, "apps", "broken.json"), "{ nope");

        var text = Text(Call("reload_config"));

        StringAssert.Contains(text, "broken.json");
        StringAssert.Contains(text, "\"ok\": false");
    }

    [TestMethod]
    public void AddThenRemove_RoundTrips()
    {
        StringAssert.Contains(Text(Call("add_shortcut", """{"app":"devin","keys":"Ctrl+Z","action":"Undo","category":"Edit"}""")), "true");
        StringAssert.Contains(Text(Call("add_shortcut", """{"app":"devin","keys":"Ctrl+Z","action":"Undo"}""")), "false");

        var listed = Text(Call("list_shortcuts", """{"app":"devin"}"""));
        StringAssert.Contains(listed, "Ctrl+Z");
        StringAssert.Contains(listed, "Edit");

        StringAssert.Contains(Text(Call("remove_shortcut", """{"app":"devin","keys":"Ctrl+Z"}""")), "true");
        Assert.IsFalse(Text(Call("list_shortcuts", """{"app":"devin"}""")).Contains("Ctrl+Z"));
    }
}
