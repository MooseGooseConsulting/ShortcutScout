namespace ShortcutScout.Core;

/// <summary>
/// Finds the config folder without knowing where the repo was cloned.
///
/// The exe runs from <c>src/ShortcutScout/bin/&lt;cfg&gt;/net8.0-windows/</c>, five levels below
/// the repo's <c>config/</c>, so each starting directory is searched upwards. A folder only
/// counts if it looks like ours (<c>shortcuts.json</c> or <c>apps/</c>): launching from some
/// other project that happens to have a <c>config/</c> directory must not adopt it.
/// </summary>
public static class ConfigLocator
{
    public const string FolderName = "config";

    /// <param name="startDirs">Directories to search from, most preferred first.</param>
    /// <param name="maxDepth">How many parent levels to climb from each start.</param>
    /// <returns>
    /// The first config folder found; otherwise <c>config</c> beside the first start directory,
    /// which is where a first save will create it.
    /// </returns>
    public static string Find(IEnumerable<string> startDirs, int maxDepth = 8)
    {
        string? first = null;

        foreach (var start in startDirs)
        {
            if (string.IsNullOrWhiteSpace(start)) continue;

            DirectoryInfo? dir;
            try { dir = new DirectoryInfo(Path.GetFullPath(start)); }
            catch { continue; } // an unusable path is just not a candidate

            first ??= dir.FullName;

            for (var depth = 0; dir != null && depth <= maxDepth; depth++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, FolderName);
                if (LooksLikeConfig(candidate)) return candidate;
            }
        }

        return Path.Combine(first ?? Directory.GetCurrentDirectory(), FolderName);
    }

    public static bool LooksLikeConfig(string dir) =>
        File.Exists(Path.Combine(dir, "shortcuts.json")) || Directory.Exists(Path.Combine(dir, "apps"));
}
