using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

// Shop / payment / newbie / battle-pass domain, ported from Alpha-Persei-Cluster operations.py
// (paid_entitlement_service, shop_service, recharge_service, newbie_*_service). Payments are local:
// nothing real is charged; "purchases" only change local entitlement state.

/// <summary>Result-only acknowledgements whose server effect is just a UI flag (red dots, tags, views).</summary>
public sealed class SimpleAckHandler(uint command, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => command;

    public ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<GameResponse>>([HandlerHelpers.ResultOnly(codec, request, command + 1)]);

    /// <summary>12024 unclaimed tip read, 12100 text language, 16016 pool UP, 17028 accumulate view,
    /// 20014/20020 shop refresh / red dot, 34036 pass level buy, 34114/34116 big card buy / expire tip,
    /// 59016..59024 newbie "new" tags.</summary>
    public static readonly uint[] Commands = [12024, 12100, 17028, 34036, 34116, 44014, 44032, 59016, 59018, 59020, 59022, 59024];
}

/// <summary>Lobby BGM (12026 -> 12027 + 12029 push).</summary>
public sealed class SetBgmHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 12026;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var id = HandlerHelpers.ReadUInt(r.Body, "id");
        await accounts.SetGameUserIntAsync(r.AccountId, "bgm_id", (int)id, ct);
        return [HandlerHelpers.ResultOnly(codec, r, 12027),
            HandlerHelpers.Reply(codec, r, 12029, new Dictionary<string, object?> { ["id"] = id })];
    }
}

/// <summary>Birthday (12030 -> 12031 + 12033 push).</summary>
public sealed class SetBirthdayHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 12030;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var month = HandlerHelpers.ReadUInt(r.Body, "month");
        var day = HandlerHelpers.ReadUInt(r.Body, "day");
        if (month is < 1 or > 12 || day is < 1 or > 31) return [HandlerHelpers.ResultOnly(codec, r, 12031, 2)];
        await accounts.SetGameUserIntAsync(r.AccountId, "birth_month", (int)month, ct);
        await accounts.SetGameUserIntAsync(r.AccountId, "birth_day", (int)day, ct);
        return [HandlerHelpers.ResultOnly(codec, r, 12031),
            HandlerHelpers.Reply(codec, r, 12033, new Dictionary<string, object?> { ["month"] = month, ["day"] = day })];
    }
}

/// <summary>Activity sign-in (11081 -> 11082): reuses the cumulative sign service.</summary>
public sealed class ActivitySignHandler(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 11081;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var aid = HandlerHelpers.ReadUInt(r.Body, "activity_id");
        var result = await accounts.ClaimCumulativeSignAsync(r.AccountId, (int)aid, config, ct);
        IReadOnlyList<(long, long)> rewards = result.Rewards is { Count: > 0 } list ? list.Select(x => (x.ItemId, x.Count)).ToArray()
            : result.ItemId > 0 ? [(result.ItemId, result.ItemCount)] : [];
        return await InventorySync.ReplyWithSyncAsync(accounts, codec, r, 11082, new()
        {
            ["result"] = 0u, ["sign_num"] = (uint)Math.Max(0, result.SignCount), ["today_buy_num"] = 0u, ["left_sign_time"] = 0u,
            ["item_list"] = InventorySync.Rewards(rewards)
        }, rewards.Select(x => x.Item1), ct);
    }
}

/// <summary>Free daily fatigue dessert (12046 -> 12047): +60 fatigue (item 4) once per slot (11h / 18h) per day.</summary>
public sealed class DailyFatigueDessertHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 12046;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var type = HandlerHelpers.ReadUInt(r.Body, "type");
        if (type is not (11 or 18)) return [HandlerHelpers.ResultOnly(codec, r, 12047, 2)];
        if (await accounts.TryClaimTodayAsync(r.AccountId, $"dessert_{type}", ct))
            await accounts.AddItemAsync(r.AccountId, 4, 60, ct);
        return await InventorySync.ReplyWithSyncAsync(accounts, codec, r, 12047, new() { ["result"] = 0u }, [4], ct);
    }
}

/// <summary>Exchange flowers (free 32 first, then paid 31/30) for item 1 (15020 -> 15021).</summary>
public sealed class FlowerToDiamondHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 15020;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        long num = HandlerHelpers.ReadUInt(r.Body, "num");
        if (num <= 0) return [HandlerHelpers.ResultOnly(codec, r, 15021, 2)];
        var costs = new List<(long, long)>();
        var left = num;
        foreach (var id in new long[] { 32, 31, 30 })
        {
            if (left == 0) break;
            var take = Math.Min(left, await accounts.GetItemCountAsync(r.AccountId, id, ct));
            if (take > 0) { costs.Add((id, take)); left -= take; }
        }
        var ok = left == 0 && await accounts.ConsumeAndGrantAsync(r.AccountId, costs, [(1, num)], ct);
        return await InventorySync.ReplyWithSyncAsync(accounts, codec, r, 15021, new()
        {
            ["result"] = ok ? 0u : 2104u, ["reward_list"] = ok ? InventorySync.Rewards([(1, num)]) : Array.Empty<object>()
        }, [1, 30, 31, 32], ct);
    }
}

