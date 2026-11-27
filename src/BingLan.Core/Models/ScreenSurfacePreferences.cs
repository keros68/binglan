using System.Text.Json;
using System.Text.Json.Serialization;
using BingLan.Core.Dock;

namespace BingLan.Core.Models;

/// <summary>Machine-local overrides. Null values inherit the shared bar settings.</summary>
public sealed record ScreenSurfacePreferences
{
    public string? DisplayLabel { get; set; }
    public bool? DockEnabled { get; set; }
    public bool? TopBarEnabled { get; set; }
    public DockEdge? DockEdge { get; set; }
    public DockVisibilityMode? DockVisibilityMode { get; set; }
    public TopBarVisibilityMode? TopBarVisibilityMode { get; set; }
    public double? TopBarHeight { get; set; }
    public double? DockHiddenHandleLeftDip { get; set; }
    public double? DockHiddenHandleTopDip { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonIgnore]
    public bool IsEmpty => DockEnabled is null && TopBarEnabled is null && DockEdge is null
        && DockVisibilityMode is null && TopBarVisibilityMode is null && TopBarHeight is null
        && DockHiddenHandleLeftDip is null && DockHiddenHandleTopDip is null
        && ExtensionData is not { Count: > 0 };

    public void Normalize()
    {
        DockEdge = ValidEnum(DockEdge);
        DockVisibilityMode = ValidEnum(DockVisibilityMode);
        TopBarVisibilityMode = ValidEnum(TopBarVisibilityMode);
        TopBarHeight = TopBarHeight is { } height && double.IsFinite(height)
            ? Math.Clamp(height, TopBarState.MinimumHeightDip, TopBarState.MaximumHeightDip) : null;
        DockHiddenHandleLeftDip = Finite(DockHiddenHandleLeftDip);
        DockHiddenHandleTopDip = Finite(DockHiddenHandleTopDip);
        if (DockHiddenHandleLeftDip is null || DockHiddenHandleTopDip is null)
        {
            DockHiddenHandleLeftDip = DockHiddenHandleTopDip = null;
        }
    }

    private static T? ValidEnum<T>(T? value) where T : struct, Enum =>
        value is { } candidate && Enum.IsDefined(candidate) ? candidate : null;

    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;
}
