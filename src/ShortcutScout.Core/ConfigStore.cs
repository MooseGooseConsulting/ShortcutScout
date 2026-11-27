using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShortcutScout.Core;

public sealed class ConfigStore
{
    // Relaxed escaping: these files are meant to be read and edited by hand, and the default
    // encoder writes "Ctrl+T" as "Ctrl+T".
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Ids of the apps defined inside shortcuts.json rather than in apps/*.json.</summary>
    private HashSet<string> _inlineAppIds = new(StringComparer.Ordinal);

    public string ConfigDir { get; }
    public GlobalConfig Config { get; private set; } = new();

    /// <summary>
    /// What the last <see cref="Reload"/> could not read, one line per file. Empty when
    /// everything loaded. Loading never throws; callers log or show these instead.
    /// </summary>
    public IReadOnlyList<string> LoadErrors { get; private set; } = Array.Empty<string>();

    public ConfigStore(string configDir) { ConfigDir = configDir; Reload(); }

    private string MainPath => Path.Combine(ConfigDir, "shortcuts.json");
    private string AppsDir => Path.Combine(ConfigDir, "apps");

    /// <summary>
    /// Re-read everything from disk. Never throws: a file that cannot be read is skipped and
    /// reported in <see cref="LoadErrors"/>.
    ///
    /// If shortcuts.json itself is unreadable, the overlay settings and inline apps from the
    /// last good load are kept. A half-saved hand edit must not snap a running overlay back to
    /// defaults — or, as it did before, take the whole app down from a tray-menu click.
    /// </summary>
    public void Reload()
    {
        var errors = new List<string>();
        var cfg = new GlobalConfig();

        try
        {
            if (File.Exists(MainPath))
                cfg = JsonSerializer.Deserialize<GlobalConfig>(File.ReadAllText(MainPath)) ?? cfg;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            errors.Add($"shortcuts.json: {ex.Message}");
            cfg = new GlobalConfig
            {
                Overlay = Config.Overlay,
                Apps = Config.Apps.Where(a => _inlineAppIds.Contains(a.Id)).ToList(),
            };
        }

        cfg.Overlay ??= new OverlaySettings();
        cfg.Apps = (cfg.Apps ?? new()).Where(a => a != null).ToList();
        foreach (var app in cfg.Apps) Normalise(app, fallbackId: "");
        cfg.Apps.RemoveAll(a => a.Id.Length == 0);

        var inline = new HashSet<string>(cfg.Apps.Select(a => a.Id), StringComparer.Ordinal);

        if (Directory.Exists(AppsDir))
        {
            foreach (var f in Directory.EnumerateFiles(AppsDir, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var app = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(f));
                    if (app == null) continue;

                    Normalise(app, fallbackId: Path.GetFileNameWithoutExtension(f));
                    if (cfg.Apps.Any(a => a.Id == app.Id))
                    {
                        errors.Add($"apps/{Path.GetFileName(f)}: id '{app.Id}' is already defined; this file is ignored");
                        continue;
                    }
                    cfg.Apps.Add(app);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    // Skip the one bad file and keep running, but say which one it was.
                    errors.Add($"apps/{Path.GetFileName(f)}: {ex.Message}");
                }
            }
        }

        _inlineAppIds = inline;
        Config = cfg;
        LoadErrors = errors;
    }

    /// <summary>
    /// JSON happily deserialises <c>"shortcuts": null</c> or a missing <c>id</c>; everything
    /// downstream assumes neither can happen.
    /// </summary>
    private static void Normalise(AppConfig app, string fallbackId)
    {
        if (string.IsNullOrWhiteSpace(app.Id)) app.Id = fallbackId;
        app.Id = (app.Id ?? "").Trim();
        if (string.IsNullOrWhiteSpace(app.Name)) app.Name = app.Id;

        app.ProcessNames = Clean(app.ProcessNames);
        app.ExeNames = Clean(app.ExeNames);
        app.TitleContains = Clean(app.TitleContains);
        app.Shortcuts = (app.Shortcuts ?? new())
            .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Keys))
            .Select(s => s with { Action = s.Action ?? "", Category = s.Category ?? "General" })
            .ToList();
    }

    private static List<string> Clean(List<string>? values) =>
        (values ?? new()).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();

    /// <summary>
    /// An app id doubles as a file name (<c>apps/&lt;id&gt;.json</c>) and arrives from agents over
    /// MCP, so it has to be a plain name: no separators, no drive, no trailing dot or space
    /// (which Windows silently strips, aliasing another file).
    /// </summary>
    public static bool IsValidAppId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
        if (id != id.Trim() || id.EndsWith('.') || id.StartsWith('.')) return false;
        foreach (var c in id)
            if (char.IsControl(c) || "/\\:*?\"<>|".Contains(c)) return false;
        return true;
    }

    /// <summary>
    /// Persist only the `overlay` block, in place.
    ///
    /// Rewriting the whole file would copy every app loaded from config/apps/*.json into
    /// shortcuts.json, and on the next reload those copies would shadow the real files.
    /// Throws <see cref="JsonException"/> rather than overwrite a file it cannot parse.
    /// </summary>
    public void SaveOverlay()
    {
        var root = ReadMainForEdit();
        root["overlay"] = JsonSerializer.SerializeToNode(Config.Overlay, Json);
        WriteAtomic(MainPath, root.ToJsonString(Json));
    }

    /// <summary>
    /// Write one app back to wherever it was loaded from.
    ///
    /// An app defined inside shortcuts.json (the shipped <c>global</c> fallback) is updated
    /// there. Writing it to apps/&lt;id&gt;.json instead produced a file the loader ignores,
    /// because the main file's definition wins — the edit reported success and then vanished.
    /// </summary>
    public void SaveApp(AppConfig app)
    {
        if (!IsValidAppId(app.Id))
            throw new ArgumentException($"'{app.Id}' is not a usable app id (it is also a file name)", nameof(app));

        if (_inlineAppIds.Contains(app.Id))
        {
            var root = ReadMainForEdit();
            var apps = root["apps"] as JsonArray ?? new JsonArray();
            root["apps"] = apps;

            var node = JsonSerializer.SerializeToNode(app, Json);
            var index = -1;
            for (var i = 0; i < apps.Count && index < 0; i++)
                if (apps[i] is JsonObject o && o["id"]?.GetValueKind() == JsonValueKind.String
                    && o["id"]!.GetValue<string>().Trim() == app.Id)
                    index = i;

            if (index >= 0) apps[index] = node;
            else apps.Add(node);

            WriteAtomic(MainPath, root.ToJsonString(Json));
        }
        else
        {
            Directory.CreateDirectory(AppsDir);
            WriteAtomic(Path.Combine(AppsDir, app.Id + ".json"), JsonSerializer.Serialize(app, Json));
        }

        Reload();
    }

    public bool AddShortcut(string appId, Shortcut s)
    {
        if (!IsValidAppId(appId))
            throw new ArgumentException($"'{appId}' is not a usable app id (it is also a file name)", nameof(appId));
        if (string.IsNullOrWhiteSpace(s.Keys))
            throw new ArgumentException("a shortcut needs its keys", nameof(s));

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

    private JsonObject ReadMainForEdit()
    {
        Directory.CreateDirectory(ConfigDir);
        if (!File.Exists(MainPath)) return new JsonObject();
        return JsonNode.Parse(File.ReadAllText(MainPath)) as JsonObject ?? new JsonObject();
    }

    /// <summary>
    /// Write beside the target, then swap. A crash or a full disk mid-write leaves the old
    /// file intact instead of a truncated one that the next start cannot parse.
    /// </summary>
    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
