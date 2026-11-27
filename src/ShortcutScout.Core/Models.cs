using System.Text.Json.Serialization;

namespace ShortcutScout.Core;

public sealed record Shortcut(
    [property: JsonPropertyName("keys")] string Keys,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("discovery")] bool Discovery = false,
    [property: JsonPropertyName("category")] string Category = "General");

public sealed class AppConfig
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>
    /// Higher wins when several apps could match the same window.
    /// Needed because web apps (Devin, ChatGPT) live inside a browser whose
    /// window title also matches the browser's own config.
    /// </summary>
    [JsonPropertyName("priority")] public int Priority { get; set; }

    [JsonPropertyName("process_names")] public List<string> ProcessNames { get; set; } = new();
    [JsonPropertyName("exe_names")] public List<string> ExeNames { get; set; } = new();
    [JsonPropertyName("title_contains")] public List<string> TitleContains { get; set; } = new();
    [JsonPropertyName("shortcuts")] public List<Shortcut> Shortcuts { get; set; } = new();
}

public sealed class GlobalConfig
{
    [JsonPropertyName("apps")] public List<AppConfig> Apps { get; set; } = new();

    [JsonPropertyName("overlay")] public OverlaySettings Overlay { get; set; } = new();
}

public sealed record ForegroundInfo(string ProcessName, string ExeName, string Title);
