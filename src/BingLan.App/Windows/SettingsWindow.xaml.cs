using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Themes;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Themes;
using BingLan.Core.Services;
using BingLan.Core.TopBar;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Control = System.Windows.Controls.Control;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;

namespace BingLan.App.Windows;

public partial class SettingsWindow : Window
{
    private static readonly DesktopComponentKind[] CurrentComponentKinds =
    [
        DesktopComponentKind.TimeDate,
        DesktopComponentKind.Greeting,
        DesktopComponentKind.Weather,
        DesktopComponentKind.Performance,
        DesktopComponentKind.Todo,
        DesktopComponentKind.QuickLaunch
    ];

    private readonly CityLookupService _citySearchService;
    private readonly Action<string, bool> _applyDisplaySettings;
    private readonly Action<CitySearchResult> _applyCity;
    private readonly Action<DesktopExperienceState> _applyDesktopExperience;
    private readonly Action<DesktopExperienceState> _applyLayoutPreset;
    private readonly Func<IReadOnlyList<WidgetWindowBase>> _widgetProvider;
    private readonly Action<WidgetWindowBase, bool> _applyWidgetLock;
    private readonly DesktopExperienceState _desktopExperience;
    private readonly DockState _dockState;
    private readonly Action _applyDock;
    private readonly TaskbarState _taskbarState;
    private readonly Action _applyTaskbar;
    private readonly Func<(TaskbarMode ActiveMode, string Problem)> _taskbarStatus;
    private readonly SettingsMaintenance _maintenance;
    private readonly TopBarState? _topBarState;
    private readonly Action? _applyTopBar;
    private readonly Func<string, bool>? _probeTopBarSearchHotkey;
    private DesktopComponentSettingsEntry? _selectedComponentEntry;
    private CancellationTokenSource? _searchCancellation;
    private bool _loadingExperience;
    private bool _loadingComponentEditor;
    // Settings apply as soon as they change; typed values wait for a short pause first.
    private readonly DispatcherTimer _componentApplyTimer =
        new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _displaySettingsTimer =
        new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _loadingDock;
    private bool _loadingTaskbar;
    private bool _loadingDesktopMode;
    private bool _loadingStartup;
    private bool _loadingCleanDesktop;
    private int _cleanDesktopRefreshVersion;
    private bool _loadingFileBoxAutomation;
    private bool _loadingStyle;
    private bool _loadingTopBar;
    private bool _loadingTopBarLook;
    private bool _loadingSearchAppearance;
    private bool _topBarApplyQueued;
    private bool _presetPreviewQueued;
    private bool _isClosed;
    private DesktopLayoutPreset _previewPreset;

    public SettingsWindow(
        CityLookupService citySearchService,
        InformationWidgetState state,
        Action<string, bool> applyDisplaySettings,
        Action<CitySearchResult> applyCity)
        : this(
            citySearchService,
            state,
            DesktopExperienceRules.CreateDefault(),
            applyDisplaySettings,
            applyCity,
            _ => { })
    {
    }

