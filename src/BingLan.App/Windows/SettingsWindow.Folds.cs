using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BingLan.Core.Models;

namespace BingLan.App.Windows;

public partial class SettingsWindow
{
    private long _revealGeneration;

    internal long CurrentRevealGeneration => _revealGeneration;

    private long BeginRevealGeneration()
    {
        _revealGeneration = SettingsQuickActions.NextRevealGeneration(_revealGeneration);
        return _revealGeneration;
    }

    private void InvalidatePendingReveal() =>
        _revealGeneration = SettingsQuickActions.NextRevealGeneration(_revealGeneration);
    /// <summary>
    /// Jumps to a setting element: uses the search index entry when available so page,
    /// tab, component selection and folds are all revealed consistently; when no search entry
    /// matches, resolves the owning page/tab/scope first before expanding enclosing folds and
    /// scrolling to the target.
    /// </summary>
    internal void JumpToTarget(FrameworkElement target, bool focus = true)
    {
        var entry = FindSearchEntryForTarget(target);
        if (entry is not null)
        {
            if (ReferenceEquals(entry.Page, ComponentsPage))
            {
                RefreshComponentList(_selectedComponentEntry?.Window, _selectedComponentEntry?.Kind);
            }

            if (IsSearchEntryAvailable(entry))
            {
                OpenSearchResult(entry, focus);
                return;
            }
        }

        EnsureTargetContainerVisible(target);
        ExpandFoldsForElement(target);
        RevealElement(target, focus);
    }

    internal SettingsSearchEntry? FindSearchEntryForTarget(FrameworkElement target)
    {
        foreach (var entry in SearchIndex)
        {
            if (ReferenceEquals(entry.Target, target))
            {
                return entry;
            }
        }

        for (var node = LogicalTreeHelper.GetParent(target); node is not null; node = LogicalTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement ancestor)
            {
                foreach (var entry in SearchIndex)
                {
                    if (ReferenceEquals(entry.Target, ancestor))
                    {
                        return entry;
                    }
                }
            }
        }

        return null;
    }

    private (FrameworkElement Page, string Tag, string Title)? FindOwningPage(DependencyObject target)
    {
        for (DependencyObject? node = target; node is not null; node = LogicalTreeHelper.GetParent(node))
        {
            foreach (var page in SearchPages)
            {
                if (ReferenceEquals(page.Page, node))
                {
                    return page;
                }
            }
        }

        return null;
    }

    private void EnsureTargetContainerVisible(FrameworkElement target)
    {
        string? scope = null;
        (FrameworkElement Page, string Tag, string Title)? owningPage = null;
        for (DependencyObject? node = target; node is not null; node = LogicalTreeHelper.GetParent(node))
        {
            if (scope is null && SettingsSearch.GetScope(node) is { Length: > 0 } foundScope)
            {
                scope = foundScope;
            }

            foreach (var page in SearchPages)
            {
                if (ReferenceEquals(page.Page, node))
                {
                    owningPage = page;
                    break;
                }
            }

            if (owningPage is not null)
            {
                break;
            }
        }

        if (owningPage is not { } owner)
        {
            return;
        }

        SettingsSearchPopup.IsOpen = false;
        SelectNavigationPage(owner.Tag);
        if (scope is not null)
        {
            SelectSearchScope(scope, target);
        }
    }

    private void ScheduleRevealElement(FrameworkElement target, bool focus, long generation, int remainingRetries)
    {
        var priority = remainingRetries == SettingsQuickActions.MaxRevealRetries
            ? DispatcherPriority.Loaded
            : DispatcherPriority.Background;
        Dispatcher.BeginInvoke(priority, () =>
        {
            if (_isClosed || !SettingsQuickActions.IsCurrentRevealGeneration(generation, _revealGeneration))
            {
                return;
            }

            UpdateLayout();
            switch (SettingsQuickActions.EvaluateRevealAttempt(
                        generation,
                        _revealGeneration,
                        _isClosed,
                        target.IsVisible,
                        remainingRetries))
            {
                case SettingsRevealAction.Complete:
                    CompleteRevealElement(target, focus);
                    break;

                case SettingsRevealAction.Retry:
                    ExpandFoldsForElement(target);
                    ScheduleRevealElement(target, focus, generation, remainingRetries - 1);
                    break;
            }
        });
    }

    private void ExpandFoldsForElement(DependencyObject target)
    {
        var ancestorFolds = new List<Expander>();
        AppearancePartKind? targetAccordionPart = null;
        for (var node = target; node is not null; node = LogicalTreeHelper.GetParent(node))
        {
            if (targetAccordionPart is null
                && SettingsSearch.GetAccordionPart(node) is { Length: > 0 } partKey
                && AppearancePartLookRules.TryParsePartKey(partKey, out var parsedPart))
            {
                targetAccordionPart = parsedPart;
            }

            if (!ReferenceEquals(node, target) && node is Expander fold)
            {
                ancestorFolds.Add(fold);
            }
        }

        var targetName = (target as FrameworkElement)?.Name;
        targetAccordionPart ??= AppearancePartLookRules.ResolvePartForControl(targetName);
        if (targetAccordionPart is { } accordionPart)
        {
            ExpandAppearancePart(AppearancePartLookRules.ExpandAccordion(_expandedAppearancePart, accordionPart));
        }

        ancestorFolds.Reverse();
        foreach (var fold in ancestorFolds)
        {
            fold.IsExpanded = true;
        }

        if (target is Expander targetFold)
        {
            targetFold.IsExpanded = true;
        }

        var foldKeys = SettingsFoldRules.ResolveFoldsToExpand(
            targetName,
            ancestorFolds.Select(fold => fold.Name).Where(name => !string.IsNullOrEmpty(name)),
            target is Expander);

        foreach (var foldKey in foldKeys)
        {
            if (FindName(foldKey) is Expander mappedFold)
            {
                mappedFold.IsExpanded = true;
            }
        }
    }

    private void ExpandFoldForSection(string? section)
    {
        if (SettingsFoldRules.ResolveFoldForSection(section) is { } foldKey
            && FindName(foldKey) is Expander fold)
        {
            fold.IsExpanded = true;
        }
    }

    private void ResetComponentMoreFolds()
    {
        if (ComponentMoreFold is not null)
        {
            ComponentMoreFold.IsExpanded = false;
        }

        if (SelectedWidgetMoreFold is not null)
        {
            SelectedWidgetMoreFold.IsExpanded = false;
        }
    }
}
