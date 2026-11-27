using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using BingLan.Core.Models;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;

namespace BingLan.App.Windows;

public partial class SettingsWindow
{
    private string? _activeScreenKey;
    private IReadOnlyList<MonitorCardOption> _multiScreenOptions = [];
    private bool _updatingMultiScreenUi;

    internal string? ActiveScreenKey => _activeScreenKey;

    internal bool IsMultiScreenUiVisible =>
        SchemeMultiScreenHost?.Visibility == Visibility.Visible;

    internal bool IsMultiScreenTopologyBarVisible =>
        ComponentsMultiScreenBarHost?.Visibility == Visibility.Visible;

    internal bool HasInstantiatedMultiScreenChildren =>
        SchemeMultiScreenHost?.Child is not null
        || ComponentsMultiScreenBarHost?.Child is not null
        || LayoutMultiScreenBarHost?.Child is not null
        || BuiltInMoveScreenHost?.Child is not null
        || SelectedWidgetMoveScreenHost?.Child is not null;

    private bool IsMultiScreenActive =>
        _maintenance.MultiScreen is { } bridge
        && bridge.IsEnabled()
        && _multiScreenOptions.Count > 0;

    private DesktopExperienceState ActiveDesktopExperience =>
        _maintenance.MultiScreen is { } bridge
        && bridge.IsEnabled()
        && _activeScreenKey is not null
            ? bridge.GetExperience(_activeScreenKey)
            : _desktopExperience;

    private DesktopExperienceState GetExperienceForInformationWindow(InformationWidgetWindow window)
    {
        if (_maintenance.MultiScreen is { } bridge && bridge.IsEnabled())
        {
            var screenKey = bridge.GetScreenKeyForWindow(window);
            if (screenKey is not null)
            {
                return bridge.GetExperience(screenKey);
            }
        }

        return _desktopExperience;
    }

    private void ApplyActiveDesktopExperience(DesktopExperienceState experience)
    {
        if (_maintenance.MultiScreen is { } bridge && bridge.IsEnabled() && _activeScreenKey is not null)
        {
            bridge.ApplyExperience(_activeScreenKey, experience);
            RefreshMultiScreenTopologyBarsOnly();
            return;
        }

        _applyDesktopExperience(experience);
        if (IsMultiScreenActive)
        {
            RefreshMultiScreenTopologyBarsOnly();
        }
    }

    private void ApplyActiveLayoutPreset(DesktopExperienceState experience)
    {
        if (_maintenance.MultiScreen is { } bridge && bridge.IsEnabled() && _activeScreenKey is not null)
        {
            bridge.ApplyLayoutPreset(_activeScreenKey, experience);
            RefreshMultiScreenTopologyBarsOnly();
            return;
        }

        _applyLayoutPreset(experience);
        if (IsMultiScreenActive)
        {
            RefreshMultiScreenTopologyBarsOnly();
        }
    }

    private IReadOnlyList<WidgetWindowBase> GetActiveScreenWindows()
    {
        if (_maintenance.MultiScreen is { } bridge && bridge.IsEnabled())
        {
            return bridge.GetWindowsForScreen(_activeScreenKey);
        }

        return _widgetProvider();
    }

    private Guid? GetActivePrimaryTodoId()
    {
        if (_maintenance.MultiScreen is { } bridge && bridge.IsEnabled())
        {
            return bridge.GetPrimaryTodoId(_activeScreenKey);
        }

        return _maintenance.PrimaryTodoId();
    }

    private IReadOnlyList<QuickPlaceState> GetActiveQuickPlaces()
    {
        if (_maintenance.MultiScreen is { } bridge && bridge.IsEnabled() && _activeScreenKey is not null)
        {
            return bridge.GetQuickPlaces(_activeScreenKey);
        }

        return _maintenance.QuickPlaces();
    }

    private void SaveActiveQuickPlaces(List<QuickPlaceState> items)
    {
        if (_maintenance.MultiScreen is { } bridge && bridge.IsEnabled() && _activeScreenKey is not null)
        {
            bridge.SetQuickPlaces(_activeScreenKey, items);
            return;
        }

        _maintenance.SetQuickPlaces(items);
    }

