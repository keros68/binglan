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
            () => { },
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
            () => { },
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

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
