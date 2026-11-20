using System.Windows;
using System.Windows.Media.Animation;
using BingLan.Core.Models;

namespace BingLan.App.Windows;

public partial class LauncherPanelWindow
{
    private bool _panelFadingOut;
    private bool _panelVisualClosed;

    private void InitializePanelAppearance()
    {
        PreparePanelAppearance();
        Loaded += (_, _) => AnimatePanelOpen();
        Closed += (_, _) =>
        {
            _panelVisualClosed = true;
            BeginAnimation(OpacityProperty, null);
            _glass.TrackAnimation(false);
        };
    }

    private void PreparePanelAppearance()
    {
        if (!IsLoaded)
        {
            Opacity = PanelAnimationDuration(opening: true) > TimeSpan.Zero ? 0d : 1d;
        }
    }

    private void ApplyPanelMaterial() => _glass.SetClearMode(_appearance.UseClearGlass);

    private void AnimatePanelOpen()
    {
        if (_panelVisualClosed || _panelFadingOut) return;
        AnimatePanelOpacity(1d, PanelAnimationDuration(opening: true), () => { });
    }

    private void AnimatePanelDismiss(Action finish)
    {
        if (_panelVisualClosed || _panelFadingOut) return;
        _panelFadingOut = true;
        PanelSurface.IsHitTestVisible = false;
        AnimatePanelOpacity(0d, PanelAnimationDuration(opening: false), finish);
    }

    private void AnimatePanelOpacity(double target, TimeSpan duration, Action completed)
    {
        var current = Opacity;
        BeginAnimation(OpacityProperty, null);
        Opacity = current;
        if (duration <= TimeSpan.Zero || !IsVisible || current == target)
        {
            Opacity = target;
            _glass.TrackAnimation(false);
            completed();
            return;
        }

        _glass.TrackAnimation(true);
        var animation = new DoubleAnimation(current, target, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (_panelVisualClosed) return;
            BeginAnimation(OpacityProperty, null);
            Opacity = target;
            _glass.TrackAnimation(false);
            completed();
        };
        BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static TimeSpan PanelAnimationDuration(bool opening)
    {
        if (!SystemParameters.ClientAreaAnimation || IceBlueDockWindow.Motion == MotionLevel.Off)
        {
            return TimeSpan.Zero;
        }
        if (IceBlueDockWindow.Motion == MotionLevel.Reduced)
        {
            return TimeSpan.FromMilliseconds(opening ? 80 : 60);
        }
        return TimeSpan.FromMilliseconds(opening ? 150 : 100);
    }
}
