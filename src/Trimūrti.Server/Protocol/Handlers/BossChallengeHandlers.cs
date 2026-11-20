using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 梦境再构 (boss challenge), port of Alpha-Persei weekly_challenge_service.get_boss_challenge_data,
/// boss_challenge_cycle, boss_battle_identity and the 45xxx operations. Weekly cycle: Thursday 05:00 UTC+8.
/// Normal mode (1): area 3/4 boss groups with stage stars → star chests. Advanced mode (2): bosses with chosen
/// difficulty/time/affixes → score → point chests. Catalog: boss_challenge_data_complete.json.
/// </summary>
public sealed class BossChallenge(GameAccountRepository accounts, SourceGameConfig config)
{
    private const long LegacyAnchor = 1700000000, Week = 604800;

    public JsonElement Catalog => config.Get("boss_challenge_data_complete.json") ?? config.Get("boss_challenge_catalog.json") ?? default;

    public static (long Start, long Next) Bounds(DateTimeOffset? at = null)
    {
        var now = (at ?? DateTimeOffset.UtcNow).ToOffset(GameClock.Offset);
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, 5, 0, 0, GameClock.Offset).AddDays(-(((int)now.DayOfWeek - 4 + 7) % 7));
        if (start > now) start = start.AddDays(-7);
        return (start.ToUnixTimeSeconds(), start.ToUnixTimeSeconds() + Week);
    }

    /// <summary>1 = normal boss stage, 2 = advanced boss stage, 0 = not a boss challenge battle.</summary>
    public static int Kind(uint stageType, long stageId) =>
        stageType == 100 || stageId is >= 3012201 and <= 3012399 ? 2 : stageType == 10 || stageId is >= 3010101 and <= 3012015 ? 1 : 0;

    public sealed record State(Dictionary<string, object?> Overview, Dictionary<string, object?> Normal, Dictionary<string, object?> Advance,
        int Mode, int AreaId, int AdvanceId, IReadOnlyList<long> NormalGroups, IReadOnlyList<long> AdvanceBosses);

    public async Task<State> LoadAsync(long uid, CancellationToken ct)
    {
        var (start, next) = Bounds();
        var seed = (start - LegacyAnchor) / Week;
        var p = await accounts.GetBossProgressAsync(uid, start, seed, ct);
        var cycle = p.CycleId > 0 ? p.CycleId : seed;
        var cat = Catalog;
        var affixes = (await accounts.GetBossAffixesAsync(uid, ct)).ToDictionary(a => a.BossId);
        var advRows = (await accounts.GetBossAdvanceAsync(uid, ct)).ToDictionary(a => a.BossId);
        var normalRows = (await accounts.GetBossNormalAsync(uid, ct)).ToDictionary(n => n.GroupId);
        var claimed = await accounts.GetBossClaimedAsync(uid, ct);

        // Deterministic per-cycle boss draw (3 bosses, 4 in advanced mode 102).
        var pool = cat.TryGetProperty("advance_pool", out var ap) ? ap : default;
        var poolIds = pool.ValueKind == JsonValueKind.Object ? pool.EnumerateObject().Select(x => long.Parse(x.Name)).OrderBy(x => x).ToList() : [20001L, 20002, 20003, 20004];
        var advIds = Sample(poolIds, p.AdvanceId == 102 ? 4 : 3, new Random((int)(cycle * 10007 + 45101)));
        var areas = cat.GetProperty("normal_areas");
        var area = areas.TryGetProperty(p.AreaId.ToString(), out var ar) ? ar : default;
        var groups = area.ValueKind == JsonValueKind.Object && area.TryGetProperty("boss_groups", out var bg) ? bg.EnumerateArray().Select(x => x.GetInt64()).ToList() : [5009L, 5010, 5023, 5024, 5025, 5026];
        var groupCount = area.ValueKind == JsonValueKind.Object && area.TryGetProperty("boss_nums", out var bn) ? bn.GetInt32() : 3;
        var normalIds = Sample(groups, groupCount, new Random((int)(cycle * 10007 + 45001)));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var advanceList = advIds.Select(id =>
        {
            affixes.TryGetValue(id, out var af);
            advRows.TryGetValue(id, out var row);
            return (object)new Dictionary<string, object?>
            {
                ["id"] = (uint)id, ["unlock_timestamp"] = (uint)(now - 86400), ["max_point"] = (uint)(row?.Score ?? 0),
                ["used_heroes"] = Ints(row?.UsedHeroes), ["last_heroes_cfg"] = row is null ? new uint[] { 0, 0, 0 } : Ints(row.LastHeroesCfg),
                ["affix_index_list"] = Ints(af?.Affixes), ["time_index_list"] = Ints(af?.Times), ["diffculty_index"] = (uint)(af?.Difficulty ?? 1)
            };
        }).ToArray();
        var normalList = normalIds.Select(gid =>
        {
            normalRows.TryGetValue(gid, out var row);
            return (object)new Dictionary<string, object?>
            {
                ["group_id"] = (uint)gid, ["finish_stage"] = (uint)(row?.FinishStage ?? 0),
                ["used_heroes"] = Ints(row?.UsedHeroes), ["last_heroes_cfg"] = row is null ? new uint[] { 0, 0, 0 } : Ints(row.LastHeroesCfg),
                ["star_info"] = StarInfo(row?.StarInfo), ["unlock_timestamp"] = (uint)(now - 86400)
            };
        }).ToArray();

        return new State(
            new() { ["mode"] = (uint)p.Mode, ["next_refresh_time"] = (uint)next, ["difficulty_list"] = new uint[] { 3, 4, 101, 102 } },
            new()
            {
                ["use_times"] = (uint)p.UseTimes, ["boss_challenge_list"] = normalList, ["area_id"] = (uint)p.AreaId,
                ["receive_star_list"] = claimed.Where(c => c.Type == 1).Select(c => (uint)c.Id).ToArray()
            },
            new()
            {
                ["advance_id"] = (uint)p.AdvanceId, ["boss_list"] = advanceList,
                ["receive_point_list"] = claimed.Where(c => c.Type == 2).Select(c => (uint)c.Id).ToArray()
            },
            p.Mode, p.AreaId, p.AdvanceId, normalIds, advIds);
    }

    /// <summary>Advanced score: difficulty base + time conditions + affixes (calc_boss_advance_score).</summary>
    public long AdvanceScore(int advanceId, int difficulty, IEnumerable<long> times, IEnumerable<long> affixList)
    {
        var cat = Catalog;
        long score = 7000;
        if (cat.GetProperty("advance_modes").TryGetProperty(advanceId.ToString(), out var mode) && mode.TryGetProperty("difficult_point", out var dp) && dp.ValueKind == JsonValueKind.Array)
        {
            var points = dp.EnumerateArray().Select(x => x.GetInt64()).ToArray();
            if (difficulty >= 1 && difficulty <= points.Length) score = points[difficulty - 1];
        }
        foreach (var t in times)
            if (cat.GetProperty("condition_pool").TryGetProperty(t.ToString(), out var c) && c.TryGetProperty("point", out var cp)) score += cp.GetInt64();
        foreach (var a in affixList)
            if (cat.GetProperty("affix_pool").TryGetProperty(a.ToString(), out var x) && x.TryGetProperty("point", out var xp)) score += xp.GetInt64();
        return score;
    }

    /// <summary>Which current-cycle boss a stage belongs to (boss_battle_identity.resolve); 0 if not unique.</summary>
    public long Resolve(int kind, long stageId, long index, State state)
    {
        var cat = Catalog;
        if (kind == 1)
        {
            var groups = cat.GetProperty("stage_groups");
            var hits = state.NormalGroups.Where(g => groups.TryGetProperty(g.ToString(), out var gi) && gi.GetProperty("stages").EnumerateArray().Any(s => s.GetInt64() == stageId)).ToArray();
            return hits.Length == 1 ? hits[0] : 0;
        }
        var pool = cat.GetProperty("advance_pool");
        var cands = state.AdvanceBosses.Where(b => pool.TryGetProperty(b.ToString(), out var bi) && bi.TryGetProperty("stage_id", out var s) && s.GetInt64() == stageId).ToArray();
        if (index > 0) return cands.Contains(index) ? index : 0;
        return cands.Length == 1 ? cands[0] : 0;
    }

    /// <summary>Reward items of a star (type 1, per area) or point (type 2, per advanced mode) tier.</summary>
    public IReadOnlyList<(long, long)> RewardItems(int type, long tier, int areaId, int advanceId)
    {
        var cat = Catalog;
        var (group, key, field) = type == 1 ? ("normal_areas", areaId.ToString(), "star") : ("advance_modes", advanceId.ToString(), "point");
        if (!cat.GetProperty(group).TryGetProperty(key, out var g) || !g.TryGetProperty("rewards", out var rewards)) return [];
        foreach (var r in rewards.EnumerateArray())
            if (r.TryGetProperty(field, out var v) && v.GetInt64() == tier)
                return r.GetProperty("drops").EnumerateArray().Select(d => (d[0].GetInt64(), d[1].GetInt64())).ToArray();
        return [];
    }

    public IReadOnlyList<long> Tiers(int type, int areaId, int advanceId)
    {
        var cat = Catalog;
        var (group, key, field) = type == 1 ? ("normal_areas", areaId.ToString(), "star") : ("advance_modes", advanceId.ToString(), "point");
        return cat.GetProperty(group).TryGetProperty(key, out var g) && g.TryGetProperty("rewards", out var rewards)
            ? rewards.EnumerateArray().Where(r => r.TryGetProperty(field, out _)).Select(r => r.GetProperty(field).GetInt64()).ToArray() : [];
    }

    private static List<long> Sample(List<long> items, int count, Random rng) => items.OrderBy(_ => rng.Next()).Take(Math.Min(count, items.Count)).ToList();

    private static uint[] Ints(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<long[]>(json)?.Select(x => (uint)x).ToArray() ?? []; } catch (JsonException) { return []; }
    }

    private static object[] StarInfo(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            return JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => (object)new Dictionary<string, object?>
            {
                ["stage_id"] = (uint)e.GetProperty("stage_id").GetInt64(),
                ["star_list"] = e.GetProperty("star_list").EnumerateArray().Select(x => (uint)x.GetInt64()).ToArray()
            }).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return []; }
    }
}

