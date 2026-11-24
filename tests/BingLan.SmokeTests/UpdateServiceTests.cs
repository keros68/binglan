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
        Check(UpdateService.ParseRelease(ReleaseJson("v0.3.0", downloadUrl: "https://evil.example.com/BingLan-Setup-0.3.0.exe")) is { Installer: null },
            "非可信域名下载地址被拒绝");
    }

    internal static void TestCheck()
    {
        Check(Run(new Version(0, 2, 2), "v0.3.0").Status == UpdateCheckStatus.Available, "新版本可用");
        Check(Run(new Version(0, 3, 0), "v0.3.0").Status == UpdateCheckStatus.UpToDate, "同版本视为最新");
        Check(Run(new Version(0, 4, 0), "v0.3.0").Status == UpdateCheckStatus.UpToDate, "本机更新时不提示降级");

        var failing = new UpdateService(new Version(0, 2, 2),
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var failed = failing.CheckAsync().GetAwaiter().GetResult();
        Check(failed is { Status: UpdateCheckStatus.Failed, ErrorMessage: "GitHub 服务暂时不可用（HTTP 500），请稍后重试" },
            "服务器 500 返回明确状态提示");

        string? userAgent = null;
        var probe = new UpdateService(new Version(0, 2, 2), new StubHandler(request =>
        {
            userAgent = request.Headers.UserAgent.ToString();
            return Json(ReleaseJson("v0.2.2"));
        }));
        probe.CheckAsync().GetAwaiter().GetResult();
        Check(userAgent == "BingLan/0.2.2", "请求只带程序名和版本号");
    }

    internal static void TestRedirectDiscoverySkipsApiWhenUpToDate()
    {
        var apiCalls = 0;
        var headCalls = 0;
        var redirectOnlyHandler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                apiCalls++;
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            if (request.Method == HttpMethod.Head && request.RequestUri.AbsoluteUri == UpdateService.LatestReleasePageEndpoint)
            {
                headCalls++;
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://github.com/keros68/binglan/releases/tag/v0.4.8");
                return redirect;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var upToDate = new UpdateService(new Version(0, 4, 8), redirectOnlyHandler)
            .CheckAsync().GetAwaiter().GetResult();
        Check(upToDate.Status == UpdateCheckStatus.UpToDate, "302 发现版本不高于当前版本时判定最新");
        Check(headCalls == 1 && apiCalls == 0, "已是最新时通过 302 零次调用 api.github.com");

        // HEAD 返回 405 时回退 GET 仅读响应头
        var fallbackHeadCalls = 0;
        var fallbackGetCalls = 0;
        var fallbackApiCalls = 0;
        var headUnsupported = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                fallbackApiCalls++;
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            if (request.Method == HttpMethod.Head)
            {
                fallbackHeadCalls++;
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsoluteUri == UpdateService.LatestReleasePageEndpoint)
            {
                fallbackGetCalls++;
                var redirect = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                redirect.Headers.Location = new Uri("/keros68/binglan/releases/tag/v0.4.8", UriKind.Relative);
                return redirect;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var fallbackResult = new UpdateService(new Version(0, 4, 8), headUnsupported)
            .CheckAsync().GetAwaiter().GetResult();
        Check(fallbackResult.Status == UpdateCheckStatus.UpToDate, "HEAD 不支持时回退 GET 301 仍可判定最新");
        Check(fallbackHeadCalls == 1 && fallbackGetCalls == 1 && fallbackApiCalls == 0,
            "HEAD 回退 GET 同样零次调用 api.github.com");

        var baseUri = new Uri(UpdateService.LatestReleasePageEndpoint);
        Check(!UpdateService.TryParseLatestRedirectLocation(
                new Uri("https://evil.example.com/keros68/binglan/releases/tag/v9.9.9"), baseUri, out _, out _, out _),
            "拒绝非 github.com 的重定向目标");
        Check(!UpdateService.TryParseLatestRedirectLocation(
                new Uri("http://github.com/keros68/binglan/releases/tag/v9.9.9"), baseUri, out _, out _, out _),
            "拒绝 HTTP 降级重定向目标");
    }

    internal static void TestRateLimitedApiFallsBackToChecksumsAndReleasePage()
    {
        var expectedSha = Sha256Hex(InstallerBytes);
        var checksumsBody = $"\uFEFF{expectedSha}  BingLan-Setup-0.4.8.exe\n" +
                            "0D5DC0353FCA7B857D57EB8F8CD9E702B6FDACE7039589BB5F351D72C694699A  BingLan-0.4.8-portable.zip\n";

        var degradedHandler = new StubHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Head && uri == UpdateService.LatestReleasePageEndpoint)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://github.com/keros68/binglan/releases/tag/v0.4.8");
                return redirect;
            }
            if (uri == "https://api.github.com/repos/keros68/binglan/releases/tags/v0.4.8")
            {
                var limited = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    ReasonPhrase = "rate limit exceeded",
                    Content = new StringContent("""{"message":"API rate limit exceeded for 38.175.104.157."}""")
                };
                limited.Headers.Add("X-RateLimit-Remaining", "0");
                return limited;
            }
            if (request.Method == HttpMethod.Get
                && uri == "https://github.com/keros68/binglan/releases/download/v0.4.8/SHA256SUMS.txt")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                        "https://release-assets.githubusercontent.com/assets/SHA256SUMS.txt"),
                    Content = new StringContent(checksumsBody, Encoding.UTF8, "text/plain")
                };
            }
            if (request.Method == HttpMethod.Head
                && uri == "https://github.com/keros68/binglan/releases/download/v0.4.8/BingLan-Setup-0.4.8.exe")
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Head,
                        "https://release-assets.githubusercontent.com/assets/BingLan-Setup-0.4.8.exe"),
                    Content = new ByteArrayContent([])
                };
                head.Content.Headers.ContentLength = 47_321_600;
                return head;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = new UpdateService(new Version(0, 4, 6), degradedHandler)
            .CheckAsync().GetAwaiter().GetResult();
        Check(result.Status == UpdateCheckStatus.Available && result.Release is not null,
            "API 403 限流时走 SHA256SUMS.txt + HEAD 降级仍发现新版本");
        Check(result.Release!.Version == new Version(0, 4, 8), "降级解析版本号为 0.4.8");
        Check(result.Release.PageUrl == new Uri("https://github.com/keros68/binglan/releases/tag/v0.4.8"),
            "降级保留官方发布页链接");
        Check(result.Release.Installer is { } asset
            && asset.Name == "BingLan-Setup-0.4.8.exe"
            && asset.Size == 47_321_600
            && string.Equals(asset.Sha256, expectedSha, StringComparison.OrdinalIgnoreCase),
            "降级解析 SHA256SUMS.txt 与 HEAD 安装包大小");

        // 资产不可得、重定向到不可信域名或超限时，降级为仅发布页可用（Installer == null）
        var untrustedRedirectHandler = new StubHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Head && uri == UpdateService.LatestReleasePageEndpoint)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://github.com/keros68/binglan/releases/tag/v0.4.8");
                return redirect;
            }
            if (uri.StartsWith(UpdateService.ReleaseByTagEndpointPrefix, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            if (uri.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(checksumsBody, Encoding.UTF8, "text/plain")
                };
            }
            if (request.Method == HttpMethod.Head && uri.EndsWith("BingLan-Setup-0.4.8.exe", StringComparison.Ordinal))
            {
                var evil = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Head, "https://evil.example.com/BingLan-Setup-0.4.8.exe"),
                    Content = new ByteArrayContent([])
                };
                evil.Content.Headers.ContentLength = 47_321_600;
                return evil;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var pageOnly = new UpdateService(new Version(0, 4, 6), untrustedRedirectHandler)
            .CheckAsync().GetAwaiter().GetResult();
        Check(pageOnly is { Status: UpdateCheckStatus.Available, Release: { Installer: null } },
            "安装包最终重定向域名不可信时拒绝直接安装但仍返回发布页");

        var missingChecksumsHandler = new StubHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Head && uri == UpdateService.LatestReleasePageEndpoint)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://github.com/keros68/binglan/releases/tag/v0.4.8");
                return redirect;
            }
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        });

        var noAsset = new UpdateService(new Version(0, 4, 6), missingChecksumsHandler)
            .CheckAsync().GetAwaiter().GetResult();
        Check(noAsset is { Status: UpdateCheckStatus.Available, Release: { Installer: null } }
            && noAsset.Release!.Version == new Version(0, 4, 8),
            "SHA256SUMS 不可得时仍降级返回 Available + PageUrl");
    }

    internal static void TestRateLimitMessageAndCooldown()
    {
        var fixedNowUtc = new DateTimeOffset(2026, 11, 24, 14, 0, 0, TimeSpan.Zero);
        var chinaZone = TimeZoneInfo.CreateCustomTimeZone("TestUTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        var clock = new FixedTimeProvider(fixedNowUtc, chinaZone);
        var resetUtc = new DateTimeOffset(2026, 11, 24, 14, 24, 0, TimeSpan.Zero);

        var rateLimited = UpdateService.Create(
            new Version(0, 4, 6),
            handler: new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    ReasonPhrase = "rate limit exceeded"
                };
                response.Headers.Add("X-RateLimit-Remaining", "0");
                response.Headers.Add("X-RateLimit-Reset", resetUtc.ToUnixTimeSeconds().ToString());
                return response;
            }),
            noRedirectHandler: new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    ReasonPhrase = "rate limit exceeded"
                };
                response.Headers.Add("X-RateLimit-Remaining", "0");
                response.Headers.Add("X-RateLimit-Reset", resetUtc.ToUnixTimeSeconds().ToString());
                return response;
            }),
            timeProvider: clock);

        var limitedResult = rateLimited.CheckAsync().GetAwaiter().GetResult();
        Check(limitedResult.Status == UpdateCheckStatus.Failed, "403 限流返回失败状态");
        Check(limitedResult.ErrorMessage == "GitHub 访问受限（当前出口 IP 请求过多），请于 22:24 后重试或前往发布页查看",
            $"403 限流提示应包含本地重置时间 22:24，实际：{limitedResult.ErrorMessage}");
        Check(limitedResult.RetryAfterUtc == resetUtc, "403 限流应带出 RetryAfterUtc");

        Check(!UpdateService.IsAutoCheckDue(null, fixedNowUtc, limitedResult.RetryAfterUtc), "冷却期内阻止自动重试");
        Check(!UpdateService.IsAutoCheckDue(null, resetUtc.AddSeconds(-1), limitedResult.RetryAfterUtc), "冷却期结束前 1 秒仍阻止");
        Check(UpdateService.IsAutoCheckDue(null, resetUtc, limitedResult.RetryAfterUtc), "到达 RetryAfterUtc 后恢复自动检查");
        Check(UpdateService.IsRetryAllowed(limitedResult.RetryAfterUtc, fixedNowUtc.AddHours(-5)),
            "时钟大幅回调超过最大冷却上限时不永久锁死");

        var retryAfterHandler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return response;
        });
        var retryAfterResult = UpdateService.Create(
            new Version(0, 4, 6),
            handler: retryAfterHandler,
            noRedirectHandler: retryAfterHandler,
            timeProvider: clock).CheckAsync().GetAwaiter().GetResult();
        Check(retryAfterResult.Status == UpdateCheckStatus.Failed
            && retryAfterResult.RetryAfterUtc == fixedNowUtc.AddMinutes(10)
            && retryAfterResult.ErrorMessage == "GitHub 访问受限（当前出口 IP 请求过多），请于 22:10 后重试或前往发布页查看",
            "429 Retry-After 解析冷却时间与本地提示");

        var bodyOnly403Handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"message":"API rate limit exceeded for 38.175.104.157."}""")
        });
        var bodyOnlyResult = UpdateService.Create(
            new Version(0, 4, 6),
            handler: bodyOnly403Handler,
            noRedirectHandler: bodyOnly403Handler,
            timeProvider: clock).CheckAsync().GetAwaiter().GetResult();
        Check(bodyOnlyResult is { Status: UpdateCheckStatus.Failed, ErrorMessage: "GitHub 访问受限（当前出口 IP 请求过多），请稍后重试或前往发布页查看" },
            "无重置头但响应体含 rate limit 的 403 也能精准识别");
    }

    internal static void TestErrorClassificationForProxyTlsDnsAndHttpIOException()
    {
        var proxy407 = new UpdateService(new Version(0, 4, 6),
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired)))
            .CheckAsync().GetAwaiter().GetResult();
        Check(proxy407 is { Status: UpdateCheckStatus.Failed, ErrorMessage: "代理连接失败，请检查系统代理设置" },
            "407 映射为代理连接失败");

        var proxyTunnel = new UpdateService(new Version(0, 4, 6),
            new StubHandler(_ => throw new HttpRequestException(HttpRequestError.ProxyTunnelError)))
            .CheckAsync().GetAwaiter().GetResult();
        Check(proxyTunnel is { Status: UpdateCheckStatus.Failed, ErrorMessage: "代理连接失败，请检查系统代理设置" },
            "ProxyTunnelError 映射为代理连接失败");

        var dnsError = new UpdateService(new Version(0, 4, 6),
            new StubHandler(_ => throw new HttpRequestException(HttpRequestError.NameResolutionError)))
            .CheckAsync().GetAwaiter().GetResult();
        Check(dnsError is { Status: UpdateCheckStatus.Failed, ErrorMessage: "无法解析 GitHub 域名，请检查网络或 DNS 设置" },
            "NameResolutionError 映射为 DNS 失败");

        var tlsError = new UpdateService(new Version(0, 4, 6),
            new StubHandler(_ => throw new HttpRequestException(HttpRequestError.SecureConnectionError)))
            .CheckAsync().GetAwaiter().GetResult();
        Check(tlsError is { Status: UpdateCheckStatus.Failed, ErrorMessage: "安全连接（TLS）失败，请检查系统时间或代理证书" },
            "SecureConnectionError 映射为 TLS 失败");

        var ioError = new UpdateService(new Version(0, 4, 6),
            new StubHandler(_ => throw new HttpIOException(HttpRequestError.ResponseEnded, "premature EOF")))
            .CheckAsync().GetAwaiter().GetResult();
        Check(ioError is { Status: UpdateCheckStatus.Failed, ErrorMessage: "网络连接中断，请稍后重试" },
            "HttpIOException 被安全捕获并映射为网络连接中断");

        var serviceUnavailable = new UpdateService(new Version(0, 4, 6),
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))
            .CheckAsync().GetAwaiter().GetResult();
        Check(serviceUnavailable is { Status: UpdateCheckStatus.Failed, ErrorMessage: "GitHub 服务暂时不可用（HTTP 503），请稍后重试" },
            "503 映射为服务暂时不可用");

        var genericConnection = new UpdateService(new Version(0, 4, 6),
            new StubHandler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError)))
            .CheckAsync().GetAwaiter().GetResult();
        Check(genericConnection is { Status: UpdateCheckStatus.Failed, ErrorMessage: "网络请求失败" },
            "常规连接异常保留网络请求失败提示");

        var downloadDir = Path.Combine(Path.GetTempPath(), $"BingLan-update-io-{Guid.NewGuid():N}");
        try
        {
            var trustedUrl = new Uri("https://github.com/keros68/binglan/releases/download/v0.4.8/BingLan-Setup-0.4.8.exe");
            var asset = new UpdateAsset("BingLan-Setup-0.4.8.exe", trustedUrl,
                InstallerBytes.Length, Sha256Hex(InstallerBytes));
            var downloadService = new UpdateService(new Version(0, 4, 6),
                new StubHandler(_ => throw new HttpIOException(HttpRequestError.ResponseEnded, "stream cut")));
            ExpectFailure(() => downloadService.DownloadAsync(asset, downloadDir), "网络连接中断，请稍后重试");

            // 网络流读取期间抛出普通 IOException（如 SocketException）应报网络中断，而非本地写盘失败
            var socketCutService = new UpdateService(new Version(0, 4, 6),
                new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new FailingReadStream())
                }));
            ExpectFailure(() => socketCutService.DownloadAsync(asset, downloadDir), "网络连接中断，请稍后重试");
        }
        finally
        {
            if (Directory.Exists(downloadDir))
            {
                Directory.Delete(downloadDir, true);
            }
        }
    }

    internal static void TestDiscoveryFallbackAndSubTimeoutResilience()
    {
        // D2: github.com 遇到网络异常或非限流 403 时回退 api.github.com，保持与旧版网络兼容
        var apiFallbackCalls = 0;
        var blockedWebHandler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "github.com")
            {
                throw new HttpRequestException(HttpRequestError.ConnectionError, "web blocked");
            }
            if (request.RequestUri.AbsoluteUri == UpdateService.LatestReleaseEndpoint)
            {
                apiFallbackCalls++;
                return Json(ReleaseJson("v0.4.8", assetName: "BingLan-Setup-0.4.8.exe",
                    downloadUrl: "https://github.com/keros68/binglan/releases/download/v0.4.8/BingLan-Setup-0.4.8.exe"));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var fromApi = new UpdateService(new Version(0, 4, 6), blockedWebHandler)
            .CheckAsync().GetAwaiter().GetResult();
        Check(fromApi is { Status: UpdateCheckStatus.Available, Release: { Version.Minor: 4, Version.Build: 8 } }
            && apiFallbackCalls == 1,
            "github.com 连接失败时应回退 api.github.com 并成功发现新版本");

        var nonRateLimit403Handler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "github.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("Access denied by corporate proxy")
                };
            }
            if (request.RequestUri.AbsoluteUri == UpdateService.LatestReleaseEndpoint)
            {
                return Json(ReleaseJson("v0.4.8", assetName: "BingLan-Setup-0.4.8.exe",
                    downloadUrl: "https://github.com/keros68/binglan/releases/download/v0.4.8/BingLan-Setup-0.4.8.exe"));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var fromApiAfter403 = new UpdateService(new Version(0, 4, 6), nonRateLimit403Handler)
            .CheckAsync().GetAwaiter().GetResult();
        Check(fromApiAfter403.Status == UpdateCheckStatus.Available,
            "github.com 返回非限流 403 时应回退 api.github.com");

        // D7: 若外部注入了自动跟随重定向的 handler（返回 200 且最终 RequestUri 为 /releases/tag/），仍保持 0 次 API 调用
        var autoRedirectApiCalls = 0;
        var autoRedirectHandler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                autoRedirectApiCalls++;
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(request.Method,
                    "https://github.com/keros68/binglan/releases/tag/v0.4.8")
            };
        });
        var autoRedirectResult = new UpdateService(new Version(0, 4, 8), autoRedirectHandler)
            .CheckAsync().GetAwaiter().GetResult();
        Check(autoRedirectResult.Status == UpdateCheckStatus.UpToDate && autoRedirectApiCalls == 0,
            "自动重定向 handler 返回 200 且终址为 /releases/tag/ 时仍保持零 API 调用");

        // D3: API tag 查询卡死触发子超时，不耗尽总超时，后续 SHA256SUMS.txt + HEAD 降级仍成功
        var expectedSha = Sha256Hex(InstallerBytes);
        var checksumsCrlf = $"# BingLan release checksums\r\n" +
                            $"{expectedSha.ToLowerInvariant()}\t*BingLan-Setup-0.4.8.exe\r\n" +
                            $"0D5DC0353FCA7B857D57EB8F8CD9E702B6FDACE7039589BB5F351D72C694699A  BingLan-0.4.8-portable.zip\r\n";
        var stallingApiHandler = new AsyncStubHandler(async (request, ct) =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Head && uri == UpdateService.LatestReleasePageEndpoint)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://github.com/keros68/binglan/releases/tag/v0.4.8");
                return redirect;
            }
            if (uri == "https://api.github.com/repos/keros68/binglan/releases/tags/v0.4.8")
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (uri == "https://github.com/keros68/binglan/releases/download/v0.4.8/SHA256SUMS.txt")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(checksumsCrlf, Encoding.UTF8, "text/plain")
                };
            }
            if (request.Method == HttpMethod.Head
                && uri == "https://github.com/keros68/binglan/releases/download/v0.4.8/BingLan-Setup-0.4.8.exe")
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([])
                };
                head.Content.Headers.ContentLength = 47_321_600;
                return head;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var subTimeoutResult = new UpdateService(new Version(0, 4, 6), stallingApiHandler, TimeSpan.FromMilliseconds(900))
            .CheckAsync().GetAwaiter().GetResult();
        Check(subTimeoutResult is { Status: UpdateCheckStatus.Available, Release: { Installer: not null } }
            && string.Equals(subTimeoutResult.Release!.Installer!.Sha256, expectedSha, StringComparison.OrdinalIgnoreCase),
            "API tag 子超时不耗尽总预算，仍可走 SHA256SUMS.txt（CRLF/* 注释变体）降级成功");
    }

    internal static void TestApiPathRateLimitAndExtremeResetValues()
    {
        var fixedNowUtc = new DateTimeOffset(2026, 11, 24, 14, 0, 0, TimeSpan.Zero);
        var chinaZone = TimeZoneInfo.CreateCustomTimeZone("TestUTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        var clock = new FixedTimeProvider(fixedNowUtc, chinaZone);

        // T3 & D9: 发现阶段回退到 api.github.com，API 返回 403 + X-RateLimit-Reset + 存在 10 分钟时钟偏差的 Date 头
        var apiCalled = 0;
        var serverNowUtc = fixedNowUtc.AddMinutes(-10); // 服务器时钟比本机慢 10 分钟
        var serverResetUtc = serverNowUtc.AddMinutes(24); // 距服务器当前时间 24 分钟后重置 => 本地 22:24
        var apiRateLimitHandler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "github.com")
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (request.RequestUri.AbsoluteUri == UpdateService.LatestReleaseEndpoint)
            {
                apiCalled++;
                var resp = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    ReasonPhrase = "rate limit exceeded"
                };
                resp.Headers.Date = serverNowUtc;
                resp.Headers.Add("X-RateLimit-Remaining", "0");
                resp.Headers.Add("X-RateLimit-Reset", serverResetUtc.ToUnixTimeSeconds().ToString());
                return resp;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var apiLimited = UpdateService.Create(
            new Version(0, 4, 6),
            handler: apiRateLimitHandler,
            noRedirectHandler: apiRateLimitHandler,
            timeProvider: clock).CheckAsync().GetAwaiter().GetResult();
        Check(apiCalled == 1 && apiLimited.Status == UpdateCheckStatus.Failed,
            "回退到 API 路径后命中 403 限流应返回失败");
        Check(apiLimited.RetryAfterUtc == fixedNowUtc.AddMinutes(24)
            && apiLimited.ErrorMessage == "GitHub 访问受限（当前出口 IP 请求过多），请于 22:24 后重试或前往发布页查看",
            $"API 路径限流应按响应 Date 校正偏差得到 22:24，实际：{apiLimited.ErrorMessage}");

        // D1 & T5: 超大 X-RateLimit-Reset（超过 DateTimeOffset.MaxValue）与超大 Retry-After 均不抛异常且安全钳制
        foreach (var rawReset in new[] { "999999999999999", long.MaxValue.ToString() })
        {
            var hugeResetHandler = new StubHandler(_ =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Forbidden);
                resp.Headers.Add("X-RateLimit-Remaining", "0");
                resp.Headers.Add("X-RateLimit-Reset", rawReset);
                return resp;
            });
            var hugeResetResult = UpdateService.Create(
                new Version(0, 4, 6),
                handler: hugeResetHandler,
                noRedirectHandler: hugeResetHandler,
                timeProvider: clock).CheckAsync().GetAwaiter().GetResult();
            Check(hugeResetResult.Status == UpdateCheckStatus.Failed
                && hugeResetResult.RetryAfterUtc == fixedNowUtc + UpdateService.MaxRateLimitCooldown,
                $"超大 X-RateLimit-Reset={rawReset} 不崩溃且钳制到 MaxRateLimitCooldown");
        }

        var hugeRetryAfterHandler = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromDays(365));
            return resp;
        });
        var hugeRetryAfterResult = UpdateService.Create(
            new Version(0, 4, 6),
            handler: hugeRetryAfterHandler,
            noRedirectHandler: hugeRetryAfterHandler,
            timeProvider: clock).CheckAsync().GetAwaiter().GetResult();
        Check(hugeRetryAfterResult.Status == UpdateCheckStatus.Failed
            && hugeRetryAfterResult.RetryAfterUtc == fixedNowUtc + UpdateService.MaxRateLimitCooldown,
            "超大 Retry-After 不崩溃且钳制到 MaxRateLimitCooldown");
    }

    internal static void TestVerifiedDownload()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"BingLan-update-{Guid.NewGuid():N}");
        try
        {
            var trustedUrl = new Uri("https://github.com/keros68/binglan/releases/download/v0.3.0/BingLan-Setup-0.3.0.exe");
            var asset = new UpdateAsset("BingLan-Setup-0.3.0.exe", trustedUrl,
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

            // D8 & T1: 不可信初始 URL 与不可信最终重定向域名均被强制拒绝
            var networkCalled = 0;
            var untrustedAsset = new UpdateAsset("BingLan-Setup-0.3.0.exe",
                new Uri("https://evil.example.com/BingLan-Setup-0.3.0.exe"),
                InstallerBytes.Length, Sha256Hex(InstallerBytes));
            var rejectInitial = new UpdateService(new Version(0, 2, 2), new StubHandler(_ =>
            {
                networkCalled++;
                return Bytes(InstallerBytes);
            }));
            ExpectFailure(() => rejectInitial.DownloadAsync(untrustedAsset, directory), "下载地址不受信任");
            Check(networkCalled == 0, "不可信初始下载 URL 在发起请求前即被拒绝");

            var rejectRedirect = new UpdateService(new Version(0, 2, 2), new StubHandler(_ =>
            {
                var resp = Bytes(InstallerBytes);
                resp.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example.com/redirect.exe");
                return resp;
            }));
            ExpectFailure(() => rejectRedirect.DownloadAsync(asset, directory), "下载地址不受信任");
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo localTimeZone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override TimeZoneInfo LocalTimeZone => localTimeZone;
    }

    private sealed class AsyncStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class FailingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("connection reset", new System.Net.Sockets.SocketException(10054));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("connection reset", new System.Net.Sockets.SocketException(10054));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