    public SettingsWindow(
        CityLookupService citySearchService,
        InformationWidgetState state,
        DesktopExperienceState desktopExperience,
        Action<string, bool> applyDisplaySettings,
        Action<CitySearchResult> applyCity,
        Action<DesktopExperienceState> applyDesktopExperience,
        Action<DesktopExperienceState>? applyLayoutPreset = null,
        Func<IReadOnlyList<WidgetWindowBase>>? widgetProvider = null,
        Action<WidgetWindowBase, bool>? applyWidgetLock = null,
        DockState? dockState = null,
        Action? applyDock = null,
        TaskbarState? taskbarState = null,
        Action? applyTaskbar = null,
        Func<(TaskbarMode ActiveMode, string Problem)>? taskbarStatus = null,
        SettingsMaintenance? maintenance = null,
        TopBarState? topBarState = null,
        Action? applyTopBar = null,
        Func<string, bool>? probeTopBarSearchHotkey = null)
    {
        AccessibilityThemeManager.EnsureInitialized();
        _citySearchService = citySearchService;
        _applyDisplaySettings = applyDisplaySettings;
        _applyCity = applyCity;
        _applyDesktopExperience = applyDesktopExperience;
        _applyLayoutPreset = applyLayoutPreset ?? applyDesktopExperience;
        _widgetProvider = widgetProvider ?? (() => []);
        _applyWidgetLock = applyWidgetLock ?? ((_, _) => { });
        _desktopExperience = desktopExperience;
        DesktopExperienceRules.Normalize(_desktopExperience);
        _dockState = dockState ?? new DockState();
        _applyDock = applyDock ?? (() => { });
        _taskbarState = taskbarState ?? new TaskbarState();
        _applyTaskbar = applyTaskbar ?? (() => { });
        _taskbarStatus = taskbarStatus ?? (() => (TaskbarMode.SystemDefault, string.Empty));
        _maintenance = maintenance ?? new SettingsMaintenance();
        _topBarState = topBarState;
        _applyTopBar = applyTopBar;
        _probeTopBarSearchHotkey = probeTopBarSearchHotkey;

        InitializeComponent();
        ApplyTheme();
        SourceInitialized += (_, _) =>
        {
            ApplyTheme();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(ThemeMessageHook);
        };
        AccessibilityThemeManager.HighContrastChanged += ReapplyTheme;
        AccessibilityThemeManager.DarkModeOverrideChanged += ReapplyTheme;
        Closed += (_, _) =>
        {
            AccessibilityThemeManager.HighContrastChanged -= ReapplyTheme;
            AccessibilityThemeManager.DarkModeOverrideChanged -= ReapplyTheme;
        };
        PaletteSwatches.Fill(GlassPaletteSwatches, palette =>
        {
            _maintenance.Style.Palette = palette.Key;
            _maintenance.Style.Glass = GlassMode.Light;
            _maintenance.ApplyGlass();
            LoadStyle();
        });
        PaletteSwatches.Fill(DockPaletteSwatches, palette =>
        {
            _dockState.FollowCardLook = false;
            _dockState.SurfaceColor = palette.Body;
            ApplyDockChange($"Dock 已改为{palette.Name}配色");
            LoadDockLook();
        });
        PaletteSwatches.Fill(ComponentPaletteSwatches, palette =>
        {
            SelectedComponentBackgroundColorEditor.Text = palette.Body;
            SelectedComponentTextColorEditor.Text = palette.Text;
            ScheduleComponentApply();
        });
        SelectedComponentFontPicker.ItemsSource = new[] { DefaultFontLabel }.Concat(InstalledFontCatalog.Names).ToList();
        _componentApplyTimer.Tick += (_, _) =>
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: false);
        };
        _displaySettingsTimer.Tick += (_, _) =>
        {
            _displaySettingsTimer.Stop();
            ApplyDisplaySettings();
        };

        GreetingNameEditor.Text = state.GreetingName;
        Use24HourClockCheckBox.IsChecked = state.Use24HourClock;
        UpdateCurrentCity(state.WeatherCity);
        LoadDesktopExperience();
        RefreshComponentList();
        LoadDockSettings();
        if (_topBarState is null)
        {
            // Tests and older call sites construct the window without a top bar; every
            // place that shows or changes it stays out of sight.
            TopBarSection.Visibility = Visibility.Collapsed;
            TopBarLookPanel.Visibility = Visibility.Collapsed;
            ScreenMapTopBarButton.Visibility = Visibility.Collapsed;
            SchemeTopBarRow.Visibility = Visibility.Collapsed;
        }
        else
        {
            LoadTopBarSettings();
        }
        LoadTaskbarSettings();
        LoadDesktopMode();
        LoadPrivacySettings();
        LoadUpdateSettings();
        RefreshCleanDesktopSettings();
        LoadFileBoxAutomation();
        LoadStyle();
        RefreshBackupList();
        ShowPage("Scheme");
        Closing += (_, _) => CommitPendingChanges();
        Closed += (_, _) => { _isClosed = true; CancelSearch(); };
        // The index reads the finished pages once, after the window has shown.
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _ = SearchIndex);
        Deactivated += (_, _) => SettingsSearchPopup.IsOpen = false;
        LocationChanged += (_, _) => SettingsSearchPopup.IsOpen = false;
        SystemParameters.StaticPropertyChanged += SystemParameters_Changed;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= SystemParameters_Changed;
        if (_maintenance.Updater is { } updater)
        {
            updater.Changed += RefreshUpdateView;
            Closed += (_, _) => updater.Changed -= RefreshUpdateView;
        }
    }

    /// <summary>Whether the window shows the Mica material instead of the solid page colour.</summary>
    internal bool IsBackdropActive { get; private set; }

    /// <summary>Whether the window uses its dark palette, following the Windows app mode.</summary>
    internal bool IsDarkTheme { get; private set; }

    private const int WmSettingChange = 0x001A;

    private void ReapplyTheme(object? sender, EventArgs e) => ApplyTheme();

    /// <summary>
    /// Light or dark with the Windows app mode, translucent over Mica and solid without
    /// it, and system colours in high contrast. The palette lives in this window's own
    /// resources, so the cards and other dialogs keep their colours.
    /// </summary>
    private void ApplyTheme()
    {
        IsDarkTheme = AccessibilityThemeManager.IsDarkAppMode && !AccessibilityThemeManager.IsHighContrastEnabled;
        IsBackdropActive = SettingsBackdrop.Apply(this, IsDarkTheme);
        SettingsPalette.Apply(
            Resources,
            AccessibilityThemeManager.IsHighContrastEnabled,
            IsDarkTheme,
            translucent: IsBackdropActive,
            includeShared: true);
    }

    // Switching the app mode or the transparency effects arrives as ImmersiveColorSet.
    private nint ThemeMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmSettingChange
            && lParam != 0
            && System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            Dispatcher.BeginInvoke(ApplyTheme);
        }
        return 0;
    }

    private void SystemParameters_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.WorkArea))
        {
            Dispatcher.InvokeAsync(() => UpdatePresetPreview(GetSelectedPreset()));
        }
    }

    private void LoadDesktopExperience()
    {
        _loadingExperience = true;
        try
        {
            QuietInformationPresetRadio.IsChecked =
                _desktopExperience.ActivePreset == DesktopLayoutPreset.QuietInformation;
            GlacierWorkbenchPresetRadio.IsChecked =
                _desktopExperience.ActivePreset == DesktopLayoutPreset.GlacierWorkbench;
            TransparentApplePresetRadio.IsChecked =
                _desktopExperience.ActivePreset == DesktopLayoutPreset.TransparentApple;
            CenterClockPresetRadio.IsChecked =
                _desktopExperience.ActivePreset == DesktopLayoutPreset.CenterClock;
            UpdatePresetPreview(_desktopExperience.ActivePreset);
        }
        finally
        {
            _loadingExperience = false;
        }
    }

    private void DesktopComponentList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (DesktopComponentList?.SelectedItem is not DesktopComponentSettingsEntry entry ||
            SelectedComponentVisibleCheckBox is null)
        {
            return;
        }

        // A change typed for the previous card is saved before its editor is replaced.
        if (_componentApplyTimer.IsEnabled)
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: false);
        }
        _selectedComponentEntry = entry;
        if (entry.Kind is { } kind)
        {
            SelectedWidgetAppearanceHost.Content = null;
            FileBoxCollectPanel.Visibility = Visibility.Collapsed;
            FileBoxDesktopHintPanel.Visibility = Visibility.Collapsed;
            SelectedWidgetEditorPanel.Visibility = Visibility.Collapsed;
            BuiltInComponentEditorPanel.Visibility = Visibility.Visible;
            LoadSelectedComponentEditor(kind, entry.Window);
            // A card with its own colours opens its fold, so what was set is in view.
            ComponentOwnLookFold.IsExpanded = UpdateOwnLookSummary(kind);
            // 待办卡片自身走内置组件分支（DesktopExperience 有 Todo 组件项），
            // 周历设置面板必须在这里点亮；其余内置组件保持隐藏。
            if (kind == DesktopComponentKind.Todo && entry.Window is TodoWidgetWindow todoWindow)
            {
                LoadTodoSettings(todoWindow);
            }
            else
            {
                TodoSettingsPanel.Visibility = Visibility.Collapsed;
            }
            DisplayContentSettingsCard.Visibility = kind is DesktopComponentKind.TimeDate or
                DesktopComponentKind.Greeting
                ? Visibility.Visible
                : Visibility.Collapsed;
            _loadingDivider = true;
            var clock = _desktopExperience.GetComponent(DesktopComponentKind.TimeDate);
            ClockDividerCheckBox.IsChecked = clock.ShowDivider;
            ClockDividerThicknessSlider.Value = clock.DividerThickness;
            ClockDividerThicknessText.Text = $"{clock.DividerThickness:0.#} px";
            _loadingDivider = false;
            WeatherContentSettingsCard.Visibility = kind == DesktopComponentKind.Weather
                ? Visibility.Visible
                : Visibility.Collapsed;
            QuickPlacesSettingsCard.Visibility = kind == DesktopComponentKind.QuickLaunch
                ? Visibility.Visible
                : Visibility.Collapsed;
            // Quick launch shows only icon glyphs, and only the weather and performance
            // cards lay out by density, so the other components hide options that do nothing.
            SelectedComponentFontRow.Visibility = kind == DesktopComponentKind.QuickLaunch
                ? Visibility.Collapsed
                : Visibility.Visible;
            SelectedComponentDensityRow.Visibility = kind is DesktopComponentKind.Weather or
                DesktopComponentKind.Performance
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (kind == DesktopComponentKind.QuickLaunch)
            {
                _quickPlaces = _maintenance.QuickPlaces()
                    .Select(item => new QuickPlaceState { Icon = item.Icon, Name = item.Name, Target = item.Target })
                    .ToList();
                ShowQuickPlaces(0);
            }
            ComponentDetailsScrollViewer.ScrollToTop();
            return;
        }

        if (entry.Window is null)
        {
            return;
        }

        var panel = new WidgetAppearancePanel { SharedStyle = _maintenance.Style };
        panel.Attach(entry.Window);
        LoadFileBoxViewMode(entry.Window as FileBoxWindow);
        LoadFileBoxCollect(entry.Window as FileBoxWindow);
        LoadTodoSettings(entry.Window as TodoWidgetWindow);
        SelectedWidgetAppearanceHost.Content = panel;
        SelectedWidgetHeadingText.Text = entry.Name;
        SelectedWidgetLockedCheckBox.IsChecked = entry.Window.IsWidgetLocked;
        BuiltInComponentEditorPanel.Visibility = Visibility.Collapsed;
        SelectedWidgetEditorPanel.Visibility = Visibility.Visible;
        DisplayContentSettingsCard.Visibility = Visibility.Collapsed;
        WeatherContentSettingsCard.Visibility = Visibility.Collapsed;
        QuickPlacesSettingsCard.Visibility = Visibility.Collapsed;
        ComponentDetailsScrollViewer.ScrollToTop();
    }

    private void LoadSelectedComponentEditor(
        DesktopComponentKind kind,
        WidgetWindowBase? targetWindow)
    {
        _loadingComponentEditor = true;
        try
        {
            var component = _desktopExperience.GetComponent(kind);
            SelectedComponentVisibleCheckBox.IsChecked = component.IsVisible;
            SelectedComponentLockedCheckBox.IsChecked =
                targetWindow?.IsWidgetLocked ?? component.IsLocked;
            SelectedComponentFontScaleSlider.Value = component.FontScale;
            SelectedComponentFontPicker.SelectedItem = component.FontFamily is { } font
                ? InstalledFontCatalog.Resolve(font)
                : DefaultFontLabel;
            SelectedComponentTextColorEditor.Text = component.Appearance.TextColor;
            SelectedComponentBackgroundColorEditor.Text =
                component.Appearance.BackgroundColor;
            SelectedComponentOpacitySlider.Value = component.Appearance.BackgroundOpacity;
            // 标题栏设置只在有标题栏的组件（待办卡片）上显示；裸文字信息卡没有
            // 这一层，隐藏不等于不可调。
            SelectedComponentHeaderRows.Visibility = kind == DesktopComponentKind.Todo
                ? Visibility.Visible
                : Visibility.Collapsed;
            SelectedComponentHeaderColorEditor.Text = component.Appearance.HeaderColor;
            SelectedComponentHeaderOpacitySlider.Value = component.Appearance.HeaderOpacity;
            SelectedComponentCornerRadiusSlider.Value = component.CornerRadius;
            SelectedComponentWidthEditor.Text = $"{component.Placement.Width:0.#}";
            SelectedComponentHeightEditor.Text = $"{component.Placement.Height:0.#}";
            SelectedComponentDensitySelector.SelectedIndex = component.Density switch
            {
                DesktopInformationDensity.Comfortable => 0,
                DesktopInformationDensity.Compact => 2,
                _ => 1
            };
            // The segment shows which material this card wears; custom colours read as
            // the nearer of light and dark glass.
            var inferredLook = InferLook(component.Appearance);
            ComponentLookLightRadio.IsChecked = inferredLook == GlassMode.Light;
            ComponentLookDarkRadio.IsChecked = inferredLook == GlassMode.Dark;
            ComponentLookClearRadio.IsChecked = inferredLook == GlassMode.Clear;
            RefreshComponentColorChips();
            UpdateComponentStyleValueText();
            SelectedComponentHeadingText.Text =
                _selectedComponentEntry?.Name ?? DesktopExperienceRules.GetComponentName(kind);
        }
        finally
        {
            _loadingComponentEditor = false;
        }
    }

    private void ScheduleComponentApply()
    {
        if (_loadingComponentEditor || _selectedComponentEntry?.Kind is null)
        {
            return;
        }
        _componentApplyTimer.Stop();
        _componentApplyTimer.Start();
    }

    /// <summary>Applies any change still waiting for its typing pause.</summary>
    public void CommitPendingChanges()
    {
        if (_componentApplyTimer.IsEnabled)
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: false);
        }
        if (_displaySettingsTimer.IsEnabled)
        {
            _displaySettingsTimer.Stop();
            ApplyDisplaySettings();
        }
    }

    private void ComponentEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        // The swatches follow the boxes so a typed colour shows before it applies.
        RefreshComponentColorChips();
        ScheduleComponentApply();
    }

    private const string DefaultFontLabel = "默认（界面字体）";

    private void ComponentFont_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingComponentEditor && _selectedComponentEntry?.Kind is not null)
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: false);
        }
    }

    // One card takes light glass, dark glass or no backing without changing the others.
    // The segmented radios also show the current material while the editor loads.
    private void ComponentLook_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingComponentEditor
            || sender is not FrameworkElement { Tag: string tag }
            || !Enum.TryParse<GlassMode>(tag, out var mode))
        {
            return;
        }

        var look = CardLook(mode, _maintenance.Style, SelectedComponentOpacitySlider.Value);
        SelectedComponentBackgroundColorEditor.Text = look.BackgroundColor;
        SelectedComponentTextColorEditor.Text = look.TextColor;
        SelectedComponentOpacitySlider.Value = look.BackgroundOpacity;
        _componentApplyTimer.Stop();
        ApplySelectedComponent(refreshList: false);
    }

    /// <summary>
    /// The colours of one card in a material, keeping the card's opacity when it has a
    /// visible body and falling back to the shared glass density when it had none.
    /// </summary>
    internal static GlassAppearance CardLook(GlassMode mode, DesktopStyleState shared, double currentOpacity)
    {
        var density = mode == GlassMode.Clear || currentOpacity >= 0.05d ? currentOpacity : shared.GlassDensity;
        return DesktopStyleRules.Appearance(new DesktopStyleState
        {
            Glass = mode,
            Palette = shared.Palette,
            GlassDensity = density,
            TextInk = TextInk.Auto
        });
    }

    /// <summary>
    /// Which material a card currently wears, for the segment control's checked state;
    /// a custom colour reads as whichever of light or dark glass it is closest to.
    /// </summary>
    internal static GlassMode InferLook(WidgetAppearanceState look)
    {
        if (string.IsNullOrWhiteSpace(look.BackgroundColor) || look.BackgroundOpacity < 0.05d)
        {
            return GlassMode.Clear;
        }

        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                .ConvertFromString(look.BackgroundColor)!;
            var luminance = (0.2126d * color.R + 0.7152d * color.G + 0.0722d * color.B) / 255d;
            return luminance > 0.5d ? GlassMode.Light : GlassMode.Dark;
        }
        catch (FormatException)
        {
            return GlassMode.Light;
        }
    }

    // A colour dialog for one card's own colour field; cancel keeps the typed value.
    private void ComponentColorPicker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string target })
        {
            return;
        }

        var editor = target switch
        {
            "Background" => SelectedComponentBackgroundColorEditor,
            "Header" => SelectedComponentHeaderColorEditor,
            _ => SelectedComponentTextColorEditor
        };
        var current = System.Windows.Media.Colors.White;
        try
        {
            if (!string.IsNullOrWhiteSpace(editor.Text))
            {
                current = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                    .ConvertFromString(editor.Text)!;
            }
        }
        catch (FormatException)
        {
        }

        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            FullOpen = true,
            AnyColor = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        editor.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }

    private void RefreshComponentColorChips()
    {
        if (SelectedComponentTextColorChip is null)
        {
            return;
        }

        SetColorChip(SelectedComponentTextColorChip, SelectedComponentTextColorEditor.Text);
        SetColorChip(SelectedComponentBackgroundChip, SelectedComponentBackgroundColorEditor.Text);
        SetColorChip(SelectedComponentHeaderChip, SelectedComponentHeaderColorEditor.Text);
    }

    private static void SetColorChip(Border chip, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            chip.Background = null;
            return;
        }

        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value)!;
            var brush = new System.Windows.Media.SolidColorBrush(color);
            brush.Freeze();
            chip.Background = brush;
        }
        catch (FormatException)
        {
            // Keep the last valid swatch while a colour is still being typed.
        }
    }

    private void ComponentDensity_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingComponentEditor && _selectedComponentEntry?.Kind is not null)
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: false);
        }
    }

    // Showing or locking a card changes its label in the list, so the list is refreshed.
    private void ComponentToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loadingComponentEditor && _selectedComponentEntry?.Kind is not null)
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: true);
        }
    }

    private void DisplaySettings_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        _displaySettingsTimer.Stop();
        if (sender is CheckBox)
        {
            ApplyDisplaySettings();
        }
        else
        {
            _displaySettingsTimer.Start();
        }
    }

    // The quick-launch icons being edited. An added icon stays here until it has a
    // location; the saved list only holds icons that open something.
    private List<QuickPlaceState> _quickPlaces = [];
    private bool _loadingQuickPlace;

    private sealed record QuickPlaceRow(string Glyph, string Name, string Target);

    private void ShowQuickPlaces(int select)
    {
        QuickPlaceList.ItemsSource = _quickPlaces
            .Select(item => new QuickPlaceRow(QuickPlaceRules.GetIcon(item.Icon).Glyph, item.Name, DescribeTarget(item.Target)))
            .ToList();
        QuickPlaceIconPicker.ItemsSource ??= QuickPlaceRules.Icons;
        QuickPlaceList.SelectedIndex = _quickPlaces.Count == 0 ? -1 : Math.Clamp(select, 0, _quickPlaces.Count - 1);
        LoadQuickPlaceEditor();
    }

    private static string DescribeTarget(string target) => target switch
    {
        "" => "（还没有选择位置）",
        _ when target.StartsWith(QuickPlaceRules.KnownPrefix, StringComparison.OrdinalIgnoreCase) =>
            $"系统{QuickPlaceRules.GetIcon(target[QuickPlaceRules.KnownPrefix.Length..]).Name}文件夹",
        _ when target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) => "系统位置",
        _ => target
    };

    private QuickPlaceState? SelectedQuickPlace =>
        QuickPlaceList.SelectedIndex is var index && index >= 0 && index < _quickPlaces.Count ? _quickPlaces[index] : null;

    private void LoadQuickPlaceEditor()
    {
        _loadingQuickPlace = true;
        var item = SelectedQuickPlace;
        QuickPlaceEditor.IsEnabled = item is not null;
        QuickPlaceIconPicker.SelectedItem = item is null ? null : QuickPlaceRules.GetIcon(item.Icon);
        QuickPlaceNameEditor.Text = item?.Name ?? string.Empty;
        QuickPlaceTargetEditor.Text = item is null || item.Target.StartsWith(QuickPlaceRules.KnownPrefix, StringComparison.OrdinalIgnoreCase)
            || item.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            ? DescribeTarget(item?.Target ?? string.Empty)
            : item.Target;
        _loadingQuickPlace = false;
    }

    private void SaveQuickPlaces()
    {
        var index = QuickPlaceList.SelectedIndex;
        _maintenance.SetQuickPlaces(_quickPlaces
            .Select(item => new QuickPlaceState { Icon = item.Icon, Name = item.Name, Target = item.Target })
            .ToList());
        ShowQuickPlaces(index);
        ComponentSettingsStatusText.Text = "快捷入口已更新";
    }

    private void QuickPlaceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingQuickPlace)
        {
            LoadQuickPlaceEditor();
        }
    }

    private void QuickPlaceIcon_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingQuickPlace || SelectedQuickPlace is not { } item || QuickPlaceIconPicker.SelectedItem is not QuickPlaceIcon icon)
        {
            return;
        }
        item.Icon = icon.Key;
        SaveQuickPlaces();
    }

    private void QuickPlaceName_Commit(object sender, RoutedEventArgs e)
    {
        if (_loadingQuickPlace || SelectedQuickPlace is not { } item || item.Name == QuickPlaceNameEditor.Text.Trim())
        {
            return;
        }
        item.Name = QuickPlaceNameEditor.Text.Trim();
        SaveQuickPlaces();
    }

    // A typed path replaces the location; the friendly text shown for a system place is
    // left alone.
    private void QuickPlaceTarget_Commit(object sender, RoutedEventArgs e)
    {
        var text = QuickPlaceTargetEditor.Text.Trim().Trim('"');
        if (_loadingQuickPlace || SelectedQuickPlace is not { } item || text.Length == 0
            || text == item.Target || text == DescribeTarget(item.Target))
        {
            return;
        }
        item.Target = text;
        SaveQuickPlaces();
    }

    private void QuickPlaceEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }
        if (ReferenceEquals(sender, QuickPlaceNameEditor))
        {
            QuickPlaceName_Commit(sender, e);
        }
        else
        {
            QuickPlaceTarget_Commit(sender, e);
        }
        e.Handled = true;
    }

    private void QuickPlaceChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQuickPlace is not { } item)
        {
            return;
        }
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择快捷入口打开的文件夹" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        SetQuickPlaceTarget(item, dialog.FolderName, Path.GetFileName(dialog.FolderName.TrimEnd(Path.DirectorySeparatorChar)));
    }

    private void QuickPlaceChooseFile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQuickPlace is not { } item)
        {
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择快捷入口打开的程序或文件",
            Filter = "程序 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*",
            DereferenceLinks = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        var name = Path.GetExtension(dialog.FileName).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            ? BingLan.App.Dock.DockAppResolver.GetFileDescription(dialog.FileName) ?? Path.GetFileNameWithoutExtension(dialog.FileName)
            : Path.GetFileNameWithoutExtension(dialog.FileName);
        SetQuickPlaceTarget(item, dialog.FileName, name);
    }

    // A new icon takes the chosen item's name; a named one keeps the name it was given.
    private void SetQuickPlaceTarget(QuickPlaceState item, string target, string name)
    {
        if (item.Target.Length == 0 && name.Length > 0)
        {
            item.Name = name[..Math.Min(name.Length, QuickPlaceRules.MaximumNameLength)];
        }
        item.Target = target;
        SaveQuickPlaces();
    }

    private void QuickPlaceAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_quickPlaces.Count >= QuickPlaceRules.MaximumItems)
        {
            ComponentSettingsStatusText.Text = $"快捷入口最多 {QuickPlaceRules.MaximumItems} 个";
            return;
        }
        _quickPlaces.Add(new QuickPlaceState { Icon = QuickPlaceRules.FolderIcon, Name = "新入口", Target = string.Empty });
        ShowQuickPlaces(_quickPlaces.Count - 1);
        ComponentSettingsStatusText.Text = "选择图标，再用“选文件夹…”或“选程序或文件…”指定位置";
    }

    private void QuickPlaceRemove_Click(object sender, RoutedEventArgs e)
    {
        if (QuickPlaceList.SelectedIndex is var index and >= 0 && index < _quickPlaces.Count)
        {
            _quickPlaces.RemoveAt(index);
            SaveQuickPlaces();
            ShowQuickPlaces(index);
        }
    }

    private void QuickPlaceUp_Click(object sender, RoutedEventArgs e) => MoveQuickPlace(-1);

    private void QuickPlaceDown_Click(object sender, RoutedEventArgs e) => MoveQuickPlace(1);

    private void MoveQuickPlace(int offset)
    {
        var index = QuickPlaceList.SelectedIndex;
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _quickPlaces.Count)
        {
            return;
        }
        (_quickPlaces[index], _quickPlaces[target]) = (_quickPlaces[target], _quickPlaces[index]);
        SaveQuickPlaces();
        ShowQuickPlaces(target);
    }

    private void QuickPlaceReset_Click(object sender, RoutedEventArgs e)
    {
        _quickPlaces = QuickPlaceRules.CreateDefaults();
        SaveQuickPlaces();
        ShowQuickPlaces(0);
    }

    private bool _loadingDivider;

    private void ClockDivider_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingDivider || ClockDividerThicknessText is null)
        {
            return;
        }
        var clock = _desktopExperience.GetComponent(DesktopComponentKind.TimeDate);
        clock.ShowDivider = ClockDividerCheckBox.IsChecked == true;
        clock.DividerThickness = ClockDividerThicknessSlider.Value;
        ClockDividerThicknessText.Text = $"{clock.DividerThickness:0.#} px";
        _applyDesktopExperience(_desktopExperience);
        DisplaySettingsStatusText.Text = "已保存";
    }

    private void ApplyDisplaySettings()
    {
        _applyDisplaySettings(
            GreetingNameEditor.Text.Trim(),
            Use24HourClockCheckBox.IsChecked == true);
        DisplaySettingsStatusText.Text = "已保存";
    }

    private void ComponentStyleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loadingComponentEditor &&
            SelectedComponentFontScaleSlider is not null &&
            SelectedComponentFontScaleValueText is not null &&
            SelectedComponentOpacitySlider is not null &&
            SelectedComponentOpacityValueText is not null &&
            SelectedComponentHeaderOpacitySlider is not null &&
            SelectedComponentHeaderOpacityValueText is not null &&
            SelectedComponentCornerRadiusSlider is not null &&
            SelectedComponentCornerRadiusValueText is not null)
        {
            UpdateComponentStyleValueText();
            ScheduleComponentApply();
        }
    }

    private void UpdateComponentStyleValueText()
    {
        SelectedComponentFontScaleValueText.Text =
            $"{SelectedComponentFontScaleSlider.Value * 100:0}%";
        SelectedComponentOpacityValueText.Text =
            $"{SelectedComponentOpacitySlider.Value * 100:0}%";
        SelectedComponentHeaderOpacityValueText.Text =
            $"{SelectedComponentHeaderOpacitySlider.Value * 100:0}%";
        SelectedComponentCornerRadiusValueText.Text =
            $"{SelectedComponentCornerRadiusSlider.Value:0}px";
    }

    private void QuickTextColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            SelectedComponentTextColorEditor.Text = color;
        }
    }

    private void QuickBackgroundColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            SelectedComponentBackgroundColorEditor.Text = color;
        }
    }

    private void QuickHeaderColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            SelectedComponentHeaderColorEditor.Text = color;
        }
    }

    private void ApplySelectedComponent(bool refreshList)
    {
        if (_selectedComponentEntry?.Kind is not { } kind)
        {
            return;
        }

        var component = _desktopExperience.GetComponent(kind);
        component.IsVisible = SelectedComponentVisibleCheckBox.IsChecked == true;
        component.FontScale = SelectedComponentFontScaleSlider.Value;
        component.FontFamily = SelectedComponentFontPicker.SelectedItem is string font && font != DefaultFontLabel
            ? font
            : null;
        component.Appearance.TextColor = WidgetAppearanceRules.CoerceTextColor(
            SelectedComponentTextColorEditor.Text);
        component.Appearance.BackgroundColor = WidgetAppearanceRules.CoerceBackgroundColor(
            SelectedComponentBackgroundColorEditor.Text);
        component.Appearance.BackgroundOpacity =
            WidgetAppearanceRules.CoerceBackgroundOpacity(
                SelectedComponentOpacitySlider.Value);
        component.Appearance.HeaderColor = WidgetAppearanceRules.CoerceHeaderColor(
            SelectedComponentHeaderColorEditor.Text);
        component.Appearance.HeaderOpacity =
            WidgetAppearanceRules.CoerceHeaderOpacity(
                SelectedComponentHeaderOpacitySlider.Value);
        component.CornerRadius = SelectedComponentCornerRadiusSlider.Value;
        var (minimumWidth, minimumHeight) = GetMinimumComponentSize(kind);
        component.Placement.Width = ParseSize(
            SelectedComponentWidthEditor.Text,
            component.Placement.Width,
            minimumWidth,
            1600d);
        component.Placement.Height = ParseSize(
            SelectedComponentHeightEditor.Text,
            component.Placement.Height,
            minimumHeight,
            1200d);
        component.Density = SelectedComponentDensitySelector.SelectedIndex switch
        {
            0 => DesktopInformationDensity.Comfortable,
            2 => DesktopInformationDensity.Compact,
            _ => DesktopInformationDensity.Standard
        };
        if (_selectedComponentEntry.Window is { } targetWindow)
        {
            // 先应用组件状态、再应用锁定：ApplyWidgetLock 会触发窗口状态回写，
            // 把窗口当前尺寸写回组件 Placement。放在后面时窗口已拿到上面写入的
            // 新尺寸，回写与设置一致；放在前面则会用旧尺寸覆盖，卡片尺寸设置
            // 永远不生效。
            _applyDesktopExperience(_desktopExperience);
            _applyWidgetLock(
                targetWindow,
                SelectedComponentLockedCheckBox.IsChecked == true);
        }
        else
        {
            component.IsLocked = SelectedComponentLockedCheckBox.IsChecked == true;
            _applyDesktopExperience(_desktopExperience);
        }
        ComponentSettingsStatusText.Text = $"已保存“{_selectedComponentEntry.Name}”设置";
        UpdateOwnLookSummary(kind);
        RefreshAppearanceOverrides();
        if (refreshList)
        {
            RefreshComponentList(_selectedComponentEntry.Window, kind);
        }
    }

    /// <summary>
    /// Says in the fold header whether the card follows the shared look, and offers the
    /// way back when it does not. Returns whether the card has its own colours.
    /// </summary>
    private bool UpdateOwnLookSummary(DesktopComponentKind kind)
    {
        var own = HasOwnLook(kind);
        ComponentOwnLookSummaryText.Text = own
            ? "已独立设置颜色，不跟随全局外观"
            : "跟随全局外观，展开后可为这张卡片独立设置";
        ComponentFollowSharedLookRow.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
        return own;
    }

    private bool HasOwnLook(DesktopComponentKind kind) => !DesktopStyleRules.FollowsSharedLook(
        _desktopExperience.GetComponent(kind),
        _maintenance.Style,
        QuickPlacesWindow.ClearBarOpacity);

    // Gives one card the colours of the shared material again, as applying the global
    // material would.
    private void FollowSharedLook(DesktopComponentKind kind)
    {
        var component = _desktopExperience.GetComponent(kind);
        var look = DesktopStyleRules.ComponentLook(kind, _maintenance.Style, QuickPlacesWindow.ClearBarOpacity);
        component.Appearance.BackgroundColor = look.BackgroundColor;
        component.Appearance.BackgroundOpacity = look.BackgroundOpacity;
        component.Appearance.TextColor = look.TextColor;
        _applyDesktopExperience(_desktopExperience);
        if (_selectedComponentEntry?.Kind == kind)
        {
            _componentApplyTimer.Stop();
            LoadSelectedComponentEditor(kind, _selectedComponentEntry.Window);
            UpdateOwnLookSummary(kind);
        }
        ComponentSettingsStatusText.Text = $"“{DesktopExperienceRules.GetComponentName(kind)}”已恢复跟随全局外观";
        RefreshAppearanceOverrides();
    }

    private void ComponentFollowSharedLook_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedComponentEntry?.Kind is { } kind)
        {
            FollowSharedLook(kind);
        }
    }

    public void ShowWidgetSettings(WidgetWindowBase window)
    {
        SelectNavigationPage("Components");
        ShowPage("Components");
        RefreshComponentList(window);
    }

    /// <summary>
    /// Opens the components page with one kind selected, for deep links from surfaces
    /// that show a card's data without the card itself, like the top bar.
    /// </summary>
    public void ShowComponentSettings(DesktopComponentKind kind)
    {
        SelectNavigationPage("Components");
        ShowPage("Components");
        RefreshComponentList(null, kind);
    }

    private bool _loadingCollect;

    private bool _loadingViewMode;

    private bool _loadingTodoWeekCount;

    private void LoadTodoSettings(TodoWidgetWindow? todo)
    {
        TodoSettingsPanel.Visibility = todo is null ? Visibility.Collapsed : Visibility.Visible;
        if (todo is null)
        {
            return;
        }

        _loadingTodoWeekCount = true;
        try
        {
            var weeks = Math.Clamp(todo.State.WeeklyWeekCount, 1, 5);
            TodoWeekCountPicker.SelectedIndex = weeks - 1;
        }
        finally
        {
            _loadingTodoWeekCount = false;
        }
    }

    private void TodoWeekCount_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTodoWeekCount
            || TodoWeekCountPicker.SelectedIndex < 0
            || _selectedComponentEntry?.Window is not TodoWidgetWindow todo)
        {
            return;
        }

        todo.SetWeeklyWeekCount(TodoWeekCountPicker.SelectedIndex + 1);
        ComponentSettingsStatusText.Text = $"已应用“{todo.State.Title}”的周历周数";
    }

    private void LoadFileBoxViewMode(FileBoxWindow? box)
    {
        FileBoxViewModePanel.Visibility = box is null ? Visibility.Collapsed : Visibility.Visible;
        if (box is null)
        {
            return;
        }

        _loadingViewMode = true;
        try
        {
            var list = box.State.ViewMode == FileBoxViewMode.List;
            FileBoxTilesRadio.IsChecked = !list;
            FileBoxListRadio.IsChecked = list;
        }
        finally
        {
            _loadingViewMode = false;
        }
    }

    private void FileBoxViewMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingViewMode || _selectedComponentEntry?.Window is not FileBoxWindow box)
        {
            return;
        }

        box.SetViewMode(FileBoxListRadio.IsChecked == true ? FileBoxViewMode.List : FileBoxViewMode.Tiles);
        ComponentSettingsStatusText.Text = $"已保存“{box.State.Title}”的显示方式";
    }

    private void LoadFileBoxCollect(FileBoxWindow? box)
    {
        FileBoxCollectPanel.Visibility = box is null ? Visibility.Collapsed : Visibility.Visible;
        FileBoxDesktopHintPanel.Visibility = box is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshFileBoxCleanDesktopHint();
        if (box is null)
        {
            return;
        }

        _loadingCollect = true;
        try
        {
            FileBoxCollectTypes.Children.Clear();
            var folders = new CheckBox
            {
                Content = "文件夹",
                Tag = "folders",
                Margin = new Thickness(0, 4, 18, 4),
                IsChecked = box.State.CollectFolders
            };
            folders.Click += FileBoxCollect_Changed;
            FileBoxCollectTypes.Children.Add(folders);
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in FileMappingService.CollectTypes)
            {
                var check = new CheckBox
                {
                    Content = type.Name,
                    Tag = type.Key,
                    Margin = new Thickness(0, 4, 18, 4),
                    ToolTip = string.Join(" ", type.Extensions),
                    IsChecked = type.Extensions.All(box.State.CollectExtensions.Contains)
                };
                check.Click += FileBoxCollect_Changed;
                FileBoxCollectTypes.Children.Add(check);
                if (check.IsChecked == true)
                {
                    known.UnionWith(type.Extensions);
                }
            }
            FileBoxExtraExtensionsEditor.Text = string.Join(
                " ",
                box.State.CollectExtensions.Where(extension => !known.Contains(extension)));
        }
        finally
        {
            _loadingCollect = false;
        }
    }

    private void FileBoxCollect_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingCollect || _selectedComponentEntry?.Window is not FileBoxWindow box)
        {
            return;
        }

        var checks = FileBoxCollectTypes.Children.OfType<CheckBox>().ToList();
        var extensions = FileMappingService.CollectTypes
            .Where(type => checks.Any(check => Equals(check.Tag, type.Key) && check.IsChecked == true))
            .SelectMany(type => type.Extensions)
            .Concat(FileMappingService.NormalizeExtensions([FileBoxExtraExtensionsEditor.Text]));
        var folders = checks.Any(check => Equals(check.Tag, "folders") && check.IsChecked == true);
        box.SetCollectRule(extensions, folders);
        ComponentSettingsStatusText.Text = $"已保存“{box.State.Title}”的收纳类型";
    }

    private void SelectedWidgetLockedCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedComponentEntry?.Window is not { } window)
        {
            return;
        }

        _applyWidgetLock(window, SelectedWidgetLockedCheckBox.IsChecked == true);
        RefreshComponentList(window);
    }


    private void AddTodo_Click(object sender, RoutedEventArgs e) => AddWidget(NewWidgetKind.Todo);

    private void AddNote_Click(object sender, RoutedEventArgs e) => AddWidget(NewWidgetKind.Note);

    private void AddFileBox_Click(object sender, RoutedEventArgs e) => AddWidget(NewWidgetKind.FileBox);

    // The new card is selected so it can be adjusted right away.
    private void AddWidget(NewWidgetKind kind)
    {
        if (_maintenance.AddWidget(kind) is { } window)
        {
            RefreshComponentList(window);
            ComponentSettingsStatusText.Text = $"已新建“{GetWidgetName(window)}”";
        }
    }

    private void ExitApp_Click(object sender, RoutedEventArgs e) => _maintenance.ExitApp();

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_maintenance.Uninstall() is { } error)
        {
            UninstallStatusText.Text = $"无法打开卸载程序：{error}。也可以在 Windows 设置 > 应用 > 已安装的应用中卸载。";
            UninstallStatusText.Visibility = Visibility.Visible;
        }
    }

    private void SettingsNavigationList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (SettingsNavigationList.SelectedItem is ListBoxItem item && item.Tag is string page)
        {
            ShowPage(page);
        }
    }

    /// <summary>
    /// The page and section a settings link opens. Page names from earlier versions
    /// ("Dock", "TopBar", "Taskbar", "Layout", "About" …) still lead to the settings
    /// they used to show.
    /// </summary>
    internal static (string Page, string? Section) ResolveSettingsLink(string link) => link switch
    {
        "Scheme" or "DesktopMode" or "Overall" => ("Scheme", null),
        "CleanDesktop" => ("Scheme", "CleanDesktop"),
        "Appearance" or "Cards" => ("Appearance", null),
        "Layout" or "Theme" => ("Appearance", "Layout"),
        "Components" => ("Components", null),
        "Bars" => ("Bars", null),
        "Dock" or "TopBar" or "Taskbar" => ("Bars", link),
        "General" or "About" or "Update" => ("General", null),
        "Privacy" or "Backup" => ("General", link),
        _ => (link, null)
    };

    private void ShowPage(string page)
    {
        if (SchemePage is null || AppearancePage is null || ComponentsPage is null || BarsPage is null ||
            GeneralPage is null)
        {
            return;
        }

        SettingsSearchPopup.IsOpen = false;
        SchemePage.Visibility = page == "Scheme" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = page == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        ComponentsPage.Visibility = page == "Components" ? Visibility.Visible : Visibility.Collapsed;
        BarsPage.Visibility = page == "Bars" ? Visibility.Visible : Visibility.Collapsed;
        GeneralPage.Visibility = page == "General" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "Appearance")
        {
            UpdatePresetPreview(GetSelectedPreset());
        }
        if (page == "General")
        {
            RefreshBackupList();
        }

        if (page == "Components")
        {
            RefreshComponentList(
                _selectedComponentEntry?.Window,
                _selectedComponentEntry?.Kind);
        }
    }

    internal void ShowSettingsPage(string page) => SelectNavigationPage(page);

    // ---------------------------------------------------------------- 设置搜索

    private List<SettingsSearchEntry>? _searchIndex;

    private (FrameworkElement Page, string Tag, string Title)[] SearchPages =>
    [
        (SchemePage, "Scheme", "桌面方案"),
        (AppearancePage, "Appearance", "外观样式"),
        (ComponentsPage, "Components", "桌面组件"),
        (BarsPage, "Bars", "顶栏底栏"),
        (GeneralPage, "General", "通用设置")
    ];

    /// <summary>Built once from the pages; see <see cref="SettingsSearch"/>.</summary>
    internal IReadOnlyList<SettingsSearchEntry> SearchIndex => _searchIndex ??= SettingsSearch.Build(
        SearchPages.Select(page => (page.Page, page.Title)),
        (Style)FindResource("SectionHeaderStyle"),
        (Style)FindResource("ChoiceCardStyle"));

    /// <summary>The results the search box shows for a query, best match first.</summary>
    internal IReadOnlyList<SettingsSearchEntry> SearchSettings(string query) =>
        SettingsSearch.Find(SearchIndex, query.Trim(), IsSearchEntryAvailable);

    // A row that is hidden right now (a feature this computer lacks, an option of
    // another mode) is left out; component rows need a matching card to select.
    private bool IsSearchEntryAvailable(SettingsSearchEntry entry) => entry.Scope is { } scope
        ? DesktopComponentList.Items.OfType<DesktopComponentSettingsEntry>().Any(item => MatchesSearchScope(item, scope))
        : SettingsSearch.IsShown(entry.Target, entry.Page);

    private static bool MatchesSearchScope(DesktopComponentSettingsEntry item, string scope) => scope switch
    {
        "Builtin" => item.Kind is not null,
        "Widget" => item.Kind is null,
        "FileBox" => item.Window is FileBoxWindow,
        "Todo" => item.Window is TodoWidgetWindow,
        _ => scope.StartsWith("Kind:", StringComparison.Ordinal) && item.Kind?.ToString() == scope["Kind:".Length..]
    };

    /// <summary>
    /// Goes to a setting: switches page, selects a card the setting belongs to, opens
    /// the folds around it, then scrolls it into the middle, tints it and focuses it.
    /// </summary>
    internal void OpenSearchResult(SettingsSearchEntry entry)
    {
        SettingsSearchPopup.IsOpen = false;
        var tag = SearchPages.First(page => ReferenceEquals(page.Page, entry.Page)).Tag;
        SelectNavigationPage(tag);
        if (entry.Scope is { } scope)
        {
            SelectSearchScope(scope, entry.Target);
        }
        foreach (var fold in entry.Folds)
        {
            fold.IsExpanded = true;
        }
        if (entry.Target is Expander target)
        {
            target.IsExpanded = true;
        }
        RevealElement(entry.Target, focus: true);
    }

    // The current card is kept when it shows the setting; otherwise the first matching
    // card that shows it is selected.
    private void SelectSearchScope(string scope, FrameworkElement target)
    {
        var candidates = DesktopComponentList.Items.OfType<DesktopComponentSettingsEntry>()
            .Where(item => MatchesSearchScope(item, scope))
            .ToList();
        if (DesktopComponentList.SelectedItem is DesktopComponentSettingsEntry current && candidates.Remove(current))
        {
            candidates.Insert(0, current);
        }
        foreach (var candidate in candidates)
        {
            if (!ReferenceEquals(DesktopComponentList.SelectedItem, candidate))
            {
                DesktopComponentList.SelectedItem = candidate;
            }
            if (SettingsSearch.IsShown(target, ComponentDetailsScrollViewer))
            {
                return;
            }
        }
    }

    private void UpdateSearchResults()
    {
        var query = SettingsSearchBox.Text.Trim();
        if (query.Length == 0)
        {
            SettingsSearchPopup.IsOpen = false;
            return;
        }

        var hits = SearchSettings(query);
        SettingsSearchResults.ItemsSource = hits;
        SettingsSearchResults.SelectedIndex = hits.Count > 0 ? 0 : -1;
        SettingsSearchResults.Visibility = hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsSearchEmptyText.Text = hits.Count > 0 ? string.Empty : $"没有找到“{query}”。试试 透明、圆角、热键。";
        SettingsSearchEmptyText.Visibility = hits.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        SettingsSearchPopup.IsOpen = SettingsSearchBox.IsKeyboardFocusWithin;
    }

    private void SettingsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SettingsSearchPlaceholder.Visibility = SettingsSearchBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateSearchResults();
    }

    private void SettingsSearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        UpdateSearchResults();

    private void SettingsSearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        SettingsSearchPopup.IsOpen = false;

    private void SettingsSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var count = SettingsSearchResults.Items.Count;
        switch (e.Key)
        {
            case Key.Down or Key.Up when count > 0 && SettingsSearchBox.Text.Trim().Length > 0:
                var step = e.Key == Key.Down ? 1 : -1;
                SettingsSearchResults.SelectedIndex = (SettingsSearchResults.SelectedIndex + step + count) % count;
                SettingsSearchResults.ScrollIntoView(SettingsSearchResults.SelectedItem);
                SettingsSearchPopup.IsOpen = SettingsSearchBox.IsKeyboardFocusWithin;
                e.Handled = true;
                break;
            case Key.Enter when SettingsSearchResults.SelectedItem is SettingsSearchEntry entry:
                OpenSearchResult(entry);
                e.Handled = true;
                break;
            case Key.Escape when SettingsSearchPopup.IsOpen:
                SettingsSearchPopup.IsOpen = false;
                e.Handled = true;
                break;
            case Key.Escape when SettingsSearchBox.Text.Length > 0:
                SettingsSearchBox.Clear();
                e.Handled = true;
                break;
        }
    }

    private void SettingsSearchResults_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(SettingsSearchResults, source) is ListBoxItem { DataContext: SettingsSearchEntry entry })
        {
            OpenSearchResult(entry);
            e.Handled = true;
        }
    }

    // Ctrl+F reaches the search from anywhere in the window, except while a hotkey is
    // being recorded, where the keys belong to the recording.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && !_recordingSearchHotkey)
        {
            SettingsSearchBox.Focus();
            SettingsSearchBox.SelectAll();
            e.Handled = true;
        }
    }

    internal void RefreshDesktopExperience()
    {
        LoadDesktopExperience();
        RefreshAppearanceOverrides();
    }

    internal void RefreshDockSettings()
    {
        LoadDockSettings();
        LoadDesktopMode();
    }

    internal void RefreshTopBarSettings()
    {
        if (_topBarState is null)
        {
            return;
        }

        LoadTopBarSettings();
        LoadDesktopMode();
    }

    internal void RefreshTaskbarSettings()
    {
        LoadTaskbarSettings();
        LoadDesktopMode();
    }

    private void LoadPrivacySettings()
    {
        _loadingStartup = true;
        try
        {
            StartupCheckBox.IsChecked = _maintenance.IsStartupEnabled();
            StartupCheckBox.IsEnabled = _maintenance.CanChangeStartup;
            if (!_maintenance.CanChangeStartup)
            {
                StartupNoteText.Text = "测试模式不修改开机启动。";
                StartupNoteText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _loadingStartup = false;
        }
        DataDirectoryText.Text = _maintenance.DataDirectory;
        UninstallRow.Visibility = _maintenance.CanUninstall ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingStartup)
        {
            return;
        }

        _maintenance.SetStartupEnabled(StartupCheckBox.IsChecked == true);
        LoadPrivacySettings();
    }

    internal void RefreshCleanDesktopSettings()
    {
        _loadingCleanDesktop = true;
        try
        {
            CleanDesktopCheckBox.IsChecked = _maintenance.IsCleanDesktopEnabled();
        }
        finally
        {
            _loadingCleanDesktop = false;
        }
        RefreshFileBoxCleanDesktopHint();

        // A restore that did not complete stays visible here until it is resolved.
        var notice = _maintenance.CleanDesktopNotice();
        if (notice.Length > 0)
        {
            CleanDesktopStatusText.Text = notice;
            CleanDesktopRetryButton.Content = "重新恢复";
            CleanDesktopRetryButton.Visibility = Visibility.Visible;
            return;
        }

        // With nothing to retry, the icons may still be stuck hidden while the mode is
        // off (a lost record from an earlier version). The Shell query runs off the UI
        // thread; a newer refresh or a closed window cancels applying what it found.
        CleanDesktopRetryButton.Visibility = Visibility.Collapsed;
        var version = ++_cleanDesktopRefreshVersion;
        _ = RefreshCleanDesktopOrphanStateAsync(version);
    }

    private async Task RefreshCleanDesktopOrphanStateAsync(int version)
    {
        var orphaned = await _maintenance.CleanDesktopIconsOrphaned();
        if (!orphaned || version != _cleanDesktopRefreshVersion || _isClosed)
        {
            return;
        }

        CleanDesktopStatusText.Text = "桌面图标当前处于隐藏状态。";
        CleanDesktopRetryButton.Content = "恢复显示";
        CleanDesktopRetryButton.Visibility = Visibility.Visible;
    }

    private async void CleanDesktopRetry_Click(object sender, RoutedEventArgs e)
    {
        CleanDesktopRetryButton.IsEnabled = false;
        CleanDesktopStatusText.Text = "正在恢复桌面图标…";
        try
        {
            CleanDesktopStatusText.Text = await _maintenance.RestoreCleanDesktopIcons();
        }
        finally
        {
            CleanDesktopRetryButton.IsEnabled = true;
            RefreshCleanDesktopSettings();
        }
    }

    internal void LoadStyle()
    {
        _loadingStyle = true;
        try
        {
            SelectByTag(CardShadowSelector, _maintenance.Style.CardShadow.ToString());
            SelectByTag(MotionSelector, _maintenance.Style.Motion.ToString());
            var spacing = CardSpacingSelector.Items.OfType<ComboBoxItem>()
                .OrderBy(item => Math.Abs(double.Parse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture)
                    - _maintenance.Style.CardSpacing))
                .First();
            CardSpacingSelector.SelectedItem = spacing;
            var style = _maintenance.Style;
            GridSnapCheckBox.IsChecked = style.GridSnap;
            GlassLightRadio.IsChecked = style.Glass == GlassMode.Light;
            GlassDarkRadio.IsChecked = style.Glass == GlassMode.Dark;
            GlassClearRadio.IsChecked = style.Glass == GlassMode.Clear;
            TextInkAutoRadio.IsChecked = style.TextInk == TextInk.Auto;
            TextInkWhiteRadio.IsChecked = style.TextInk == TextInk.White;
            TextInkDarkRadio.IsChecked = style.TextInk == TextInk.Dark;
            TextInkCustomRadio.IsChecked = style.TextInk == TextInk.Custom;
            CustomTextColorChip.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(style.CustomTextColor)!);
            GlassDensitySlider.Value = style.GlassDensity;
            GlassDensityText.Text = $"{style.GlassDensity * 100:0}%";
            GlassDensityPanel.IsEnabled = style.Glass != GlassMode.Clear;
            ItemSurfaceLightRadio.IsChecked = style.ItemSurfaceTone == SurfaceTone.Light;
            ItemSurfaceDarkRadio.IsChecked = style.ItemSurfaceTone == SurfaceTone.Dark;
            ItemSurfaceStrengthSlider.Value = style.ItemSurfaceStrength;
            ItemSurfaceStrengthText.Text = $"{style.ItemSurfaceStrength * 100:0}%";
            ItemSurfacePanel.IsEnabled = style.Glass == GlassMode.Light;
            PaletteSwatches.Select(GlassPaletteSwatches, style.Glass == GlassMode.Light ? style.Palette : null);
        }
        finally
        {
            _loadingStyle = false;
        }
        UpdatePresetPreview(GetSelectedPreset());
        RefreshAppearanceOverrides();
        if (_selectedComponentEntry?.Kind is { } kind)
        {
            UpdateOwnLookSummary(kind);
        }
    }

    private void AppearancePreview_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AppearancePreviewWindow(_maintenance.Style) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.AppliedAppearance is { } selection)
        {
            selection.ApplyTo(_maintenance.Style);
            _maintenance.ApplyGlass();
            LoadStyle();
        }
    }

    private void Glass_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingStyle || GlassClearRadio is null || TextInkCustomRadio is null)
        {
            return;
        }

        var style = _maintenance.Style;
        style.Glass = GlassDarkRadio.IsChecked == true
            ? GlassMode.Dark
            : GlassClearRadio.IsChecked == true ? GlassMode.Clear : GlassMode.Light;
        style.TextInk = TextInkCustomRadio.IsChecked == true
            ? TextInk.Custom
            : TextInkDarkRadio.IsChecked == true
                ? TextInk.Dark
                : TextInkWhiteRadio.IsChecked == true ? TextInk.White : TextInk.Auto;
        _maintenance.ApplyGlass();
        LoadStyle();
    }

    // Reads a skin's look, shows what will change and applies it after a yes.
    private async void ImportRainmeterLook_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择一个 Rainmeter 皮肤文件夹",
            InitialDirectory = BingLan.App.Themes.RainmeterSkinFiles.SkinsFolder() ?? string.Empty
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var folder = dialog.FolderName;
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        RainmeterImportStatusText.Text = "正在读取…";
        var look = await Task.Run(() => RainmeterSkinReader.Read(BingLan.App.Themes.RainmeterSkinFiles.Collect(folder)));
        if (look.IsEmpty)
        {
            RainmeterImportStatusText.Text = $"“{name}”中没有找到字体或颜色设置";
            return;
        }

        var lines = new List<string>();
        if (look.FontFace is { } font) lines.Add($"字体：{font}");
        if (look.TextColor is { } text) lines.Add($"文字颜色：{text}");
        if (look.BackgroundColor is { } backing) lines.Add($"卡片底色：{backing}，不透明度 {look.BackgroundOpacity:P0}");
        if (look.AccentColor is { } accent) lines.Add($"强调色：{accent}");
        if (look.CornerRadius is { } radius) lines.Add($"圆角：{radius:0}");
        var answer = System.Windows.MessageBox.Show(
            this,
            $"将按“{name}”调整所有卡片：\n\n{string.Join("\n", lines)}\n\n当前设置会先备份，可在“通用设置”页的备份中恢复。继续吗？",
            "从 Rainmeter 皮肤导入外观",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            RainmeterImportStatusText.Text = "已取消";
            return;
        }

        RainmeterImportStatusText.Text = _maintenance.ImportRainmeterLook(look, name);
        LoadStyle();
    }

    // Picking a colour also switches the cards to it.
    private void CustomTextColor_Click(object sender, RoutedEventArgs e)
    {
        var style = _maintenance.Style;
        var current = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(style.CustomTextColor)!;
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            FullOpen = true,
            AnyColor = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        style.CustomTextColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        style.TextInk = TextInk.Custom;
        _maintenance.ApplyGlass();
        LoadStyle();
    }

    private void GlassDensity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingStyle || GlassDensityText is null)
        {
            return;
        }

        _maintenance.Style.GlassDensity = e.NewValue;
        GlassDensityText.Text = $"{_maintenance.Style.GlassDensity * 100:0}%";
        _maintenance.ApplyGlass();
        UpdatePresetPreview(GetSelectedPreset());
    }

    // Inner surfaces only change the shared brushes, so the cards keep their own colours.
    private void ItemSurfaceTone_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingStyle || ItemSurfaceDarkRadio is null)
        {
            return;
        }

        _maintenance.Style.ItemSurfaceTone = ItemSurfaceDarkRadio.IsChecked == true
            ? SurfaceTone.Dark
            : SurfaceTone.Light;
        _maintenance.ApplyStyle();
    }

    private void ItemSurfaceStrength_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingStyle || ItemSurfaceStrengthText is null)
        {
            return;
        }

        _maintenance.Style.ItemSurfaceStrength = e.NewValue;
        ItemSurfaceStrengthText.Text = $"{_maintenance.Style.ItemSurfaceStrength * 100:0}%";
        _maintenance.ApplyStyle();
    }

    private static void SelectByTag(System.Windows.Controls.ComboBox selector, string tag) =>
        selector.SelectedItem = selector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals((string)item.Tag, tag, StringComparison.Ordinal));

    private void StyleSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingStyle
            || CardShadowSelector.SelectedItem is not ComboBoxItem { Tag: string shadowTag }
            || CardSpacingSelector.SelectedItem is not ComboBoxItem { Tag: string spacingTag }
            || MotionSelector.SelectedItem is not ComboBoxItem { Tag: string motionTag })
        {
            return;
        }

        _maintenance.Style.CardShadow = Enum.Parse<CardShadow>(shadowTag);
        _maintenance.Style.CardSpacing = double.Parse(spacingTag, System.Globalization.CultureInfo.InvariantCulture);
        _maintenance.Style.Motion = Enum.Parse<MotionLevel>(motionTag);
        _maintenance.ApplyStyle();
    }

    private void GridSnapCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingStyle || GridSnapCheckBox is null)
        {
            return;
        }

        _maintenance.Style.GridSnap = GridSnapCheckBox.IsChecked == true;
        _maintenance.ApplyStyle();
    }

    private void LoadFileBoxAutomation()
    {
        _loadingFileBoxAutomation = true;
        try
        {
            FileBoxAutomationCheckBox.IsChecked = _maintenance.IsFileBoxAutomationEnabled();
            FileBoxDeleteOriginalsCheckBox.IsChecked = _maintenance.IsFileBoxDeleteOriginalsEnabled();
            HeaderToolsOnHoverCheckBox.IsChecked = _maintenance.IsHeaderToolsOnHover();
        }
        finally
        {
            _loadingFileBoxAutomation = false;
        }
    }

    private void FileBoxAutomationCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingFileBoxAutomation)
        {
            return;
        }

        _maintenance.SetFileBoxAutomation(FileBoxAutomationCheckBox.IsChecked == true);
    }

    private void FileBoxDeleteOriginalsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingFileBoxAutomation)
        {
            return;
        }

        _maintenance.SetFileBoxDeleteOriginals(FileBoxDeleteOriginalsCheckBox.IsChecked == true);
    }

    private void HeaderToolsOnHoverCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingFileBoxAutomation)
        {
            return;
        }

        _maintenance.SetHeaderToolsOnHover(HeaderToolsOnHoverCheckBox.IsChecked == true);
    }

    private async void CleanDesktopCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingCleanDesktop)
        {
            return;
        }

        var enable = CleanDesktopCheckBox.IsChecked == true;
        CleanDesktopCheckBox.IsEnabled = false;
        CleanDesktopStatusText.Text = enable ? "正在隐藏桌面图标…" : "正在恢复桌面图标…";
        try
        {
            CleanDesktopStatusText.Text = await _maintenance.SetCleanDesktop(enable);
        }
        finally
        {
            CleanDesktopCheckBox.IsEnabled = true;
            RefreshCleanDesktopSettings();
        }
    }

    // 收纳只记录位置，桌面上的原文件仍可见；分组盒设置里指向清爽桌面开关，避免用户找不到隐藏入口。
    private void RefreshFileBoxCleanDesktopHint()
    {
        var enabled = _maintenance.IsCleanDesktopEnabled();
        GoToCleanDesktopButton.IsEnabled = !enabled;
        GoToCleanDesktopButton.Content = enabled ? "清爽桌面已开启" : "前往开启清爽桌面";
    }

    private void GoToCleanDesktop_Click(object sender, RoutedEventArgs e) => SelectNavigationPage("CleanDesktop");

    private void OpenDataDirectory_Click(object sender, RoutedEventArgs e) =>
        _maintenance.OpenDataDirectory();

    private void RefreshBackupList()
    {
        var entries = _maintenance.ListBackups()
            .Select(path => new BackupEntry(path))
            .ToList();
        BackupList.ItemsSource = entries;
        BackupEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _maintenance.CreateBackup();
            RefreshBackupList();
            BackupStatusText.Text = $"已备份到 {path}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            BackupStatusText.Text = $"备份未完成：{exception.Message}";
        }
    }

    private void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupEntry entry)
        {
            BackupStatusText.Text = "请先选择一个备份";
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            this,
            $"将用 {entry.Label} 的备份替换当前桌面组件、个人内容和设置。当前状态会先另存为备份，随后冰蓝桌面会重新启动。继续吗？",
            "从备份恢复",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _maintenance.RestoreBackup(entry.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidDataException)
        {
            BackupStatusText.Text = $"无法使用这个备份：{exception.Message}";
        }
    }

    private void DeleteBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupEntry entry)
        {
            BackupStatusText.Text = "请先选择一个备份";
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            this,
            $"将删除 {entry.Label} 的备份文件，删除后无法恢复。继续吗？",
            "删除备份",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (!_maintenance.DeleteBackup(entry.Path))
            {
                BackupStatusText.Text = "无法删除这个备份";
                RefreshBackupList();
                return;
            }
            RefreshBackupList();
            BackupStatusText.Text = "已删除备份";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            BackupStatusText.Text = $"删除未完成：{exception.Message}";
        }
    }

    private void ResetComponentPlacement_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedComponentEntry is not { } entry)
        {
            return;
        }

        var reset = _maintenance.ResetWidgetPlacement(entry.Window, entry.Kind);
        ComponentSettingsStatusText.Text = reset ? "已恢复默认位置" : "这张卡片没有默认位置";
        RefreshComponentList(entry.Window, entry.Kind);
    }

    // Same as the card's own "删除" menu: it asks first, and built-in cards are hidden
    // rather than losing their settings.
    private void DeleteComponent_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedComponentEntry?.Window is not { } window)
        {
            ComponentSettingsStatusText.Text = "这张卡片当前没有显示在桌面上";
            return;
        }
        if (_maintenance.DeleteWidget(window))
        {
            ComponentSettingsStatusText.Text = "已删除";
            RefreshComponentList();
        }
    }

    private void ExportTheme_Click(object sender, RoutedEventArgs e)
    {
        var nameDialog = new ThemeExportDialog { Owner = this };
        if (nameDialog.ShowDialog() != true)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出冰蓝桌面主题",
            Filter = $"冰蓝桌面主题 (*{ThemeArchive.FileExtension})|*{ThemeArchive.FileExtension}",
            FileName = nameDialog.ThemeName,
            DefaultExt = ThemeArchive.FileExtension,
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ThemeStatusText.Text = _maintenance.ExportTheme(dialog.FileName, nameDialog.ThemeName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ThemeStatusText.Text = $"导出未完成：{exception.Message}";
        }
    }

    private async void ImportTheme_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入冰蓝桌面主题",
            Filter = $"冰蓝桌面主题 (*{ThemeArchive.FileExtension})|*{ThemeArchive.FileExtension}"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var preview = _maintenance.ReadTheme(dialog.FileName);
            if (preview.Package is not { } package)
            {
                ThemeStatusText.Text = preview.Error;
                return;
            }

            var confirmDialog = new ThemeImportDialog(package, preview.PreviewPng) { Owner = this };
            if (confirmDialog.ShowDialog() != true)
            {
                return;
            }

            ThemeStatusText.Text = "正在匹配本机应用…";
            var outcome = await _maintenance.ImportTheme(dialog.FileName);
            ThemeStatusText.Text = outcome.Message;
            LoadDockSettings();
            LoadDesktopMode();
            RefreshBackupList();

            if (outcome.MissingSlots.Count > 0)
            {
                new ThemeBindingWizardWindow(outcome.MissingSlots, _maintenance.PinBoundApp) { Owner = this }
                    .ShowDialog();
                LoadDockSettings();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException)
        {
            ThemeStatusText.Text = $"导入未完成：{exception.Message}";
        }
    }

    /// <summary>
    /// The desktop mode is where Dock, taskbar and top bar settings start from; the bars
    /// page fine-tunes them. The scheme page and the bars page each name the mode in use
    /// and what was changed since, with one button back to the mode's defaults.
    /// </summary>
    private void LoadDesktopMode()
    {
        var match = DesktopModeRules.Match(_dockState, _taskbarState, _topBarState);
        _loadingDesktopMode = true;
        try
        {
            WindowsNativeModeRadio.IsChecked = match.IsExact && match.Mode == DesktopMode.WindowsNative;
            IceBlueHybridModeRadio.IsChecked = match.IsExact && match.Mode == DesktopMode.IceBlueHybrid;
            AppleStyleModeRadio.IsChecked = match.IsExact && match.Mode == DesktopMode.AppleStyle;
        }
        finally
        {
            _loadingDesktopMode = false;
        }

        var name = DesktopModeRules.GetName(match.Mode);
        var changes = string.Join("，", match.Differences);
        var restore = $"恢复{name}默认";
        foreach (var button in new[] { RestoreDesktopModeButton, BarsRestoreModeButton })
        {
            button.Content = restore;
            button.Tag = match.Mode;
            button.Visibility = match.IsExact ? Visibility.Collapsed : Visibility.Visible;
        }
        BarsModeNoteText.Text = match.IsExact
            ? $"当前桌面方案：{name}。这里的调整在方案基础上生效，可随时恢复方案默认。"
            : $"当前桌面方案：在{name}基础上自定义（{changes}）。";

        var (activeMode, problem) = _taskbarStatus();
        var taskbarHeldBack = problem.Length > 0
            && activeMode == TaskbarMode.SystemDefault
            && _taskbarState.Mode != TaskbarMode.SystemDefault;
        DesktopModeStatusText.Text = taskbarHeldBack
            ? $"任务栏保持系统默认：{problem}"
            : match.IsExact
                ? $"当前桌面方案：{name}。"
                : $"在{name}基础上自定义：{changes}";
        DesktopModeDriftPanel.Visibility = taskbarHeldBack || !match.IsExact
            ? Visibility.Visible
            : Visibility.Collapsed;

        RefreshSchemeSummary(match, activeMode, problem);
        RefreshBarsMap();
    }

    // “当前状态”: what each bar really does now, marked where it left the mode's defaults.
    private void RefreshSchemeSummary(DesktopModeMatch match, TaskbarMode activeMode, string problem)
    {
        SetSummary(
            SchemeDockDot,
            SchemeDockStateText,
            SchemeDockCustomTag,
            _dockState.IsEnabled,
            !_dockState.IsEnabled
                ? "已关闭"
                : _dockState.VisibilityMode == DockVisibilityMode.SmartHide ? "智能隐藏" : "始终显示，保留底部空间",
            match.DockChanged);
        if (_topBarState is { } topBar)
        {
            SetSummary(
                SchemeTopBarDot,
                SchemeTopBarStateText,
                SchemeTopBarCustomTag,
                topBar.IsEnabled,
                !topBar.IsEnabled
                    ? "已关闭"
                    : $"{(topBar.VisibilityMode == TopBarVisibilityMode.SmartHide ? "智能隐藏" : "始终显示")} · {CountTopBarModules(topBar)} 个模块",
                match.TopBarChanged);
        }
        var activeName = DesktopModeRules.GetTaskbarName(activeMode);
        SetSummary(
            SchemeTaskbarDot,
            SchemeTaskbarStateText,
            SchemeTaskbarCustomTag,
            activeMode != TaskbarMode.SystemDefault,
            problem.Length == 0 ? activeName : $"{activeName}（{problem}）",
            match.TaskbarChanged);

        static void SetSummary(System.Windows.Shapes.Ellipse dot, TextBlock text, Border tag, bool on, string state, bool changed)
        {
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, on ? "SettingsOkBrush" : "SettingsFaintInkBrush");
            text.Text = state;
            tag.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static int CountTopBarModules(TopBarState state)
    {
        var modules = state.Modules;
        bool[] shown =
        [
            modules.Clock, modules.TodoSummary, modules.Weather, modules.Performance, modules.Attention,
            modules.InputMethod, modules.Volume, modules.Network, modules.Battery, modules.Search,
            modules.TrayOverflow && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
        ];
        return shown.Count(value => value);
    }

    // The screen sketch dims bars that are off or hidden, and one line explains how the
    // Dock and the taskbar share the bottom edge.
    private void RefreshBarsMap()
    {
        const double Dimmed = 0.35;
        var dock = _dockState.IsEnabled;
        var topBar = _topBarState?.IsEnabled == true;
        var taskbar = _taskbarState.Mode;
        ScreenMapTopBarButton.Opacity = topBar ? 1 : Dimmed;
        ScreenMapDockButton.Opacity = dock ? 1 : Dimmed;
        ScreenMapTaskbarButton.Opacity = taskbar == TaskbarMode.SmartHide ? Dimmed : 1;
        BarsRelationText.Text = (dock, taskbar) switch
        {
            (true, TaskbarMode.SmartHide) => "任务栏自动隐藏时，Dock 贴近屏幕底边显示。",
            (true, TaskbarMode.SystemDefault) => "Dock 与系统任务栏同时显示，Dock 位于任务栏上方。",
            (true, _) => "Dock 显示在任务栏上方；任务栏样式只改变任务栏本身。",
            _ when topBar => "顶端信息条在屏幕顶部，底部只用 Windows 任务栏。",
            _ => "当前只用 Windows 任务栏。开启 Dock 或信息条后，桌面方案会显示为自定义。"
        };
    }

    private void DesktopMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingDesktopMode
            || sender is not RadioButton { Tag: string tag }
            || !Enum.TryParse<DesktopMode>(tag, out var mode))
        {
            return;
        }

        ApplyDesktopMode(mode);
    }

    private void RestoreDesktopMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DesktopMode mode })
        {
            ApplyDesktopMode(mode);
        }
    }

    private void ApplyDesktopMode(DesktopMode mode)
    {
        if (_topBarState is null)
        {
            DesktopModeRules.Apply(mode, _dockState, _taskbarState);
        }
        else
        {
            // The apple mode also turns the top bar on and hides the three information
            // cards it replaces, so the same facts do not show twice.
            DesktopModeRules.Apply(mode, _dockState, _taskbarState, _topBarState, _desktopExperience);
            _applyDesktopExperience(_desktopExperience);
            _applyTopBar?.Invoke();
            LoadTopBarSettings();
        }
        _applyDock();
        _applyTaskbar();
        LoadDockSettings();
        LoadTaskbarSettings();
        LoadDesktopMode();
    }

    private void LoadTaskbarSettings()
    {
        _loadingTaskbar = true;
        try
        {
            TaskbarDefaultRadio.IsChecked = _taskbarState.Mode == TaskbarMode.SystemDefault;
            TaskbarTransparentRadio.IsChecked = _taskbarState.Mode == TaskbarMode.Transparent;
            TaskbarBlurRadio.IsChecked = _taskbarState.Mode == TaskbarMode.Blur;
            TaskbarSmartHideRadio.IsChecked = _taskbarState.Mode == TaskbarMode.SmartHide;
            RevealOnlyAtCornersCheckBox.IsChecked = _taskbarState.RevealOnlyAtCorners;
            // Both pop-up options only matter while the taskbar hides itself.
            TaskbarPopupPanel.Visibility = _taskbarState.Mode == TaskbarMode.SmartHide
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _loadingTaskbar = false;
        }

        var (activeMode, problem) = _taskbarStatus();
        var activeName = activeMode switch
        {
            TaskbarMode.Transparent => "透明",
            TaskbarMode.Blur => "模糊",
            TaskbarMode.SmartHide => "自动隐藏",
            _ => "系统默认"
        };
        TaskbarStatusText.Text = problem.Length == 0
            ? $"当前：{activeName}"
            : activeMode == TaskbarMode.SystemDefault
                ? $"当前保持系统默认：{problem}"
                : $"当前：{activeName}（{problem}）";
    }

    private void RevealOnlyAtCorners_Click(object sender, RoutedEventArgs e)
    {
        _taskbarState.RevealOnlyAtCorners = RevealOnlyAtCornersCheckBox.IsChecked == true;
        _applyTaskbar();
        TaskbarStatusText.Text = _taskbarState.RevealOnlyAtCorners
            ? "任务栏只在底边左右两端弹出"
            : "已恢复：鼠标碰到底边任何位置都可弹出任务栏";
    }

    private void TaskbarMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingTaskbar
            || sender is not RadioButton { Tag: string tag }
            || !Enum.TryParse<TaskbarMode>(tag, out var mode))
        {
            return;
        }

        _taskbarState.Mode = mode;
        _applyTaskbar();
        LoadTaskbarSettings();
        LoadDesktopMode();
    }

    private void LoadDockSettings()
    {
        _loadingDock = true;
        try
        {
            DockEnabledCheckBox.IsChecked = _dockState.IsEnabled;
            DockOptionsPanel.IsEnabled = _dockState.IsEnabled;
            DockReserveRadio.IsChecked =
                _dockState.VisibilityMode == DockVisibilityMode.ReserveWorkArea;
            DockSmartHideRadio.IsChecked =
                _dockState.VisibilityMode == DockVisibilityMode.SmartHide;
            DockIconSizeSlider.Value = _dockState.IconSize;
            DockAutoIconSizeCheckBox.IsChecked = _dockState.AutoIconSize;
            QuietTaskbarFlashCheckBox.IsChecked = !BingLan.App.Services.TaskbarFlashingSetting.IsEnabled();
            LoadDockLook();
            DockIconSizeText.Text = $"{_dockState.IconSize:0}";
            DockMaximizeReleaseCheckBox.IsChecked = _dockState.ReleaseWhenMaximized;
            DockRevealModeComboBox.SelectedItem = DockRevealModeComboBox.Items
                .OfType<ComboBoxItem>()
                .First(item => (string)item.Tag == _dockState.RevealMode.ToString());
            DockStartMenuButtonCheckBox.IsChecked = _dockState.ShowStartMenuButton;
            DockShowDesktopButtonCheckBox.IsChecked = _dockState.ShowDesktopButton;
            DockBottomGapSlider.Value = _dockState.BottomGapDip;
            DockBottomGapText.Text = $"{_dockState.BottomGapDip:0} DIP";

            DockMonitorComboBox.Items.Clear();
            foreach (var monitor in MonitorCatalog.GetAll())
            {
                var item = new ComboBoxItem { Content = monitor.Label, Tag = monitor.DeviceName };
                DockMonitorComboBox.Items.Add(item);
                if (string.Equals(
                        monitor.DeviceName,
                        _dockState.MonitorDeviceName,
                        StringComparison.OrdinalIgnoreCase)
                    || (DockMonitorComboBox.SelectedItem is null && monitor.IsPrimary))
                {
                    DockMonitorComboBox.SelectedItem = item;
                }
            }

            RefreshDockPinnedList();
            RefreshDockHiddenList();
        }
        finally
        {
            _loadingDock = false;
        }
    }

    private void RefreshDockPinnedList(DockPinnedApp? selected = null)
    {
        var entries = _dockState.PinnedApps
            .Select(app => new DockPinnedEntry(app))
            .ToList();
        DockPinnedList.ItemsSource = entries;
        DockPinnedList.SelectedItem = entries.FirstOrDefault(entry => ReferenceEquals(entry.App, selected));
        DockPinnedEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshDockHiddenList()
    {
        var entries = _dockState.HiddenApps
            .Select(app => new DockPinnedEntry(app))
            .ToList();
        DockHiddenList.ItemsSource = entries;
        DockHiddenEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UnhideDockApp_Click(object sender, RoutedEventArgs e)
    {
        if (DockHiddenList.SelectedItem is not DockPinnedEntry entry)
        {
            DockStatusText.Text = "请先选择一个应用";
            return;
        }

        if (DockPinRules.Unhide(_dockState, DockPinRules.IdentityKey(entry.App)))
        {
            RefreshDockHiddenList();
            ApplyDockChange($"{entry.App.DisplayName} 会重新显示在 Dock 上");
        }
    }

    private void DockSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingDock || DockEnabledCheckBox is null || DockMonitorComboBox is null)
        {
            return;
        }

        _dockState.IsEnabled = DockEnabledCheckBox.IsChecked == true;
        DockOptionsPanel.IsEnabled = _dockState.IsEnabled;
        _dockState.VisibilityMode = DockSmartHideRadio.IsChecked == true
            ? DockVisibilityMode.SmartHide
            : DockVisibilityMode.ReserveWorkArea;
        _dockState.ReleaseWhenMaximized = DockMaximizeReleaseCheckBox.IsChecked == true;
        if (DockRevealModeComboBox?.SelectedItem is ComboBoxItem { Tag: string revealTag }
            && Enum.TryParse<DockRevealMode>(revealTag, out var revealMode))
        {
            _dockState.RevealMode = revealMode;
        }
        _dockState.ShowStartMenuButton = DockStartMenuButtonCheckBox.IsChecked == true;
        _dockState.ShowDesktopButton = DockShowDesktopButtonCheckBox.IsChecked == true;
        if (DockMonitorComboBox.SelectedItem is ComboBoxItem { Tag: string deviceName })
        {
            _dockState.MonitorDeviceName = deviceName;
        }
        ApplyDockChange(_dockState.IsEnabled ? "已应用" : "冰蓝 Dock 已关闭");
        LoadDesktopMode();
    }

    private bool _loadingDockLook;

    private void LoadDockLook()
    {
        _loadingDockLook = true;
        try
        {
            DockFollowLookRadio.IsChecked = _dockState.FollowCardLook;
            DockCustomLookRadio.IsChecked = !_dockState.FollowCardLook;
            DockCustomLookPanel.IsEnabled = !_dockState.FollowCardLook;
            DockColorEditor.Text = _dockState.SurfaceColor;
            DockOpacitySlider.Value = _dockState.SurfaceOpacity;
            DockOpacityText.Text = $"{_dockState.SurfaceOpacity * 100:0}%";
            var preview = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dockState.SurfaceColor)!);
            preview.Freeze();
            DockColorPreview.Background = preview;
            PaletteSwatches.Select(
                DockPaletteSwatches,
                _dockState.FollowCardLook
                    ? null
                    : DesktopStyleRules.Palettes.FirstOrDefault(palette => palette.Body == _dockState.SurfaceColor)?.Key);
        }
        finally
        {
            _loadingDockLook = false;
        }
        RefreshAppearanceOverrides();
    }

    /// <summary>
    /// Lists what does not follow the shared look: the Dock and the top bar in their own
    /// colours, and built-in cards whose colours were set on their own. Each line opens
    /// where it was set, or goes back to following the shared look. The bars page says
    /// the same in one line per bar.
    /// </summary>
    private void RefreshAppearanceOverrides()
    {
        if (AppearanceOverridesGroup is null)
        {
            return;
        }

        var frosted = _topBarState?.FrostedGlass != false;
        DockLookSummaryRow.Description = _dockState.FollowCardLook ? "跟随全局外观" : "已独立设置颜色与玻璃浓度";
        TopBarLookSummaryRow.Description = (_topBarState?.FollowCardLook != false ? "跟随全局外观" : "已独立设置颜色与不透明度")
            + (frosted ? string.Empty : "；磨砂玻璃已关闭");

        AppearanceOverridesGroup.Items.Clear();
        if (!_dockState.FollowCardLook)
        {
            AddOverride("冰蓝 Dock", "已独立设置颜色与玻璃浓度", () => ShowBarsLook("Dock"), () =>
            {
                _dockState.FollowCardLook = true;
                ApplyDockChange("Dock 跟随卡片外观");
                LoadDockLook();
            });
        }
        if (_topBarState is { FollowCardLook: false } topBar)
        {
            AddOverride("顶端信息条", "已独立设置颜色与不透明度", () => ShowBarsLook("TopBar"), () =>
            {
                topBar.FollowCardLook = true;
                ApplyTopBarChange("信息条跟随卡片外观");
                LoadTopBarLook();
            });
        }
        foreach (var kind in CurrentComponentKinds.Where(kind =>
                     _desktopExperience.GetComponent(kind).IsVisible && HasOwnLook(kind)))
        {
            AddOverride(DesktopExperienceRules.GetComponentName(kind), "已独立设置颜色", () =>
            {
                SelectNavigationPage("Components");
                RefreshComponentList(null, kind);
                ComponentOwnLookFold.IsExpanded = true;
                RevealElement(ComponentOwnLookFold);
            }, () => FollowSharedLook(kind));
        }
        if (AppearanceOverridesGroup.Items.Count == 0)
        {
            AppearanceOverridesGroup.Items.Add(new TextBlock
            {
                Margin = new Thickness(18, 16, 18, 16),
                Style = (Style)FindResource("CaptionStyle"),
                FontSize = 13,
                Text = "所有部分都跟随全局外观。"
            });
        }

        void AddOverride(string name, string what, Action view, Action follow)
        {
            var viewButton = new Button
            {
                Content = "查看",
                Style = (Style)FindResource("GhostButtonStyle")
            };
            System.Windows.Automation.AutomationProperties.SetName(viewButton, $"查看{name}的外观设置");
            viewButton.Click += (_, _) => view();
            var followButton = new Button { Content = "恢复跟随", Margin = new Thickness(8, 0, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(followButton, $"{name}恢复跟随全局外观");
            followButton.Click += (_, _) => follow();
            var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            actions.Children.Add(viewButton);
            actions.Children.Add(followButton);
            AppearanceOverridesGroup.Items.Add(new SettingsRow { Header = name, Description = what, Content = actions });
        }
    }

    private void DockLook_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingDockLook || _loadingDock || DockCustomLookRadio is null)
        {
            return;
        }

        _dockState.FollowCardLook = DockFollowLookRadio.IsChecked == true;
        ApplyDockChange(_dockState.FollowCardLook ? "Dock 跟随卡片外观" : "Dock 使用自定义外观");
        LoadDockLook();
    }

    private void DockOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingDockLook || _loadingDock || DockOpacityText is null)
        {
            return;
        }

        _dockState.SurfaceOpacity = e.NewValue;
        DockOpacityText.Text = $"{e.NewValue * 100:0}%";
        ApplyDockChange("已调整 Dock 玻璃浓度");
    }

    private void DockColorEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            DockColorEditor_Commit(sender, e);
            e.Handled = true;
        }
    }

    private void DockColorEditor_Commit(object sender, RoutedEventArgs e)
    {
        if (_loadingDockLook)
        {
            return;
        }

        var color = WidgetAppearanceRules.CoerceColorOrDefault(DockColorEditor.Text, _dockState.SurfaceColor);
        if (color != _dockState.SurfaceColor)
        {
            _dockState.SurfaceColor = color;
            ApplyDockChange("已调整 Dock 颜色");
        }
        LoadDockLook();
    }

    private void DockChooseColor_Click(object sender, RoutedEventArgs e)
    {
        var current = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dockState.SurfaceColor)!;
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            FullOpen = true,
            AnyColor = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        _dockState.SurfaceColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        ApplyDockChange("已调整 Dock 颜色");
        LoadDockLook();
    }

    // ---------------------------------------------------------------- 顶端信息条

    private void LoadTopBarSettings()
    {
        if (_topBarState is not { } state)
        {
            return;
        }

        _loadingTopBar = true;
        try
        {
            TopBarEnabledCheckBox.IsChecked = state.IsEnabled;
            TopBarOptionsPanel.IsEnabled = state.IsEnabled;
            TopBarFrostedCheckBox.IsChecked = state.FrostedGlass;
            TopBarReserveRadio.IsChecked = state.VisibilityMode == TopBarVisibilityMode.ReserveTopEdge;
            TopBarSmartHideRadio.IsChecked = state.VisibilityMode == TopBarVisibilityMode.SmartHide;
            TopBarHeightSlider.Value = Math.Clamp(
                double.IsFinite(state.Height) ? state.Height : TopBarState.DefaultHeightDip,
                TopBarState.MinimumHeightDip,
                TopBarState.MaximumHeightDip);
            TopBarClockCheckBox.IsChecked = state.Modules.Clock;
            TopBarTodoSummaryCheckBox.IsChecked = state.Modules.TodoSummary;
            TopBarWeatherCheckBox.IsChecked = state.Modules.Weather;
            TopBarPerformanceCheckBox.IsChecked = state.Modules.Performance;
            TopBarAttentionCheckBox.IsChecked = state.Modules.Attention;
            TopBarInputMethodCheckBox.IsChecked = state.Modules.InputMethod;
            TopBarVolumeCheckBox.IsChecked = state.Modules.Volume;
            TopBarNetworkCheckBox.IsChecked = state.Modules.Network;
            TopBarBatteryCheckBox.IsChecked = state.Modules.Battery;
            TopBarSearchCheckBox.IsChecked = state.Modules.Search;
            TopBarTrayOverflowCheckBox.IsChecked = state.Modules.TrayOverflow;
            TopBarTrayOverflowCheckBox.Visibility = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                ? Visibility.Visible : Visibility.Collapsed;
            TopBarEverythingCheckBox.IsChecked = state.UseEverything;
            SearchPresentationComboBox.SelectedIndex = (int)state.SearchPresentation;
            LoadSearchAppearance();
            LoadTopBarSearchHotkey();

            TopBarMonitorComboBox.Items.Clear();
            foreach (var monitor in MonitorCatalog.GetAll())
            {
                var item = new ComboBoxItem { Content = monitor.Label, Tag = monitor.DeviceName };
                TopBarMonitorComboBox.Items.Add(item);
                if (string.Equals(
                        monitor.DeviceName,
                        state.MonitorDeviceName,
                        StringComparison.OrdinalIgnoreCase)
                    || (TopBarMonitorComboBox.SelectedItem is null && monitor.IsPrimary))
                {
                    TopBarMonitorComboBox.SelectedItem = item;
                }
            }

            LoadTopBarLook();
        }
        finally
        {
            _loadingTopBar = false;
        }
    }

    private void TopBarSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingTopBar || _topBarState is not { } state
            || TopBarEnabledCheckBox is null || TopBarMonitorComboBox is null)
        {
            return;
        }

        state.IsEnabled = TopBarEnabledCheckBox.IsChecked == true;
        TopBarOptionsPanel.IsEnabled = state.IsEnabled;
        state.FrostedGlass = TopBarFrostedCheckBox.IsChecked == true;
        state.VisibilityMode = TopBarSmartHideRadio.IsChecked == true
            ? TopBarVisibilityMode.SmartHide
            : TopBarVisibilityMode.ReserveTopEdge;
        state.Modules.Clock = TopBarClockCheckBox.IsChecked == true;
        state.Modules.TodoSummary = TopBarTodoSummaryCheckBox.IsChecked == true;
        state.Modules.Weather = TopBarWeatherCheckBox.IsChecked == true;
        state.Modules.Performance = TopBarPerformanceCheckBox.IsChecked == true;
        state.Modules.Attention = TopBarAttentionCheckBox.IsChecked == true;
        state.Modules.InputMethod = TopBarInputMethodCheckBox.IsChecked == true;
        state.Modules.Volume = TopBarVolumeCheckBox.IsChecked == true;
        state.Modules.Network = TopBarNetworkCheckBox.IsChecked == true;
        state.Modules.Battery = TopBarBatteryCheckBox.IsChecked == true;
        state.Modules.Search = TopBarSearchCheckBox.IsChecked == true;
        state.Modules.TrayOverflow = TopBarTrayOverflowCheckBox.IsChecked == true;
        state.UseEverything = TopBarEverythingCheckBox.IsChecked == true;
        if (TopBarMonitorComboBox.SelectedItem is ComboBoxItem { Tag: string deviceName })
        {
            state.MonitorDeviceName = deviceName;
        }
        ApplyTopBarChange(state.IsEnabled ? "已应用" : "顶端信息条已关闭");
        RefreshAppearanceOverrides();
    }

    // ---------------------------------------------------------------- 顶栏搜索热键

    private void SearchPresentation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTopBar || _topBarState is not { } state
            || SearchPresentationComboBox.SelectedIndex < 0)
        {
            return;
        }
        state.SearchPresentation = (SearchPresentation)SearchPresentationComboBox.SelectedIndex;
        LoadSearchAppearance();
        ApplyTopBarChange("已应用搜索入口");
    }

    private void TopBarModules_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.UniformGrid grid)
            grid.Columns = Math.Clamp((int)(grid.ActualWidth / 150d), 2, 5);
    }

    private void LoadSearchAppearance()
    {
        if (_topBarState is not { } state || SearchLookComboBox is null) return;
        _loadingSearchAppearance = true;
        try
        {
            SearchAppearancePanel.IsEnabled = state.SearchPresentation != SearchPresentation.TopBar;
            SearchLookComboBox.SelectedIndex = state.SearchFollowCardLook ? 2
                : state.SearchSurfaceColor == TopBarState.DefaultSurfaceColor ? 0
                : state.SearchSurfaceColor == "#F3F6FC" ? 1 : 3;
            SearchOpacitySlider.Value = state.SearchSurfaceOpacity;
            SearchCornerSlider.Value = state.SearchCornerRadius;
            SearchOpacitySlider.IsEnabled = SearchColorPreview.IsEnabled = !state.SearchFollowCardLook;
            SearchOpacityText.Text = $"{state.SearchSurfaceOpacity * 100:0}%";
            SearchCornerText.Text = $"{state.SearchCornerRadius:0} DIP";
            var brush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(state.SearchSurfaceColor));
            brush.Freeze();
            SearchColorPreview.Background = brush;
        }
        finally { _loadingSearchAppearance = false; }
    }

    private void SearchLook_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSearchAppearance || _loadingTopBar || _topBarState is not { } state || SearchLookComboBox.SelectedIndex < 0) return;
        state.SearchFollowCardLook = SearchLookComboBox.SelectedIndex == 2;
        if (SearchLookComboBox.SelectedIndex is 0 or 1)
        {
            state.SearchSurfaceColor = SearchLookComboBox.SelectedIndex == 0 ? TopBarState.DefaultSurfaceColor : "#F3F6FC";
            state.SearchSurfaceOpacity = 0.94d;
        }
        LoadSearchAppearance();
        ApplyTopBarChange("已调整搜索框配色");
    }

    private void SearchLook_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingSearchAppearance || _loadingTopBar || _topBarState is not { } state || SearchCornerSlider is null) return;
        state.SearchSurfaceOpacity = SearchOpacitySlider.Value;
        state.SearchCornerRadius = SearchCornerSlider.Value;
        SearchOpacityText.Text = $"{state.SearchSurfaceOpacity * 100:0}%";
        SearchCornerText.Text = $"{state.SearchCornerRadius:0} DIP";
        ApplyTopBarChange("已调整搜索框外观");
    }

    private void SearchLook_Reset(object sender, RoutedEventArgs e)
    {
        if (_topBarState is not { } state) return;
        state.SearchFollowCardLook = false;
        state.SearchSurfaceColor = TopBarState.DefaultSurfaceColor;
        state.SearchSurfaceOpacity = 0.94d;
        state.SearchCornerRadius = 10d;
        LoadSearchAppearance();
        ApplyTopBarChange("已恢复搜索框默认外观");
    }

    private void SearchLook_Color(object sender, RoutedEventArgs e)
    {
        if (_topBarState is not { } state) return;
        var current = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(state.SearchSurfaceColor);
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B), FullOpen = true, AnyColor = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        state.SearchFollowCardLook = false;
        state.SearchSurfaceColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        LoadSearchAppearance();
        ApplyTopBarChange("已调整搜索框底色");
    }

    /// <summary>
    /// A click-to-record capture: the next modifier-plus-key press becomes the search
    /// hotkey. Bare keys are refused (they would swallow typing system-wide) and the
    /// optional probe rejects combinations another program already owns.
    /// </summary>
    private bool _recordingSearchHotkey;

    private void LoadTopBarSearchHotkey()
    {
        if (_topBarState is not { } state)
        {
            return;
        }
        TopBarSearchHotkeyHint.Text = string.Empty;
        TopBarSearchHotkeyButton.Content = TopBarSearchRules.ParseHotkey(state.SearchHotkey) is { } binding
            ? TopBarSearchRules.FormatHotkey(binding)
            : state.SearchHotkey;
    }

    private void TopBarSearchHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_topBarState is null)
        {
            return;
        }
        _recordingSearchHotkey = !_recordingSearchHotkey;
        if (_recordingSearchHotkey)
        {
            TopBarSearchHotkeyHint.Text = string.Empty;
            TopBarSearchHotkeyButton.Content = "按下组合键…";
            TopBarSearchHotkeyButton.Focus();
        }
        else
        {
            LoadTopBarSearchHotkey();
        }
    }

    private void TopBarSearchHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingSearchHotkey || _topBarState is not { } state)
        {
            return;
        }
        e.Handled = true;
        // With Alt held the real key arrives on SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Escape)
        {
            StopSearchHotkeyRecording(state);
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return;
        }
        if (HotkeyToken(key) is not { } token)
        {
            TopBarSearchHotkeyHint.Text = "这个键不能作为热键主键";
            return;
        }
        var parts = new List<string>(5);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }
        if (parts.Count == 0)
        {
            TopBarSearchHotkeyHint.Text = "需要至少一个修饰键（Ctrl/Alt/Shift/Win）";
            return;
        }
        parts.Add(token);
        var text = string.Join("+", parts);
        if (TopBarSearchRules.ParseHotkey(text) is null)
        {
            TopBarSearchHotkeyHint.Text = "组合无效";
            return;
        }
        if (_probeTopBarSearchHotkey?.Invoke(text) == false)
        {
            TopBarSearchHotkeyHint.Text = "该组合键已被其他程序占用";
            return;
        }
        state.SearchHotkey = text;
        StopSearchHotkeyRecording(state);
        ApplyTopBarChange("已更新搜索热键");
    }

    private void TopBarSearchHotkey_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_recordingSearchHotkey && _topBarState is { } state)
        {
            StopSearchHotkeyRecording(state);
        }
    }

    private void StopSearchHotkeyRecording(TopBarState state)
    {
        _recordingSearchHotkey = false;
        LoadTopBarSearchHotkey();
    }

    private static string? HotkeyToken(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Space => "Space",
        _ => null
    };

    private void LoadTopBarLook()
    {
        if (_topBarState is not { } state)
        {
            return;
        }

        _loadingTopBarLook = true;
        try
        {
            TopBarFollowLookRadio.IsChecked = state.FollowCardLook;
            TopBarCustomLookRadio.IsChecked = !state.FollowCardLook;
            TopBarCustomLookPanel.IsEnabled = !state.FollowCardLook;
            TopBarColorEditor.Text = state.SurfaceColor;
            TopBarOpacitySlider.Value = state.SurfaceOpacity;
            TopBarOpacityText.Text = $"{state.SurfaceOpacity * 100:0}%";
            var preview = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(state.SurfaceColor)!);
            preview.Freeze();
            TopBarColorPreview.Background = preview;
        }
        finally
        {
            _loadingTopBarLook = false;
        }
        RefreshAppearanceOverrides();
    }

    private void TopBarLook_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingTopBarLook || _loadingTopBar || _topBarState is null || TopBarCustomLookRadio is null)
        {
            return;
        }

        _topBarState.FollowCardLook = TopBarFollowLookRadio.IsChecked == true;
        ApplyTopBarChange(_topBarState.FollowCardLook ? "信息条跟随卡片外观" : "信息条使用自定义外观");
        LoadTopBarLook();
    }

    private void TopBarOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingTopBarLook || _loadingTopBar || _topBarState is null || TopBarOpacityText is null)
        {
            return;
        }

        _topBarState.SurfaceOpacity = e.NewValue;
        TopBarOpacityText.Text = $"{e.NewValue * 100:0}%";
        ApplyTopBarChange("已调整信息条不透明度");
    }

    private void TopBarHeight_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TopBarHeightText is not null)
        {
            TopBarHeightText.Text = $"{e.NewValue:0} DIP";
        }
        // IsLoaded keeps the parse-time coercion (unset Value snapping to the minimum)
        // from counting as a user change before the window even exists.
        if (!IsLoaded || _loadingTopBar || _topBarState is null)
        {
            return;
        }

        _topBarState.Height = e.NewValue;
        ApplyTopBarChange("已调整信息条高度");
    }

    private void TopBarColorEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            TopBarColorEditor_Commit(sender, e);
            e.Handled = true;
        }
    }

    private void TopBarColorEditor_Commit(object sender, RoutedEventArgs e)
    {
        if (_loadingTopBarLook || _topBarState is not { } state)
        {
            return;
        }

        var color = WidgetAppearanceRules.CoerceColorOrDefault(TopBarColorEditor.Text, state.SurfaceColor);
        if (color != state.SurfaceColor)
        {
            state.SurfaceColor = color;
            ApplyTopBarChange("已调整信息条颜色");
        }
        LoadTopBarLook();
    }

    private void TopBarChooseColor_Click(object sender, RoutedEventArgs e)
    {
        if (_topBarState is not { } state)
        {
            return;
        }

        var current = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(state.SurfaceColor)!;
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            FullOpen = true,
            AnyColor = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        state.SurfaceColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        ApplyTopBarChange("已调整信息条颜色");
        LoadTopBarLook();
    }

    private void ApplyTopBarChange(string status)
    {
        if (TopBarStatusText is not null)
        {
            TopBarStatusText.Text = status;
        }
        // Let the current input/selection message finish, and apply only the latest
        // slider or preset value if several events arrived in the same dispatcher turn.
        if (_topBarApplyQueued) return;
        _topBarApplyQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _topBarApplyQueued = false;
            _applyTopBar?.Invoke();
            LoadDesktopMode();
        }, DispatcherPriority.Background);
    }

    private void QuietTaskbarFlash_Click(object sender, RoutedEventArgs e)
    {
        var quiet = QuietTaskbarFlashCheckBox.IsChecked == true;
        try
        {
            BingLan.App.Services.TaskbarFlashingSetting.SetEnabled(!quiet);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or System.Security.SecurityException or IOException)
        {
            QuietTaskbarFlashCheckBox.IsChecked = !quiet;
            TaskbarStatusText.Text = $"无法更改任务栏闪烁设置：{exception.Message}";
            return;
        }
        TaskbarStatusText.Text = quiet
            ? "已关闭任务栏应用闪烁，新消息只在 Dock 上标记"
            : "已恢复 Windows 默认的任务栏应用闪烁";
    }

    private void DockIconSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingDock || DockIconSizeText is null)
        {
            return;
        }

        _dockState.IconSize = e.NewValue;
        DockIconSizeText.Text = $"{e.NewValue:0}";
        ApplyDockChange("已调整图标大小");
    }

    private void DockAutoIconSize_Changed(object sender, RoutedEventArgs e)
    {
        // DockStatusText parses after this row; a null means XAML loading is still
        // running and the checked state is the initial one, not a user action.
        if (_loadingDock || DockAutoIconSizeCheckBox is null || DockStatusText is null)
        {
            return;
        }

        _dockState.AutoIconSize = DockAutoIconSizeCheckBox.IsChecked == true;
        ApplyDockChange(
            _dockState.AutoIconSize ? "已开启自动缩小图标" : "已关闭自动缩小图标");
    }

    private void DockBottomGap_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingDock || DockBottomGapText is null)
        {
            return;
        }

        _dockState.BottomGapDip = e.NewValue;
        DockBottomGapText.Text = $"{e.NewValue:0} DIP";
        ApplyDockChange("已调整距底部高度");
    }

    private void ImportTaskbarPins_Click(object sender, RoutedEventArgs e)
    {
        var added = _maintenance.ImportTaskbarPins();
        RefreshDockPinnedList();
        DockStatusText.Text = added > 0 ? $"已导入 {added} 个应用" : "任务栏上的应用都已在 Dock 中";
    }

    private void AddDockApp_Click(object sender, RoutedEventArgs e)
    {
        AppPickerDialog.Open(this, _dockState.PinnedApps, chosen =>
        {
            var pinned = chosen.Where(app => DockPinRules.Pin(_dockState, app)).ToList();
            RefreshDockPinnedList(pinned.LastOrDefault());
            ApplyDockChange(pinned.Count > 0 ? $"已固定 {pinned.Count} 个应用" : "所选应用已在 Dock 中");
        });
    }

    private void MoveDockAppUp_Click(object sender, RoutedEventArgs e) => MoveSelectedDockApp(-1);

    private void MoveDockAppDown_Click(object sender, RoutedEventArgs e) => MoveSelectedDockApp(1);

    private void MoveSelectedDockApp(int offset)
    {
        if (DockPinnedList.SelectedItem is not DockPinnedEntry entry)
        {
            DockStatusText.Text = "请先选择一个应用";
            return;
        }

        var key = DockPinRules.IdentityKey(entry.App);
        if (DockPinRules.Move(_dockState, key, DockPinRules.IndexOf(_dockState, key) + offset))
        {
            RefreshDockPinnedList(entry.App);
            ApplyDockChange("已调整顺序");
        }
    }

    private void UnpinDockApp_Click(object sender, RoutedEventArgs e)
    {
        if (DockPinnedList.SelectedItem is not DockPinnedEntry entry)
        {
            DockStatusText.Text = "请先选择一个应用";
            return;
        }

        if (DockPinRules.Unpin(_dockState, DockPinRules.IdentityKey(entry.App)))
        {
            RefreshDockPinnedList();
            ApplyDockChange($"已取消固定 {entry.App.DisplayName}");
        }
    }

    private bool _loadingUpdates;

    private void LoadUpdateSettings()
    {
        _loadingUpdates = true;
        try
        {
            var updater = _maintenance.Updater;
            AppVersionText.Text = updater is null
                ? "冰蓝桌面"
                : $"冰蓝桌面 {updater.CurrentVersion.ToString(3)}";
            var version = updater?.CurrentVersion ?? typeof(SettingsWindow).Assembly.GetName().Version;
            NavVersionText.Text = version is null ? string.Empty : $"版本 {version.ToString(3)}";
            AutoCheckUpdatesCheckBox.IsChecked = updater?.AutoCheck == true;
            AutoCheckUpdatesCheckBox.IsEnabled = updater is not null;
        }
        finally
        {
            _loadingUpdates = false;
        }
        RefreshUpdateView();
    }

    private void RefreshUpdateView()
    {
        var updater = _maintenance.Updater;
        CheckUpdatesButton.IsEnabled = updater is { IsBusy: false };
        UpdateStatusText.Text = updater?.Status ?? "此运行方式不提供更新检查。";
        if (updater?.Available is { } release)
        {
            UpdateAvailableCard.Visibility = Visibility.Visible;
            UpdateAvailableTitle.Text = $"新版本 {release.Version.ToString(3)}";
            UpdateNotesText.Text = release.Notes.Length > 0 ? release.Notes : "这个版本没有附更新说明。";
            var installable = release.Installer is not null;
            InstallUpdateButton.Visibility = installable ? Visibility.Visible : Visibility.Collapsed;
            InstallUpdateButton.IsEnabled = !updater.IsBusy;
            InstallUpdateNoteText.Text = installable
                ? "安装时冰蓝桌面会暂时退出，装完自动重新打开。"
                : "这个版本没有可校验的安装包，请在发布页下载。";
        }
        else
        {
            UpdateAvailableCard.Visibility = Visibility.Collapsed;
        }
    }

    private void AutoCheckUpdates_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUpdates || _maintenance.Updater is not { } updater)
        {
            return;
        }
        updater.AutoCheck = AutoCheckUpdatesCheckBox.IsChecked == true;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_maintenance.Updater is { } updater)
        {
            await updater.CheckNowAsync();
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_maintenance.Updater is { } updater)
        {
            await updater.DownloadAndInstallAsync();
        }
    }

    private void OpenReleasePage_Click(object sender, RoutedEventArgs e) =>
        _maintenance.Updater?.OpenReleasePage();

    private void OpenRepository_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("https://github.com/keros68/binglan") { UseShellExecute = true })?.Dispose();

    private void ApplyDockChange(string status)
    {
        _applyDock();
        DockStatusText.Text = status;
    }

    private void SelectNavigationPage(string link)
    {
        var (page, section) = ResolveSettingsLink(link);
        if (SettingsNavigationList.Items.OfType<ListBoxItem>().FirstOrDefault(item => Equals(item.Tag, page)) is not { } item)
        {
            return;
        }

        SettingsNavigationList.SelectedItem = item;
        switch (section)
        {
            case "Layout":
                AppearanceLayoutFold.IsExpanded = true;
                RevealElement(AppearanceLayoutFold);
                break;
            case "Dock" or "TopBar" or "Taskbar":
                RevealBarSection(section);
                break;
            case "CleanDesktop":
                RevealElement(CleanDesktopRow);
                break;
            case "Backup":
                RevealElement(BackupList);
                break;
            case "Privacy":
                RevealElement(DataDirectoryText);
                break;
        }
    }

    private Expander BarSection(string name) => name switch
    {
        "Dock" => DockSection,
        "TopBar" when _topBarState is not null => TopBarSection,
        _ => TaskbarSection
    };

    // Opens one of the three bars on its page and brings its header into view.
    private void RevealBarSection(string name)
    {
        var section = BarSection(name);
        section.IsExpanded = true;
        RevealElement(section);
    }

    private void BarsJump_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string bar })
        {
            SelectNavigationPage(bar);
        }
    }

    private void ScreenMap_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string bar })
        {
            RevealBarSection(bar);
        }
    }

    // The Dock and top bar look settings sit with the shared look on the appearance page.
    private void BarsLookJump_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string bar })
        {
            ShowBarsLook(bar);
        }
    }

    private void ShowBarsLook(string bar)
    {
        SelectNavigationPage("Appearance");
        AppearanceBarsLookFold.IsExpanded = true;
        RevealElement(bar == "TopBar" ? TopBarLookSettingsRow : DockLookSettingsRow);
    }

    /// <summary>
    /// Scrolls a setting to the middle of its page once layout has caught up, tints it
    /// briefly (a fold only on its header), and optionally moves keyboard focus to its
    /// first control.
    /// </summary>
    private void RevealElement(FrameworkElement target, bool focus = false)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_isClosed || !target.IsVisible)
            {
                return;
            }

            UpdateLayout();
            var flash = target is Expander fold
                ? fold.Template?.FindName("HeaderSite", fold) as FrameworkElement
                : null;
            if (FindVisualAncestor<ScrollViewer>(target) is { } scroller)
            {
                var top = target.TranslatePoint(new System.Windows.Point(), scroller).Y;
                var centred = Math.Max(0, (scroller.ViewportHeight - target.ActualHeight) / 2);
                scroller.ScrollToVerticalOffset(Math.Max(0, scroller.VerticalOffset + top - centred));
                scroller.UpdateLayout();
            }
            SettingsSearch.Flash(flash ?? target);
            if (focus)
            {
                FocusFirstControl(target);
            }
        });
    }

    private static T? FindVisualAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var node = System.Windows.Media.VisualTreeHelper.GetParent(element);
             node is not null;
             node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is T match)
            {
                return match;
            }
        }
        return null;
    }

    // Depth first, so the first keyboard stop inside the setting takes focus.
    private static bool FocusFirstControl(DependencyObject node)
    {
        if (node is Control { IsVisible: true, IsEnabled: true, Focusable: true, IsTabStop: true } control)
        {
            return control.Focus();
        }
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(node, index);
            if (child is not UIElement { IsVisible: false } && FocusFirstControl(child))
            {
                return true;
            }
        }
        return false;
    }

    private void RefreshComponentList(
        WidgetWindowBase? preferredWindow = null,
        DesktopComponentKind? preferredKind = null)
    {
        if (DesktopComponentList is null)
        {
            return;
        }

        var windows = _widgetProvider();
        var entries = new List<DesktopComponentSettingsEntry>();
        foreach (var kind in CurrentComponentKinds)
        {
            var matchingWindows = windows
                .Where(window =>
                    TryGetDesktopComponentKind(window, out var windowKind) &&
                    windowKind == kind)
                .ToArray();
            if (matchingWindows.Length == 0)
            {
                entries.Add(CreateComponentEntry(kind, null));
                continue;
            }

            entries.AddRange(matchingWindows.Select(window => CreateComponentEntry(kind, window)));
        }
        entries.AddRange(windows
            .Where(window => !TryGetDesktopComponentKind(window, out _))
            .Select(CreateWidgetEntry));

        DesktopComponentList.ItemsSource = entries;
        var selected = preferredWindow is not null
            ? entries.FirstOrDefault(entry => ReferenceEquals(entry.Window, preferredWindow))
            : null;
        selected ??= preferredKind is { } requestedKind
            ? entries.FirstOrDefault(entry => entry.Kind == requestedKind)
            : null;
        selected ??= entries.FirstOrDefault();
        DesktopComponentList.SelectedItem = selected;
        if (selected is not null)
        {
            DesktopComponentList.ScrollIntoView(selected);
        }
    }

    private DesktopComponentSettingsEntry CreateComponentEntry(
        DesktopComponentKind kind,
        WidgetWindowBase? window)
    {
        var component = _desktopExperience.GetComponent(kind);
        var locked = window?.IsWidgetLocked ?? component.IsLocked;
        return new DesktopComponentSettingsEntry(
            window is null ? DesktopExperienceRules.GetComponentName(kind) : GetWidgetName(window),
            kind == DesktopComponentKind.Todo ? "待办卡片" : "桌面信息",
            !component.IsVisible ? "未显示" : locked ? "已锁定" : "可拖动",
            kind,
            window);
    }

    private static DesktopComponentSettingsEntry CreateWidgetEntry(WidgetWindowBase window)
    {
        var typeName = window switch
        {
            NoteWidgetWindow => "普通便签",
            FileBoxWindow => "桌面分组盒",
            _ => "桌面卡片"
        };
        return new DesktopComponentSettingsEntry(
            GetWidgetName(window),
            typeName,
            window.IsWidgetLocked ? "已锁定" : "可拖动",
            null,
            window);
    }

    private static bool TryGetDesktopComponentKind(
        WidgetWindowBase window,
        out DesktopComponentKind kind)
    {
        switch (window)
        {
            case TodoWidgetWindow:
                kind = DesktopComponentKind.Todo;
                return true;
            case InformationWidgetWindow { ComponentKind: { } componentKind }:
                kind = componentKind;
                return true;
            case QuickPlacesWindow places:
                kind = places.ComponentKind;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static string GetWidgetName(WidgetWindowBase window) => window switch
    {
        TodoWidgetWindow todo => todo.State.Title,
        NoteWidgetWindow note => note.State.Title,
        FileBoxWindow box => box.State.Title,
        InformationWidgetWindow { ComponentKind: { } kind } =>
            DesktopExperienceRules.GetComponentName(kind),
        _ => window.Title
    };

    private void PresetRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingExperience || sender is not RadioButton radio || radio.Tag is not string tag)
        {
            return;
        }
        if (Enum.TryParse<DesktopLayoutPreset>(tag, out var preset))
        {
            UpdatePresetPreview(preset);
        }
    }

    // Previews use the same placements as applying a preset, with current card colours.
    private void UpdatePresetPreview(DesktopLayoutPreset preset)
    {
        _previewPreset = preset;
        if (_presetPreviewQueued || _isClosed) return;
        _presetPreviewQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _presetPreviewQueued = false;
            if (!_isClosed) RenderPresetPreviews(_previewPreset);
        }));
    }

    private void RenderPresetPreviews(DesktopLayoutPreset preset)
    {
        PresetPreviewImage.Source = ThemePreviewRenderer.RenderPreset(preset, _desktopExperience);
        QuietPresetThumb.Source = ThemePreviewRenderer.RenderPreset(DesktopLayoutPreset.QuietInformation, _desktopExperience);
        GlacierPresetThumb.Source = ThemePreviewRenderer.RenderPreset(DesktopLayoutPreset.GlacierWorkbench, _desktopExperience);
        ApplePresetThumb.Source = ThemePreviewRenderer.RenderPreset(DesktopLayoutPreset.TransparentApple, _desktopExperience);
        CenterClockPresetThumb.Source = ThemePreviewRenderer.RenderPreset(DesktopLayoutPreset.CenterClock, _desktopExperience);
    }

    private void ApplyLayout_Click(object sender, RoutedEventArgs e)
    {
        _desktopExperience.ActivePreset = GetSelectedPreset();
        DesktopExperienceRules.Normalize(_desktopExperience);
        _applyLayoutPreset(_desktopExperience);
        LayoutStatusText.Text =
            $"已应用“{DesktopExperienceRules.GetPresetName(_desktopExperience.ActivePreset)}”";
    }

    private DesktopLayoutPreset GetSelectedPreset()
    {
        if (GlacierWorkbenchPresetRadio.IsChecked == true)
        {
            return DesktopLayoutPreset.GlacierWorkbench;
        }
        if (CenterClockPresetRadio.IsChecked == true)
        {
            return DesktopLayoutPreset.CenterClock;
        }
        return TransparentApplePresetRadio.IsChecked == true
            ? DesktopLayoutPreset.TransparentApple
            : DesktopLayoutPreset.QuietInformation;
    }

    private static (double Width, double Height) GetMinimumComponentSize(
        DesktopComponentKind kind) =>
        kind == DesktopComponentKind.Todo ? (226d, 166d) : (180d, 72d);

    private static double ParseSize(
        string value,
        double fallback,
        double minimum,
        double maximum) =>
        double.TryParse(value, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;

    private async void SearchCity_Click(object sender, RoutedEventArgs e) =>
        await SearchCityAsync();

    private async Task SearchCityAsync()
    {
        CancelSearch();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        SearchCityButton.IsEnabled = false;
        ApplyCityButton.IsEnabled = false;
        CityResultsList.ItemsSource = null;
        CityResultsList.Visibility = Visibility.Collapsed;
        CitySearchStatusText.Text = "正在搜索…";

        try
        {
            var outcome = await _citySearchService.SearchAsync(
                CityQueryEditor.Text,
                cancellation.Token);
            if (cancellation.IsCancellationRequested ||
                !ReferenceEquals(_searchCancellation, cancellation))
            {
                return;
            }

            CityResultsList.ItemsSource = outcome.Results;
            // The list only takes room while there is something to choose.
            CityResultsList.Visibility = outcome.Results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            CitySearchStatusText.Text = outcome.Message;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 关闭窗口或开始新搜索时安静取消旧请求。
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                _searchCancellation = null;
                SearchCityButton.IsEnabled = true;
            }
            cancellation.Dispose();
        }
    }

    private void CityQueryEditor_KeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        _ = SearchCityAsync();
        e.Handled = true;
    }

    private void CityResultsList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        ApplyCityButton.IsEnabled = CityResultsList.SelectedItem is CitySearchResult;

    private void ApplyCity_Click(object sender, RoutedEventArgs e)
    {
        if (CityResultsList.SelectedItem is not CitySearchResult selected)
        {
            return;
        }

        _applyCity(selected);
        UpdateCurrentCity(selected.DisplayName);
        CitySearchStatusText.Text = "城市已保存，正在刷新天气";
    }

    private void UpdateCurrentCity(string city) =>
        CurrentCityText.Text = string.IsNullOrWhiteSpace(city)
            ? "当前城市：未设置"
            : $"当前城市：{city.Trim()}";

    private void CancelSearch()
    {
        _searchCancellation?.Cancel();
        _searchCancellation = null;
    }

    public sealed record DesktopComponentSettingsEntry(
        string Name,
        string TypeName,
        string Status,
        DesktopComponentKind? Kind,
        WidgetWindowBase? Window);
}

internal sealed class DockPinnedEntry(DockPinnedApp app)
{
    public DockPinnedApp App { get; } = app;
    public string DisplayName => App.DisplayName;
    public string Detail => App.ExecutablePath ?? App.TargetPath ?? "Microsoft Store 或网页应用";
}

internal sealed class BackupEntry(string path)
{
    public string Path { get; } = path;

    // The time plus what the backup holds, so the right one can be picked without opening it.
    public string Label { get; } = LocalStateStore.DescribeBackup(path) is { } summary
        ? $"{File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}  ·  {summary.PresetName}  ·  {summary.CardCount} 张卡片  ·  Dock {summary.DockAppCount} 个应用"
        : $"{File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}  ·  无法读取此备份";
}
