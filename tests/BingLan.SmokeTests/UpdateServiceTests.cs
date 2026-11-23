using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BingLan.Core.Models;
using BingLan.Core.Services;

/// <summary>Update checks and verified downloads, against an offline HTTP stub.</summary>
internal static class UpdateServiceTests
{
    private static readonly byte[] InstallerBytes = Encoding.UTF8.GetBytes("installer payload");

    internal static void TestVersionAndSchedule()
    {
        Check(UpdateService.ParseVersion("v0.2.2") == new Version(0, 2, 2), "v 前缀版本号可解析");
        Check(UpdateService.ParseVersion("0.3") == new Version(0, 3, 0), "两段版本号补齐为三段");
        Check(UpdateService.ParseVersion("nightly") is null, "非版本号标签被忽略");
        Check(UpdateService.Normalize(new Version(0, 2, 2, 0)) == new Version(0, 2, 2), "程序集四段版本按三段比较");

        var now = DateTimeOffset.Now;
        Check(UpdateService.IsAutoCheckDue(null, now), "从未检查过时到期");
        Check(!UpdateService.IsAutoCheckDue(now.AddHours(-23), now), "一天内不重复检查");
        Check(UpdateService.IsAutoCheckDue(now.AddHours(-24), now), "满一天后到期");
        Check(UpdateService.IsAutoCheckDue(now.AddHours(3), now), "时钟被调回时视为到期");

        Check(UpdateService.FormatAvailableBadgeText(null) is null, "无新版本时不生成徽标文案");
        var badgeRelease = new UpdateRelease(
            new Version(0, 4, 1, 0),
            string.Empty,
            new Uri("https://github.com/keros68/binglan/releases/tag/v0.4.1"),
            null);
        Check(UpdateService.FormatAvailableBadgeText(badgeRelease) == "有新版本 0.4.1", "有新版本时格式化三段版本号徽标文案");
    }

    internal static void TestReleaseParsing()
    {
        var release = UpdateService.ParseRelease(ReleaseJson("v0.3.0"));
        Check(release is { Installer: not null } && release.Version == new Version(0, 3, 0), "解析版本和安装包");
        Check(string.Equals(release!.Installer!.Sha256, Sha256Hex(InstallerBytes), StringComparison.OrdinalIgnoreCase), "读取 GitHub 公布的 SHA-256");
        Check(release.Notes == "更新说明", "读取更新说明");

        Check(UpdateService.ParseRelease(ReleaseJson("v0.3.0", prerelease: true)) is null, "预发布版本被忽略");
        Check(UpdateService.ParseRelease(ReleaseJson("v0.3.0", digest: null)) is { Installer: null },
            "没有校验值的安装包不可直接安装");
        Check(UpdateService.ParseRelease(ReleaseJson("v0.3.0", assetName: "other.zip")) is { Installer: null },
            "非安装包附件被忽略");
        Check(UpdateService.ParseRelease(ReleaseJson("v0.3.0", downloadUrl: "http://example.com/BingLan-Setup-0.3.0.exe")) is { Installer: null },
            "非 HTTPS 下载地址被拒绝");
    }

