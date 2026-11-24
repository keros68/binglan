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

public enum TaskbarEdgeRevealMode
{
    AnyEdge,
    CornersOnly,
    Disabled
}

public sealed class TaskbarState
{
    public TaskbarMode Mode { get; set; } = TaskbarMode.SystemDefault;

    /// <summary>
    /// Legacy boolean kept for Schema &lt;= 56 compatibility; mirrored from
    /// <see cref="EdgeRevealMode"/> on load and update.
    /// </summary>
    public bool RevealOnlyAtCorners { get; set; } = true;

    /// <summary>
    /// Controls where pointer contact along the bottom screen edge can reveal an
    /// auto-hidden taskbar: anywhere along the edge, only near the left/right corners
    /// (default, avoiding accidental triggers under the centred Dock), or nowhere
    /// (keyboard Win key / Dock start button still open it).
    /// </summary>
    public TaskbarEdgeRevealMode EdgeRevealMode { get; set; } = TaskbarEdgeRevealMode.CornersOnly;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
