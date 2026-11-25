using System.Text.Json;
using System.Text.Json.Serialization;

namespace BingLan.Core.Models;

/// <summary>
/// Per-display card profiles for secondary monitors. Null or disabled keeps the single-screen
/// code path with zero extra runtime allocations, windows, hooks, or timers.
/// Machine-local: never exported into shared themes.
/// </summary>
public sealed class MultiScreenState
{
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Profiles for secondary monitors keyed by stable hardware <c>MonitorKey</c>.
    /// The primary monitor always stays in the root <see cref="AppState"/> fields.
    /// </summary>
    public Dictionary<string, ScreenProfileState> Screens { get; set; } = new(StringComparer.Ordinal);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Independent desktop card profile owned by one secondary monitor.
/// </summary>
public sealed class ScreenProfileState
{
    public string MonitorKey { get; set; } = string.Empty;

    public string? LastKnownDeviceName { get; set; }

    public string DisplayLabel { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HardwareModelId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FriendlyName { get; set; }

    public DesktopExperienceState Experience { get; set; } = MultiScreenRules.CreateBlankExperience();

    public List<QuickPlaceState> QuickPlaces { get; set; } = QuickPlaceRules.CreateDefaults();

    /// <summary>
    /// Optional ID of the to-do card bound to this monitor's built-in <see cref="DesktopComponentKind.Todo"/>
    /// component slot, stored as a canonical GUID string.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryTodoId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonIgnore]
    public Guid? ParsedPrimaryTodoId =>
        Guid.TryParse(PrimaryTodoId, out var id) && id != Guid.Empty ? id : null;
}

public static class MultiScreenRules
{
    public const string PrimaryScreenSentinel = "primary";

    /// <summary>
    /// Creates an empty card profile for a newly seen secondary monitor so plugging in an
    /// unfamiliar display never clutters it with unasked-for cards.
    /// </summary>
    public static DesktopExperienceState CreateBlankExperience()
    {
        var experience = DesktopExperienceRules.CreateDefault();
        foreach (var component in experience.Components)
        {
            component.IsVisible = false;
        }
        return experience;
    }

    public static ScreenProfileState CreateBlankProfile(
        string monitorKey,
        string? deviceName = null,
        string? displayLabel = null)
    {
        var normalizedKey = NormalizeScreenKey(monitorKey)
            ?? throw new ArgumentException("Secondary monitor key cannot be empty or primary.", nameof(monitorKey));
        return new ScreenProfileState
        {
            MonitorKey = normalizedKey,
            LastKnownDeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim(),
            DisplayLabel = string.IsNullOrWhiteSpace(displayLabel) ? normalizedKey : displayLabel.Trim(),
            HardwareModelId = ExtractHardwareModelId(normalizedKey),
            Experience = CreateBlankExperience(),
            QuickPlaces = QuickPlaceRules.CreateDefaults()
        };
    }

    public static string? ExtractHardwareModelId(string? hardwarePathOrKey)
    {
        if (string.IsNullOrWhiteSpace(hardwarePathOrKey))
        {
            return null;
        }

        var trimmed = hardwarePathOrKey.Trim();
        if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            trimmed = trimmed[4..];
        }

        trimmed = trimmed.ToUpperInvariant();
        var guidIndex = trimmed.IndexOf("#{", StringComparison.Ordinal);
        if (guidIndex > 0)
        {
            trimmed = trimmed[..guidIndex];
        }

        var portIndex = trimmed.IndexOf("@PORT:", StringComparison.Ordinal);
        if (portIndex > 0)
        {
            trimmed = trimmed[..portIndex];
        }

