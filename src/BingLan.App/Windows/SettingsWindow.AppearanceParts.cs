using System.Windows;
using System.Windows.Controls;
using BingLan.Core.Models;

namespace BingLan.App.Windows;

public partial class SettingsWindow
{
    private AppearancePartKind? _expandedAppearancePart;

    internal AppearancePartKind? ExpandedAppearancePart => _expandedAppearancePart;

    internal void ExpandAppearancePart(AppearancePartKind? part)
    {
        if (part == AppearancePartKind.TopBar && _topBarState is null)
        {
            part = null;
        }

        _expandedAppearancePart = part;

        SetAccordionVisual(
            AppearanceCardsPartEditor,
            AppearanceCardsCustomizeButton,
            part == AppearancePartKind.DesktopCards);
        SetAccordionVisual(
            AppearanceLauncherPartEditor,
            AppearanceLauncherCustomizeButton,
            part == AppearancePartKind.Launcher);
        SetAccordionVisual(
            AppearanceDockPartEditor,
            AppearanceDockCustomizeButton,
            part == AppearancePartKind.Dock);
        SetAccordionVisual(
            AppearanceTopBarPartEditor,
            AppearanceTopBarCustomizeButton,
            part == AppearancePartKind.TopBar && _topBarState is not null);
    }

    private static void SetAccordionVisual(
        FrameworkElement? editor,
        System.Windows.Controls.Button? button,
        bool isExpanded)
    {
        if (editor is not null)
        {
            editor.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
        }

        if (button is not null)
        {
            button.Content = AppearancePartLookRules.FormatCustomizeButtonLabel(isExpanded);
        }
    }

    private void ToggleAppearancePart(AppearancePartKind part)
    {
        var next = AppearancePartLookRules.ToggleAccordion(_expandedAppearancePart, part);
        ExpandAppearancePart(next);
    }

