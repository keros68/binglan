using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using BingLan.Core.Models;

namespace BingLan.App.Windows;

public partial class SettingsWindow
{
    private const string DefaultComponentStatusText = "修改后立即生效";
    private static readonly TimeSpan QuickActionStatusDuration = TimeSpan.FromSeconds(4);

    private DispatcherTimer? _quickActionStatusTimer;
    private string? _activeQuickActionFallbackStatus;

    private void InitializeQuickActions()
    {
        if (SchemeQuickActionsWrapPanel is null)
        {
            return;
        }

        SchemeQuickActionsWrapPanel.Children.Clear();
        var glyphStyle = TryFindResource("ButtonGlyphStyle") as Style;

        foreach (var action in SettingsQuickActions.Items)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var glyph = new TextBlock { Text = action.Glyph };
            if (glyphStyle is not null)
            {
                glyph.Style = glyphStyle;
            }

            content.Children.Add(glyph);
            content.Children.Add(new TextBlock
            {
                Text = action.Label,
                VerticalAlignment = VerticalAlignment.Center
            });

            var button = new Button
            {
                Tag = action.Key,
                Margin = new Thickness(0, 0, 8, 8),
                Content = content
            };
            AutomationProperties.SetName(button, action.AutomationName);
            button.Click += QuickActionButton_Click;
            SchemeQuickActionsWrapPanel.Children.Add(button);
        }
    }

    private void QuickActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key }
            && !ExecuteQuickAction(key, focus: true))
        {
            ShowQuickActionUnavailableStatus();
        }
    }

    internal bool ExecuteQuickAction(string key, bool focus = true)
    {
        var action = SettingsQuickActions.FindByKey(key);
        if (action is null)
        {
            ClearQuickActionFallbackStatus();
            ShowQuickActionUnavailableStatus();
            return false;
        }

        return ExecuteQuickAction(action, focus);
    }

    internal bool ExecuteQuickAction(SettingsQuickAction action, bool focus = true)
    {
        ClearQuickActionFallbackStatus();

        var refreshedComponents = false;
        if (string.Equals(action.ExpectedPageKey, SettingsNavigationRules.ComponentsPageKey, StringComparison.Ordinal))
        {
            RefreshComponentList(_selectedComponentEntry?.Window, _selectedComponentEntry?.Kind);
            refreshedComponents = true;
        }

        var primaryTarget = FindName(action.TargetKey) as FrameworkElement;
        var entry = primaryTarget is not null ? FindSearchEntryForTarget(primaryTarget) : null;
        if (!refreshedComponents && entry is not null && ReferenceEquals(entry.Page, ComponentsPage))
        {
            RefreshComponentList(_selectedComponentEntry?.Window, _selectedComponentEntry?.Kind);
            refreshedComponents = true;
        }

        var isPrimarySearchEntryAvailable = entry is not null && IsSearchEntryAvailable(entry);
        var fallbackTarget = !string.IsNullOrWhiteSpace(action.FallbackTargetKey)
            ? FindName(action.FallbackTargetKey) as FrameworkElement
            : null;
        var isPrimaryDomainAvailable = IsQuickActionPrimaryDomainAvailable(action);

        var resolution = SettingsQuickActions.ResolveExecution(
            action,
            hasPrimaryTarget: primaryTarget is not null,
            hasPrimarySearchEntry: entry is not null,
            isPrimarySearchEntryAvailable: isPrimarySearchEntryAvailable,
            hasFallbackTarget: fallbackTarget is not null,
            isPrimaryDomainAvailable: isPrimaryDomainAvailable);

        switch (resolution.Mode)
        {
            case SettingsQuickActionExecutionMode.OpenPrimarySearchEntry:
                ClearQuickActionStatus();
                OpenSearchResult(entry!, focus);
                return true;

            case SettingsQuickActionExecutionMode.JumpToPrimaryTarget:
                ClearQuickActionStatus();
                JumpToTarget(primaryTarget!, focus);
                return true;

            case SettingsQuickActionExecutionMode.JumpToFallbackTarget:
                ClearQuickActionStatus();
                JumpToTarget(fallbackTarget!, focus);
                if (resolution.ShowFallbackStatus
                    && !string.IsNullOrWhiteSpace(action.FallbackStatusMessage)
                    && ComponentSettingsStatusText is not null
                    && FindOwningPage(fallbackTarget!) is { } owner
                    && ReferenceEquals(owner.Page, ComponentsPage))
                {
                    ComponentSettingsStatusText.Text = action.FallbackStatusMessage;
                    _activeQuickActionFallbackStatus = action.FallbackStatusMessage;
                }
                return true;

            default:
                ShowQuickActionUnavailableStatus();
                return false;
        }
    }

    private bool IsQuickActionPrimaryDomainAvailable(SettingsQuickAction action)
    {
        if (string.Equals(action.Key, SettingsQuickActions.OrganizeDesktopFilesKey, StringComparison.Ordinal)
            || string.Equals(action.ExpectedScope, "FileBox", StringComparison.Ordinal))
        {
            return _widgetProvider().OfType<FileBoxWindow>().Any();
        }

        if (string.Equals(action.ExpectedScope, "Kind:Weather", StringComparison.Ordinal))
        {
            return DesktopComponentList?.Items.OfType<DesktopComponentSettingsEntry>()
                .Any(item => item.Kind == DesktopComponentKind.Weather) == true;
        }

        return true;
    }

    private void ShowQuickActionUnavailableStatus()
    {
        if (SchemeQuickActionsStatusText is null)
        {
            return;
        }

        SchemeQuickActionsStatusText.Text = SettingsQuickActions.UnavailableStatusMessage;
        SchemeQuickActionsStatusText.Visibility = Visibility.Visible;

        if (_quickActionStatusTimer is null)
        {
            _quickActionStatusTimer = new DispatcherTimer(
                QuickActionStatusDuration,
                DispatcherPriority.Background,
                (_, _) =>
                {
                    if (!_isClosed)
                    {
                        ClearQuickActionStatus();
                    }
                },
                Dispatcher);
        }

        _quickActionStatusTimer.Stop();
        _quickActionStatusTimer.Start();
    }

    private void ClearQuickActionStatus()
    {
        _quickActionStatusTimer?.Stop();
        if (SchemeQuickActionsStatusText is null)
        {
            return;
        }

        SchemeQuickActionsStatusText.Text = string.Empty;
        SchemeQuickActionsStatusText.Visibility = Visibility.Collapsed;
    }

    private void ClearQuickActionFallbackStatus()
    {
        if (_activeQuickActionFallbackStatus is null)
        {
            return;
        }

        if (ComponentSettingsStatusText is not null
            && string.Equals(ComponentSettingsStatusText.Text, _activeQuickActionFallbackStatus, StringComparison.Ordinal))
        {
            ComponentSettingsStatusText.Text = DefaultComponentStatusText;
        }

        _activeQuickActionFallbackStatus = null;
    }

    private void StopQuickActionTimers()
    {
        _quickActionStatusTimer?.Stop();
    }
}
