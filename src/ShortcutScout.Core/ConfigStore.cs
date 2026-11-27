using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShortcutScout.Core;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string ConfigDir { get; }
    public GlobalConfig Config { get; private set; } = new();

    public ConfigStore(string configDir) { ConfigDir = configDir; Reload(); }

    public void Reload()
    {
        var cfg = new GlobalConfig();
        var main = Path.Combine(ConfigDir, "shortcuts.json");
        if (File.Exists(main))
            cfg = JsonSerializer.Deserialize<GlobalConfig>(File.ReadAllText(main)) ?? cfg;
        var appsDir = Path.Combine(ConfigDir, "apps");
        if (Directory.Exists(appsDir))
            foreach (var f in Directory.EnumerateFiles(appsDir, "*.json").OrderBy(x => x))
            {
                try
                {
                    var app = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(f));
                    if (app != null && !cfg.Apps.Any(a => a.Id == app.Id)) cfg.Apps.Add(app);
                }
                catch { /* skip malformed file, keep running */ }
            }
        Config = cfg;
    }

    /// <summary>
    /// Persist only the `overlay` block, in place.
    ///
    /// Rewriting the whole file would copy every app loaded from config/apps/*.json into
    /// shortcuts.json, and on the next reload those copies would shadow the real files.
    /// </summary>
    public void SaveOverlay()
    {
        var path = Path.Combine(ConfigDir, "shortcuts.json");
        Directory.CreateDirectory(ConfigDir);

        JsonNode root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) ?? new JsonObject()
            : new JsonObject();

        root["overlay"] = JsonSerializer.SerializeToNode(Config.Overlay);

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public void SaveApp(AppConfig app)
    {
        var dir = Path.Combine(ConfigDir, "apps");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, app.Id + ".json"), JsonSerializer.Serialize(app, Json));
        Reload();
    }

    public bool AddShortcut(string appId, Shortcut s)
    {
        var app = Config.Apps.FirstOrDefault(a => a.Id == appId);
        if (app == null) { app = new AppConfig { Id = appId, Name = appId }; Config.Apps.Add(app); }
        if (app.Shortcuts.Any(x => x.Keys == s.Keys && x.Action == s.Action)) return false;
        app.Shortcuts.Add(s); SaveApp(app); return true;
    }

    public bool RemoveShortcut(string appId, string keys)
    {
        var app = Config.Apps.FirstOrDefault(a => a.Id == appId);
        if (app == null) return false;
        var n = app.Shortcuts.RemoveAll(x => x.Keys == keys);
        if (n > 0) SaveApp(app);
        return n > 0;
    }
}
