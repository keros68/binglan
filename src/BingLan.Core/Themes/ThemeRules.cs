using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.Core.Themes;

public readonly record struct ThemeArea(double Left, double Top, double Width, double Height);

/// <summary>
/// Local apps found for a theme's dock slots. <see cref="ResolvedIconEntries"/> runs
/// parallel to <see cref="Resolved"/> and names each app's custom icon entry, if any.
/// </summary>
public sealed record ThemeBindingResult(
    IReadOnlyList<DockPinnedApp> Resolved,
    IReadOnlyList<ThemeAppBinding> Missing,
    IReadOnlyList<string?> ResolvedIconEntries);

public static class ThemeRules
{
    public const int MaximumNameLength = 64;
    public const int MaximumBindings = 40;
    public const int MaximumIcons = 16;
    private const double MinimumFraction = 0.02d;

    /// <summary>Builds a shareable theme from the current desktop, dock and work area.</summary>
    public static ThemePackage Export(AppState state, string name, ThemeArea workArea, Version appVersion)
    {
        ArgumentNullException.ThrowIfNull(state);
        DesktopExperienceRules.Normalize(state.DesktopExperience);

        var package = new ThemePackage
        {
            Manifest = new ThemeManifest
            {
                Name = CleanName(name),
                MinimumAppVersion = $"{appVersion.Major}.{appVersion.Minor}.{Math.Max(0, appVersion.Build)}"
            },
            Layout = new ThemeLayout { Preset = state.DesktopExperience.ActivePreset },
            Bindings = new ThemeBindings
            {
                DockEnabled = state.Dock.IsEnabled,
                DockVisibility = state.Dock.VisibilityMode,
                TopBarEnabled = state.TopBar.IsEnabled,
                TopBarVisibility = state.TopBar.VisibilityMode,
                TopBarModules = CopyTopBarModules(state.TopBar.Modules),
                TopBarFollowCardLook = state.TopBar.FollowCardLook,
                TopBarSurfaceColor = state.TopBar.SurfaceColor,
                TopBarSurfaceOpacity = state.TopBar.SurfaceOpacity
            }
        };
        state.Style ??= new DesktopStyleState();
        package.Tokens.Style = new ThemeStyleTokens
        {
            CardShadow = state.Style.CardShadow,
            CardSpacing = state.Style.CardSpacing,
            Motion = state.Style.Motion,
            TaskbarMaterial = state.Taskbar.Mode switch
            {
                TaskbarMode.Transparent => TaskbarMaterial.Transparent,
                TaskbarMode.Blur => TaskbarMaterial.Blur,
                _ => TaskbarMaterial.SystemDefault
            }
        };

        foreach (var component in state.DesktopExperience.Components)
        {
            package.Tokens.Components.Add(new ThemeComponentTokens
            {
                Kind = component.Kind,
                IsVisible = component.IsVisible,
                FontScale = component.FontScale,
                CornerRadius = component.CornerRadius,
                Density = component.Density,
                Appearance = CopyAppearance(component.Appearance)
            });
            package.Layout.Components.Add(new ThemeComponentPlacement
            {
                Kind = component.Kind,
                X = Fraction(component.Placement.Left - workArea.Left, workArea.Width),
                Y = Fraction(component.Placement.Top - workArea.Top, workArea.Height),
                Width = Fraction(component.Placement.Width, workArea.Width),
                Height = Fraction(component.Placement.Height, workArea.Height)
            });
        }

        var icons = IconSources(state).ToDictionary(source => source.Index, source => source.Entry);
        for (var index = 0; index < state.Dock.PinnedApps.Count; index++)
        {
            var app = state.Dock.PinnedApps[index];
            var executableName = app.ExecutablePath is null ? null : Path.GetFileName(app.ExecutablePath);
            package.Bindings.Apps.Add(new ThemeAppBinding
            {
                Slot = AppSlotCatalog.SlotFor(executableName),
                DisplayName = app.DisplayName,
                ExecutableName = executableName,
                AppUserModelId = app.AppUserModelId,
                IconEntry = icons.GetValueOrDefault(index)
            });
        }

        Normalize(package);
        return package;
    }