/// <summary>Accumulated-login tier rewards (17030 -> 17031) from accumulate_login_cfg.json.</summary>
public sealed class AccumulateSignRewardHandler(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 17030;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var cfg = config.Get("accumulate_login_cfg.json");
        var granted = new List<(long, long)>();
        foreach (var id in HandlerHelpers.ReadIdList(r.Body, "id_list"))
        {
            if (cfg is not { } c || !c.TryGetProperty(id.ToString(), out var tier)) return [HandlerHelpers.ResultOnly(codec, r, 17031, 2)];
            if (!await accounts.TryClaimAsync(r.AccountId, "accumulate_sign", id.ToString(), ct)) continue;
            granted.AddRange(await accounts.GrantItemsAsync(r.AccountId, InventorySync.Pairs(tier.GetProperty("reward")), ct));
        }
        return await InventorySync.ReplyWithSyncAsync(accounts, codec, r, 17031, new()
        {
            ["result"] = 0u, ["item"] = InventorySync.Rewards(granted)
        }, granted.Select(x => x.Item1), ct);
    }
}

/// <summary>Local payment order (34010 -> 34011): returns a completed local order; nothing is charged.</summary>
public sealed class LocalPayOrderHandler(ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 34010;

    public ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return ValueTask.FromResult<IReadOnlyList<GameResponse>>([HandlerHelpers.Reply(codec, r, 34011, new Dictionary<string, object?>
        {
            ["result"] = 0u,
            ["order"] = new Dictionary<string, object?>
            {
                ["order_id"] = $"local-{r.AccountId}-{now}", ["goods_id"] = HandlerHelpers.ReadUInt(r.Body, "id"),
                ["num"] = Math.Max(1u, HandlerHelpers.ReadUInt(r.Body, "number", 1)), ["value"] = 0u, ["sign"] = "", ["state"] = 1u,
                ["channel"] = 0u, ["platform"] = 0u, ["create_timestamp"] = now, ["extra_data"] = "",
                ["shop_id"] = HandlerHelpers.ReadUInt(r.Body, "shop_id"), ["shop_goods_id"] = HandlerHelpers.ReadUInt(r.Body, "buy_id"),
                ["is_web_recharge"] = false
            }
        })]);
    }
}

/// <summary>Total / version-limited recharge tier rewards (34012 -> 34013, 34118 -> 34119) from total_recharge_cfg.json.</summary>
public sealed class RechargeBonusHandler(uint command, string listField, GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => command;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var tiers = config.Get("total_recharge_cfg.json") is { } c && c.TryGetProperty("tiers", out var t) ? t : default;
        var granted = new List<(long, long)>();
        var claimed = new List<uint>();
        foreach (var id in HandlerHelpers.ReadIdList(r.Body, "id_list"))
        {
            if (tiers.ValueKind != JsonValueKind.Array) break;
            var tier = tiers.EnumerateArray().FirstOrDefault(x => x.GetProperty("id").GetInt64() == id);
            if (tier.ValueKind != JsonValueKind.Object || !await accounts.TryClaimAsync(r.AccountId, $"recharge_{command}", id.ToString(), ct)) continue;
            var items = tier.GetProperty("reward").EnumerateArray().Select(x => (x.GetProperty("id").GetInt64(), x.GetProperty("num").GetInt64()));
            granted.AddRange(await accounts.GrantItemsAsync(r.AccountId, items, ct));
            claimed.Add((uint)id);
        }
        return await InventorySync.ReplyWithSyncAsync(accounts, codec, r, command + 1, new()
        {
            ["result"] = 0u, [listField] = claimed.ToArray(), ["reward_list"] = InventorySync.Rewards(granted)
        }, granted.Select(x => x.Item1), ct);
    }
}

/// <summary>Battle pass: 34030 info, 34032 single claim, 34034 claim all (battlepass_cfg.json).</summary>
public static class BattlePass
{
    public const uint CurrentList = 20032;

