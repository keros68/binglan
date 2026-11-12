using System.Text.Json;
using System.Text.Json.Serialization;

namespace BingLan.Core.Models;

/// <summary>Update checking settings; local only, never part of a theme.</summary>
public sealed class UpdateState
{
    /// <summary>Whether the app looks for a new release once a day.</summary>
    public bool AutoCheck { get; set; } = true;

    /// <summary>When a check last reached GitHub, or null before the first one.</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
