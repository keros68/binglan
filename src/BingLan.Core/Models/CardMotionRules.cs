namespace BingLan.Core.Models;

/// <summary>
/// 桌面卡片过渡动效的共享时长与位移参数（折叠、展开，以及条目与图标增删）。
/// 将规则集中在此处，可在不创建 WPF 窗口的情况下由非 UI 测试直接验证。
/// </summary>
public static class CardMotionRules
{
    public const int MinimumDurationMs = 120;
    public const int MaximumDurationMs = 200;

    public static readonly TimeSpan FoldDuration = TimeSpan.FromMilliseconds(180);
    public static readonly TimeSpan ItemDuration = TimeSpan.FromMilliseconds(150);

    /// <summary>条目滑入与滑出时的垂直位移，单位 DIP。</summary>
    public const double ItemSlideOffsetDip = 6d;

    public static bool IsAnimated(bool systemAnimationsEnabled) => systemAnimationsEnabled;

    public static TimeSpan ResolveFoldDuration(bool systemAnimationsEnabled) =>
        systemAnimationsEnabled ? FoldDuration : TimeSpan.Zero;

    public static TimeSpan ResolveItemDuration(bool systemAnimationsEnabled) =>
        systemAnimationsEnabled ? ItemDuration : TimeSpan.Zero;
}