    internal static void TestCheck()
    {
        Check(Run(new Version(0, 2, 2), "v0.3.0").Status == UpdateCheckStatus.Available, "新版本可用");
        Check(Run(new Version(0, 3, 0), "v0.3.0").Status == UpdateCheckStatus.UpToDate, "同版本视为最新");
        Check(Run(new Version(0, 4, 0), "v0.3.0").Status == UpdateCheckStatus.UpToDate, "本机更新时不提示降级");

        var failing = new UpdateService(new Version(0, 2, 2),
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var failed = failing.CheckAsync().GetAwaiter().GetResult();
        Check(failed is { Status: UpdateCheckStatus.Failed, ErrorMessage: "网络请求失败" }, "服务器错误安静失败");

        string? userAgent = null;
        var probe = new UpdateService(new Version(0, 2, 2), new StubHandler(request =>
        {
            userAgent = request.Headers.UserAgent.ToString();
            return Json(ReleaseJson("v0.2.2"));
        }));
        probe.CheckAsync().GetAwaiter().GetResult();
        Check(userAgent == "BingLan/0.2.2", "请求只带程序名和版本号");
    }

    internal static void TestVerifiedDownload()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"BingLan-update-{Guid.NewGuid():N}");
        try
        {
            var asset = new UpdateAsset("BingLan-Setup-0.3.0.exe", new Uri("https://example.com/setup.exe"),
                InstallerBytes.Length, Sha256Hex(InstallerBytes));
            var service = new UpdateService(new Version(0, 2, 2), new StubHandler(_ => Bytes(InstallerBytes)));
            var path = service.DownloadAsync(asset, directory).GetAwaiter().GetResult();
            Check(File.ReadAllBytes(path).SequenceEqual(InstallerBytes), "校验通过的安装包保存到本地");

            var tampered = new UpdateService(new Version(0, 2, 2),
                new StubHandler(_ => Bytes(Encoding.UTF8.GetBytes("installer PAYLOAD"))));
            ExpectFailure(() => tampered.DownloadAsync(asset, directory), "下载的文件校验失败");
            Check(!File.Exists(path), "校验失败的文件被删除");

            var truncated = new UpdateService(new Version(0, 2, 2),
                new StubHandler(_ => Bytes(InstallerBytes[..5])));
            ExpectFailure(() => truncated.DownloadAsync(asset, directory), "下载的文件大小与发布信息不符");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    internal static void TestStateMigration()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-update-state-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temp);
            var store = new LocalStateStore(temp);
            File.WriteAllText(store.StatePath, """{"SchemaVersion":20}""");
            var loaded = store.Load();
            Check(loaded.SchemaVersion == AppState.CurrentSchemaVersion, "v20 状态迁移到当前版本");
            Check(loaded.Updates is { AutoCheck: true, LastCheckedAt: null }, "升级后默认每天自动检查更新");

            loaded.Updates.AutoCheck = false;
            loaded.Updates.LastCheckedAt = DateTimeOffset.Now;
            store.Save(loaded);
            var reloaded = store.Load();
            Check(!reloaded.Updates.AutoCheck && reloaded.Updates.LastCheckedAt is not null, "更新设置保存后保留");
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, true);
            }
        }
    }

    private static UpdateCheckResult Run(Version current, string tag) =>
        new UpdateService(current, new StubHandler(_ => Json(ReleaseJson(tag))))
            .CheckAsync().GetAwaiter().GetResult();

    private static string ReleaseJson(
        string tag,
        bool prerelease = false,
        string? digest = "",
        string assetName = "BingLan-Setup-0.3.0.exe",
        string downloadUrl = "https://github.com/keros68/binglan/releases/download/v0.3.0/BingLan-Setup-0.3.0.exe")
    {
        var asset = new Dictionary<string, object?>
        {
            ["name"] = assetName,
            ["size"] = InstallerBytes.Length,
            ["browser_download_url"] = downloadUrl
        };
        if (digest is not null)
        {
            asset["digest"] = digest.Length == 0 ? $"sha256:{Sha256Hex(InstallerBytes).ToLowerInvariant()}" : digest;
        }
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tag_name"] = tag,
            ["html_url"] = $"https://github.com/keros68/binglan/releases/tag/{tag}",
            ["draft"] = false,
            ["prerelease"] = prerelease,
            ["body"] = "更新说明",
            ["assets"] = new[] { asset }
        });
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static void ExpectFailure(Func<Task<string>> download, string message)
    {
        try
        {
            download().GetAwaiter().GetResult();
        }
        catch (UpdateDownloadException exception)
        {
            Check(exception.Message == message, $"失败原因应为“{message}”，实际“{exception.Message}”");
            return;
        }
        throw new InvalidOperationException($"应失败：{message}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
