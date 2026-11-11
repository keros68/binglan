using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using Point = System.Windows.Point;

namespace BingLan.App.Dock;

internal enum DockMorphKind
{
    None,
    Expand,
    Collapse,
    Reappear
}

/// <summary>
/// The morph animations of the dock surface: the row shrinks into the floating
/// handle when smart-hidden and grows back out of it on reveal, plus the reappear
/// used when the dock returns at its strip — a fade with a slight grow from the
/// row's own bottom edge. Every Begin call retargets from the current visual state,
/// so an interrupted morph reverses smoothly instead of restarting. The caller
/// decides whether animation is allowed at all; this class only runs what it is
/// asked to run.
/// </summary>
internal sealed class DockMorphAnimator
{
    private readonly Border _surface;
    private readonly ScaleTransform _scale = new(1, 1);
    private DockMorphKind _active;
    // Replacing or removing an animation clock still raises its Completed, so every
    // completion is tagged with the flight it belongs to; stale ones are ignored.
    private int _flight;

    internal DockMorphAnimator(Border surface)
    {
        _surface = surface;
        surface.RenderTransform = _scale;
    }

    /// <summary>Raised once when a collapse flight finishes; the owner hides the window then.</summary>
    internal event Action? CollapseCompleted;
    internal event Action? StateChanged;

    internal DockMorphKind ActiveKind => _active;

    internal bool IsCollapsing => _active == DockMorphKind.Collapse;

    /// <summary>
    /// Grows the row out of the handle. A fresh expand starts at the morph scale with
    /// the centre at the handle; reversing a running collapse keeps its current size
    /// and just animates back to full.
    /// </summary>
    internal void BeginExpand(Point center, MotionLevel motion)
    {
        _active = DockMorphKind.Expand;
        ApplyCenter(center);
        // The base values are the fresh-start state; a replaced animation snapshots
        // the current size instead, which is exactly the interruption behaviour.
        _scale.ScaleX = DockMorphMotion.MorphScale(motion);
        _scale.ScaleY = DockMorphMotion.MorphScale(motion);
        _surface.Opacity = 0d;
        var duration = new Duration(DockMorphMotion.ExpandDuration(motion));
        var flight = ++_flight;
        var scaleX = ToOne(duration, EasingMode.EaseOut);
        scaleX.Completed += (_, _) => OnExpandCompleted(flight);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, ToOne(duration, EasingMode.EaseOut));
        _surface.BeginAnimation(UIElement.OpacityProperty, ToOne(duration, EasingMode.EaseOut));
        StateChanged?.Invoke();
    }

    /// <summary>Shrinks the row into the handle; <see cref="CollapseCompleted"/> fires at the end.</summary>
    internal void BeginCollapse(Point center, MotionLevel motion)
    {
        _active = DockMorphKind.Collapse;
        ApplyCenter(center);
        var duration = new Duration(DockMorphMotion.CollapseDuration(motion));
        var flight = ++_flight;
        var scaleX = new DoubleAnimation(DockMorphMotion.MorphScale(motion), duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        scaleX.Completed += (_, _) => OnCollapseCompleted(flight);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(
            DockMorphMotion.MorphScale(motion), duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        });
        _surface.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0d, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        });
        StateChanged?.Invoke();
    }

    /// <summary>
    /// A short fade with a slight shrink toward the row's bottom edge, used when the
    /// dock steps aside for a system surface; <see cref="CollapseCompleted"/> fires at
    /// the end like a collapse, and an expand reverses it from wherever it reached.
    /// </summary>
    internal void BeginYield(Point center, MotionLevel motion)
    {
        _active = DockMorphKind.Collapse;
        ApplyCenter(center);
        var duration = new Duration(DockMorphMotion.YieldDuration(motion));
        var flight = ++_flight;
        var shrink = DockMorphMotion.ReappearScale(motion);
        var fade = new DoubleAnimation(0d, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => OnCollapseCompleted(flight);
        _surface.BeginAnimation(UIElement.OpacityProperty, fade);
        if (shrink != 1d)
        {
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(shrink, duration));
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(shrink, duration));
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// The reappear-at-strip transition: fade plus a slight grow about the row's own
    /// bottom edge, so it reads as emerging in place instead of popping in. Reduced
    /// keeps a pure fade (scale target 1).
    /// </summary>
    internal void BeginReappear(Point center, MotionLevel motion)
    {
        _active = DockMorphKind.Reappear;
        _surface.Opacity = 0d;
        var grow = DockMorphMotion.ReappearScale(motion);
        var duration = new Duration(DockMorphMotion.ReappearDuration(motion));
        var flight = ++_flight;
        var fade = ToOne(duration, EasingMode.EaseOut);
        fade.Completed += (_, _) => OnExpandCompleted(flight);
        _surface.BeginAnimation(UIElement.OpacityProperty, fade);
        StateChanged?.Invoke();
        if (grow == 1d)
        {
            return;
        }

        ApplyCenter(center);
        _scale.ScaleX = grow;
        _scale.ScaleY = grow;
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, ToOne(duration, EasingMode.EaseOut));
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, ToOne(duration, EasingMode.EaseOut));
    }

    /// <summary>
    /// Stops every flight and restores the resting surface at once. The pending
    /// collapse completion is cancelled with it: the caller owns the window state.
    /// </summary>
    internal void Snap()
    {
        _active = DockMorphKind.None;
        _flight++;
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _surface.BeginAnimation(UIElement.OpacityProperty, null);
        _scale.ScaleX = 1d;
        _scale.ScaleY = 1d;
        _surface.Opacity = 1d;
        StateChanged?.Invoke();
    }

    private static DoubleAnimation ToOne(Duration duration, EasingMode easing) => new(1d, duration)
    {
        EasingFunction = new QuadraticEase { EasingMode = easing }
    };

    private void ApplyCenter(Point center)
    {
        _scale.CenterX = center.X;
        _scale.CenterY = center.Y;
    }

    private void OnExpandCompleted(int flight)
    {
        if (flight != _flight)
        {
            return;
        }

        // Rest at full size: the end values equal the base we restore to, so this is
        // invisible, but it clears the held animation clocks and the shrunken base
        // values a fresh expand had set.
        Snap();
    }

    private void OnCollapseCompleted(int flight)
    {
        if (flight != _flight)
        {
            return;
        }

        // Retire the flight first: removing the clocks below can itself raise a
        // stale Completed, which must not raise CollapseCompleted twice.
        _flight++;
        _active = DockMorphKind.None;
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _surface.BeginAnimation(UIElement.OpacityProperty, null);
        _scale.ScaleX = 1d;
        _scale.ScaleY = 1d;
        _surface.Opacity = 1d;
        StateChanged?.Invoke();
        CollapseCompleted?.Invoke();
    }
}