    /// <summary>
    /// The custom dock icons an export carries: the pinned app's index, the package entry
    /// the icon is written to and the local icon file it comes from.
    /// </summary>
    public static IEnumerable<(int Index, string Entry, string IconFile)> IconSources(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var count = 0;
        var limit = Math.Min(state.Dock.PinnedApps.Count, MaximumBindings);
        for (var index = 0; index < limit && count < MaximumIcons; index++)
        {
            if (state.Dock.PinnedApps[index].IconFile is { } iconFile && IsIconFileName(iconFile))
            {
                count++;
                yield return (index, $"icons/{index}.png", iconFile);
            }
        }
    }

    /// <summary>A custom icon is stored under a generated name: 32 hex digits and ".png".</summary>
    public static bool IsIconFileName(string? name) =>
        name is { Length: 36 }
        && name.EndsWith(".png", StringComparison.Ordinal)
        && name[..32].All(Uri.IsHexDigit);

    /// <summary>A package icon entry is "icons/" followed by a small number and ".png".</summary>
    public static bool IsIconEntry(string? entry) =>
        entry is { Length: > 10 and <= 14 }
        && entry.StartsWith("icons/", StringComparison.Ordinal)
        && entry.EndsWith(".png", StringComparison.Ordinal)
        && entry[6..^4].All(char.IsAsciiDigit);

    /// <summary>
    /// Applies the desktop-wide style. The taskbar material is only taken over while the
    /// taskbar is not set to hide, since hiding is a behaviour the user chose, not a look.
    /// </summary>
    public static void ApplyStyle(ThemePackage package, DesktopStyleState style, TaskbarState taskbar)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(taskbar);
        if (package.Tokens.Style is not { } tokens)
        {
            return;
        }

