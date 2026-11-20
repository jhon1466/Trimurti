using System.Text.Json;

namespace Trimūrti.Server;

public static class SdkLogin
{
    public static async Task<IResult> HandleAsync(HttpRequest request, AccountStore accounts, CancellationToken cancellationToken)
    {
        string login;
        string password;
        var isTokenOrMixLogin = request.Path.Value?.Contains("token", StringComparison.OrdinalIgnoreCase) == true
            || request.Path.Value?.Contains("/mix-sdk-api/pass/user/login", StringComparison.OrdinalIgnoreCase) == true;

        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(cancellationToken);
            login = form["username"].FirstOrDefault()
                ?? form["phone"].FirstOrDefault()
                ?? form["account"].FirstOrDefault()
                ?? form["loginName"].FirstOrDefault()
                ?? form["mobile"].FirstOrDefault()
                ?? "";
            password = form["password"].FirstOrDefault()
                ?? form["pwd"].FirstOrDefault()
                ?? "";
            if (!isTokenOrMixLogin && (form.ContainsKey("token") || form.ContainsKey("channelToken") || form.ContainsKey("channelUserId")))
                isTokenOrMixLogin = true;
        }
        else
        {
            try
            {
                using var json = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
                var root = json.RootElement;
                login = TryGetAnyString(root, "username", "phone", "account", "loginName", "mobile");
                password = TryGetAnyString(root, "password", "pwd");
                if (!isTokenOrMixLogin && !string.IsNullOrEmpty(TryGetAnyString(root, "token", "channelToken", "channelUserId")))
                    isTokenOrMixLogin = true;
            }
            catch (JsonException) { return Results.BadRequest(new { errorCode = "400", message = "Malformed login request." }); }
        }

        LoginSession? session;
        if (string.IsNullOrEmpty(login) && string.IsNullOrEmpty(password) && isTokenOrMixLogin)
        {
            session = await accounts.LoginOrCreateAsync("local-player", "trimurti-local-player", cancellationToken);
            login = "local-player";
        }
        else
        {
            session = await accounts.LoginOrCreateAsync(login, password, cancellationToken);
        }

        if (session is null) return Results.Json(new { errorCode = "401", message = "Invalid local account or password." }, statusCode: StatusCodes.Status401Unauthorized);
        var uid = long.Parse(session.AccountId, System.Globalization.CultureInfo.InvariantCulture);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var logoffApply = new { logoffDate = nowMs, logoffRemainTime = 0, dealStatus = "INVALID" };
        const string appId = "bd2fc85f8ecd35fd13deec46560bb36a";
        var data = new Dictionary<string, object?>
        {
            ["id"] = uid,
            ["userId"] = uid,
            ["uid"] = uid.ToString(),
            ["accountId"] = uid.ToString(),
            ["regionNo"] = "86",
            ["phone"] = login,
            ["nickName"] = login,
            ["name"] = login,
            ["token"] = session.Token,
            ["channelId"] = 9,
            ["appId"] = appId,
            ["channelAppId"] = appId,
            ["channelLoginType"] = 0,
            ["channelUdid"] = "local-udid",
            ["channelUserId"] = uid.ToString(),
            ["channelUserNickName"] = login,
            ["channelToken"] = session.Token,
            ["enableLogin"] = true,
            ["enablePay"] = true,
            ["enableNewRole"] = true,
            ["createDate"] = nowMs,
            ["valid"] = false,
            ["locked"] = false,
            ["newMsgCount"] = 0,
            ["totalRecharge"] = 0,
            ["totalRechargeDis"] = "0.00",
            ["realNameValid"] = true,
            ["realNameValidResult"] = "succ",
            ["adult"] = true,
            ["realNameUpdateLimit"] = 0,
            ["guest"] = false,
            ["privList"] = Array.Empty<string>(),
            ["privPermList"] = Array.Empty<string>(),
            ["inviteLimit"] = false,
            ["age"] = 25,
            ["registerFlag"] = false,
            ["birthday"] = "19990101",
            ["securityValid"] = false,
            ["loginType"] = "username",
            ["logoffApply"] = logoffApply,
            ["mixUserLogoffApply"] = logoffApply
        };

        return Results.Ok(new Dictionary<string, object?>
        {
            ["code"] = 0,
            ["errorCode"] = "0",
            ["errorMsg"] = "",
            ["message"] = "ok",
            ["data"] = data
        });
    }

    private static string TryGetAnyString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString() ?? "";
        }
        return "";
    }
}
