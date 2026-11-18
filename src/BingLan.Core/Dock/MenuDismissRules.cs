namespace BingLan.Core.Dock;

/// <summary>
/// Tracks dismissal of a non-activating popup menu opened from the dock or top bar.
/// Because the popup stays non-activating (<c>StaysOpen = true</c>), neither touchpad/stylus
/// trailing button releases nor foreground-activation bounces can collapse it prematurely;
/// instead it arms once the opening press has fully released and closes on an outside click,
/// Escape, a system shell surface taking the foreground, or switching to another window.
/// </summary>
public sealed class MenuDismissState
{
    private nint _initialForeground;
    private bool _mouseArmed;
    private bool _escapeArmed;

    public bool IsMouseArmed => _mouseArmed;

    public bool IsEscapeArmed => _escapeArmed;

    public void Start(nint initialForeground, bool anyMouseCurrentlyHeld, bool escapeCurrentlyHeld)
    {
        _initialForeground = initialForeground;
        _mouseArmed = !anyMouseCurrentlyHeld;
        _escapeArmed = !escapeCurrentlyHeld;
    }

    public bool ShouldCloseOnPoll(
        bool anyMouseCurrentlyHeld,
        bool anyMousePressedLatch,
        bool escapeCurrentlyHeld,
        bool escapePressedLatch,
        bool pointerInsideMenu,
        nint currentForeground,
        nint menuPopupHandle,
        bool systemMenuForeground,
        out bool newlyArmedMouse,
        out bool newlyArmedEscape)
    {
        newlyArmedMouse = false;
        newlyArmedEscape = false;

        if (systemMenuForeground)
        {
            return true;
        }

        if (!_escapeArmed)
        {
            if (!escapeCurrentlyHeld)
            {
                _escapeArmed = true;
                newlyArmedEscape = true;
            }
        }
        else if (escapeCurrentlyHeld || escapePressedLatch)
        {
            return true;
        }

        if (!_mouseArmed)
        {
            if (!anyMouseCurrentlyHeld)
            {
                _mouseArmed = true;
                newlyArmedMouse = true;
            }
        }
        else if ((anyMouseCurrentlyHeld || anyMousePressedLatch) && !pointerInsideMenu)
        {
            return true;
        }

        if (currentForeground != 0
            && currentForeground != menuPopupHandle
            && currentForeground != _initialForeground)
        {
            if (!pointerInsideMenu)
            {
                return true;
            }
            _initialForeground = currentForeground;
        }

        return false;
    }
}
