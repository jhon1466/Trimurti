using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 黑区净化 (mythic), port of Alpha-Persei mythic_cycle / mythic_affix_cfg / mythic_normal_service /
/// mythic_final_service, weekly_challenge_service.get_mythic_data and the 440xx operations.
/// Semi-weekly cycle (Monday and Thursday 05:00 UTC+8). Normal zone: difficulties 1..13, each with a main
/// partition (3 star conditions) and three sub partitions, star chests per cycle. 失序深区 (final): difficulties
/// 1..30 (mythic_final_cfg.json), chained teams per difficulty, one clear reward per difficulty.
/// </summary>
public sealed class Mythic(GameAccountRepository accounts, SourceGameConfig config)
{
    public const int Omega = 1001;
    private const string NormalKind = "mythic_normal_v1";

    // Affix rotation pools and their default levels/types (mythic_affix_cfg AFFIX_DATABASE).
    private static readonly long[] CompositePool = [444, 441, 442, 443, 445, 446, 447, 448, 449, 451];
    private static readonly long[] HostPool = [9692, 9691, 9693];
    private static readonly long[] TeamPool = [9682, 9686, 9683, 9681, 9684, 9685];
    private static readonly Dictionary<long, uint[]> CompositeRaces = new()
    {
        [441] = [1, 4], [442] = [2, 3], [443] = [3, 5], [444] = [1, 4], [445] = [4, 5],
        [446] = [1, 9], [447] = [3, 9], [448] = [2, 4], [449] = [3, 5], [451] = [2, 9]
    };
    private static readonly long[][] SuperiorityPool =
        [[417, 412, 178], [411, 414, 180], [413, 415, 179], [416, 418, 177], [412, 415, 178], [414, 417, 180], [411, 416, 179], [413, 418, 178]];
    // Star chests of the normal zone per difficulty (MYTHIC_NORMAL_STAR_REWARDS) and final clear reward.
    public static IReadOnlyList<(long, long)> NormalStarReward(int difficulty) => [(25, 75 + 60 * difficulty), (40701, 3 + difficulty)];
    public static readonly (long, long)[] FinalReward = [(43, 60), (41301, 2), (25, 150), (40701, 4)];

