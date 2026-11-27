using System.Windows;
using System.Windows.Media;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.App.Windows;

public partial class FileBoxWindow
{
    private bool ExpandsUpward => State.ExpandUpward && !IsEdgeDocked;

    public void SetExpandUpward(bool enabled)
    {
        if (State.ExpandUpward == enabled)
        {
            return;
        }

        if (_foldAnimator.IsAnimating)
        {
            SetCollapsed(State.IsCollapsed);
        }
        State.ExpandUpward = enabled;
        RefreshExpansionLayout();
        SyncGroupSharedProperties();
        NotifyStateChanged();
    }

    private void RefreshExpansionLayout()
    {
        var upward = ExpandsUpward;
        BoxLayout.RowDefinitions[0].Height = upward ? new GridLength(1, GridUnitType.Star) : new GridLength(CollapsedHeight);
        BoxLayout.RowDefinitions[1].Height = upward ? new GridLength(CollapsedHeight) : new GridLength(1, GridUnitType.Star);
        System.Windows.Controls.Grid.SetRow(TitleBarSurface, upward ? 1 : 0);
        System.Windows.Controls.Grid.SetRow(ExpandedBodyHost, upward ? 0 : 1);
        TitleDivider.VerticalAlignment = upward ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        RefreshCollapseButton();
    }

    private void RefreshCollapseButton()
    {
        CollapseButton.Content = State.IsCollapsed == ExpandsUpward ? "\uE70E" : "\uE70D";
        CollapseButton.ToolTip = State.IsCollapsed ? "展开文件收纳盒" : "折叠成标题条";
    }

    private void CaptureCollapsedExpansionPlacement()
    {
        if (ExpandsUpward && _foldAnimator.IsAnimating)
        {
            State.Placement.Top += Math.Max(0d, State.Placement.Height - CollapsedHeight);
            if (CurrentExpansionBounds() is { } bounds)
            {
                WindowScreenRecovery.RememberLayout(this, State.Placement,
                    FileBoxExpansionRules.ResolveUpwardCollapsedBounds(bounds, VisualTreeHelper.GetDpi(this).DpiScaleY));
            }
        }
    }

    private UIElement[] FoldBodyElements() =>
        [ExpandedBodyHost, TitleDivider];

