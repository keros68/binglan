using System.Text.Json;
using System.Text.Json.Serialization;
using BingLan.Core.Dock;

namespace BingLan.Core.Models;

public enum ScreenSurfaceEnableMode
{
    GlobalTarget,
    FollowGlobal,
    Enabled,
    Disabled
}

/// <summary>Machine-local overrides. Null values inherit the shared bar settings.</summary>
public sealed record ScreenSurfacePreferences
{
    public string? DisplayLabel { get; set; }
    // Existing nullable overrides remain readable by builds that only support the global target and explicit on/off.
    public bool? DockEnabled { get; set; }
    public bool? TopBarEnabled { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DockFollowsGlobal { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TopBarFollowsGlobal { get; set; }
    public DockEdge? DockEdge { get; set; }
    public DockVisibilityMode? DockVisibilityMode { get; set; }
    public TopBarVisibilityMode? TopBarVisibilityMode { get; set; }
    public double? TopBarHeight { get; set; }
    public double? DockHiddenHandleLeftDip { get; set; }
    public double? DockHiddenHandleTopDip { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonIgnore]
    public ScreenSurfaceEnableMode DockEnableMode
    {
        get => GetEnableMode(DockEnabled, DockFollowsGlobal);
        set => (DockEnabled, DockFollowsGlobal) = GetEnablePreference(value);
    }

    [JsonIgnore]
    public ScreenSurfaceEnableMode TopBarEnableMode
    {
        get => GetEnableMode(TopBarEnabled, TopBarFollowsGlobal);
        set => (TopBarEnabled, TopBarFollowsGlobal) = GetEnablePreference(value);
    }

    [JsonIgnore]
    public bool IsEmpty => DockEnableMode == ScreenSurfaceEnableMode.GlobalTarget
        && TopBarEnableMode == ScreenSurfaceEnableMode.GlobalTarget && DockEdge is null
        && DockVisibilityMode is null && TopBarVisibilityMode is null && TopBarHeight is null
        && DockHiddenHandleLeftDip is null && DockHiddenHandleTopDip is null
        && ExtensionData is not { Count: > 0 };

    public void Normalize()
    {
        DockEnableMode = GetEnableMode(DockEnabled, DockFollowsGlobal);
        TopBarEnableMode = GetEnableMode(TopBarEnabled, TopBarFollowsGlobal);
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

    private static ScreenSurfaceEnableMode GetEnableMode(bool? enabled, bool followsGlobal) => enabled switch
    {
        true => ScreenSurfaceEnableMode.Enabled,
        false => ScreenSurfaceEnableMode.Disabled,
        _ => followsGlobal ? ScreenSurfaceEnableMode.FollowGlobal : ScreenSurfaceEnableMode.GlobalTarget
    };

    private static (bool? Enabled, bool FollowsGlobal) GetEnablePreference(ScreenSurfaceEnableMode mode) => mode switch
    {
        ScreenSurfaceEnableMode.Enabled => (true, false),
        ScreenSurfaceEnableMode.Disabled => (false, false),
        ScreenSurfaceEnableMode.FollowGlobal => (null, true),
        _ => (null, false)
    };

    private static T? ValidEnum<T>(T? value) where T : struct, Enum =>
        value is { } candidate && Enum.IsDefined(candidate) ? candidate : null;

    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;
}
