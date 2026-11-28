using System.Windows;
using BingLan.App.Tray;

internal static class TraySnapshotRecoveryTests
{
    internal static void Run()
    {
        SourceRecovery();
        AccessibleSourceRecovery();
        CacheRecovery();
        ExplorerReplacement();
    }

    private static void SourceRecovery()
    {
        const uint explorer = 100;
        var primary = Surface("taskbar", "Shell_TrayWnd", explorer);
        var failedPrimary = primary with { Error = "provider disconnected" };
        var failedOverflow = Surface("existing-overflow", "TopLevelWindowForOverflowXamlIsland", explorer, "provider disconnected");
        var icon = new TrayEntry("live-uia", "应用", "taskbar", explorer, null);
        var registration = new TrayRegisteredIcon((nint)200, 201, 1, Guid.Empty, Rect.Empty);
        var fallback = new TrayEntry(registration.Key, "应用", "taskbar", explorer, null, registration);

        Check(TrayReader.SelectSnapshotEntries(Read(explorer, [primary], [icon])) is [var normal]
            && normal.Identity == icon.Identity, "成功读取应发表本次实时身份");
        Check(TrayReader.SelectSnapshotEntries(Read(explorer, [failedPrimary], [fallback])) is [var recovered]
            && recovered.Identity == fallback.Identity, "UIA 失败时，当前 Explorer 已核对的 MSAA 图标仍应可发表");
        Check(TrayReader.SelectSnapshotEntries(Read(explorer, [primary, failedOverflow], [])) is null,
            "任何已发现源读取失败时，空列表不能被当作完整结果发表");
        Check(TrayReader.SelectSnapshotEntries(Read(explorer, [failedPrimary], [])) is null
            && TrayReader.SelectSnapshotEntries(Read(explorer, [], [])) is null
            && TrayReader.SelectSnapshotEntries(Read(0, [], [])) is null,
            "缺少可信主任务栏、全部失联或 Explorer 不存在时应报告读取失败");
        Check(TrayReader.SelectSnapshotEntries(Read(explorer, [primary], [])) is { Length: 0 },
            "来源完整且没有图标时，应允许清空旧列表");
        Check(TrayReader.SelectSnapshotEntries(Read(explorer, [primary, failedOverflow], [icon])) is [var partial]
            && partial.Identity == icon.Identity, "局部失联只能发表本次仍有效的身份");
        Check(TrayReader.SelectSnapshotEntries(Read(explorer, [primary], [icon with { ExplorerProcessId = explorer - 1 }]))
            is null, "其他 Explorer 的条目不能进入新列表，也不能被当作合法空读取");
    }

    private static void AccessibleSourceRecovery()
    {
        const uint explorer = 100;
        var primary = Surface("taskbar", "Shell_TrayWnd", explorer);
        var unavailable = Surface("taskbar", "MSAA", explorer, "registered buttons unavailable");
        var candidate = new TrayRegisteredIcon((nint)200, 201, 1, Guid.Empty, Rect.Empty);
        var calls = 0;
        var entries = TrayAccessibleIcons.ReadCandidates([], icon => { calls++; return null; }, out var failed);
        Check(!failed && entries.Length == 0 && calls == 0
            && TrayReader.SelectSnapshotEntries(Read(explorer, [primary], entries)) is { Length: 0 },
            "没有适用的备用来源时不应制造读取失败，完整 UIA 空结果仍应可发表");
        entries = TrayAccessibleIcons.ReadCandidates([candidate], _ => null, out failed);
        Check(failed && entries.Length == 0
            && TrayReader.SelectSnapshotEntries(Read(explorer, [primary, unavailable], entries)) is null,
            "存在当前注册候选但无法读取按钮时，不能把临时失败发表为合法空");
        entries = TrayAccessibleIcons.ReadCandidates([candidate, candidate with { IconId = 2 }], icon =>
        {
            if (icon.IconId == 1) throw new System.Runtime.InteropServices.COMException("disconnected");
            return new TrayEntry(icon.Key, "应用", "taskbar", explorer, null, icon);
        }, out failed);
        Check(failed && entries is [var recovered] && recovered.Registration?.IconId == 2
            && TrayReader.SelectSnapshotEntries(Read(explorer, [primary, unavailable], entries)) is { Length: 1 },
            "单个备用按钮失联后仍应读取并发表其余实时按钮");
    }

