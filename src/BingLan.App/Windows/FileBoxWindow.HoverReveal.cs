using System.Windows;
using System.Windows.Media.Animation;
using BingLan.App.Services;
using BingLan.Core.Models;

namespace BingLan.App.Windows;

public partial class FileBoxWindow
{
    private readonly System.Windows.Threading.DispatcherTimer _idleHideTimer = new()
    {
        Interval = TimeSpan.FromSeconds(FileBoxHoverRevealRules.DefaultHideDelaySeconds)
    };
    private bool _isIdleHiddenVisual;
    private bool _isPeekRevealed;
    private bool _isSettingsRevealed;
    private bool _isMergeHighlightVisible;

    internal bool IsIdleHiddenVisual => _isIdleHiddenVisual;

    internal bool IsIdleHideTimerRunning => _idleHideTimer.IsEnabled;

    private void InitializeHoverReveal()
    {
        _idleHideTimer.Tick += (_, _) => OnIdleHideTimerTick();
        IsKeyboardFocusWithinChanged += (_, _) => RefreshHoverRevealState();
        ApplyHoverRevealVisual(animate: false);
    }

    private void DisposeHoverReveal()
    {
        _idleHideTimer.Stop();
    }

    protected override void ApplyEffectiveCardShadow(CardShadow shadow)
    {
        if (BodySurface is null)
        {
            return;
        }

        // Route the card shadow onto BodySurface rather than the window root so fading BodySurface
        // to opacity 0 while idle leaves no lingering shadow halo over the #01FFFFFF hit surface.
        BodySurface.Effect = AccessibilityThemeManager.IsHighContrastEnabled
            ? null
            : CreateCardShadowEffect(shadow);
    }

    protected override void OnSelectionChanged(bool isSelected)
    {
        base.OnSelectionChanged(isSelected);
        RefreshHoverRevealState();
    }

    protected override void OnAppearanceRefreshed()
    {
        base.OnAppearanceRefreshed();
        SyncGroupSharedProperties();
        RefreshHoverRevealState();
    }

    public void SetHideWhenIdle(bool enabled)
    {
        if (State.HideWhenIdle == enabled)
        {
            RefreshHoverRevealState();
            return;
        }

        State.HideWhenIdle = enabled;
        SyncGroupSharedProperties();
        ApplyHoverRevealVisual(animate: IsLoaded);
        NotifyStateChanged();
    }

    public void SetIdleHideDelay(double seconds)
    {
        var coerced = FileBoxHoverRevealRules.CoerceDelaySeconds(seconds);
        if (State.IdleHideDelaySeconds is { } current && Math.Abs(current - coerced) < 0.001d)
        {
            return;
        }

        State.IdleHideDelaySeconds = coerced;
        SyncGroupSharedProperties();
        RefreshHoverRevealState();
        NotifyStateChanged();
    }

    internal void SetPeekRevealed(bool revealed)
    {
        if (_isPeekRevealed == revealed)
        {
            return;
        }

        _isPeekRevealed = revealed;
        RefreshHoverRevealState();
    }

    internal void SetSettingsRevealed(bool revealed)
    {
        if (_isSettingsRevealed == revealed)
        {
            return;
        }

        _isSettingsRevealed = revealed;
        RefreshHoverRevealState();
    }

    internal void TriggerIdleHideTickForTesting() => OnIdleHideTimerTick();