    private void AppearancePartCustomize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key }
            && AppearancePartLookRules.TryParsePartKey(key, out var part))
        {
            ToggleAppearancePart(part);
        }
    }

    private void AppearancePartRestore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }
            || !AppearancePartLookRules.TryParsePartKey(key, out var part))
        {
            return;
        }

        switch (part)
        {
            case AppearancePartKind.DesktopCards:
                RestoreAllDesktopCardsToGlobalLook();
                break;

            case AppearancePartKind.Launcher:
                _maintenance.LauncherAppearance.FollowCardLook = true;
                _maintenance.ApplyLauncherAppearance();
                LoadLauncherLook();
                break;

            case AppearancePartKind.Dock:
                _dockState.FollowCardLook = true;
                ApplyDockChange("Dock 跟随卡片外观");
                LoadDockLook();
                break;

            case AppearancePartKind.TopBar when _topBarState is { } topBar:
                topBar.FollowCardLook = true;
                ApplyTopBarChange("信息条跟随卡片外观");
                LoadTopBarLook();
                break;
        }
    }

    private void AppearanceViewCustomCards_Click(object sender, RoutedEventArgs e)
    {
        var firstCustom = FindFirstCustomLookCard();
        if (firstCustom is { BuiltInKind: { } kind })
        {
            SelectNavigationPage(SettingsNavigationRules.ComponentsPageKey);
            RefreshComponentList(null, kind);
            ComponentOwnLookFold.IsExpanded = true;
            RevealElement(ComponentOwnLookFold);
            return;
        }

        if (firstCustom is { SecondaryTodo: { } todo })
        {
            SelectNavigationPage(SettingsNavigationRules.ComponentsPageKey);
            RefreshComponentList(todo);
            ComponentOwnLookFold.IsExpanded = true;
            RevealElement(ComponentOwnLookFold);
            return;
        }

        if (firstCustom is { Widget: { } widget })
        {
            SelectNavigationPage(SettingsNavigationRules.ComponentsPageKey);
            RefreshComponentList(widget);
            SelectedWidgetOwnLookFold.IsExpanded = true;
            RevealElement(SelectedWidgetOwnLookFold);
            return;
        }

        SelectNavigationPage(SettingsNavigationRules.ComponentsPageKey);
    }

    private void AppearanceSummaryJump_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }
            || !AppearancePartLookRules.TryParsePartKey(key, out var part))
        {
            return;
        }

        FrameworkElement? target = part switch
        {
            AppearancePartKind.Launcher => LauncherLookSettingsRow,
            AppearancePartKind.Dock => DockLookSettingsRow,
            AppearancePartKind.TopBar when _topBarState is not null => TopBarLookSettingsRow,
            _ => null
        };

        if (target is not null)
        {
            JumpToTarget(target, focus: true);
        }
    }

    private void RestoreAllDesktopCardsToGlobalLook()
    {
        var anyBuiltIn = false;
        foreach (var kind in CurrentComponentKinds.Where(kind =>
                     _desktopExperience.GetComponent(kind).IsVisible && HasOwnLook(kind)).ToList())
        {
            var component = _desktopExperience.GetComponent(kind);
            var look = DesktopStyleRules.ComponentLook(kind, _maintenance.Style, QuickPlacesWindow.ClearBarOpacity);
            component.Appearance.BackgroundColor = look.BackgroundColor;
            component.Appearance.BackgroundOpacity = look.BackgroundOpacity;
            component.Appearance.TextColor = look.TextColor;
            if (kind == DesktopComponentKind.Todo)
            {
                var glass = DesktopStyleRules.Appearance(_maintenance.Style);
                component.Appearance.HeaderColor = glass.HeaderColor;
                component.Appearance.HeaderOpacity = glass.HeaderOpacity;
            }
            anyBuiltIn = true;
        }

        if (anyBuiltIn)
        {
            _applyDesktopExperience(_desktopExperience);
            if (_selectedComponentEntry?.Kind is { } selectedKind
                && !IsSecondaryTodoWindow(_selectedComponentEntry.Window, out _))
            {
                _componentApplyTimer.Stop();
                LoadSelectedComponentEditor(selectedKind, _selectedComponentEntry.Window);
                UpdateOwnLookSummary(selectedKind);
            }
        }

        if (_desktopExperience.GetComponent(DesktopComponentKind.Todo).IsVisible)
        {
            foreach (var todoWindow in _widgetProvider()
                         .OfType<TodoWidgetWindow>()
                         .Where(todo => IsSecondaryTodoWindow(todo, out _)
                             && !DesktopStyleRules.FollowsSharedCardLook(todo.State.Appearance, _maintenance.Style))
                         .ToList())
            {
                ApplySharedCardAppearanceToWindow(todoWindow);
                if (ReferenceEquals(_selectedComponentEntry?.Window, todoWindow))
                {
                    _componentApplyTimer.Stop();
                    LoadSelectedComponentEditor(DesktopComponentKind.Todo, todoWindow);
                    UpdateOwnLookSummary(DesktopComponentKind.Todo);
                }
            }
        }

        foreach (var widget in _widgetProvider()
                     .Where(w => w is FileBoxWindow or NoteWidgetWindow
                         && !DesktopStyleRules.FollowsSharedCardLook(w.WidgetAppearance, _maintenance.Style))
                     .ToList())
        {
            ApplySharedCardAppearanceToWindow(widget);
            if (ReferenceEquals(_selectedComponentEntry?.Window, widget))
            {
                if (SelectedWidgetAppearanceHost.Content is WidgetAppearancePanel panel)
                {
                    panel.RefreshValues();
                }
                UpdateSelectedWidgetOwnLookSummary(widget);
            }
        }

        RefreshAppearanceOverrides();
    }

    private int CountCustomLookDesktopCards()
    {
        var count = 0;
        foreach (var kind in CurrentComponentKinds)
        {
            if (_desktopExperience.GetComponent(kind).IsVisible && HasOwnLook(kind))
            {
                count++;
            }
        }

        if (_desktopExperience.GetComponent(DesktopComponentKind.Todo).IsVisible)
        {
            count += _widgetProvider()
                .OfType<TodoWidgetWindow>()
                .Count(todo => IsSecondaryTodoWindow(todo, out _)
                    && !DesktopStyleRules.FollowsSharedCardLook(todo.State.Appearance, _maintenance.Style));
        }

        count += _widgetProvider()
            .Count(w => w is FileBoxWindow or NoteWidgetWindow
                && !DesktopStyleRules.FollowsSharedCardLook(w.WidgetAppearance, _maintenance.Style));

        return count;
    }

    private (DesktopComponentKind? BuiltInKind, TodoWidgetWindow? SecondaryTodo, WidgetWindowBase? Widget)? FindFirstCustomLookCard()
    {
        foreach (var kind in CurrentComponentKinds)
        {
            if (_desktopExperience.GetComponent(kind).IsVisible && HasOwnLook(kind))
            {
                return (kind, null, null);
            }
        }

        if (_desktopExperience.GetComponent(DesktopComponentKind.Todo).IsVisible)
        {
            var todo = _widgetProvider()
                .OfType<TodoWidgetWindow>()
                .FirstOrDefault(candidate => IsSecondaryTodoWindow(candidate, out _)
                    && !DesktopStyleRules.FollowsSharedCardLook(candidate.State.Appearance, _maintenance.Style));
            if (todo is not null)
            {
                return (null, todo, null);
            }
        }

        var widget = _widgetProvider()
            .FirstOrDefault(candidate => candidate is FileBoxWindow or NoteWidgetWindow
                && !DesktopStyleRules.FollowsSharedCardLook(candidate.WidgetAppearance, _maintenance.Style));
        return widget is not null ? (null, null, widget) : null;
    }

    /// <summary>
    /// Refreshes the four fixed rows in AppearancePage's "各部分外观" section and
    /// synchronizes the one-line appearance summary entries on Launcher, Dock, and TopBar pages.
    /// </summary>
    private void RefreshAppearanceOverrides()
    {
        if (AppearancePartsGroup is null)
        {
            return;
        }

        var customCardCount = CountCustomLookDesktopCards();
        var cardsStatus = AppearancePartLookRules.EvaluateDesktopCardsPart(customCardCount);
        AppearanceCardsPartRow.Description = cardsStatus.StatusText;
        AppearanceCardsRestoreButton.IsEnabled = cardsStatus.CanRestoreFollow;
        AppearanceCardsSummaryRow.Description = AppearancePartLookRules.FormatCardsExpandedSummary(
            DesktopStyleRules.DescribeSharedLookSummary(_maintenance.Style),
            customCardCount);
        AppearanceViewCustomCardsButton.Content = AppearancePartLookRules.FormatViewCardsButtonLabel(customCardCount);

        var launcherFollow = _maintenance.LauncherAppearance.FollowCardLook;
        var launcherStatus = AppearancePartLookRules.EvaluateBinaryPart(AppearancePartKind.Launcher, launcherFollow);
        AppearanceLauncherPartRow.Description = launcherStatus.StatusText;
        AppearanceLauncherRestoreButton.IsEnabled = launcherStatus.CanRestoreFollow;
        if (LauncherLookSummaryRow is not null)
        {
            LauncherLookSummaryRow.Header = AppearancePartLookRules.FormatPageSummary(launcherFollow);
        }

        var dockFollow = _dockState.FollowCardLook;
        var dockStatus = AppearancePartLookRules.EvaluateBinaryPart(AppearancePartKind.Dock, dockFollow);
        AppearanceDockPartRow.Description = dockStatus.StatusText;
        AppearanceDockRestoreButton.IsEnabled = dockStatus.CanRestoreFollow;
        if (DockLookSummaryRow is not null)
        {
            DockLookSummaryRow.Header = AppearancePartLookRules.FormatPageSummary(dockFollow);
        }

        if (_topBarState is { } topBar)
        {
            var topBarStatus = AppearancePartLookRules.EvaluateBinaryPart(AppearancePartKind.TopBar, topBar.FollowCardLook);
            AppearanceTopBarPartRow.Description = topBarStatus.StatusText;
            AppearanceTopBarRestoreButton.IsEnabled = topBarStatus.CanRestoreFollow;
            if (TopBarLookSummaryRow is not null)
            {
                TopBarLookSummaryRow.Header = AppearancePartLookRules.FormatPageSummary(topBar.FollowCardLook);
            }
        }
    }
}
