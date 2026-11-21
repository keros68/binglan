namespace BingLan.Core.Dock;

public enum AppBarMessage
{
    Register,
    Unregister
}

public sealed class AppBarReservationState
{
    public bool IsRegistered { get; private set; }

    public IReadOnlyList<AppBarMessage> Apply(bool reserveWorkArea, bool targetChanged = false)
    {
        if (reserveWorkArea && !IsRegistered)
        {
            IsRegistered = true;
            return [AppBarMessage.Register];
        }

        if (reserveWorkArea && IsRegistered && targetChanged)
        {
            return [AppBarMessage.Unregister, AppBarMessage.Register];
        }

        if (!reserveWorkArea && IsRegistered)
        {
            IsRegistered = false;
            return [AppBarMessage.Unregister];
        }

        return [];
    }

    /// <summary>
    /// Verifies that the strip Explorer returned actually sits inside the target monitor
    /// with at least the requested thickness (for instance, a shared interior edge in a
    /// vertically stacked dual-monitor layout can collapse the strip to zero height).
    /// </summary>
    public static bool IsValidStrip(PixelRect strip, PixelRect monitorBounds, int minimumThickness) =>
        monitorBounds.HasArea
        && strip.HasArea
        && strip.Height >= Math.Min(Math.Max(1, minimumThickness), monitorBounds.Height)
        && strip.Left >= monitorBounds.Left
        && strip.Right <= monitorBounds.Right
        && strip.Top >= monitorBounds.Top
        && strip.Bottom <= monitorBounds.Bottom;

    public static bool HasMonitorTargetChanged(
        string? previousDeviceName,
        PixelRect previousBounds,
        uint previousDpi,
        string currentDeviceName,
        PixelRect currentBounds,
        uint currentDpi) =>
        previousDeviceName is not null
        && (!string.Equals(previousDeviceName, currentDeviceName, StringComparison.OrdinalIgnoreCase)
            || previousBounds != currentBounds
            || previousDpi != currentDpi);

    public IReadOnlyList<AppBarMessage> Release()
    {
        if (!IsRegistered)
        {
            return [];
        }

        IsRegistered = false;
        return [AppBarMessage.Unregister];
    }

    public void ResetRegistration() => IsRegistered = false;

    public void MarkRegisterFailed() => IsRegistered = false;
}