        if (!trimmed.StartsWith("DISPLAY#", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = trimmed.Split('#', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var model = parts[1].Trim();
        if (string.IsNullOrEmpty(model)
            || string.Equals(model, "DEFAULT_MONITOR", StringComparison.Ordinal)
            || string.Equals(model, "UNKNOWN", StringComparison.Ordinal)
            || string.Equals(model, "GENERIC", StringComparison.Ordinal))
        {
            return null;
        }

        return $"DISPLAY#{model}";
    }

    public static bool IsGenericMonitorName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        var trimmed = name.Trim();
        if (string.Equals(trimmed, "Generic PnP Monitor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "Generic Non-PnP Monitor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "通用即插即用监视器", StringComparison.Ordinal)
            || string.Equals(trimmed, "通用非即插即用监视器", StringComparison.Ordinal)
            || string.Equals(trimmed, "Default Monitor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "默认监视器", StringComparison.Ordinal)
            || string.Equals(trimmed, "主屏", StringComparison.Ordinal))
        {
            return true;
        }

        if (trimmed.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            trimmed = trimmed[4..];
        }

        if (trimmed.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase)
            && trimmed.Length > 7
            && trimmed[7..].All(char.IsDigit))
        {
            return true;
        }

        if (trimmed.StartsWith("副屏 ", StringComparison.Ordinal)
            && !trimmed.Contains('·', StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    public static string? NormalizeFriendlyName(string? name) =>
        IsGenericMonitorName(name) ? null : name!.Trim();

    /// <summary>
    /// Normalizes a persisted or requested monitor key: blank or reserved "primary" maps to null
    /// (representing the primary monitor).
    /// </summary>
    public static string? NormalizeScreenKey(string? screenKey)
    {
        if (string.IsNullOrWhiteSpace(screenKey))
        {
            return null;
        }

        var trimmed = screenKey.Trim();
        return string.Equals(trimmed, PrimaryScreenSentinel, StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    public static void Normalize(
        MultiScreenState state,
        IReadOnlyList<TodoWidgetState>? allTodos = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        var cleaned = new Dictionary<string, ScreenProfileState>(StringComparer.Ordinal);
        if (state.Screens is not null)
        {
            foreach (var (rawKey, rawProfile) in state.Screens)
            {
                if (rawProfile is null)
                {
                    continue;
                }

                var key = NormalizeScreenKey(rawKey) ?? NormalizeScreenKey(rawProfile.MonitorKey);
                if (key is null)
                {
                    continue;
                }

                rawProfile.MonitorKey = key;
                rawProfile.LastKnownDeviceName = string.IsNullOrWhiteSpace(rawProfile.LastKnownDeviceName)
                    ? null
                    : rawProfile.LastKnownDeviceName.Trim();
                rawProfile.DisplayLabel = string.IsNullOrWhiteSpace(rawProfile.DisplayLabel)
                    ? rawProfile.LastKnownDeviceName ?? key
                    : rawProfile.DisplayLabel.Trim();
                rawProfile.HardwareModelId = ExtractHardwareModelId(rawProfile.HardwareModelId)
                    ?? ExtractHardwareModelId(key);
                rawProfile.FriendlyName = NormalizeFriendlyName(rawProfile.FriendlyName);
                rawProfile.Experience ??= CreateBlankExperience();
                rawProfile.Experience.Components ??= [];
                rawProfile.Experience.Components.RemoveAll(static component => component is null);
                DesktopExperienceRules.Normalize(rawProfile.Experience);
                rawProfile.QuickPlaces = QuickPlaceRules.Normalize(rawProfile.QuickPlaces);

                if (rawProfile.ParsedPrimaryTodoId is { } parsedId)
                {
                    if (allTodos is not null
                        && !allTodos.Any(todo => todo.Id == parsedId
                            && string.Equals(NormalizeScreenKey(todo.ScreenKey), key, StringComparison.Ordinal)))
                    {
                        var replacement = allTodos.FirstOrDefault(todo =>
                            string.Equals(NormalizeScreenKey(todo.ScreenKey), key, StringComparison.Ordinal));
                        rawProfile.PrimaryTodoId = replacement?.Id.ToString("D");
                    }
                    else
                    {
                        rawProfile.PrimaryTodoId = parsedId.ToString("D");
                    }
                }
                else
                {
                    rawProfile.PrimaryTodoId = allTodos?.FirstOrDefault(todo =>
                        string.Equals(NormalizeScreenKey(todo.ScreenKey), key, StringComparison.Ordinal))?.Id.ToString("D");
                }

                cleaned[key] = rawProfile;
            }
        }

        state.Screens = cleaned;
    }
}