    public static (long Start, long Next) Bounds(DateTimeOffset? at = null)
    {
        var now = (at ?? DateTimeOffset.UtcNow).ToOffset(GameClock.Offset);
        long prev = 0, next = long.MaxValue;
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Thursday })
        {
            var c = new DateTimeOffset(now.Year, now.Month, now.Day, 5, 0, 0, GameClock.Offset).AddDays(-(((int)now.DayOfWeek - (int)day + 7) % 7));
            if (c > now) c = c.AddDays(-7);
            prev = Math.Max(prev, c.ToUnixTimeSeconds());
            next = Math.Min(next, c.AddDays(7).ToUnixTimeSeconds());
        }
        return (prev, next);
    }

    public static long CycleId(long start) => Math.Max(0, start - 1767214800) / (long)(3.5 * 86400);

    public static bool IsNormal(uint type, long stage) => type == 11 || stage is >= 3026001 and <= 3026013 or >= 3027001 and <= 3027039;
    public static bool IsFinal(uint type, long stage) => type == 35 || stage is >= 3028001 and <= 3028058;

    /// <summary>(difficulty, partition id, is main partition) of a normal-zone stage.</summary>
    public static (int Difficulty, int Partition, bool Main) Partition(long stage) =>
        stage is >= 3026001 and <= 3026013 ? ((int)(stage - 3026000), (int)(stage - 3026000), true)
        : ((int)((stage - 3027001) / 3 + 1), (int)(((stage - 3027001) / 3 + 1) * 100 + (stage - 3027001) % 3 + 1), false);

    private JsonElement FinalCfg => config.Get("mythic_final_cfg.json") is { } c ? c.GetProperty("311").GetProperty("difficulties") : default;

    public int FinalTeams(int difficulty) => FinalCfg.TryGetProperty(difficulty.ToString(), out var d) ? d.GetProperty("team_num").GetInt32() : 1;

    public long[] FinalRoute(int difficulty, long cycle)
    {
        if (!FinalCfg.TryGetProperty(difficulty.ToString(), out var d)) return [];
        var choices = d.GetProperty("stages").EnumerateArray().ToArray();
        return choices[(int)(cycle % choices.Length)].EnumerateArray().Select(x => x.GetInt64()).ToArray();
    }

    // ---- persisted state ----
    public sealed class NormalState
    {
        public long Cycle { get; set; }
        public Dictionary<string, int[]> Stars { get; set; } = [];
        public List<int> Cleared { get; set; } = [];
        public List<int> Claimed { get; set; } = [];
    }

    public sealed class FinalState
    {
        public long Cycle { get; set; }
        public int NowDifficulty { get; set; } = 30;
        public List<int> CanChoose { get; set; } = Enumerable.Range(1, 30).ToList();
        public List<int> Received { get; set; } = [];
        public List<int> Cleared { get; set; } = [];
        public List<int[]> Teams { get; set; } = []; // [team_id, clear_state, use_time]
    }

    public async Task<NormalState> LoadNormalAsync(long uid, CancellationToken ct)
    {
        var start = Bounds().Start;
        var s = await accounts.GetJsonAsync<NormalState>(uid, NormalKind, ct) ?? new NormalState { Cycle = start };
        return s.Cycle < start ? new NormalState { Cycle = start } : s;
    }

    public async Task<FinalState> LoadFinalAsync(long uid, CancellationToken ct)
    {
        var start = Bounds().Start;
        var s = await accounts.GetJsonAsync<FinalState>(uid, "mythic_final_v1", ct) ?? new FinalState { Cycle = start };
        if (s.Cycle < start) { s.Cycle = start; s.Received = []; s.Cleared = []; s.Teams = []; }
        return s;
    }

    public Task SaveNormalAsync(long uid, NormalState s, CancellationToken ct) => accounts.SetJsonAsync(uid, NormalKind, s, ct);
    public Task SaveFinalAsync(long uid, FinalState s, CancellationToken ct) => accounts.SetJsonAsync(uid, "mythic_final_v1", s, ct);

    public async Task<int> SelectedDifficultyAsync(long uid, CancellationToken ct) => (int)await accounts.GetValueAsync(uid, "mythic", "difficulty", 10, ct);

    // ---- payloads ----
    public Dictionary<string, object?> Rotation()
    {
        var (start, next) = Bounds();
        var cycle = CycleId(start);
        var comp = CompositePool[cycle % CompositePool.Length];
        var sup = SuperiorityPool[cycle % SuperiorityPool.Length];
        object A(long id, uint level, uint type) => new Dictionary<string, object?> { ["id"] = (uint)id, ["level"] = level, ["type"] = type };
        return new()
        {
            ["difficulty_list"] = Enumerable.Range(1, 13).Select(d => (object)new Dictionary<string, object?>
            {
                ["difficulty"] = (uint)d,
                ["main_partition"] = new Dictionary<string, object?> { ["partition"] = (uint)d, ["stage_id"] = (uint)(3026000 + d) },
                ["sub_partition_list"] = Enumerable.Range(1, 3).Select(i => (object)new Dictionary<string, object?>
                {
                    ["partition"] = (uint)(d * 100 + i), ["stage_id"] = (uint)(3027000 + (d - 1) * 3 + i)
                }).ToArray()
            }).ToArray(),
            ["superiority_affix_list"] = new[] { A(sup[0], 10, 0), A(sup[1], 5, 0), A(sup[2], 1, 3) },
            ["inferiority_affix_list"] = new[] { A(121, 3, 0), A(228, 1, 3), A(229, 1, 3) },
            ["mythic_ultimate_affix_list"] = new[] { A(comp, 7, 0), A(HostPool[cycle % HostPool.Length], 1, 0), A(TeamPool[cycle % TeamPool.Length], 1, 3) },
            ["next_refresh_timestamp"] = (uint)next,
            ["recommend_team"] = CompositeRaces[comp]
        };
    }

    public async Task<Dictionary<string, object?>> NormalPayloadAsync(long uid, CancellationToken ct)
    {
        var s = await LoadNormalAsync(uid, ct);
        var selected = await SelectedDifficultyAsync(uid, ct);
        return new()
        {
            ["clear_partition_id_list"] = s.Cleared.Select(x => (uint)x).ToArray(),
            ["main_partition_star_list"] = (s.Stars.TryGetValue(selected.ToString(), out var st) ? st : []).Select(x => (uint)x).ToArray(),
            ["star_reward_provide_list"] = s.Claimed.Select(x => (uint)x).ToArray()
        };
    }

    public async Task<Dictionary<string, object?>> DifficultyPayloadAsync(long uid, CancellationToken ct) => new()
    {
        ["open_difficulty_list"] = new uint[] { 1001, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 },
        ["difficulty"] = (uint)await SelectedDifficultyAsync(uid, ct),
        ["is_new_difficulty"] = false
    };

    public Dictionary<string, object?> FinalStagesPayload()
    {
        var cycle = CycleId(Bounds().Start);
        var list = new List<object>();
        if (FinalCfg.ValueKind == JsonValueKind.Object)
            foreach (var d in FinalCfg.EnumerateObject().OrderBy(x => int.Parse(x.Name)))
                list.Add(new Dictionary<string, object?> { ["difficulty_id"] = uint.Parse(d.Name), ["stage_id"] = (uint)FinalRoute(int.Parse(d.Name), cycle)[0] });
        return new() { ["stage_list"] = list.ToArray() };
    }

    public static Dictionary<string, object?> FinalPayload(FinalState s) => new()
    {
        ["difficulty_id_can_choose"] = s.CanChoose.Select(x => (uint)x).ToArray(),
        ["now_difficulty"] = (uint)s.NowDifficulty,
        ["receive_reward"] = s.Received.Select(x => (uint)x).ToArray(),
        ["clear_list"] = s.Cleared.Select(x => (uint)x).ToArray(),
        ["is_new_difficulty"] = false,
        ["challenge_info"] = s.Teams.Select(t => (object)new Dictionary<string, object?> { ["team_id"] = (uint)t[0], ["clear_state"] = (uint)t[1], ["use_time"] = (uint)t[2] }).ToArray()
    };

    public async Task<List<(uint, byte[])>> AllFramesAsync(ProtoCodec codec, long uid, CancellationToken ct) =>
    [
        (44007u, codec.Encode("sc_44007", Rotation())),
        (44009u, codec.Encode("sc_44009", await NormalPayloadAsync(uid, ct))),
        (44019u, codec.Encode("sc_44019", await DifficultyPayloadAsync(uid, ct))),
        (44021u, codec.Encode("sc_44021", FinalStagesPayload())),
        (44023u, codec.Encode("sc_44023", FinalPayload(await LoadFinalAsync(uid, ct))))
    ];

    /// <summary>Normal-zone star conditions (mythic_normal_service.evaluate): 15 = collect item ≥ N, 8 = clear, 4 = within N s.
    /// Sub partitions have no stars.</summary>
    public IReadOnlyList<StarResult> EvaluateNormal(long stage, BattleStats stats)
    {
        if (!Partition(stage).Main) return [];
        var conds = config.Get("trial_system_data.json") is { } t && t.GetProperty("stage_three_stars").TryGetProperty(stage.ToString(), out var c)
            ? c.EnumerateArray().Select(x => x.EnumerateArray().Select(v => v.GetInt64()).ToArray()).ToArray() : [];
        var list = new List<StarResult>();
        for (var i = 0; i < conds.Length; i++)
        {
            var cond = conds[i];
            var (ok, now, need) = cond[0] switch
            {
                15 => (stats.Items.GetValueOrDefault(cond[2]) >= cond[1], (int)stats.Items.GetValueOrDefault(cond[2]), (int)cond[1]),
                8 => (true, 1, 1),
                4 => (stats.BattleTimeMs <= cond[1] * 1000, stats.BattleTimeMs <= cond[1] * 1000 ? 1 : 0, 1),
                _ => (false, 0, 1)
            };
            list.Add(new StarResult(i + 1, now, need, ok));
        }
        return list;
    }
}

