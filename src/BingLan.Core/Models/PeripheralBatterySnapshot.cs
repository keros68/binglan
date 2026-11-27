namespace BingLan.Core.Models;

public enum PeripheralDeviceKind
{
    Other,
    Mouse,
    Headset,
    Keyboard,
    Gamepad,
    Phone
}

public enum PeripheralBatteryStatus
{
    NoData,
    Fresh,
    Stale,
    SourceUnavailable,
    NotConfigured
}

/// <summary>A BatteryHub CLI reading; -1 means no known percentage.</summary>
public sealed record PeripheralDevice(
    string Key,
    string Name,
    PeripheralDeviceKind Kind,
    string Via,
    int Percent,
    bool Charging,
    bool Asleep,
    bool Connected);

public sealed record PeripheralBatterySnapshot(
    PeripheralBatteryStatus Status,
    IReadOnlyList<PeripheralDevice> Devices,
    DateTimeOffset? UpdatedAt,
    string? ErrorMessage = null)
{
    public static readonly PeripheralBatterySnapshot Empty =
        new(PeripheralBatteryStatus.NoData, Array.Empty<PeripheralDevice>(), null);

    public static readonly PeripheralBatterySnapshot NotConfigured =
        new(PeripheralBatteryStatus.NotConfigured, Array.Empty<PeripheralDevice>(), null);
}
