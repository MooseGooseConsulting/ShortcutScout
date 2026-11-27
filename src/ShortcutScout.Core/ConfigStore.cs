using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShortcutScout.Core;

public sealed class ConfigStore
{
    // Relaxed escaping: these files are meant to be read and edited by hand, and the default
    // encoder writes the plus in "Ctrl+T" as a six-character unicode escape (U+002B).
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Ids of the apps defined inside shortcuts.json rather than in apps/*.json.</summary>
    private HashSet<string> _inlineAppIds = new(StringComparer.Ordinal);

    /// <summary>
    /// The last <see cref="Reload"/> could not read shortcuts.json, so which ids it defines
    /// is unknown — different from the file simply not existing.
    /// </summary>
    private bool _mainUnreadable;

    // Windows device names. "con.json" opens the console device on Windows 10 and earlier,
    // whatever the extension, so these can never be an apps/<id>.json file.
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

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
        var mainUnreadable = false;

        try
        {
            if (File.Exists(MainPath))
                cfg = JsonSerializer.Deserialize<GlobalConfig>(File.ReadAllText(MainPath)) ?? cfg;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            mainUnreadable = true;
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
            foreach (var f in ListAppFiles(errors))
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
        _mainUnreadable = mainUnreadable;
        Config = cfg;
        LoadErrors = errors;
    }

    /// <summary>
    /// Listing the folder can fail on its own (access denied, or removed since the check).
    /// That is one more thing to report, not a reason to break "Reload never throws".
    /// </summary>
    private List<string> ListAppFiles(List<string> errors)
    {
        try
        {
            return Directory.EnumerateFiles(AppsDir, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"apps/: {ex.Message}");
            return new List<string>();
        }
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
    /// (which Windows silently strips, aliasing another file), and not a device name.
    /// </summary>
    public static bool IsValidAppId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
        if (id != id.Trim() || id.EndsWith('.') || id.StartsWith('.')) return false;
        foreach (var c in id)
            if (char.IsControl(c) || "/\\:*?\"<>|".Contains(c)) return false;
        // Windows decides "is this a device" from the part before the first dot.
        if (ReservedFileNames.Contains(id.Split('.')[0].TrimEnd())) return false;
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
            // While shortcuts.json is unreadable nobody knows whether it defines this id. If it
            // does, a new apps/<id>.json would be ignored the moment the main file is repaired
            // — the same vanishing edit, one step later. An app already loaded from apps/ is
            // known to live there, so it can still be saved.
            var loaded = Config.Apps.Any(a => a.Id == app.Id);
            if (_mainUnreadable && !loaded)
                throw new IOException(
                    $"shortcuts.json could not be read, so '{app.Id}' may already be defined there; fix that file first");

            // The same blindness one level down: the file is there, but this app did not come
            // out of it — it failed to parse (a hand edit in progress), or it defines another
            // id. Saving would replace everything in it with the one row being added.
            var path = Path.Combine(AppsDir, app.Id + ".json");
            if (!loaded && File.Exists(path))
                throw new IOException(
                    $"apps/{app.Id}.json exists but '{app.Id}' was not loaded from it; fix that file first");

            Directory.CreateDirectory(AppsDir);
            WriteAtomic(path, JsonSerializer.Serialize(app, Json));
        }

        Reload();
    }

    public bool AddShortcut(string appId, Shortcut s)
    {
        if (!IsValidAppId(appId))
            throw new ArgumentException($"'{appId}' is not a usable app id (it is also a file name)", nameof(appId));
        if (string.IsNullOrWhiteSpace(s.Keys))
            throw new ArgumentException("a shortcut needs its keys", nameof(s));

        // Edit what is on disk now, not what this process read when it started. An MCP server
        // stays up for hours; saving its old copy of the app would drop every shortcut added
        // by hand in the meantime.
        Reload();

        var app = Config.Apps.FirstOrDefault(a => a.Id == appId) ?? new AppConfig { Id = appId, Name = appId };
        if (app.Shortcuts.Any(x => x.Keys == s.Keys && x.Action == s.Action)) return false;
        SaveApp(WithShortcuts(app, app.Shortcuts.Append(s)));
        return true;
    }

    public bool RemoveShortcut(string appId, string keys)
    {
        Reload(); // same reason as AddShortcut

        var app = Config.Apps.FirstOrDefault(a => a.Id == appId);
        if (app == null || !app.Shortcuts.Any(x => x.Keys == keys)) return false;
        SaveApp(WithShortcuts(app, app.Shortcuts.Where(x => x.Keys != keys)));
        return true;
    }

    /// <summary>
    /// A copy to save, leaving the loaded app alone. If the save throws, memory must still
    /// match the disk: an add that stayed in memory would answer "already exists" to the retry
    /// and never be written.
    /// </summary>
    private static AppConfig WithShortcuts(AppConfig app, IEnumerable<Shortcut> shortcuts) => new()
    {
        Id = app.Id,
        Name = app.Name,
        Priority = app.Priority,
        ProcessNames = app.ProcessNames,
        ExeNames = app.ExeNames,
        TitleContains = app.TitleContains,
        Shortcuts = shortcuts.ToList(),
    };

    /// <summary>
    /// The main file as an editable tree — only if <see cref="Reload"/> would accept it.
    ///
    /// Valid JSON is not enough. A file that parses but does not load (an array at the top
    /// level, <c>"width": "460"</c>) leaves the store holding defaults, and writing those back
    /// would replace exactly the block the user still has to repair.
    /// </summary>
    private JsonObject ReadMainForEdit()
    {
        Directory.CreateDirectory(ConfigDir);
        if (!File.Exists(MainPath)) return new JsonObject();

        var text = File.ReadAllText(MainPath);
        JsonSerializer.Deserialize<GlobalConfig>(text); // throws JsonException, as Reload reports it
        return JsonNode.Parse(text) as JsonObject
            ?? throw new JsonException("shortcuts.json: the top level must be a JSON object");
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