/// <summary>44010 query: all mythic frames + 44011.</summary>
public sealed class MythicQueryHandler(Mythic mythic, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 44010;
    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var srv = (ushort)(r.ServerIndex + 1);
        var frames = (await mythic.AllFramesAsync(codec, r.AccountId, ct)).Select(f => new GameResponse(f.Item1, 0, srv, f.Item2)).ToList();
        frames.Add(HandlerHelpers.ResultOnly(codec, r, 44011));
        return frames;
    }
}

/// <summary>44012 normal-zone star chest of the selected difficulty (once per cycle and tier).</summary>
public sealed class MythicStarRewardHandler(Mythic mythic, GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 44012;
    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var star = (int)HandlerHelpers.ReadUInt(r.Body, "star");
        var selected = await mythic.SelectedDifficultyAsync(r.AccountId, ct);
        var s = await mythic.LoadNormalAsync(r.AccountId, ct);
        if (selected == Mythic.Omega || star is < 1 or > 3) return [HandlerHelpers.ResultOnly(codec, r, 44013, 2)];
        if (s.Claimed.Contains(star)) return [HandlerHelpers.ResultOnly(codec, r, 44013, 2013)];
        if ((s.Stars.TryGetValue(selected.ToString(), out var st) ? st.Length : 0) < star) return [HandlerHelpers.ResultOnly(codec, r, 44013, 2005)];
        s.Claimed.Add(star);
        s.Claimed.Sort();
        await mythic.SaveNormalAsync(r.AccountId, s, ct);
        var granted = await accounts.GrantItemsAsync(r.AccountId, Mythic.NormalStarReward(selected), ct);
        return await InventorySync.ReplyWithSyncAsync(accounts, codec, r, 44013, new() { ["result"] = 0u, ["item_list"] = InventorySync.Rewards(granted) }, granted.Select(x => x.Item1), ct);
    }
}