    internal void RefreshHoverRevealState()
    {
        if (BodySurface is null || IdleHitSurface is null)
        {
            return;
        }

        var useHitSurface = State.HideWhenIdle && !IsEdgeDocked;
        IdleHitSurface.Visibility = useHitSurface ? Visibility.Visible : Visibility.Collapsed;

        var interactionOpen = _modalDialogDepth > 0
            || _isTileOrFileDragActive
            || _externalDragOver
            || _isMergeHighlightVisible
            || HasOpenContextMenu();

        var stayVisible = FileBoxHoverRevealRules.ShouldStayVisible(
            State.HideWhenIdle,
            IsEdgeDocked,
            AccessibilityThemeManager.IsHighContrastEnabled,
            IsMouseOver,
            IsTitleEditing,
            interactionOpen,
            IsDraggingOrResizing,
            IsWidgetSelected,
            IsKeyboardFocusWithin,
            _isPeekRevealed,
            _isSettingsRevealed);

        if (stayVisible)
        {
            _idleHideTimer.Stop();
            if (_isIdleHiddenVisual)
            {
                SetIdleHiddenVisual(hidden: false, animate: IsLoaded);
            }
            return;
        }

        if (_isIdleHiddenVisual)
        {
            _idleHideTimer.Stop();
            return;
        }

        _idleHideTimer.Stop();
        _idleHideTimer.Interval = FileBoxHoverRevealRules.ResolveDelay(State.IdleHideDelaySeconds);
        _idleHideTimer.Start();
    }

    private void ApplyHoverRevealVisual(bool animate)
    {
        if (BodySurface is null || IdleHitSurface is null)
        {
            return;
        }

        var useHitSurface = State.HideWhenIdle && !IsEdgeDocked;
        IdleHitSurface.Visibility = useHitSurface ? Visibility.Visible : Visibility.Collapsed;

        if (!useHitSurface)
        {
            _idleHideTimer.Stop();
            SetIdleHiddenVisual(hidden: false, animate: animate);
            return;
        }

        var interactionOpen = _modalDialogDepth > 0
            || _isTileOrFileDragActive
            || _externalDragOver
            || _isMergeHighlightVisible
            || HasOpenContextMenu();

        var stayVisible = FileBoxHoverRevealRules.ShouldStayVisible(
            State.HideWhenIdle,
            IsEdgeDocked,
            AccessibilityThemeManager.IsHighContrastEnabled,
            IsMouseOver,
            IsTitleEditing,
            interactionOpen,
            IsDraggingOrResizing,
            IsWidgetSelected,
            IsKeyboardFocusWithin,
            _isPeekRevealed,
            _isSettingsRevealed);

        if (stayVisible)
        {
            _idleHideTimer.Stop();
            SetIdleHiddenVisual(hidden: false, animate: animate);
        }
        else if (!animate)
        {
            _idleHideTimer.Stop();
            SetIdleHiddenVisual(hidden: true, animate: false);
        }
        else
        {
            RefreshHoverRevealState();
        }
    }

    private void OnIdleHideTimerTick()
    {
        _idleHideTimer.Stop();
        var interactionOpen = _modalDialogDepth > 0
            || _isTileOrFileDragActive
            || _externalDragOver
            || _isMergeHighlightVisible
            || HasOpenContextMenu();

        if (!FileBoxHoverRevealRules.ShouldRunHideTimer(
                State.HideWhenIdle,
                IsEdgeDocked,
                AccessibilityThemeManager.IsHighContrastEnabled,
                IsMouseOver,
                IsTitleEditing,
                interactionOpen,
                IsDraggingOrResizing,
                IsWidgetSelected,
                IsKeyboardFocusWithin,
                _isPeekRevealed,
                _isSettingsRevealed))
        {
            return;
        }

        SetIdleHiddenVisual(hidden: true, animate: IsLoaded);
    }

    private void SetIdleHiddenVisual(bool hidden, bool animate)
    {
        _isIdleHiddenVisual = hidden;
        if (IsEdgeDocked && !IsEdgeDockExpanded)
        {
            return;
        }

        var targetOpacity = hidden ? 0d : 1d;
        if (!animate || !CardMotionAnimator.IsAnimationEnabled)
        {
            BodySurface.BeginAnimation(OpacityProperty, null);
            BodySurface.Opacity = targetOpacity;
            return;
        }

        var durationMs = hidden
            ? FileBoxHoverRevealRules.HideAnimationMilliseconds
            : FileBoxHoverRevealRules.RevealAnimationMilliseconds;
        var animation = new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BodySurface.BeginAnimation(OpacityProperty, animation);
    }
}