    internal static JsonElement Entries(SourceGameConfig cfg, out IReadOnlyList<long> ids)
    {
        var root = cfg.Get("battlepass_cfg.json") ?? default;
        ids = [];
        if (root.ValueKind != JsonValueKind.Object) return default;
        var type = root.GetProperty("lists").TryGetProperty(CurrentList.ToString(), out var list) ? list.GetProperty("battlepass_type").GetInt32() : 1;
        ids = root.GetProperty("types").TryGetProperty(type.ToString(), out var arr)
            ? arr.EnumerateArray().Select(e => e.GetProperty("id").GetInt64()).ToArray() : [];
        return root.GetProperty("entries");
    }

    public static async Task<object[]> ReceivedAsync(GameAccountRepository a, long uid, CancellationToken ct) =>
        (await a.GetClaimsAsync(uid, "battlepass", ct)).Select(k => k.Split(':')).Where(p => p.Length == 2)
            .Select(p => (object)new Dictionary<string, object?> { ["id"] = uint.Parse(p[0]), ["is_pay"] = uint.Parse(p[1]) }).ToArray();

    internal static async Task<List<(long, long)>> ClaimAsync(GameAccountRepository a, long uid, JsonElement entries, long id, int isPay, CancellationToken ct)
    {
        if (entries.ValueKind != JsonValueKind.Object || !entries.TryGetProperty(id.ToString(), out var e)) return [];
        if (!await a.TryClaimAsync(uid, "battlepass", $"{id}:{isPay}", ct)) return [];
        return [.. await a.GrantItemsAsync(uid, InventorySync.Pairs(e.GetProperty(isPay == 1 ? "reward_pay" : "reward_free")), ct)];
    }
}

public sealed class BattlePassInfoHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 34030;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return [HandlerHelpers.Reply(codec, r, 34031, new Dictionary<string, object?>
        {
            ["battlepass_list_id"] = BattlePass.CurrentList, ["pay_level"] = 202u, ["is_start"] = 1u, ["weekly_gain_exp"] = 0u,
            ["start_timestamp"] = (uint)(now - 86400), ["end_timestamp"] = (uint)(now + 30 * 86400),
            ["next_refresh_timestamp"] = (uint)(now + 7 * 86400),
            ["receive_info"] = await BattlePass.ReceivedAsync(accounts, r.AccountId, ct)
        })];
    }
}

public sealed class BattlePassClaimHandler(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 34032;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var entries = BattlePass.Entries(config, out _);
        var granted = await BattlePass.ClaimAsync(accounts, r.AccountId, entries, HandlerHelpers.ReadUInt(r.Body, "id"),
            (int)HandlerHelpers.ReadUInt(r.Body, "is_pay"), ct);
        return await InventorySync.ReplyWithSyncAsync(accounts, codec, r, 34033, new()
        {
            ["result"] = 0u, ["receive_info"] = await BattlePass.ReceivedAsync(accounts, r.AccountId, ct),
            ["reward_list"] = InventorySync.Rewards(granted)
        }, granted.Select(x => x.Item1), ct);
    }
}

public sealed class BattlePassClaimAllHandler(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 34034;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var entries = BattlePass.Entries(config, out var ids);
        var granted = new List<(long, long)>();
        foreach (var id in ids)
            foreach (var pay in new[] { 0, 1 })
                granted.AddRange(await BattlePass.ClaimAsync(accounts, r.AccountId, entries, id, pay, ct));
        var merged = granted.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).ToArray();
        return await InventorySync.ReplyWithSyncAsync(accounts, codec, r, 34035, new()
        {
            ["result"] = 0u, ["receive_info"] = await BattlePass.ReceivedAsync(accounts, r.AccountId, ct),
            ["reward_list"] = InventorySync.Rewards(merged)
        }, merged.Select(x => x.Item1), ct);
    }
}