        style.CardShadow = tokens.CardShadow;
        style.CardSpacing = tokens.CardSpacing;
        style.Motion = tokens.Motion;
        if (taskbar.Mode != TaskbarMode.SmartHide)
        {
            taskbar.Mode = tokens.TaskbarMaterial switch
            {
                TaskbarMaterial.Transparent => TaskbarMode.Transparent,
                TaskbarMaterial.Blur => TaskbarMode.Blur,
                _ => TaskbarMode.SystemDefault
            };
        }
    }

    /// <summary>Clamps every imported value into the range the app accepts.</summary>
    public static void Normalize(ThemePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        package.Manifest ??= new ThemeManifest();
        package.Manifest.Name = CleanName(package.Manifest.Name);
        package.Manifest.Author = CleanText(package.Manifest.Author, MaximumNameLength);
        package.Tokens ??= new ThemeTokens();
        package.Layout ??= new ThemeLayout();
        package.Bindings ??= new ThemeBindings();

        package.Tokens.Components = (package.Tokens.Components ?? [])
            .Where(component => component is not null && Enum.IsDefined(component.Kind))
            .GroupBy(component => component.Kind)
            .Select(group => group.First())
            .ToList();
        foreach (var component in package.Tokens.Components)
        {
            component.FontScale = DesktopExperienceRules.CoerceFontScale(component.FontScale);
            component.CornerRadius = WidgetAppearanceRules.CoerceCornerRadius(component.CornerRadius);
            if (!Enum.IsDefined(component.Density))
            {
                component.Density = DesktopInformationDensity.Standard;
            }
            component.Appearance = CopyAppearance(component.Appearance ?? new WidgetAppearanceState());
        }

        if (package.Tokens.Style is { } style)
        {
            if (!Enum.IsDefined(style.CardShadow))
            {
                style.CardShadow = CardShadow.None;
            }
            if (!Enum.IsDefined(style.Motion))
            {
                style.Motion = MotionLevel.Standard;
            }
            if (!Enum.IsDefined(style.TaskbarMaterial))
            {
                style.TaskbarMaterial = TaskbarMaterial.SystemDefault;
            }
            style.CardSpacing = DesktopStyleRules.CoerceCardSpacing(style.CardSpacing);
        }

        if (!Enum.IsDefined(package.Layout.Preset))
        {
            package.Layout.Preset = DesktopLayoutPreset.QuietInformation;
        }
        package.Layout.Components = (package.Layout.Components ?? [])
            .Where(placement => placement is not null && Enum.IsDefined(placement.Kind))
            .GroupBy(placement => placement.Kind)
            .Select(group => group.First())
            .ToList();
        foreach (var placement in package.Layout.Components)
        {
            placement.Width = Clamp(placement.Width, MinimumFraction, 1d);
            placement.Height = Clamp(placement.Height, MinimumFraction, 1d);
            placement.X = Clamp(placement.X, 0d, 1d - placement.Width);
            placement.Y = Clamp(placement.Y, 0d, 1d - placement.Height);
        }

        if (!Enum.IsDefined(package.Bindings.DockVisibility))
        {
            package.Bindings.DockVisibility = DockVisibilityMode.ReserveWorkArea;
        }
        if (!Enum.IsDefined(package.Bindings.TopBarVisibility))
        {
            package.Bindings.TopBarVisibility = TopBarVisibilityMode.ReserveTopEdge;
        }
        package.Bindings.TopBarSurfaceColor = WidgetAppearanceRules.CoerceBackgroundColor(
            package.Bindings.TopBarSurfaceColor);
        if (double.IsNaN(package.Bindings.TopBarSurfaceOpacity)
            || double.IsInfinity(package.Bindings.TopBarSurfaceOpacity))
        {
            package.Bindings.TopBarSurfaceOpacity = TopBarState.DefaultSurfaceOpacity;
        }
        else
        {
            package.Bindings.TopBarSurfaceOpacity = Math.Clamp(
                package.Bindings.TopBarSurfaceOpacity,
                0d,
                1d);
        }
        package.Bindings.Apps = (package.Bindings.Apps ?? [])
            .Where(binding => binding is not null)
            .Take(MaximumBindings)
            .ToList();
        foreach (var binding in package.Bindings.Apps)
        {
            binding.Slot = CleanText(binding.Slot, 32) is { Length: > 0 } slot ? slot : AppSlotCatalog.OtherSlot;
            // A display name that looks like a path could carry a local user folder; the
            // slot then stands in for it.
            binding.DisplayName = CleanText(binding.DisplayName, MaximumNameLength) is { } displayName
                && displayName.IndexOfAny(['\\', '/', ':']) < 0
                    ? displayName
                    : string.Empty;
            // Only a bare file name is accepted; anything path-like is dropped.
            binding.ExecutableName = CleanText(binding.ExecutableName, 128) is { Length: > 0 } executable
                && executable.IndexOfAny(['\\', '/', ':']) < 0
                && executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? executable
                    : null;
            binding.AppUserModelId = CleanText(binding.AppUserModelId, 256) is { Length: > 0 } id
                && id.IndexOfAny(['\\', '/']) < 0
                    ? id
                    : null;
            binding.IconEntry = IsIconEntry(binding.IconEntry) ? binding.IconEntry : null;
        }
    }

    /// <summary>
    /// Applies the top bar's shareable settings. A package from before the top bar
    /// carried no module switches, so those stay as the importing user had them; the
    /// monitor stays machine-local either way.
    /// </summary>
    public static void ApplyTopBar(ThemePackage package, TopBarState topBar)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(topBar);
        topBar.IsEnabled = package.Bindings.TopBarEnabled;
        topBar.VisibilityMode = package.Bindings.TopBarVisibility;
        if (package.Bindings.TopBarModules is { } modules)
        {
            topBar.Modules = CopyTopBarModules(modules);
        }
        topBar.FollowCardLook = package.Bindings.TopBarFollowCardLook;
        topBar.SurfaceColor = package.Bindings.TopBarSurfaceColor;
        topBar.SurfaceOpacity = package.Bindings.TopBarSurfaceOpacity;
    }

    /// <summary>Applies visual tokens, component switches and layout to the desktop.</summary>
    public static void ApplyVisuals(ThemePackage package, DesktopExperienceState experience, ThemeArea workArea)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(experience);
        DesktopExperienceRules.Normalize(experience);

        experience.ActivePreset = package.Layout.Preset;
        foreach (var tokens in package.Tokens.Components)
        {
            var component = experience.GetComponent(tokens.Kind);
            component.IsVisible = tokens.IsVisible;
            component.FontScale = tokens.FontScale;
            component.CornerRadius = tokens.CornerRadius;
            component.Density = tokens.Density;
            component.Appearance = CopyAppearance(tokens.Appearance);
        }

        foreach (var placement in package.Layout.Components)
        {
            experience.GetComponent(placement.Kind).Placement = new WindowPlacement
            {
                Left = workArea.Left + (placement.X * workArea.Width),
                Top = workArea.Top + (placement.Y * workArea.Height),
                Width = placement.Width * workArea.Width,
                Height = placement.Height * workArea.Height
            };
        }
    }

    /// <summary>
    /// Maps each slot to a local app: the recorded app first, then other well-known apps
    /// for the same slot. Slots nothing could be found for are returned as missing.
    /// </summary>
    public static ThemeBindingResult ResolveBindings(
        ThemeBindings bindings,
        Func<ThemeAppBinding, string?, DockPinnedApp?> resolve)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(resolve);

        var resolved = new List<DockPinnedApp>();
        var resolvedIcons = new List<string?>();
        var missing = new List<ThemeAppBinding>();
        var dock = new DockState();
        foreach (var binding in bindings.Apps)
        {
            var app = resolve(binding, binding.ExecutableName)
                ?? AppSlotCatalog.CandidatesFor(binding.Slot)
                    .Where(candidate => !string.Equals(candidate, binding.ExecutableName, StringComparison.OrdinalIgnoreCase))
                    .Select(candidate => resolve(binding, candidate))
                    .FirstOrDefault(candidate => candidate is not null);
            if (app is not null && DockPinRules.Pin(dock, app))
            {
                resolved.Add(app);
                resolvedIcons.Add(binding.IconEntry);
            }
            else if (app is null)
            {
                missing.Add(binding);
            }
        }
        return new ThemeBindingResult(resolved, missing, resolvedIcons);
    }

    private static WidgetAppearanceState CopyAppearance(WidgetAppearanceState source) => new()
    {
        BackgroundColor = source.BackgroundColor,
        BackgroundOpacity = source.BackgroundOpacity,
        HeaderColor = source.HeaderColor,
        HeaderOpacity = source.HeaderOpacity,
        TextColor = source.TextColor,
        TitleFontFamily = source.TitleFontFamily,
        TitleFontBold = source.TitleFontBold,
        TitleFontItalic = source.TitleFontItalic
    };

    private static TopBarModuleSwitches CopyTopBarModules(TopBarModuleSwitches source) => new()
    {
        TodoSummary = source.TodoSummary,
        Weather = source.Weather,
        Performance = source.Performance,
        Attention = source.Attention,
        InputMethod = source.InputMethod,
        Volume = source.Volume,
        Network = source.Network,
        Battery = source.Battery,
        Clock = source.Clock
    };

    private static string CleanName(string? name) =>
        CleanText(name, MaximumNameLength) is { Length: > 0 } clean ? clean : "我的冰蓝主题";

    private static string CleanText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var clean = new string(value.Trim().Where(character => !char.IsControl(character)).ToArray());
        return clean.Length > maximumLength ? clean[..maximumLength] : clean;
    }

    private static double Fraction(double value, double total) =>
        total > 0 ? value / total : 0d;

    private static double Clamp(double value, double minimum, double maximum) =>
        double.IsNaN(value) || double.IsInfinity(value)
            ? minimum
            : Math.Clamp(value, minimum, Math.Max(minimum, maximum));
}