    /// <summary>
    /// Folds the box up to its title bar, or unfolds it to the height it had. A folded box
    /// keeps its width and can still be dragged; only its height is fixed.
    /// </summary>
    internal void SetCollapsed(bool collapsed, bool animate = false)
    {
        var wasCollapsed = State.IsCollapsed;
        var wasAnimating = _foldAnimator.IsAnimating;
        var upward = ExpandsUpward;
        var startingBounds = CurrentExpansionBounds();
        var startingTop = Top;
        var startingHeight = Height;
        if (collapsed && !State.IsCollapsed)
        {
            if (!_expandedHeightTemporarilyClamped || State.ExpandedHeight <= 0)
            {
                State.ExpandedHeight = Height;
            }
            _expandedHeightTemporarilyClamped = false;
        }
        if (collapsed)
        {
            _pinnedOpenForSession = false;
        }
        else
        {
            _suppressHoverExpandUntilLeave = false;
        }
        State.IsCollapsed = collapsed;
        RefreshExpansionLayout();
        AddButton.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SortButton.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedSummaryText.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        RefreshPinOpenButton();
        RefreshCollapsedSummary();
        SyncGroupSharedProperties();

        var bodyElements = FoldBodyElements();
        if (collapsed)
        {
            TabStripHost.Visibility = Visibility.Collapsed;
            FileBoxMotionOverlay.Children.Clear();
            void CommitCollapsedLayout()
            {
                foreach (var element in bodyElements)
                {
                    element.Visibility = Visibility.Collapsed;
                }
                MinHeight = CollapsedHeight;
                MaxHeight = CollapsedHeight;
                Height = CollapsedHeight;
                if (upward && (!wasCollapsed || wasAnimating))
                {
                    if (startingBounds is { } bounds)
                    {
                        UpdateEdgeDockPosition(FileBoxExpansionRules.ResolveUpwardCollapsedBounds(
                            bounds, VisualTreeHelper.GetDpi(this).DpiScaleY));
                    }
                    else
                    {
                        Top = startingTop + Math.Max(0d, startingHeight - CollapsedHeight);
                    }
                }
            }

            if (animate && IsLoaded && CardMotionAnimator.IsAnimationEnabled)
            {
                _foldAnimator.BeginCollapse(CollapsedHeight, bodyElements, CommitCollapsedLayout, upward);
            }
            else
            {
                _foldAnimator.Snap();
                CommitCollapsedLayout();
            }
        }
        else
        {
            foreach (var element in bodyElements)
            {
                element.Visibility = Visibility.Visible;
            }
            MaxHeight = double.PositiveInfinity;
            MinHeight = DefaultMinHeight;
            var desiredHeight = Math.Max(DefaultMinHeight, State.ExpandedHeight > 0 ? State.ExpandedHeight : 306d);
            var upwardBounds = upward && startingBounds is { } bounds && ExpansionWorkArea(bounds) is { } work
                ? FileBoxExpansionRules.ResolveUpwardExpandedBounds(
                    bounds, work, desiredHeight, VisualTreeHelper.GetDpi(this).DpiScaleY)
                : (PixelRect?)null;
            var clampedHeight = upwardBounds is { } target
                ? target.Height / VisualTreeHelper.GetDpi(this).DpiScaleY
                : upward ? desiredHeight : ClampExpandedHeightToWorkArea(desiredHeight);
            _expandedHeightTemporarilyClamped = clampedHeight < desiredHeight - 0.5d;
            Height = clampedHeight;
            if (upwardBounds is { } expandedBounds)
            {
                UpdateEdgeDockPosition(expandedBounds);
            }
            else if (upward)
            {
                Top = startingTop + startingHeight - clampedHeight;
            }
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded, EnsureFullyOnScreen);

            if (animate && IsLoaded && CardMotionAnimator.IsAnimationEnabled)
            {
                _foldAnimator.BeginExpand(CollapsedHeight, Height, bodyElements, upward);
            }
            else
            {
                _foldAnimator.Snap();
            }
        }
        RefreshTabStripLayout();
        RefreshAutoCollapseTimer();
        RefreshHoverExpandTimer();
        RefreshHoverRevealState();
    }

    private PixelRect? CurrentExpansionBounds() =>
        IsLoaded && WindowHandle is var handle && handle != 0 && DockNativeMethods.GetWindowRect(handle, out var native)
            ? new PixelRect(native.Left, native.Top, native.Right, native.Bottom)
            : null;

    private static PixelRect? ExpansionWorkArea(PixelRect bounds)
    {
        var monitors = BingLan.App.Dock.MonitorCatalog.GetAll();
        var centerX = (bounds.Left + bounds.Right) / 2;
        var centerY = (bounds.Top + bounds.Bottom) / 2;
        var monitor = monitors.FirstOrDefault(candidate =>
                centerX >= candidate.Bounds.Left && centerX < candidate.Bounds.Right
                && centerY >= candidate.Bounds.Top && centerY < candidate.Bounds.Bottom)
            ?? monitors.FirstOrDefault(candidate => candidate.IsPrimary)
            ?? monitors.FirstOrDefault();
        return monitor is not null && monitor.WorkingArea.HasArea
            ? TaskbarEdgeGuard.AvoidNativeTaskbar(monitor.WorkingArea, monitor.Bounds)
            : null;
    }

    private double ClampExpandedHeightToWorkArea(double desiredHeight)
    {
        if (CurrentExpansionBounds() is not { } bounds || ExpansionWorkArea(bounds) is not { } work)
        {
            return desiredHeight;
        }

        var scaleY = VisualTreeHelper.GetDpi(this).DpiScaleY;
        if (scaleY <= 0d)
        {
            return desiredHeight;
        }

        var availableDip = (work.Bottom - bounds.Top) / scaleY;
        if (availableDip >= DefaultMinHeight && desiredHeight > availableDip)
        {
            return Math.Max(DefaultMinHeight, Math.Floor(availableDip));
        }

        return desiredHeight;
    }
}