/// <summary>百宝囊 big monthly card daily sign (34110 -> 34111); pool and milestones from paid_entitlement_service.</summary>
public sealed class BigMonthCardSignHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    private static readonly (int Index, long Item, long Num, int Limit)[] Pool =
        [(1, 40103, 3, 5), (2, 40203, 3, 5), (3, 40803, 4, 5), (4, 40701, 1, 5), (5, 40504, 5, 5), (6, 40603, 2, 5)];
    private static readonly (uint Id, int Need, long Item, long Num)[] Milestones =
        [(201, 1, 38, 10), (202, 7, 1, 200), (203, 14, 19, 10), (204, 21, 1, 300)];

    public uint Command => 34110;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var uid = r.AccountId;
        if (!await CardState.BigCardActiveAsync(accounts, uid, ct)) return [HandlerHelpers.ResultOnly(codec, r, 34111, 2)];
        IReadOnlyList<(long, long)> rewards = [];
        var total = (int)await accounts.GetValueAsync(uid, "bigcard", "total", 0, ct);
        if (total < 30 && await accounts.TryClaimTodayAsync(uid, "bigcard_day", ct))
        {
            var pending = new List<(long, long)>();
            var counts = new Dictionary<int, int>();
            foreach (var p in Pool) counts[p.Index] = (int)await accounts.GetValueAsync(uid, "bigcard_rec", p.Index.ToString(), 0, ct);
            var avail = Pool.Where(p => counts[p.Index] < p.Limit).ToArray();
            if (avail.Length > 0)
            {
                var pick = avail[Random.Shared.Next(avail.Length)];
                pending.Add((pick.Item, pick.Num));
                await accounts.SetValueAsync(uid, "bigcard_rec", pick.Index.ToString(), counts[pick.Index] + 1, ct);
            }
            pending.Add((1, 100));
            total++;
            await accounts.SetValueAsync(uid, "bigcard", "total", total, ct);
            foreach (var m in Milestones)
                if (total >= m.Need && await accounts.TryClaimAsync(uid, "bigcard_ms", m.Id.ToString(), ct)) pending.Add((m.Item, m.Num));
            rewards = await accounts.GrantItemsAsync(uid, pending, ct);
        }
        var state = await CardState.BigCardPayloadAsync(accounts, uid, ct);
        var frames = await InventorySync.FramesAsync(accounts, codec, r, rewards.Select(x => x.Item1), ct);
        // Order matters (Alpha-Persei BigMonthCardSignOp): the Lua compares old receipts in 34111, then 34101 replaces the state.
        frames.Add(HandlerHelpers.Reply(codec, r, 34111, new Dictionary<string, object?>
        {
            ["result"] = 0u, ["rewards"] = InventorySync.Rewards(rewards), ["daily_record"] = state["daily_record"], ["is_sign"] = 1u,
            ["total_sign_times"] = state["total_sign_times"], ["total_sign_receive_list"] = state["total_sign_receive_list"]
        }));
        frames.Add(CardState.Push(r, 34101, codec.Encode("sc_34101", state)));
        return frames;
    }
}

/// <summary>Newbie activity rewards: 59004 level (1×200), 59012 daily sign (new_player_cfg sign_rewards),
/// 59014 task-progress tiers, 59006 recharge rewards.</summary>
public sealed class NewbieRewardHandler(uint command, GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => command;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var uid = r.AccountId;
        var cfg = config.Get("new_player_cfg.json");
        IEnumerable<(long, long)> items = [];
        switch (command)
        {
            case 59004:
                if (await accounts.TryClaimAsync(uid, "noob_level", HandlerHelpers.ReadUInt(r.Body, "level").ToString(), ct)) items = [(1, 200)];
                break;
            case 59012:
                if (cfg is { } c1 && await accounts.TryClaimTodayAsync(uid, "noob_sign_day", ct))
                {
                    var signIds = c1.GetProperty("noob").GetProperty("noob_sign").EnumerateArray().Select(x => x.GetInt64()).ToArray();
                    var done = (await accounts.GetClaimsAsync(uid, "noob_sign", ct)).Count;
                    if (done < signIds.Length)
                    {
                        var sid = signIds[done].ToString();
                        await accounts.TryClaimAsync(uid, "noob_sign", sid, ct);
                        if (c1.GetProperty("sign_rewards").TryGetProperty(sid, out var rw)) items = [(rw[0].GetInt64(), rw[1].GetInt64())];
                    }
                }
                break;
            case 59014:
                var id = HandlerHelpers.ReadUInt(r.Body, "id");
                if (cfg is { } c2 && await accounts.TryClaimAsync(uid, "noob_progress", id.ToString(), ct))
                {
                    var tier = c2.GetProperty("noob").GetProperty("noob_task_progress_reward").EnumerateArray()
                        .FirstOrDefault(t => t[0].GetInt64() == id);
                    if (tier.ValueKind == JsonValueKind.Array) items = [(tier[1][0].GetInt64(), tier[1][1].GetInt64())];
                }
                break;
            case 59006:
                var key = $"{HandlerHelpers.ReadUInt(r.Body, "type")}:{HandlerHelpers.ReadUInt(r.Body, "reward_type")}";
                if (await accounts.TryClaimAsync(uid, "noob_recharge", key, ct)) items = [(1, 300)];
                break;
        }
        var granted = await accounts.GrantItemsAsync(uid, items, ct);
        return await InventorySync.ReplyWithSyncAsync(accounts, codec, r, command + 1, new()
        {
            ["result"] = 0u, ["reward_list"] = InventorySync.Rewards(granted)
        }, granted.Select(x => x.Item1), ct);
    }

    public static readonly uint[] Commands = [59004, 59006, 59012, 59014];
}