/// <summary>44016 change normal-zone difficulty (1..13 or Ω 1001) → 44017 + 44019.</summary>
public sealed class MythicDifficultyHandler(Mythic mythic, GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 44016;
    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var d = (int)HandlerHelpers.ReadUInt(r.Body, "difficulty", 10);
        if (d is not (>= 1 and <= 13 or Mythic.Omega)) return [HandlerHelpers.ResultOnly(codec, r, 44017, 2)];
        await accounts.SetValueAsync(r.AccountId, "mythic", "difficulty", d, ct);
        return [HandlerHelpers.ResultOnly(codec, r, 44017),
            new GameResponse(44019, 0, (ushort)(r.ServerIndex + 1), codec.Encode("sc_44019", await mythic.DifficultyPayloadAsync(r.AccountId, ct)))];
    }
}

/// <summary>44024 choose 失序深区 difficulty (new attempt, keeps history) / 44026 reset the team relay.</summary>
public sealed class MythicFinalMutationHandler(uint command, Mythic mythic, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => command;
    public static readonly uint[] Commands = [44024, 44026];
    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var s = await mythic.LoadFinalAsync(r.AccountId, ct);
        var changed = true;
        if (command == 44024)
        {
            var d = (int)HandlerHelpers.ReadUInt(r.Body, "difficulty_id");
            if (mythic.FinalRoute(d, 0).Length == 0) return [HandlerHelpers.ResultOnly(codec, r, 44025, 2)];
            changed = d != s.NowDifficulty;
            if (changed) { s.NowDifficulty = d; s.Teams = []; if (!s.CanChoose.Contains(d)) s.CanChoose.Add(d); }
        }
        else s.Teams = [];
        await mythic.SaveFinalAsync(r.AccountId, s, ct);
        var frames = new List<GameResponse> { HandlerHelpers.ResultOnly(codec, r, command + 1) };
        if (command == 44024 && changed)
            frames.Add(new GameResponse(44023, 0, (ushort)(r.ServerIndex + 1), codec.Encode("sc_44023", Mythic.FinalPayload(s))));
        return frames;
    }
}