    private WidgetWindowBase? AddWidgetOnActiveScreen(NewWidgetKind kind)
    {
        if (_maintenance.MultiScreen is { } bridge && bridge.IsEnabled() && _activeScreenKey is not null)
        {
            var created = bridge.AddWidgetOnScreen(_activeScreenKey, kind);
            RefreshMultiScreenTopologyBarsOnly();
            return created;
        }

        var result = _maintenance.AddWidget(kind);
        if (IsMultiScreenActive)
        {
            RefreshMultiScreenTopologyBarsOnly();
        }
        return result;
    }

    private bool ResetActiveWidgetPlacement(WidgetWindowBase? window, DesktopComponentKind? kind)
    {
        if (_maintenance.MultiScreen is { } bridge && bridge.IsEnabled() && _activeScreenKey is not null)
        {
            return bridge.ResetWidgetPlacement(_activeScreenKey, window, kind);
        }

        return _maintenance.ResetWidgetPlacement(window, kind);
    }

    private string ActiveScreenDisplayName()
    {
        return _multiScreenOptions
            .FirstOrDefault(option => string.Equals(option.ScreenKey, _activeScreenKey, StringComparison.Ordinal))
            ?.DisplayLabel ?? "桌面布局";
    }

    private void SyncActiveScreenForTargetWindow(WidgetWindowBase window)
    {
        if (_maintenance.MultiScreen is not { } bridge || !bridge.IsEnabled())
        {
            FlushPendingComponentEditsBeforeScreenSwitch();
            _activeScreenKey = null;
            return;
        }

        var key = bridge.GetScreenKeyForWindow(window);
        if (!string.Equals(_activeScreenKey, key, StringComparison.Ordinal))
        {
            FlushPendingComponentEditsBeforeScreenSwitch();
            _activeScreenKey = key;
            RefreshMultiScreenUi();
            SyncPresetRadiosForActiveScreen();
            UpdatePresetPreview(GetSelectedPreset());
        }
    }