/// <summary>45202 select mode / area / advanced level → 45203 + refreshed 45201/45001/45101.</summary>
public sealed class BossSelectModeHandler(BossChallenge boss, GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 45202;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        await boss.LoadAsync(r.AccountId, ct); // lazy weekly refresh / row creation
        await accounts.SetBossModeAsync(r.AccountId, (int)HandlerHelpers.ReadUInt(r.Body, "select", 2), ct);
        var s = await boss.LoadAsync(r.AccountId, ct);
        var srv = (ushort)(r.ServerIndex + 1);
        var frames = new List<GameResponse> { new(45201, 0, srv, codec.Encode("sc_45201", s.Overview)) };
        if (s.Mode == 1) frames.Add(new GameResponse(45001, 0, srv, codec.Encode("sc_45001", s.Normal)));
        if (s.Mode == 2) frames.Add(new GameResponse(45101, 0, srv, codec.Encode("sc_45101", s.Advance)));
        frames.Add(HandlerHelpers.ResultOnly(codec, r, 45203));
        return frames;
    }
}

/// <summary>45004 star chest / 45102 point chest / 45112 all point chests (_claim_boss_challenge_rewards).</summary>
public sealed class BossRewardHandler(uint command, BossChallenge boss, GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => command;
    public static readonly uint[] Commands = [45004, 45102, 45112];

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var type = command == 45004 ? 1 : 2;
        var s = await boss.LoadAsync(r.AccountId, ct);
        if (s.Mode != type) return [HandlerHelpers.ResultOnly(codec, r, command + 1, 2)];
        var claimed = (type == 1 ? (uint[])s.Normal["receive_star_list"]! : (uint[])s.Advance["receive_point_list"]!).Select(x => (long)x).ToHashSet();
        long total;
        if (type == 1)
        {
            // Lua counts each stage's 3 stars once (stars == 1), not repeated records.
            total = 0;
            foreach (Dictionary<string, object?> b in (object[])s.Normal["boss_challenge_list"]!)
                foreach (Dictionary<string, object?> row in (object[])b["star_info"]!)
                    total += ((uint[])row["star_list"]!).Count(x => x == 1);
        }
        else total = ((object[])s.Advance["boss_list"]!).Cast<Dictionary<string, object?>>().Sum(b => (long)(uint)b["max_point"]!);
        var tiers = boss.Tiers(type, s.AreaId, s.AdvanceId);
        List<long> selected;
        if (command == 45112) selected = tiers.Where(t => t <= total && !claimed.Contains(t)).ToList();
        else
        {
            var tier = (long)HandlerHelpers.ReadUInt(r.Body, type == 1 ? "star" : "point");
            if (!tiers.Contains(tier)) return [HandlerHelpers.ResultOnly(codec, r, command + 1, 2)];
            if (claimed.Contains(tier)) return [HandlerHelpers.ResultOnly(codec, r, command + 1, 2013)];
            if (total < tier) return [HandlerHelpers.ResultOnly(codec, r, command + 1, 2005)];
            selected = [tier];
        }
        var items = new List<(long, long)>();
        foreach (var t in selected)
            if (await accounts.ClaimBossRewardAsync(r.AccountId, type, t, ct)) items.AddRange(boss.RewardItems(type, t, s.AreaId, s.AdvanceId));
        var merged = items.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).ToArray();
        var granted = await accounts.GrantItemsAsync(r.AccountId, merged, ct);
        return await InventorySync.ReplyWithSyncAsync(accounts, codec, r, command + 1, new()
        {
            ["result"] = 0u, ["item_list"] = InventorySync.Rewards(granted)
        }, granted.Select(x => x.Item1), ct);
    }
}

