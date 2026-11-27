using ShortcutScout.Core;

namespace ShortcutScout.Tests;

/// <summary>Self-contained <see cref="McpServer"/> round trips, each on its own temp config dir.</summary>
[TestClass]
public class McpRoundTripTests
{
    [TestMethod]
    public void Mcp_InitializeAndToolsList()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var server = new McpServer(new ConfigStore(dir));

            var init = server.Handle("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""");
            StringAssert.Contains(init, "shortcutscout");

            var tools = server.Handle("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            foreach (var name in new[] { "list_apps", "list_shortcuts", "add_shortcut", "remove_shortcut", "reload_config" })
                StringAssert.Contains(tools, name);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Mcp_AddThenListRoundTripsThroughDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss-mcp2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var server = new McpServer(new ConfigStore(dir));
            var add = server.Handle("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"add_shortcut","arguments":{"app":"devin","keys":"Ctrl+Z","action":"Undo","discovery":true}}}""");
            StringAssert.Contains(add, "ok");

            // Fresh store: proves it was persisted, not just held in memory.
            var list = new McpServer(new ConfigStore(dir))
                .Handle("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"list_shortcuts","arguments":{"app":"devin"}}}""");
            StringAssert.Contains(list, "Ctrl+Z");
            StringAssert.Contains(list, "Undo");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Mcp_UnknownMethodReturnsJsonRpcError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss-mcp3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var response = new McpServer(new ConfigStore(dir))
                .Handle("""{"jsonrpc":"2.0","id":9,"method":"nope/nope"}""");
            StringAssert.Contains(response, "\"error\"");
            StringAssert.Contains(response, "\"id\":9");
        }
        finally { Directory.Delete(dir, true); }
    }
}