    private static void CacheRecovery()
    {
        uint explorer = 100;
        var entries = Entries(explorer);
        var calls = 0;
        TraySnapshot? next = null;
        var cache = new TraySnapshotCache(() => explorer, () =>
        {
            Interlocked.Increment(ref calls);
            return next;
        });
        Check(Result(cache.ReadAsync(refresh: false)) is null && cache.CachedSnapshot is null,
            "首次暖读失败时不能制造可用快照");
        next = new TraySnapshot(explorer, entries);
        Check(ReferenceEquals(Result(cache.ReadAsync(refresh: false)), entries) && calls == 2,
            "失败后的暖读应重新读取并恢复");
        Check(ReferenceEquals(Result(cache.ReadAsync(refresh: false)), entries) && calls == 2,
            "成功暖读应复用当前 Explorer 的缓存");
        next = null;
        Check(Result(cache.ReadAsync(refresh: true)) is null && calls == 3,
            "刷新失败应返回失败而不是把缓存伪装成刷新结果");
        var recovered = Entries(explorer, "fresh");
        next = new TraySnapshot(explorer, recovered);
        Check(ReferenceEquals(Result(cache.ReadAsync(refresh: false)), recovered) && calls == 4,
            "刷新失败后，暖读仍应允许重试");
        next = new TraySnapshot(explorer, []);
        Check(Result(cache.ReadAsync(refresh: true)) is { Length: 0 }
            && cache.CachedSnapshot is { Length: 0 }, "完整空快照应替换缓存并清除旧身份");
        explorer++;
        Check(cache.CachedSnapshot is null, "空快照同样必须按 Explorer 身份失效");
    }

    private static void ExplorerReplacement()
    {
        uint explorer = 100;
        var first = Entries(explorer);
        var next = new TraySnapshot(explorer, first);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var block = false;
        var calls = 0;
        var cache = new TraySnapshotCache(() => Volatile.Read(ref explorer), () =>
        {
            Interlocked.Increment(ref calls);
            var captured = next;
            if (block)
            {
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("测试未释放后台读取");
            }
            return captured;
        });
        Check(ReferenceEquals(Result(cache.ReadAsync(refresh: false)), first), "当前 Explorer 的暖读应成功");
        block = true;
        var pending = cache.ReadAsync(refresh: true);
        try
        {
            Check(started.Wait(TimeSpan.FromSeconds(5)), "后台读取应已开始");
            Check(ReferenceEquals(pending, cache.ReadAsync(refresh: false))
                && ReferenceEquals(pending, cache.ReadAsync(refresh: true)), "读取过程中暖读和刷新应共用同一个任务");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                Result(pending.WaitAsync(cancellation.Token));
                throw new InvalidOperationException("已取消的等待不应完成");
            }
            catch (OperationCanceledException) { }
            Volatile.Write(ref explorer, 101);
            Check(cache.CachedSnapshot is null, "Explorer 更换时旧图标必须立即失效");
            Check(ReferenceEquals(pending, cache.ReadAsync(refresh: true)) && calls == 2,
                "等待取消和 Explorer 更换都不能启动第二个并行提供程序调用");
        }
        finally { release.Set(); }
        Check(Result(pending) is null && cache.CachedSnapshot is null,
            "旧 Explorer 的迟到结果不得成为新列表");
        block = false;
        var replacement = Entries(explorer, "new-explorer");
        next = new TraySnapshot(explorer, replacement);
        Check(ReferenceEquals(Result(cache.ReadAsync(refresh: false)), replacement) && calls == 3,
            "旧读取结束后应能读取新 Explorer 并恢复");
        Volatile.Write(ref explorer, 0);
        Check(cache.CachedSnapshot is null, "Explorer 失联时不能返回旧身份缓存");
    }

    private static TrayPanelEntry[] Entries(uint explorer, string key = "icon") =>
        [new TrayPanelEntry(new TrayIdentity(explorer, key), "应用", "taskbar", null)];

    private static SurfaceReport Surface(string source, string windowClass, uint explorer, string? error = null) =>
        new(source, (int)explorer, windowClass, [], [], error);

    private static TrayRead Read(uint explorer, SurfaceReport[] surfaces, TrayEntry[] entries) =>
        new(new TrayReport("test", 1, true, surfaces, entries.Length, ""), entries, explorer);

    private static TrayPanelEntry[]? Result(Task<TrayPanelEntry[]?> task) =>
        task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
