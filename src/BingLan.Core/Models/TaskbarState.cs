using System.Text.Json;
using System.Text.Json.Serialization;

namespace BingLan.Core.Models;

public enum TaskbarMode
{
    SystemDefault,
    Transparent,
    SmartHide,
    Blur
}

public sealed class TaskbarState
{
    public TaskbarMode Mode { get; set; } = TaskbarMode.SystemDefault;

    /// <summary>
    /// Whether an auto-hidden bottom taskbar appears only at the left and right ends of the
    /// edge rather than anywhere along it.
    /// </summary>
    public bool RevealOnlyAtCorners { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
