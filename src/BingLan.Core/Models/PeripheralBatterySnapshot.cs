namespace BingLan.Core.Models;

// 外设种类，与 BatteryEngine.dll 输出 JSON 的 kind 字段一一对应
// （引擎的 KindId 只区分这几类，其余一律 other）。
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
    EngineUnavailable
}

// 一台外设的一次读数。Percent 是展示值：有实时读数时为实时值，
// 设备休眠/不应答时为最后已知值（此时 Live 为 false），无任何已知值时为 -1。
// EstimateText 是引擎按电量历史估算的剩余可用时长文本（放电时才有，充电时为空），
// 来自 BhExtrasJson，与主轮询 JSON 分开取。
public sealed record PeripheralDevice(
    string Key,
    string Name,
    PeripheralDeviceKind Kind,
    string Via,
    int Percent,
    bool Live,
    bool Charging,
    bool Asleep,
    bool Connected,
    bool Low,
    string LevelText,
    string Detail,
    int Millivolts,
    string EstimateText);

public sealed record PeripheralBatterySnapshot(
    PeripheralBatteryStatus Status,
    IReadOnlyList<PeripheralDevice> Devices,
    DateTimeOffset? UpdatedAt,
    string? ErrorMessage)
{
    public static readonly PeripheralBatterySnapshot Empty =
        new(PeripheralBatteryStatus.NoData, Array.Empty<PeripheralDevice>(), null, null);
}
