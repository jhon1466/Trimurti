using System.Text.Json;

namespace Trimūrti.Server;

/// <summary>Additional HTTP/SDK, server-zone, notice, and version endpoints backed by SourceGameConfig.</summary>
public static class SdkEndpoints
{
    private static volatile string _lastPcAppVersion = "308";

    public static void Map(WebApplication app)
    {
        foreach (var prefix in new[] { "", "/sdk-api", "/mix-sdk-api" })
        {
            if (prefix == "/mix-sdk-api")
            {
                app.MapMethods($"{prefix}/pass/user/login", ["GET", "POST"], SdkLogin.HandleAsync);
            }
            app.MapMethods($"{prefix}/pass/user/token/login", ["GET", "POST"], SdkLogin.HandleAsync);
            app.MapMethods($"{prefix}/pass/user/loginByLoginName", ["GET", "POST"], SdkLogin.HandleAsync);
            app.MapMethods($"{prefix}/pass/user/loginByPhoneValid", ["GET", "POST"], SdkLogin.HandleAsync);
            app.MapMethods($"{prefix}/pass/user/loginBySms", ["GET", "POST"], SdkLogin.HandleAsync);
            app.MapMethods($"{prefix}/pass/user/loginByToken", ["GET", "POST"], SdkLogin.HandleAsync);
            app.MapMethods($"{prefix}/pass/user/registerBySms", ["GET", "POST"], SdkLogin.HandleAsync);
            app.MapMethods($"{prefix}/pass/game/updateversion/getLatest", ["GET", "POST"], HandleUpdateVersionLatest);
            app.MapMethods($"{prefix}/pass/game/agreementupdate/getLatest", ["GET", "POST"], HandleAgreementUpdate);
            app.MapMethods($"{prefix}/pass/game/sdkConfig", ["GET", "POST"], (SourceGameConfig config) => ServeJsonConfig(config, "sdk_config.json"));
            app.MapMethods($"{prefix}/pass/gamecfg/getInfo", ["GET", "POST"], HandleGameCfgGetInfo);
            app.MapMethods($"{prefix}/pass/game/getInfo", ["GET", "POST"], HandleGameGetInfo);
            app.MapMethods($"{prefix}/pass/game/notice/getCurrent", ["GET", "POST"], () => Results.Ok(new { errorCode = "0" }));
            app.MapMethods($"{prefix}/pass/game/channelext/getAgeTip", ["GET", "POST"], () => Results.Ok(new { errorCode = "0", data = "" }));
            app.MapMethods($"{prefix}/pass/syscfg/getIndulgeLimitCfgInfo", ["GET", "POST"], HandleAntiAddictionConfig);
            app.MapMethods($"{prefix}/pass/syscfg/getGlobalCfgInfo", ["GET", "POST"], () => Results.Ok(new { errorCode = "0", errorMsg = "", data = new { enableLogin = true, enablePay = true, enableNewRole = true, realNameStatus = "off", indulgeTimeStatus = "off" } }));
            app.MapMethods($"{prefix}/auth/user/indulge/getInfo", ["GET", "POST"], HandleUserAntiAddictionInfo);
            app.MapMethods($"{prefix}/pass/captcha/init", ["GET", "POST"], HandleCaptchaInit);
            app.MapMethods($"{prefix}/pass/basedata/list", ["GET", "POST"], () => Results.Ok(new { code = 0, errorCode = "0", errorMsg = "", data = Array.Empty<object>() }));
            app.MapMethods($"{prefix}/pass/sys/time", ["GET", "POST"], () => Results.Ok(new { errorCode = "0", data = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));
        }

        app.MapMethods("/config", ["GET", "POST"], () => Results.Ok(new { code = 0, msg = "success", data = new { sync_interval = 30, sync_batch_size = 20 } }));
        app.MapGet("/pc/resources/{**file}", HandleLocalPcResource);

        app.MapMethods("/skzy-activity/gateway/get", ["GET", "POST"], HandleGatewayGet);
        app.MapMethods("/gateway/get", ["GET", "POST"], HandleGatewayGet);

        app.MapGet("/sdk-api/config", (SourceGameConfig config) => ServeJsonConfig(config, "sdk_config.json"));
        app.MapGet("/sdk-api/app/config", (SourceGameConfig config) => ServeJsonConfig(config, "sdk_config.json"));
        app.MapGet("/sdk-api/agreement", (SourceGameConfig config) => ServeJsonConfig(config, "sdk_agreement.json"));

        app.MapGet("/notice", (SourceGameConfig config) => ServeJsonConfig(config, "notice_cfg.json"));
        app.MapGet("/announcement", (SourceGameConfig config) => ServeJsonConfig(config, "notice_cfg.json"));

        app.MapGet("/server/list", (SourceGameConfig config) => BuildServerList(config));
        app.MapGet("/api/server-zones", (SourceGameConfig config) => BuildServerList(config));

        app.MapGet("/api/client-versions", (SourceGameConfig config) =>
        {
            var dynamicVersions = config.Get("dynamic_versions.json");
            var supportedVersions = config.Get("supported_client_versions.json");
            return Results.Ok(new
            {
                supported = supportedVersions,
                dynamic = dynamicVersions
            });
        });
    }

    private static readonly HttpClient CdnFallbackClient = new(new SocketsHttpHandler
    {
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
            TargetHost = "download-eo.ys4fun.com"
        },
        ConnectTimeout = TimeSpan.FromSeconds(5)
    })
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    private static async Task<IResult> HandleLocalPcResource(string file, ClientBuildCatalog builds, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(file);
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Data", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "Data", fileName)
        ];
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return Results.File(candidate, "application/octet-stream", fileName, enableRangeProcessing: true);
        }

        var normalizedSubpath = file.Replace('\\', '/').TrimStart('/');
        var cdnPaths = string.Equals(normalizedSubpath, fileName, StringComparison.OrdinalIgnoreCase)
            ? new[] { fileName }
            : [normalizedSubpath, fileName];

        foreach (var cdnPath in cdnPaths)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://43.168.223.93/pc/resources/{cdnPath}");
                req.Headers.Host = "download-eo.ys4fun.com";
                if (httpContext.Request.Headers.TryGetValue("Range", out var rangeHeader) && !string.IsNullOrWhiteSpace(rangeHeader))
                    req.Headers.TryAddWithoutValidation("Range", rangeHeader.ToString());

                using var resp = await CdnFallbackClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    httpContext.Response.StatusCode = (int)resp.StatusCode;
                    httpContext.Response.ContentType = "application/octet-stream";
                    if (resp.Content.Headers.ContentLength is long contentLength)
                        httpContext.Response.ContentLength = contentLength;
                    if (resp.Content.Headers.TryGetValues("Content-Range", out var contentRange))
                        httpContext.Response.Headers["Content-Range"] = contentRange.FirstOrDefault();
                    await resp.Content.CopyToAsync(httpContext.Response.Body, cancellationToken);
                    return Results.Empty;
                }
            }
            catch (Exception) when (httpContext.Response.HasStarted || cancellationToken.IsCancellationRequested)
            {
                return Results.Empty;
            }
            catch (Exception)
            {
                // Ignore CDN errors and try fallback below when offline.
            }
        }

        if (fileName.StartsWith("voice_package_list", StringComparison.OrdinalIgnoreCase))
            return Results.Text("[]", "application/json");
        if (fileName.StartsWith("voice_hash_", StringComparison.OrdinalIgnoreCase))
        {
            var parts = Path.GetFileNameWithoutExtension(fileName).Split('_');
            var lang = parts.Length >= 3 ? parts[2] : "ja";
            var ver = parts.Length >= 4 && long.TryParse(parts[3], out var v) ? v : 30700321L;
            return Results.Ok(new { versionName = lang, version = ver, size = 0, assetHashList = Array.Empty<string>() });
        }
        return Results.NotFound();
    }

    private static IResult HandleCaptchaInit() => Results.Ok(new
    {
        errorCode = "0",
        data = new
        {
            providerType = "geetest_v3",
            success = true,
            clientInitParam = new
            {
                success = false,
                captchaId = "e2ad319e27f7359a23322515c536e643",
                challenge = "",
                newFailback = true,
                geetestUserId = "6b262a7fbdbee8b3134a1355d52c864e",
                offline = true
            }
        }
    });

    private static IResult HandleGameGetInfo() => Results.Ok(new
    {
        code = 0,
        errorCode = 0,
        errorMsg = "",
        data = new { appId = "bd2fc85f8ecd35fd13deec46560bb36a", gameId = "d1a81b0705", gameName = "Trimurti", status = 1 }
    });

    private static IResult HandleAgreementUpdate() => Results.Ok(new
    {
        errorCode = "0",
        data = new
        {
            id = 3,
            createDate = 1712783341000L,
            modifyDate = 1712785252000L,
            gameId = 3,
            title = "更新公告",
            content = "",
            startDate = 1712786400000L,
            status = "on",
            sortWeight = 0
        }
    });

    private static IResult HandleAntiAddictionConfig() => Results.Ok(new
    {
        errorCode = "0",
        errorMsg = "",
        data = new
        {
            realNameTip = "",
            realNameStatus = "off",
            realNameDefResult = "adult",
            indulgeTimeStatus = "off",
            guestIndulgeTimeLimit = 0.0,
            juvenileIndulgeTimeLimitWithHoliday = 0.0,
            juvenileIndulgeTimeLimitWithoutHoliday = 0.0,
            juvenileCurfewStartTime = "00:00",
            juvenileCurfewEndTime = "00:00",
            indulgeRechargeStatus = "off"
        }
    });

    private static IResult HandleUserAntiAddictionInfo() => Results.Ok(new
    {
        errorCode = "0",
        errorMsg = "",
        data = new
        {
            playDuration = 0,
            latestGameStartDate = 0L,
            latestGameEndDate = 0L,
            latestPayDate = 0L,
            totalRecharge = 0,
            adult = true,
            realNameValidResult = "",
            age = 30,
            remainingTime = 86400,
            juvenileEnableStartTime = "00:00",
            juvenileEnableEndTime = "23:59",
            tipForLoginLimit = "",
            tipForKickOffline = "",
            tipForRemainTime = ""
        }
    });

    private static IResult HandleGameCfgGetInfo(HttpRequest request)
    {
        const string appId = "bd2fc85f8ecd35fd13deec46560bb36a";
        return Results.Ok(new
        {
            errorCode = "0",
            data = new
            {
                privacyUrl = $"https://ys4fun-prod-pub.ys4fun.com/privacy/{appId}.html",
                infoCollectionListUrl = $"https://ys4fun-prod-pub.ys4fun.com/privacy/{appId}-collection-info.html",
                thirdSdkPrivacyUrl = $"https://ys4fun-prod-pub.ys4fun.com/privacy/{appId}-include-sdk.html",
                userAgreementUrl = "https://ys4fun-prod-pub.ys4fun.com/terms/agreement.html",
                childrenPrivacyUrl = "https://ys4fun-prod-pub.ys4fun.com/terms/privacy-children.html",
                helpUrl = "https://www.soboten.com/ticketclient/index.html",
                regist = new { pc = true },
                login = new { pc = true, account = true, loginName = true, phone = true, sms = true }
            }
        });
    }

    private static IResult HandleGatewayGet(HttpRequest request)
    {
        var action = request.Query["action"].FirstOrDefault() ?? "";
        var gatewayConfig = new object[]
        {
            new { key = "FORUM_URL_HOME", value = "https://m.bbs.ys4fun.com/" },
            new { key = "USE_SDK_HOT_FIX", value = "true" },
            new { key = "PC_FEEDBACK", value = "https://open.ys4fun.com/web/agreement.html?tab=support" },
            new { key = "PC_SHOP", value = "https://skzy.ys4fun.com/main/ipay/?uidByGame=%s_%s&gameAppId=%s&gameToken=%s" },
            new { key = "FORUM_URL", value = "https://m.bbs.ys4fun.com/gameLogin/?uidByGame=%s_%s&gameAppId=%s&gameToken=%s" },
            new { key = "OFFICIAL_URL", value = "https://skzy.ys4fun.com/m/" },
            new { key = "REST_URL", value = "https://prod-api-activity.ys4fun.com/skzy-activity" }
        };

        if (string.Equals(action, "base", StringComparison.OrdinalIgnoreCase))
        {
            var ver = request.Query["version"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(ver)) _lastPcAppVersion = ver.Trim();
            return Results.Ok(new
            {
                errorCode = "0",
                data = new
                {
                    env = "prod",
                    config = gatewayConfig
                }
            });
        }

        return Results.Ok(new
        {
            errorCode = "0",
            errorMsg = "",
            data = new object[]
            {
                new
                {
                    serverId = "1",
                    serverName = "Trimurti Local",
                    env = "release",
                    ip = "127.0.0.1",
                    port = 8102,
                    newServerFlag = 0,
                    maintain = false,
                    maintainReason = "",
                    config = gatewayConfig,
                    gameUserInfoList = Array.Empty<object>()
                }
            }
        });
    }

    private static IResult HandleUpdateVersionLatest(HttpRequest request, ClientBuildCatalog builds)
    {
        var appVersion = request.Query["appVersion"].FirstOrDefault() ?? "307";
        var resVersion = builds.ActiveBuild;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var assetHash = "assethash_307_321_a66c600ea8c72f003e03969fdd352250.bytes";

        return Results.Ok(new
        {
            errorCode = "0",
            data = new object[]
            {
                new
                {
                    createDate = nowMs,
                    modifyDate = nowMs,
                    sortWeight = 0,
                    type = "noInstall",
                    version = resVersion,
                    versionName = "v5.3.1",
                    matchedAppVersion = appVersion,
                    forceUpdate = false,
                    fileSize = 0,
                    disDate = nowMs,
                    downloadUrl = "https://open.ys4fun.com/pc/resources/;https://download-eo.ys4fun.com/pc/resources/;https://download.ys4fun.com/pc/resources/",
                    extraData = JsonSerializer.Serialize(new { assethash = assetHash }),
                    platformType = "pc",
                    enableWhiteList = false,
                    enableFullPub = true,
                    enableGrayPub = true,
                    assethash = assetHash,
                    batchId = 0
                },
                new
                {
                    createDate = nowMs,
                    modifyDate = nowMs,
                    sortWeight = 0,
                    type = "install",
                    version = "308",
                    versionName = "308",
                    forceUpdate = false,
                    fileSize = 0,
                    disDate = nowMs,
                    downloadUrl = "https://download-eo.ys4fun.com/pc/install/",
                    platformType = "pc",
                    enableWhiteList = false,
                    enableFullPub = true,
                    enableGrayPub = false,
                    batchId = 0
                }
            }
        });
    }

    private static IResult ServeJsonConfig(SourceGameConfig config, string fileName)
    {
        var element = config.Get(fileName);
        return element is { ValueKind: not JsonValueKind.Undefined }
            ? Results.Ok(element)
            : Results.NotFound(new { error = $"Missing config {fileName}" });
    }

    private static IResult BuildServerList(SourceGameConfig config)
    {
        var zones = config.Get("server_zones.json");
        return Results.Ok(new
        {
            gatewayHost = "127.0.0.1",
            gatewayPort = 8102,
            gamePort = 8105,
            zones
        });
    }
}
