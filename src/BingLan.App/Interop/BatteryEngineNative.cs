using System.Runtime.InteropServices;

namespace BingLan.App.Interop;

/// <summary>
/// BatteryEngine.dll（BatteryHub 读数引擎的融合构建）的原生封装。
/// 引擎是可选组件：DLL 缺失或位数不符时所有调用安静返回不可用，界面按「引擎不可用」呈现，
/// 不影响冰蓝其余功能。
/// </summary>
internal static class BatteryEngineNative
{
    [DllImport("BatteryEngine.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint BhPollJson();

    [DllImport("BatteryEngine.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint BhExtrasJson();

    [DllImport("BatteryEngine.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void BhShutdown();

    /// <summary>一次全量轮询，返回引擎 JSON（与 BatteryHub --json 同一份文档）；
    /// 引擎不可用时返回 null。轮询阻塞 1–2 秒，只能在后台线程调用。</summary>
    public static string? PollJson()
    {
        try
        {
            var ptr = BhPollJson();
            return ptr == 0 ? null : Marshal.PtrToStringUTF8(ptr);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>上次轮询设备按电量历史估算的剩余时长（BhExtrasJson）；不可用时返回 null。
    /// 只是装饰信息，调用方取不到时应安静跳过。</summary>
    public static string? PollExtrasJson()
    {
        try
        {
            var ptr = BhExtrasJson();
            return ptr == 0 ? null : Marshal.PtrToStringUTF8(ptr);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>进程退出时释放引擎的后台驱动会话；引擎从未加载时安静跳过。</summary>
    public static void Shutdown()
    {
        try
        {
            BhShutdown();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
        }
    }
}