/// <summary>44028 claim one cleared 失序深区 difficulty / 44030 claim all cleared, unclaimed difficulties.</summary>
public sealed class MythicFinalRewardHandler(uint command, Mythic mythic, GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => command;
    public static readonly uint[] Commands = [44028, 44030];
    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var s = await mythic.LoadFinalAsync(r.AccountId, ct);
        var maxCleared = s.Cleared.Count > 0 ? s.Cleared.Max() : 0;
        var targets = command == 44028
            ? [(int)HandlerHelpers.ReadUInt(r.Body, "difficulty_id", 1)]
            : Enumerable.Range(1, maxCleared).ToList();
        var newly = targets.Where(d => d <= maxCleared && !s.Received.Contains(d)).ToList();
        s.Received.AddRange(newly);
        s.Received.Sort();
        await mythic.SaveFinalAsync(r.AccountId, s, ct);
        var items = newly.SelectMany(_ => Mythic.FinalReward).GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).ToArray();
        var granted = await accounts.GrantItemsAsync(r.AccountId, items, ct);
        var frames = new List<GameResponse> { new(44023, 0, (ushort)(r.ServerIndex + 1), codec.Encode("sc_44023", Mythic.FinalPayload(s))) };
        frames.AddRange(await InventorySync.ReplyWithSyncAsync(accounts, codec, r, command + 1,
            new() { ["result"] = 0u, ["item_list"] = InventorySync.Rewards(granted) }, granted.Select(x => x.Item1), ct));
        return frames;
    }
}

/// <summary>Settlement hook for mythic battles (called from the battle settlement).</summary>
public static class MythicSettlement
{
    public static async Task<IReadOnlyList<GameResponse>> OnWinAsync(Mythic mythic, ProtoCodec codec, GameRequest r, uint type, long stage,
        BattleStats stats, Dictionary<string, object?> battleResult, CancellationToken ct)
    {
        var srv = (ushort)(r.ServerIndex + 1);
        if (Mythic.IsNormal(type, stage))
        {
            var (difficulty, partition, main) = Mythic.Partition(stage);
            var stars = mythic.EvaluateNormal(stage, stats);
            battleResult["star_list"] = stars.Select(s => (object)new Dictionary<string, object?>
            {
                ["star_id"] = (uint)s.StarId, ["now_progress"] = (uint)s.Now, ["need_progress"] = (uint)s.Need, ["is_achieve"] = s.Achieved ? 1u : 0u
            }).ToArray();
            var state = await mythic.LoadNormalAsync(r.AccountId, ct);
            if (main)
            {
                var key = difficulty.ToString();
                var merged = (state.Stars.TryGetValue(key, out var old) ? old : []).Concat(stars.Where(s => s.Achieved).Select(s => s.StarId)).Distinct().Order().ToArray();
                state.Stars[key] = merged;
            }
            if (!state.Cleared.Contains(partition)) { state.Cleared.Add(partition); state.Cleared.Sort(); }
            await mythic.SaveNormalAsync(r.AccountId, state, ct);
            return [new GameResponse(44009, 0, srv, codec.Encode("sc_44009", await mythic.NormalPayloadAsync(r.AccountId, ct)))];
        }
        if (Mythic.IsFinal(type, stage))
        {
            var s = await mythic.LoadFinalAsync(r.AccountId, ct);
            var route = mythic.FinalRoute(s.NowDifficulty, Mythic.CycleId(Mythic.Bounds().Start));
            var team = Array.IndexOf(route, stage) + 1;
            var teams = mythic.FinalTeams(s.NowDifficulty);
            if (team <= 0 || team > teams) return [];
            if (s.Teams.Count == 0) s.Teams = Enumerable.Range(1, teams).Select(t => new[] { t, 0, 0 }).ToList();
            if (s.Teams[team - 1][1] == 0)
                s.Teams[team - 1] = [team, 1, Math.Max(1, (int)Math.Round(stats.BattleTimeMs / 1000.0))];
            if (s.Teams.All(t => t[1] == 1))
            {
                foreach (var d in Enumerable.Range(1, s.NowDifficulty)) if (!s.Cleared.Contains(d)) s.Cleared.Add(d);
                s.Cleared.Sort();
                if (mythic.FinalRoute(s.NowDifficulty + 1, 0).Length > 0 && !s.CanChoose.Contains(s.NowDifficulty + 1)) s.CanChoose.Add(s.NowDifficulty + 1);
            }
            await mythic.SaveFinalAsync(r.AccountId, s, ct);
            return [new GameResponse(44023, 0, srv, codec.Encode("sc_44023", Mythic.FinalPayload(s)))];
        }
        return [];
    }
}
