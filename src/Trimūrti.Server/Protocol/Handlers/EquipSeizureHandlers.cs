using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 介质攫取 (equip seizure), port of Alpha-Persei weekly_challenge_service seizure functions, the settlement branch
/// and operation 35014. Daily stage rotation (05:00), element affix pair rotating Monday/Thursday/Saturday, weekend
/// ×1.2 score rate. Each battle (win or not) is scored from waves/kills/time; the day's best adds its improvement to
/// the weekly sum (reset Thursday 05:00), which unlocks the score chests of equip_seizure_catalog.json.
/// </summary>
public sealed class EquipSeizure(GameAccountRepository accounts, SourceGameConfig config)
{
    public JsonElement Catalog => config.Get("equip_seizure_catalog.json") ?? default;

    public static bool IsStage(uint type, long stage) => type == 53 || stage is >= 3070101 and <= 3070126;

    private static DateTimeOffset GameDayNow => GameClock.Now.AddHours(-5);

    public long DailyStage()
    {
        var stages = Catalog.TryGetProperty("stages", out var s) ? s.EnumerateArray().Select(x => x.GetInt64()).ToArray()
            : Enumerable.Range(1, 26).Select(i => 3070100L + i).ToArray();
        var day = (int)(GameDayNow.Date - new DateTime(2026, 1, 1)).TotalDays;
        return stages[((day % stages.Length) + stages.Length) % stages.Length];
    }

    public static double Rate() => GameDayNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1.2 : 1.0;

    /// <summary>Element affix pair: attribute field 9201+e and shield 9215+e, e rotating every Mon/Thu/Sat.</summary>
    public static long[] Affixes()
    {
        var d = GameDayNow;
        var weeks = (int)Math.Floor((d.Date - new DateTime(2026, 1, 5)).TotalDays / 7);
        var sub = d.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Tuesday or DayOfWeek.Wednesday ? 0 : d.DayOfWeek is DayOfWeek.Thursday or DayOfWeek.Friday ? 1 : 2;
        var e = (((weeks * 3 + sub) % 8) + 8) % 8;
        return [9201 + e, 9215 + e];
    }

    public static long NextAffixRefresh()
    {
        var now = GameClock.Now;
        return new[] { DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Saturday }.Select(day =>
        {
            var c = new DateTimeOffset(now.Year, now.Month, now.Day, 5, 0, 0, GameClock.Offset).AddDays(((int)day - (int)now.DayOfWeek + 7) % 7);
            return c <= now ? c.AddDays(7) : c;
        }).Min().ToUnixTimeSeconds();
    }

    /// <summary>Score of a run from reported waves (record 42) or kills (waves estimated), with boss bounties (calc_equip_seizure_score).</summary>
    public static int Score(int enemyDead, int battleTimeMs, bool win, int? reportedWaves = null)
    {
        int waves;
        if (reportedWaves is int rw && rw >= 0) waves = rw;
        else if (enemyDead > 0) waves = enemyDead <= 91 ? Math.Max(1, (int)Math.Round(enemyDead / 4.8, MidpointRounding.ToEven)) : Math.Max(1, (int)Math.Round(19 + (enemyDead - 91) / 20.2 * 5, MidpointRounding.ToEven));
        else if (battleTimeMs > 0) waves = Math.Max(1, (int)((battleTimeMs > 1000 ? battleTimeMs / 1000.0 : battleTimeMs) / 15.0));
        else waves = win ? 40 : 0;
        if (waves <= 0) return 0;
        var score = Math.Min(waves, 10) * 220;
        if (waves > 10) score += Math.Min(waves - 10, 10) * 280;
        if (waves > 20) score += Math.Min(waves - 20, 10) * 420;
        if (waves > 30) score += Math.Min(waves - 30, 10) * 400;
        if (waves > 40) score += (waves - 40) * 500;
        var bosses = Math.Max(0, (waves - 15) / 5);
        score += 500 * bosses + 50 * bosses * (bosses - 1);
        return Math.Clamp(score, 0, 24000);
    }

    public sealed class State
    {
        public long Week { get; set; }
        public string Day { get; set; } = "";
        public int TodayMax { get; set; }
        public int Sum { get; set; }
        public List<long> Claimed { get; set; } = [];
        public List<long> Team { get; set; } = [];
    }

