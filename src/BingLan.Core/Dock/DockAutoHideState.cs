namespace BingLan.Core.Dock;

public readonly record struct DockAutoHideInput(
    bool WindowOverlapsDock,
    bool ForegroundIsFullScreen,
    bool PointerInRevealZone,
    bool PointerOverDock,
    bool InteractionActive,
    bool OverlapIsMaximizedWindow);

/// <summary>
/// Smart-hide decisions for the floating dock: it stays visible until a window
/// needs the space it covers, reappears when the pointer reaches the screen edge,
/// and never appears over a full-screen foreground window, even mid-interaction.
/// A maximized window covering the dock hides it at once — that overlap will not
/// clear on its own, so idling through the debounce delay only reads as a pause.
/// </summary>
public sealed class DockAutoHideState
{
    public static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// Brief window after the pointer hovers or clicks the corner handle so the user can
    /// travel from the corner handle to the centred dock without a maximized foreground
    /// window collapsing the dock mid-way.
    /// </summary>
    public static readonly TimeSpan HandleRevealGrace = TimeSpan.FromSeconds(1);

    private DateTimeOffset? _hideRequestedAt;

    /// <summary>
    /// Updates the deadline that keeps a handle-revealed dock visible while the pointer
    /// moves from the handle toward the centred dock strip.
    /// </summary>
    public static DateTimeOffset UpdateHandleRevealDeadline(
        DateTimeOffset currentDeadline,
        DateTimeOffset now,
        bool revealedFromHandle,
        bool pointerOverStrip,
        bool clearRequested)
    {
        if (clearRequested || pointerOverStrip)
        {
            return DateTimeOffset.MinValue;
        }

        return revealedFromHandle ? now + HandleRevealGrace : currentDeadline;
    }

    /// <summary>
    /// The overlap the state machine sees: with the dock set to stay tucked away it always
    /// counts as covered, so only the pointer or an interaction brings it back.
    /// </summary>
    public static bool TreatAsOverlapping(bool autoHideAlways, bool windowOverlapsDock) =>
        autoHideAlways || windowOverlapsDock;

    public bool IsShown { get; private set; } = true;

    public bool Update(DockAutoHideInput input, DateTimeOffset now)
    {
        if (input.ForegroundIsFullScreen)
        {
            return Hide();
        }

        if (input.InteractionActive
            || input.PointerOverDock || input.PointerInRevealZone || !input.WindowOverlapsDock)
        {
            return Show();
        }

        if (!IsShown)
        {
            return false;
        }

        if (input.OverlapIsMaximizedWindow)
        {
            return Hide();
        }

        _hideRequestedAt ??= now;
        return now - _hideRequestedAt.Value >= HideDelay && Hide();
    }

    private bool Show()
    {
        _hideRequestedAt = null;
        if (IsShown)
        {
            return false;
        }

        IsShown = true;
        return true;
    }

    private bool Hide()
    {
        _hideRequestedAt = null;
        if (!IsShown)
        {
            return false;
        }

        IsShown = false;
        return true;
    }
}
