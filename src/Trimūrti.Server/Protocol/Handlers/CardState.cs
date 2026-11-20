namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// Monthly card (恒定观测, sc_34023) and big monthly card (百宝囊, sc_34101) state, built from the player's
/// saved claims. Sent at login and after every buy/claim so the client knows whether today's reward is
/// taken (otherwise its login popup keeps reopening) and whether 百宝囊 is active.
/// </summary>
public static class CardState
{
    private const long Day = 86400;
    public const long BigCardPrice = 900;

    public static async Task<bool> MonthCardSignedTodayAsync(GameAccountRepository a, long uid, CancellationToken ct) =>
        await a.HasClaimAsync(uid, "monthcard_day", GameAccountRepository.TodayKey, ct);

    public static async Task<byte[]> MonthCardAsync(GameAccountRepository a, ProtoCodec codec, long uid, CancellationToken ct) =>
        codec.Encode("sc_34023", new Dictionary<string, object?>
        {
            ["monthly_card_num"] = 1u,
            // Long-term card: same far-future deadline Alpha-Persei sends (res_version_manager.FAR_FUTURE_TIMESTAMP).
            ["monthly_card_timestamp"] = 1957554000u,
            ["is_sign"] = await MonthCardSignedTodayAsync(a, uid, ct) ? 1u : 0u
        });

    /// <summary>Purchase day used by the client: deadline = buy_timestamp 05:00 + 31 days.</summary>
    public static async Task<long> BigCardBuyTimestampAsync(GameAccountRepository a, long uid, CancellationToken ct) =>
        await a.GetValueAsync(uid, "bigcard", "buy_ts", 0, ct);

    public static async Task<bool> BigCardActiveAsync(GameAccountRepository a, long uid, CancellationToken ct)
    {
        var buy = await BigCardBuyTimestampAsync(a, uid, ct);
        return buy > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < buy + 31 * Day;
    }

    public static async Task<Dictionary<string, object?>> BigCardPayloadAsync(GameAccountRepository a, long uid, CancellationToken ct)
    {
        var record = new List<object>();
        for (var i = 1; i <= 6; i++)
        {
            var n = await a.GetValueAsync(uid, "bigcard_rec", i.ToString(), 0, ct);
            if (n > 0) record.Add(new Dictionary<string, object?> { ["index"] = (uint)i, ["times"] = (uint)n });
        }
        return new Dictionary<string, object?>
        {
            ["buy_timestamp"] = (uint)await BigCardBuyTimestampAsync(a, uid, ct),
            ["is_sign"] = await a.HasClaimAsync(uid, "bigcard_day", GameAccountRepository.TodayKey, ct) ? 1u : 0u,
            ["total_sign_times"] = (uint)await a.GetValueAsync(uid, "bigcard", "total", 0, ct),
            ["total_sign_receive_list"] = (await a.GetClaimsAsync(uid, "bigcard_ms", ct)).Select(uint.Parse).ToArray(),
            ["daily_record"] = record.ToArray(),
            ["is_expire_tip"] = 0u,
            ["template_id"] = 2u
        };
    }

    public static async Task<byte[]> BigCardAsync(GameAccountRepository a, ProtoCodec codec, long uid, CancellationToken ct) =>
        codec.Encode("sc_34101", await BigCardPayloadAsync(a, uid, ct));

    /// <summary>sc_59009 newbie recharge rights (no first-recharge / first-monthly-card / pass rewards locally).</summary>
    public static byte[] NewbieRecharge(ProtoCodec codec) => codec.Encode("sc_59009", new Dictionary<string, object?>
    {
        ["newbie_recharge_reward"] = new Dictionary<string, object?>
        {
            ["first_recharge_reward"] = new Dictionary<string, object?> { ["first_gear_recharge_reward"] = 0u, ["second_gear_recharge_flag"] = false, ["now_sign_times"] = 0u, ["last_sign_timestamp"] = 0u, ["is_new_recharge_6"] = false, ["is_new_recharge_18"] = false },
            ["first_monthly_card_reward"] = new Dictionary<string, object?> { ["flag"] = false, ["role_reward_flag"] = false, ["sign_times"] = 0u, ["sign_reward_flag"] = false, ["is_new_tag_role_reward"] = false, ["is_new_tag_sign_reward"] = false },
            ["first_battlepass_reward"] = new Dictionary<string, object?> { ["first_battlepass_reward"] = 0u, ["is_new_battlepass_reward"] = false }
        }
    });

    public static GameResponse Push(GameRequest r, uint command, byte[] payload) => new(command, 0, (ushort)(r.ServerIndex + 1), payload);
}

/// <summary>Buy 百宝囊 (34114 -> 34115): 900 × item 1, starts a fresh 30-day period, pushes sc_34101.</summary>
public sealed class BigMonthCardBuyHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 34114;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var uid = r.AccountId;
        if (await CardState.BigCardActiveAsync(accounts, uid, ct)) return [HandlerHelpers.ResultOnly(codec, r, 34115, 2)];
        if (!await accounts.ConsumeAndGrantAsync(uid, [(1, CardState.BigCardPrice)], [], ct)) return [HandlerHelpers.ResultOnly(codec, r, 34115, 2104)];
        // Purchase day at 05:00: the client counts elapsed sign days from buy_timestamp; an earlier stamp makes it
        // think a day is unclaimed and reopen the 百宝囊 popup.
        await accounts.SetValueAsync(uid, "bigcard", "buy_ts", GameAccountRepository.TodayStartUnix, ct);
        await accounts.SetValueAsync(uid, "bigcard", "total", 0, ct);
        await accounts.DeleteClaimsAsync(uid, "bigcard_rec", ct);
        await accounts.DeleteClaimsAsync(uid, "bigcard_ms", ct);
        await accounts.DeleteClaimsAsync(uid, "bigcard_day", ct);
        // Same order as Alpha-Persei BigMonthCardBuyOp: 34115, then 34101, then inventory refresh.
        var frames = new List<GameResponse>
        {
            HandlerHelpers.ResultOnly(codec, r, 34115),
            CardState.Push(r, 34101, await CardState.BigCardAsync(accounts, codec, uid, ct))
        };
        frames.AddRange(await InventorySync.FramesAsync(accounts, codec, r, [1], ct));
        return frames;
    }
}