    public async Task<State> LoadAsync(long uid, CancellationToken ct)
    {
        var week = BossChallenge.Bounds().Start; // Thursday 05:00 weekly reset
        var day = GameDayNow.ToString("yyyy-MM-dd");
        var s = await accounts.GetJsonAsync<State>(uid, "equip_seizure_v1", ct) ?? new State { Week = week, Day = day };
        if (s.Week != week) s = new State { Week = week, Day = day };
        if (s.Day != day) { s.Day = day; s.TodayMax = 0; }
        return s;
    }

    public Task SaveAsync(long uid, State s, CancellationToken ct) => accounts.SetJsonAsync(uid, "equip_seizure_v1", s, ct);

    public async Task<Dictionary<string, object?>> PayloadAsync(long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        return new()
        {
            ["stage_id"] = (uint)DailyStage(), ["challenge_rate"] = Rate(),
            ["affix_info"] = new Dictionary<string, object?> { ["affix_id_list"] = Affixes().Select(x => (uint)x).ToArray(), ["refresh_timestamp"] = (uint)NextAffixRefresh() },
            ["today_max_score"] = (uint)s.TodayMax, ["sum_score"] = (uint)s.Sum,
            ["refresh_timestamp"] = (uint)BossChallenge.Bounds().Next, ["got_reward_id_list"] = s.Claimed.Select(x => (uint)x).ToArray()
        };
    }

    /// <summary>Scores a finished seizure battle (any outcome) and returns the 35013 push.</summary>
    public async Task<GameResponse> SettleAsync(ProtoCodec codec, GameRequest r, BattleStats stats, bool win, IReadOnlyList<long> heroes, CancellationToken ct)
    {
        int? reportedWaves = stats.Records.TryGetValue(42, out var rw) && rw >= 0 ? (int)Math.Min(int.MaxValue, rw) : null;
        var final = (int)Math.Round(Score(stats.EnemyDeadNum, stats.BattleTimeMs, win, reportedWaves) * Rate(), MidpointRounding.ToEven);
        var s = await LoadAsync(r.AccountId, ct);
        s.Sum += Math.Max(0, final - s.TodayMax);
        s.TodayMax = Math.Max(s.TodayMax, final);
        if (heroes.Count > 0) s.Team = heroes.ToList();
        await SaveAsync(r.AccountId, s, ct);
        return new GameResponse(35013, 0, (ushort)(r.ServerIndex + 1), codec.Encode("sc_35013", new Dictionary<string, object?>
        {
            ["is_valid"] = true, ["score"] = (uint)final, ["today_max_score"] = (uint)s.TodayMax, ["sum_score"] = (uint)s.Sum
        }));
    }
}

/// <summary>35014 claim weekly score chests (single or batch).</summary>
public sealed class EquipSeizureRewardHandler(EquipSeizure seizure, GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 35014;
    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var s = await seizure.LoadAsync(r.AccountId, ct);
        var rewards = seizure.Catalog.TryGetProperty("rewards", out var rw) ? rw : default;
        var items = new List<(long, long)>();
        foreach (var id in HandlerHelpers.ReadIdList(r.Body, "id_list"))
        {
            if (s.Claimed.Contains(id) || rewards.ValueKind != JsonValueKind.Object || !rewards.TryGetProperty(id.ToString(), out var cfg)) continue;
            if (s.Sum < cfg.GetProperty("need").GetInt32()) continue;
            s.Claimed.Add(id);
            items.AddRange(cfg.GetProperty("reward_item_list").EnumerateArray().Select(x => (x.GetProperty("id").GetInt64(), x.GetProperty("num").GetInt64())));
        }
        await seizure.SaveAsync(r.AccountId, s, ct);
        var granted = await accounts.GrantItemsAsync(r.AccountId, items.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))), ct);
        return await InventorySync.ReplyWithSyncAsync(accounts, codec, r, 35015, new() { ["result"] = 0u, ["reward_list"] = InventorySync.Rewards(granted) }, granted.Select(x => x.Item1), ct);
    }
}
