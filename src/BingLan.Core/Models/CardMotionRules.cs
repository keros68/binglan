namespace BingLan.Core.Models;

/// <summary>
/// 桌面卡片过渡动效的共享时长与位移参数（折叠、展开，以及条目与图标增删）。
/// 过渡只会在系统“动画效果”开启且应用动效等级未设为关闭时播放；规则集中在此处，可在不创建 WPF 窗口的情况下直接验证。
/// </summary>
public static class CardMotionRules
{
    public const int MinimumDurationMs = 120;
    public const int MaximumDurationMs = 200;

    public static readonly TimeSpan FoldDuration = TimeSpan.FromMilliseconds(180);
    public static readonly TimeSpan ItemDuration = TimeSpan.FromMilliseconds(150);

    /// <summary>条目滑入与滑出时的垂直位移，单位 DIP。</summary>
    public const double ItemSlideOffsetDip = 6d;

    public static bool IsAnimated(MotionLevel motion, bool systemAnimationsEnabled) =>
        systemAnimationsEnabled && motion != MotionLevel.Off;

    public static TimeSpan ResolveFoldDuration(MotionLevel motion, bool systemAnimationsEnabled) =>
        IsAnimated(motion, systemAnimationsEnabled) ? FoldDuration : TimeSpan.Zero;

    public static TimeSpan ResolveItemDuration(MotionLevel motion, bool systemAnimationsEnabled) =>
        IsAnimated(motion, systemAnimationsEnabled) ? ItemDuration : TimeSpan.Zero;
}
