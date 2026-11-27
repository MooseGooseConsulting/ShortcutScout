namespace ShortcutScout.Core;

public static class Matcher
{
    public const int TitleScore = 1000;
    public const int ExeScore = 500;
    public const int ProcessScore = 400;

    /// <summary>
    /// Picks the single best-matching app. A title match always beats a process
    /// match (web apps like Devin/ChatGPT run inside Chrome, so the window title
    /// is the only reliable signal), and explicit <see cref="AppConfig.Priority"/>
    /// breaks ties between apps that both match the title.
    /// </summary>
    public static AppConfig? Match(IEnumerable<AppConfig> apps, ForegroundInfo fg)
    {
        AppConfig? best = null;
        var bestScore = 0;

        foreach (var app in apps)
        {
            var score = Score(app, fg);
            if (score <= 0) continue;
            if (score > bestScore || (score == bestScore && best != null && app.Priority > best.Priority))
            {
                best = app;
                bestScore = score;
            }
        }
        return best;
    }

    public static int Score(AppConfig app, ForegroundInfo fg)
    {
        var kind = 0;
        var needle = 0;

        foreach (var t in app.TitleContains)
        {
            if (!Contains(fg.Title, t)) continue;
            if (TitleScore > kind || (TitleScore == kind && t.Length > needle)) { kind = TitleScore; needle = t.Length; }
        }
        if (kind == 0)
        {
            foreach (var e in app.ExeNames)
                if (Eq(e, fg.ExeName)) { kind = ExeScore; break; }
        }
        if (kind == 0)
        {
            foreach (var p in app.ProcessNames)
                if (Eq(p, fg.ProcessName)) { kind = ProcessScore; break; }
        }
        if (kind == 0) return 0;

        // Priority dominates; match kind and title specificity order the rest.
        return app.Priority * 1_000_000 + kind + Math.Min(needle, 99);
    }

    private static bool Eq(string a, string b) =>
        string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string haystack, string needle) =>
        !string.IsNullOrEmpty(needle) && (haystack ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string Norm(string s) =>
        (s ?? "").Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
}
