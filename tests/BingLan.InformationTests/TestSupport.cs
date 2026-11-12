using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using BingLan.Core.Models;
using BingLan.Core.Services;

namespace BingLan.InformationTests;

internal static class TestSupport
{
    public static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：期望 {expected}，实际 {actual}");
        }
    }

    public static void Run(string name, Action test, List<string> failures)
    {
        try
        {
            test();
            Console.WriteLine($"通过：{name}");
        }
        catch (Exception exception)
        {
            failures.Add($"失败：{name} — {exception}");
        }
    }

    public static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    public static void PumpUntil(Func<bool> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException($"等待超时：{description}");
            }
            Pump();
            Thread.Sleep(10);
        }
        Pump();
    }

    public static void ShowAndPump(Window window)
    {
        window.Show();
        Pump();
    }

    public static T Require<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        root.FindName(name) as T
        ?? throw new InvalidOperationException($"找不到控件 {name}（{typeof(T).Name}）");
}

internal sealed class FakeSamplingService : IPerformanceSamplingService
{
    public event Action<PerformanceSnapshot>? Sampled;

    public bool IsRunning { get; private set; }
    public PerformanceSamplingMode Mode { get; private set; } = PerformanceSamplingMode.Normal;
    public TimeSpan CurrentInterval => PerformanceSamplingRules.ResolveInterval(Mode);

    public void Start() => IsRunning = true;

    public void SetMode(PerformanceSamplingMode mode) => Mode = mode;

    public void Stop() => IsRunning = false;

    public void Dispose() => Stop();

    public void Emit(PerformanceSnapshot snapshot) => Sampled?.Invoke(snapshot);
}

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Behavior =
        (_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

    public int CallCount;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        return Behavior(request, cancellationToken);
    }
}