/// <summary>45104 save difficulty / time conditions / affixes of an advanced boss.</summary>
public sealed class BossAffixHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 45104;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        await accounts.SaveBossAffixesAsync(r.AccountId, HandlerHelpers.ReadUInt(r.Body, "id"),
            JsonSerializer.Serialize(HandlerHelpers.ReadIdList(r.Body, "affix_index_list")),
            JsonSerializer.Serialize(HandlerHelpers.ReadIdList(r.Body, "time_index_list")),
            (int)Math.Max(1, HandlerHelpers.ReadUInt(r.Body, "diffculty_index", 1)), ct);
        return [HandlerHelpers.ResultOnly(codec, r, 45105)];
    }
}

/// <summary>45006 reset normal boss, 45106 reset advanced boss, 45204 reset all advanced bosses.</summary>
public sealed class BossResetHandler(uint command, BossChallenge boss, GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => command;
    public static readonly uint[] Commands = [45006, 45106, 45204];

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var s = await boss.LoadAsync(r.AccountId, ct);
        var (kind, target) = command switch
        {
            45006 => (1, (long)HandlerHelpers.ReadUInt(r.Body, "group_id")),
            45106 => (2, (long)HandlerHelpers.ReadUInt(r.Body, "boss_id")),
            _ => (0, 0L)
        };
        if (kind == 1 && !s.NormalGroups.Contains(target) || kind == 2 && !s.AdvanceBosses.Contains(target))
            return [HandlerHelpers.ResultOnly(codec, r, command + 1, 2)];
        await accounts.ResetBossAsync(r.AccountId, kind, target, ct);
        return [HandlerHelpers.ResultOnly(codec, r, command + 1)];
    }
}

/// <summary>45108 save advanced boss team / 45110 save normal boss team.</summary>
public sealed class BossTeamHandler(uint command, GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => command;
    public static readonly uint[] Commands = [45108, 45110];

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var kind = command == 45110 ? 1 : 2;
        var id = HandlerHelpers.ReadUInt(r.Body, kind == 1 ? "group_id" : "id");
        await accounts.SaveBossTeamAsync(r.AccountId, kind, id, JsonSerializer.Serialize(HandlerHelpers.ReadIdList(r.Body, "heroes_cfg")), ct);
        return [HandlerHelpers.ResultOnly(codec, r, command + 1)];
    }
}