    private void FlushPendingComponentEditsBeforeScreenSwitch()
    {
        CommitPendingTitleEdits();
        if (_componentApplyTimer.IsEnabled)
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: false);
        }
    }

    private void InitializeMultiScreenUi() => RefreshMultiScreenUi();

    internal void RefreshMultiScreenUi(bool refreshComponentList = false)
    {
        if (_isClosed)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => RefreshMultiScreenUi(refreshComponentList));
            return;
        }

        var bridge = _maintenance.MultiScreen;
        if (bridge is null)
        {
            CollapseAllMultiScreenHosts();
            return;
        }

        var hasMultiple = bridge.HasMultipleMonitors();
        var enabled = bridge.IsEnabled();
        var hasSavedProfiles = !hasMultiple && bridge.HasSavedSecondaryState();
        if (!hasMultiple && !enabled && !hasSavedProfiles)
        {
            // Strict zero-overhead single-screen path: collapse all placeholders with Child = null.
            CollapseAllMultiScreenHosts();
            return;
        }

        var connectedCount = hasMultiple ? Math.Max(2, bridge.GetConnectedMonitorCount()) : 1;
        // Never invoke ResolveConnected()/QueryDisplayConfig while multi-screen is disabled.
        IReadOnlyList<MonitorCardOption> screens = enabled ? bridge.GetScreens() : [];
        var selectedKey = !enabled ? null
            : screens.Any(option => string.Equals(option.ScreenKey, _activeScreenKey, StringComparison.Ordinal))
                ? _activeScreenKey
                : (screens.FirstOrDefault(option => option.IsPrimary) ?? screens.FirstOrDefault())?.ScreenKey;
        if (!string.Equals(selectedKey, _activeScreenKey, StringComparison.Ordinal))
        {
            FlushPendingComponentEditsBeforeScreenSwitch();
            _activeScreenKey = selectedKey;
        }
        _multiScreenOptions = screens;

        _updatingMultiScreenUi = true;
        try
        {
            BuildSchemeMultiScreenSection(hasMultiple || hasSavedProfiles, enabled, connectedCount);

            if (enabled && screens.Count > 0)
            {
                ComponentsMultiScreenBarHost.Child = BuildTopologySwitcherCard(
                    bridge,
                    screens,
                    isLayoutBar: false);
                ComponentsMultiScreenBarHost.Visibility = Visibility.Visible;

                LayoutMultiScreenBarHost.Child = BuildTopologySwitcherCard(
                    bridge,
                    screens,
                    isLayoutBar: true);
                LayoutMultiScreenBarHost.Visibility = Visibility.Visible;
            }
            else
            {
                ComponentsMultiScreenBarHost.Child = null;
                ComponentsMultiScreenBarHost.Visibility = Visibility.Collapsed;
                LayoutMultiScreenBarHost.Child = null;
                LayoutMultiScreenBarHost.Visibility = Visibility.Collapsed;
            }

            UpdateMoveScreenControls(_selectedComponentEntry);
        }
        finally
        {
            _updatingMultiScreenUi = false;
        }

        if (!_isClosed)
        {
            if (refreshComponentList)
            {
                RefreshComponentList(_selectedComponentEntry?.Window, _selectedComponentEntry?.Kind);
            }
            SyncPresetRadiosForActiveScreen();
            RefreshAppearanceOverrides();
        }
    }

    private void RefreshMultiScreenTopologyBarsOnly()
    {
        if (_isClosed || _updatingMultiScreenUi || _maintenance.MultiScreen is not { } bridge)
        {
            return;
        }

        if (!bridge.IsEnabled())
        {
            return;
        }

        var screens = bridge.GetScreens();
        _multiScreenOptions = screens;
        if (screens.Count > 0)
        {
            ComponentsMultiScreenBarHost.Child = BuildTopologySwitcherCard(bridge, screens, isLayoutBar: false);
            ComponentsMultiScreenBarHost.Visibility = Visibility.Visible;
            LayoutMultiScreenBarHost.Child = BuildTopologySwitcherCard(bridge, screens, isLayoutBar: true);
            LayoutMultiScreenBarHost.Visibility = Visibility.Visible;
        }
    }

    internal void SelectActiveScreen(string? screenKey)
    {
        var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
        if (string.Equals(_activeScreenKey, normalized, StringComparison.Ordinal))
        {
            return;
        }

        CommitPendingTitleEdits();
        if (_componentApplyTimer.IsEnabled)
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: false);
        }

        _activeScreenKey = normalized;
        RefreshMultiScreenUi();
        SyncPresetRadiosForActiveScreen();
        UpdatePresetPreview(GetSelectedPreset());
        RefreshComponentList();
    }

    internal void SetMultiScreenEnabledFromUi(bool enabled)
    {
        if (_maintenance.MultiScreen is not { } bridge)
        {
            return;
        }

        bridge.SetEnabled(enabled);
        if (!enabled)
        {
            _activeScreenKey = null;
        }
        RefreshMultiScreenUi();
        SyncPresetRadiosForActiveScreen();
        RefreshComponentList();
    }

    private void CollapseAllMultiScreenHosts()
    {
        if (_activeScreenKey is not null)
        {
            FlushPendingComponentEditsBeforeScreenSwitch();
        }
        _activeScreenKey = null;
        _multiScreenOptions = [];
        if (SchemeMultiScreenHost is not null)
        {
            SchemeMultiScreenHost.Child = null;
            SchemeMultiScreenHost.Visibility = Visibility.Collapsed;
        }
        if (ComponentsMultiScreenBarHost is not null)
        {
            ComponentsMultiScreenBarHost.Child = null;
            ComponentsMultiScreenBarHost.Visibility = Visibility.Collapsed;
        }
        if (LayoutMultiScreenBarHost is not null)
        {
            LayoutMultiScreenBarHost.Child = null;
            LayoutMultiScreenBarHost.Visibility = Visibility.Collapsed;
        }
        if (BuiltInMoveScreenHost is not null)
        {
            BuiltInMoveScreenHost.Child = null;
            BuiltInMoveScreenHost.Visibility = Visibility.Collapsed;
        }
        if (SelectedWidgetMoveScreenHost is not null)
        {
            SelectedWidgetMoveScreenHost.Child = null;
            SelectedWidgetMoveScreenHost.Visibility = Visibility.Collapsed;
        }
    }

    private void SyncPresetRadiosForActiveScreen()
    {
        var experience = ActiveDesktopExperience;
        DesktopExperienceRules.Normalize(experience);
        var previousLoading = _loadingExperience;
        _loadingExperience = true;
        try
        {
            if (QuietInformationPresetRadio is not null)
            {
                QuietInformationPresetRadio.IsChecked =
                    experience.ActivePreset == DesktopLayoutPreset.QuietInformation;
            }
            if (GlacierWorkbenchPresetRadio is not null)
            {
                GlacierWorkbenchPresetRadio.IsChecked =
                    experience.ActivePreset == DesktopLayoutPreset.GlacierWorkbench;
            }
            if (TransparentApplePresetRadio is not null)
            {
                TransparentApplePresetRadio.IsChecked =
                    experience.ActivePreset == DesktopLayoutPreset.TransparentApple;
            }
            if (CenterClockPresetRadio is not null)
            {
                CenterClockPresetRadio.IsChecked =
                    experience.ActivePreset == DesktopLayoutPreset.CenterClock;
            }
            UpdatePresetPreview(experience.ActivePreset);
        }
        finally
        {
            _loadingExperience = previousLoading;
        }
    }

    private void BuildSchemeMultiScreenSection(bool canEnable, bool enabled, int connectedMonitorCount)
    {
        var stack = new StackPanel();
        var header = new TextBlock { Text = "多屏" };
        if (TryFindResource("SectionHeaderStyle") is Style headerStyle)
        {
            header.Style = headerStyle;
        }
        stack.Children.Add(header);

        var group = new SettingsGroup();
        var toggleRow = new SettingsRow
        {
            Header = "每屏独立桌面",
            Description = connectedMonitorCount > 1
                ? $"已连接 {connectedMonitorCount} 台显示器。开启后可分别设置各屏卡片、布局、Dock 和顶栏，新显示器从空白开始。"
                : "当前连接 1 台显示器。"
        };

        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        if (enabled)
        {
            var jumpButton = new Button
            {
                Content = "管理各屏桌面 →",
                Margin = new Thickness(0, 0, 12, 0)
            };
            if (TryFindResource("GhostButtonStyle") is Style ghostStyle)
            {
                jumpButton.Style = ghostStyle;
            }
            AutomationProperties.SetName(jumpButton, "前往桌面卡片管理各屏桌面");
            jumpButton.Click += (_, _) =>
            {
                SelectNavigationPage("Components");
                ShowPage("Components");
                RefreshMultiScreenUi();
            };
            controls.Children.Add(jumpButton);
        }

        var toggle = new CheckBox
        {
            IsChecked = enabled,
            IsEnabled = canEnable || enabled
        };
        if (TryFindResource("ToggleSwitchStyle") is Style toggleStyle)
        {
            toggle.Style = toggleStyle;
        }
        AutomationProperties.SetName(toggle, "每屏独立桌面");
        toggle.Checked += (_, _) =>
        {
            if (!_updatingMultiScreenUi)
            {
                SetMultiScreenEnabledFromUi(true);
            }
        };
        toggle.Unchecked += (_, _) =>
        {
            if (!_updatingMultiScreenUi)
            {
                SetMultiScreenEnabledFromUi(false);
            }
        };
        controls.Children.Add(toggle);
        toggleRow.Content = controls;
        group.Items.Add(toggleRow);
        stack.Children.Add(group);

        SchemeMultiScreenHost.Child = stack;
        SchemeMultiScreenHost.Visibility = Visibility.Visible;
    }

    private Border BuildTopologySwitcherCard(
        MultiScreenSettingsBridge bridge,
        IReadOnlyList<MonitorCardOption> screens,
        bool isLayoutBar)
    {
        var outer = new Border
        {
            Padding = new Thickness(14, 10, 14, 10)
        };
        if (TryFindResource("SettingsPanelStyle") is Style panelStyle)
        {
            outer.Style = panelStyle;
        }

        var rootStack = new StackPanel();
        var rowGrid = new Grid();
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left: proportional mini monitor topology map reflecting real bounds and Y offsets.
        var miniMap = BuildMiniTopologyCanvas(screens);
        Grid.SetColumn(miniMap, 0);
        rowGrid.Children.Add(miniMap);

        // Center: screen selector buttons + summary.
        var centerStack = new StackPanel
        {
            Margin = new Thickness(14, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var titleLine = new TextBlock
        {
            Text = isLayoutBar
                ? $"布局应用于 {ActiveScreenDisplayName()}"
                : $"正在配置 {ActiveScreenDisplayName()}",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        };
        centerStack.Children.Add(titleLine);

        var pillWrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var screen in screens)
        {
            var isSelected = string.Equals(screen.ScreenKey, _activeScreenKey, StringComparison.Ordinal);
            var pill = new Button
            {
                Margin = new Thickness(0, 0, 8, 4),
                Padding = new Thickness(10, 4, 10, 4),
                Content = $"{screen.ShortLabel} · {screen.ResolutionSummary} ({screen.CardCount}张)",
                ToolTip = screen.DisplayLabel
            };
            if (isSelected && TryFindResource("AccentButtonStyle") is Style accentStyle)
            {
                pill.Style = accentStyle;
            }
            AutomationProperties.SetName(pill, $"选择屏幕 {screen.ShortLabel}");
            var targetKey = screen.ScreenKey;
            pill.Click += (_, _) => SelectActiveScreen(targetKey);
            pillWrap.Children.Add(pill);
        }
        centerStack.Children.Add(pillWrap);
        Grid.SetColumn(centerStack, 1);
        rowGrid.Children.Add(centerStack);

        if (_activeScreenKey is { } secondaryKey && !bridge.CanBindLegacyRoot())
        {
            var copyButton = new Button
            {
                Content = "复制原桌面布局",
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(12, 6, 12, 6)
            };
            AutomationProperties.SetName(copyButton, "复制原桌面布局");
            copyButton.Click += (_, _) =>
            {
                var status = bridge.CopyPrimaryProfileToScreen(secondaryKey);
                if (ComponentSettingsStatusText is not null)
                {
                    ComponentSettingsStatusText.Text = status;
                }
                if (LayoutStatusText is not null)
                {
                    LayoutStatusText.Text = status;
                }
                RefreshMultiScreenUi();
                SyncPresetRadiosForActiveScreen();
                RefreshComponentList();
            };
            Grid.SetColumn(copyButton, 2);
            rowGrid.Children.Add(copyButton);
        }

        rootStack.Children.Add(rowGrid);

        var selectedScreen = screens.FirstOrDefault(screen => screen.IsConnected
            && string.Equals(screen.ScreenKey, _activeScreenKey, StringComparison.Ordinal));
        if (selectedScreen is not null)
        {
            AddSelectedScreenSurfaceControls(rootStack, bridge, selectedScreen);
            AddOfflineScreenProfileControls(rootStack, bridge, selectedScreen);
        }

        outer.Child = rootStack;
        return outer;
    }

    private Border BuildMiniTopologyCanvas(IReadOnlyList<MonitorCardOption> screens)
    {
        const double canvasWidth = 148d;
        const double canvasHeight = 52d;
        const double pad = 4d;

        var frame = new Border
        {
            Width = canvasWidth,
            Height = canvasHeight,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true
        };
        frame.SetResourceReference(Border.BackgroundProperty, "SettingsPreviewBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "SettingsSurfaceBorderBrush");

        var canvas = new Canvas { Width = canvasWidth, Height = canvasHeight };
        if (screens.Count > 0)
        {
            var minX = screens.Min(s => s.Bounds.Left);
            var minY = screens.Min(s => s.Bounds.Top);
            var maxX = screens.Max(s => s.Bounds.Right);
            var maxY = screens.Max(s => s.Bounds.Bottom);
            var spanX = Math.Max(1d, maxX - minX);
            var spanY = Math.Max(1d, maxY - minY);
            var availW = canvasWidth - (pad * 2d);
            var availH = canvasHeight - (pad * 2d);
            var scale = Math.Min(availW / spanX, availH / spanY);
            var drawnW = spanX * scale;
            var drawnH = spanY * scale;
            var offsetX = (canvasWidth - drawnW) / 2d;
            var offsetY = (canvasHeight - drawnH) / 2d;

            foreach (var screen in screens)
            {
                var left = offsetX + ((screen.Bounds.Left - minX) * scale);
                var top = offsetY + ((screen.Bounds.Top - minY) * scale);
                var width = Math.Max(20d, (screen.Bounds.Width * scale) - 2d);
                var height = Math.Max(16d, (screen.Bounds.Height * scale) - 2d);
                var isSelected = string.Equals(screen.ScreenKey, _activeScreenKey, StringComparison.Ordinal);

                var tile = new Button
                {
                    Width = width,
                    Height = height,
                    Padding = new Thickness(0),
                    MinWidth = 0,
                    MinHeight = 0,
                    BorderThickness = new Thickness(isSelected ? 2 : 1),
                    ToolTip = $"{screen.DisplayLabel}\n{screen.ResolutionSummary}\n已显示 {screen.CardCount} 张卡片",
                    Content = new TextBlock
                    {
                        Text = screen.IsPrimary ? "主" : screen.DisplayIndex.ToString(),
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                tile.SetResourceReference(
                    Control.BackgroundProperty,
                    isSelected ? "SettingsAccentSoftBrush" : "SettingsSurfaceBrush");
                tile.SetResourceReference(
                    Control.BorderBrushProperty,
                    isSelected ? "SettingsSelectedTextBrush" : "SettingsControlBorderBrush");
                AutomationProperties.SetName(tile, $"拓扑切换到 {screen.ShortLabel}");
                var targetKey = screen.ScreenKey;
                tile.Click += (_, _) => SelectActiveScreen(targetKey);
                Canvas.SetLeft(tile, left + 1d);
                Canvas.SetTop(tile, top + 1d);
                canvas.Children.Add(tile);
            }
        }

        frame.Child = canvas;
        return frame;
    }

    private void UpdateMoveScreenControls(DesktopComponentSettingsEntry? entry)
    {
        if (BuiltInMoveScreenHost is null || SelectedWidgetMoveScreenHost is null)
        {
            return;
        }

        var bridge = _maintenance.MultiScreen;
        if (bridge is null
            || !bridge.IsEnabled()
            || _multiScreenOptions.Count < 2
            || entry?.Window is not { } targetWindow)
        {
            BuiltInMoveScreenHost.Child = null;
            BuiltInMoveScreenHost.Visibility = Visibility.Collapsed;
            SelectedWidgetMoveScreenHost.Child = null;
            SelectedWidgetMoveScreenHost.Visibility = Visibility.Collapsed;
            return;
        }

        var currentKey = bridge.GetScreenKeyForWindow(targetWindow);
        var otherScreens = _multiScreenOptions
            .Where(option => option.IsConnected
                && !string.Equals(option.ScreenKey, currentKey, StringComparison.Ordinal))
            .ToList();
        if (otherScreens.Count == 0)
        {
            BuiltInMoveScreenHost.Child = null;
            BuiltInMoveScreenHost.Visibility = Visibility.Collapsed;
            SelectedWidgetMoveScreenHost.Child = null;
            SelectedWidgetMoveScreenHost.Visibility = Visibility.Collapsed;
            return;
        }

        var buttonRow = new WrapPanel();
        foreach (var destination in otherScreens)
        {
            var moveButton = new Button
            {
                Content = $"移至 {destination.ShortLabel}",
                Margin = new Thickness(0, 0, 6, 0)
            };
            AutomationProperties.SetName(moveButton, $"移至{destination.ShortLabel}");
            var destKey = destination.ScreenKey;
            moveButton.Click += (_, _) =>
            {
                var result = bridge.MoveWidgetToScreen(targetWindow, destKey);
                if (ComponentSettingsStatusText is not null)
                {
                    ComponentSettingsStatusText.Text = result.Message;
                }
                if (result.Succeeded)
                {
                    _activeScreenKey = destKey;
                    RefreshMultiScreenUi();
                    SyncPresetRadiosForActiveScreen();
                    RefreshComponentList(result.ResultingWindow ?? targetWindow, entry.Kind);
                }
            };
            buttonRow.Children.Add(moveButton);
        }

        if (entry.Kind is not null)
        {
            BuiltInMoveScreenHost.Child = buttonRow;
            BuiltInMoveScreenHost.Visibility = Visibility.Visible;
            SelectedWidgetMoveScreenHost.Child = null;
            SelectedWidgetMoveScreenHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            SelectedWidgetMoveScreenHost.Child = buttonRow;
            SelectedWidgetMoveScreenHost.Visibility = Visibility.Visible;
            BuiltInMoveScreenHost.Child = null;
            BuiltInMoveScreenHost.Visibility = Visibility.Collapsed;
        }
    }
}
