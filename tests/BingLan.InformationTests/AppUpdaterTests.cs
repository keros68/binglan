using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using BingLan.App.Services;
using BingLan.Core.Models;
using BingLan.Core.Services;
using static BingLan.InformationTests.TestSupport;

namespace BingLan.InformationTests;

internal static class AppUpdaterTests
{
    public static void AutoCheckRespectsRateLimitCooldownAndRecoversAfterReset()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 24, 14, 0, 0, TimeSpan.Zero));
        var resetUtc = clock.GetUtcNow().AddMinutes(24);
        var rateLimited = true;
        var stub = new StubHttpMessageHandler
        {
            Behavior = (request, _) =>
            {
                if (rateLimited)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        ReasonPhrase = "rate limit exceeded"
                    };
                    response.Headers.Add("X-RateLimit-Remaining", "0");
                    response.Headers.Add("X-RateLimit-Reset", resetUtc.ToUnixTimeSeconds().ToString());
                    return Task.FromResult(response);
                }

                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://github.com/keros68/binglan/releases/tag/v0.4.8");
                return Task.FromResult(redirect);
            }
        };

        var state = new UpdateState { AutoCheck = true };
        var saves = 0;
        using var updater = new AppUpdater(
            state,
            new Version(0, 4, 8),
            () => saves++,
            static () => Task.CompletedTask,
            canAutoCheck: true,
            handler: stub,
            timeProvider: clock);

        updater.Start();
        PumpUntil(() => !updater.IsBusy && updater.RetryAfterUtc == resetUtc, TimeSpan.FromSeconds(5),
            "首次自动检查命中 403 限流后记录 RetryAfterUtc");
        var callsAfterLimit = stub.CallCount;
        Assert(callsAfterLimit > 0, "首次自动检查已发起网络请求");

        // 冷却期内关闭再打开自动检查，不应再次触发网络请求
        updater.AutoCheck = false;
        updater.AutoCheck = true;
        Pump();
        AssertEqual(callsAfterLimit, stub.CallCount, "冷却期内自动检查不重复请求");

        // 时间推进至冷却期结束，重新触发自动检查并清除冷却状态
        rateLimited = false;
        clock.Advance(TimeSpan.FromMinutes(25));
        updater.AutoCheck = false;
        updater.AutoCheck = true;
        PumpUntil(() => !updater.IsBusy && state.LastCheckedAt is not null, TimeSpan.FromSeconds(5),
            "冷却期过后自动检查恢复并成功更新 LastCheckedAt");
        Assert(updater.RetryAfterUtc is null, "成功后清空 RetryAfterUtc");
        Assert(stub.CallCount > callsAfterLimit, "冷却期过后发起了新的检查请求");
        Assert(saves > 0, "检查成功后保存更新状态");
    }

    public static void UnexpectedExceptionsResetBusyAndNotifyChanged()
    {
        var installerBytes = Encoding.UTF8.GetBytes("installer payload");
        var digest = Convert.ToHexString(SHA256.HashData(installerBytes)).ToLowerInvariant();
        var releaseJson = $$"""
            {
              "tag_name": "v9.9.9",
              "html_url": "https://github.com/keros68/binglan/releases/tag/v9.9.9",
              "body": "新版本说明",
              "assets": [
                {
                  "name": "BingLan-Setup-9.9.9.exe",
                  "size": {{installerBytes.Length}},
                  "digest": "sha256:{{digest}}",
                  "browser_download_url": "https://github.com/keros68/binglan/releases/download/v9.9.9/BingLan-Setup-9.9.9.exe"
                }
              ]
            }
            """;

        var throwOnCheck = true;
        var stub = new StubHttpMessageHandler
        {
            Behavior = (request, _) =>
            {
                if (throwOnCheck)
                {
                    throw new InvalidOperationException("unexpected check error");
                }
                if (request.RequestUri!.AbsoluteUri.EndsWith(".exe", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("unexpected download error");
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                });
            }
        };

        var changedStates = new List<(bool IsBusy, string Status)>();
        using var updater = new AppUpdater(
            new UpdateState(),
            new Version(0, 4, 8),
            () => { },
            static () => Task.CompletedTask,
            canAutoCheck: false,
            handler: stub);
        updater.Changed += () => changedStates.Add((updater.IsBusy, updater.Status));

        var checkTask = updater.CheckNowAsync();
        PumpUntil(() => checkTask.IsCompleted, TimeSpan.FromSeconds(5), "异常检查任务完成");
        checkTask.GetAwaiter().GetResult();
        Assert(!updater.IsBusy, "检查更新遇未预期异常后必须复位 IsBusy");
        AssertEqual("检查更新失败：发生意外错误，请稍后重试", updater.Status, "检查异常后更新失败状态而非停在正在检查");
        Assert(changedStates.Count >= 2 && !changedStates[^1].IsBusy, "检查异常后应以 IsBusy=false 触发 Changed");

        // 正常发现新版本后，下载阶段抛出未预期异常也必须复位 IsBusy 并刷新状态
        throwOnCheck = false;
        var okCheckTask = updater.CheckNowAsync();
        PumpUntil(() => okCheckTask.IsCompleted, TimeSpan.FromSeconds(5), "正常检查任务完成");
        okCheckTask.GetAwaiter().GetResult();
        Assert(updater.Available?.Installer is not null, "正常检查发现可下载安装包");

        changedStates.Clear();
        var downloadTask = updater.DownloadAndInstallAsync();
        PumpUntil(() => downloadTask.IsCompleted, TimeSpan.FromSeconds(5), "异常下载任务完成");
        downloadTask.GetAwaiter().GetResult();
        Assert(!updater.IsBusy, "下载遇未预期异常后必须复位 IsBusy");
        AssertEqual("下载失败：发生意外错误，请稍后重试", updater.Status, "下载异常后更新失败状态而非停在正在下载");
        Assert(changedStates.Count >= 2 && !changedStates[^1].IsBusy, "下载异常后应以 IsBusy=false 触发 Changed");
    }

    public static void ChangedAndExitAppExceptionsStillResetBusy()
    {
        var installerBytes = Encoding.UTF8.GetBytes("installer payload");
        var digest = Convert.ToHexString(SHA256.HashData(installerBytes)).ToLowerInvariant();
        var releaseJson = $$"""
            {
              "tag_name": "v9.9.9",
              "html_url": "https://github.com/keros68/binglan/releases/tag/v9.9.9",
              "body": "新版本说明",
              "assets": [
                {
                  "name": "BingLan-Setup-9.9.9.exe",
                  "size": {{installerBytes.Length}},
                  "digest": "sha256:{{digest}}",
                  "browser_download_url": "https://github.com/keros68/binglan/releases/download/v9.9.9/BingLan-Setup-9.9.9.exe"
                }
              ]
            }
            """;

        var stub = new StubHttpMessageHandler
        {
            Behavior = (request, _) =>
            {
                if (request.RequestUri!.AbsoluteUri.EndsWith(".exe", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(installerBytes)
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                });
            }
        };

        string? launchedPath = null;
        var throwOnChanged = true;
        var changedStates = new List<(bool IsBusy, string Status)>();
        using var updater = new AppUpdater(
            new UpdateState(),
            new Version(0, 4, 8),
            () => { },
            async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("async exit failed");
            },
            canAutoCheck: false,
            handler: stub,
            launchInstaller: (path, _) => launchedPath = path);

        updater.Changed += () =>
        {
            if (throwOnChanged)
            {
                throw new InvalidOperationException("changed listener failed");
            }
            changedStates.Add((updater.IsBusy, updater.Status));
        };

        try
        {
            // 1. CheckAsync 首次 OnChanged 抛出时，IsBusy 必须立刻复位为 false
            var checkThrew = false;
            try
            {
                var task = updater.CheckNowAsync();
                PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5), "Changed 抛出检查完成");
                task.GetAwaiter().GetResult();
            }
            catch (InvalidOperationException)
            {
                checkThrew = true;
            }
            Assert(checkThrew && !updater.IsBusy, "CheckAsync 首次 OnChanged 抛出后必须复位 IsBusy");

            // 正常检查发现新版本
            throwOnChanged = false;
            var okCheck = updater.CheckNowAsync();
            PumpUntil(() => okCheck.IsCompleted, TimeSpan.FromSeconds(5), "正常检查发现新版本");
            okCheck.GetAwaiter().GetResult();
            Assert(updater.Available?.Installer is not null, "正常检查应拿到安装包信息");

            // 2. DownloadAndInstallAsync 首次 OnChanged 抛出时，IsBusy 必须立刻复位为 false
            throwOnChanged = true;
            var downloadThrew = false;
            try
            {
                var task = updater.DownloadAndInstallAsync();
                PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5), "Changed 抛出下载完成");
                task.GetAwaiter().GetResult();
            }
            catch (InvalidOperationException)
            {
                downloadThrew = true;
            }
            Assert(downloadThrew && !updater.IsBusy, "DownloadAndInstallAsync 首次 OnChanged 抛出后必须复位 IsBusy");

            // 3. 安装程序启动后异步 _exitApp 抛出时，IsBusy 必须复位并更新提示且触发 Changed
            throwOnChanged = false;
            changedStates.Clear();
            var exitFailTask = updater.DownloadAndInstallAsync();
            PumpUntil(() => exitFailTask.IsCompleted, TimeSpan.FromSeconds(5), "_exitApp 抛出路径完成");
            exitFailTask.GetAwaiter().GetResult();
            Assert(launchedPath is not null, "安装程序应已被触发启动");
            Assert(!updater.IsBusy, "_exitApp 抛出后必须复位 IsBusy");
            AssertEqual("已启动安装程序，请手动关闭冰蓝桌面", updater.Status, "_exitApp 抛出后应给出明确提示");
            Assert(changedStates.Count >= 2 && !changedStates[^1].IsBusy, "_exitApp 抛出后应以 IsBusy=false 通知 Changed");
        }
        finally
        {
            if (launchedPath is not null && System.IO.File.Exists(launchedPath))
            {
                System.IO.File.Delete(launchedPath);
            }
        }
    }

    public static void PartialReleaseWithRateLimitedFallbackRetriesInstallerAfterCooldown()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        var resetUtc = clock.GetUtcNow().AddMinutes(20);
        var installerBytes = Encoding.UTF8.GetBytes("installer payload");
        var digest = Convert.ToHexString(SHA256.HashData(installerBytes)).ToLowerInvariant();
        var releaseJson = $$"""
            {
              "tag_name": "v9.9.9",
              "html_url": "https://github.com/keros68/binglan/releases/tag/v9.9.9",
              "body": "完整说明",
              "assets": [
                {
                  "name": "BingLan-Setup-9.9.9.exe",
                  "size": {{installerBytes.Length}},
                  "digest": "sha256:{{digest}}",
                  "browser_download_url": "https://github.com/keros68/binglan/releases/download/v9.9.9/BingLan-Setup-9.9.9.exe"
                }
              ]
            }
            """;

        var apiLimited = true;
        var stub = new StubHttpMessageHandler
        {
            Behavior = (request, _) =>
            {
                var uri = request.RequestUri!.AbsoluteUri;
                if (uri == UpdateService.LatestReleasePageEndpoint)
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri("https://github.com/keros68/binglan/releases/tag/v9.9.9");
                    return Task.FromResult(redirect);
                }
                if (uri == $"{UpdateService.ReleaseByTagEndpointPrefix}v9.9.9")
                {
                    if (apiLimited)
                    {
                        var limited = new HttpResponseMessage(HttpStatusCode.Forbidden)
                        {
                            ReasonPhrase = "rate limit exceeded"
                        };
                        limited.Headers.Add("X-RateLimit-Remaining", "0");
                        limited.Headers.Add("X-RateLimit-Reset", resetUtc.ToUnixTimeSeconds().ToString());
                        return Task.FromResult(limited);
                    }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }
        };

        var state = new UpdateState { AutoCheck = true };
        var releaseFoundCount = 0;
        using var updater = new AppUpdater(
            state,
            new Version(0, 4, 9),
            () => { },
            static () => Task.CompletedTask,
            canAutoCheck: true,
            handler: stub,
            timeProvider: clock);
        updater.ReleaseFound += _ => releaseFoundCount++;

        updater.Start();
        PumpUntil(() => !updater.IsBusy && updater.Available is not null, TimeSpan.FromSeconds(5),
            "tag API 限流且降级失败时仍应展示可见新版本");
        Assert(updater.Available!.Installer is null, "降级资产失败时 Installer 为 null");
        Assert(updater.RetryAfterUtc == resetUtc, "保留 RetryAfterUtc 等待冷却后重新探测安装包");
        Assert(state.LastCheckedAt is null, "仅返回发布页且命中限流时不提前写入 LastCheckedAt 阻塞重试");
        AssertEqual(1, releaseFoundCount, "首次发现新版本触发一次 ReleaseFound");

        var callsAfterFirst = stub.CallCount;
        updater.AutoCheck = false;
        updater.AutoCheck = true;
        Pump();
        AssertEqual(callsAfterFirst, stub.CallCount, "冷却期内自动检查不重复请求");

        // 冷却结束后重新触发自动检查，补全 Installer 且不重复弹 ReleaseFound
        apiLimited = false;
        clock.Advance(TimeSpan.FromMinutes(21));
        updater.AutoCheck = false;
        updater.AutoCheck = true;
        PumpUntil(() => !updater.IsBusy && updater.Available?.Installer is not null, TimeSpan.FromSeconds(5),
            "冷却结束后自动重试应补全直接安装包信息");
        Assert(updater.RetryAfterUtc is null, "补全安装包后清空 RetryAfterUtc");
        Assert(state.LastCheckedAt is not null, "补全安装包后写入 LastCheckedAt");
        AssertEqual(1, releaseFoundCount, "同版本补全安装包不应重复触发 ReleaseFound");
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
