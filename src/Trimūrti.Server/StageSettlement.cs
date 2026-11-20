using System.Text.Json;

namespace Trimūrti.Server;

/// <summary>Combat report fields from the UDP battle result (opcode 132, CharResultInfo / BattleResultInfo).</summary>
public sealed record BattleStats(int BattleTimeMs, int DeadNum, int HittedNum, int InjuredNum, int FallDownNum,
    int KnockoutNum, int ResurrectTimes, int EnemyDeadNum)
{
    public static readonly BattleStats Empty = new(0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Items collected in battle (BattleResultInfo fields 12 item ids / 13 item numbers), used by mythic stars.</summary>
    public IReadOnlyDictionary<long, long> Items { get; init; } = new Dictionary<long, long>();
}

/// <summary>One star condition evaluated against the battle report (battle_result_net_rec.star_list entry).</summary>
public sealed record StarResult(int StarId, int Now, int Need, bool Achieved);

/// <summary>Rewards of a single battle run (all_drop_list entry): repeat/random drops and first-clear extras.</summary>
public sealed record RunDrops(IReadOnlyList<(long ItemId, long Count)> Gain, IReadOnlyList<(long ItemId, long Count)> Extra);

/// <summary>
/// Generic stage settlement rules, ported from Alpha-Persei middleware._settle_frames / stage_drop / chapter_bonus /
/// evaluate_three_star / _stage_stamina_cost and hero_service.on_battle_result. Drives main/sub-plot chapters,
/// daily resource stages, sigil stages, tower and teaching stages from the client configs in Data/Configs.
/// </summary>
public sealed class StageSettlement(SourceGameConfig config)
{
    private JsonElement Cfg(string file) => config.Get(file) ?? default;

    public string Category(long stageId) =>
        Cfg("stage_meta.json") is { ValueKind: JsonValueKind.Object } meta && meta.TryGetProperty("stages", out var stages)
        && stages.TryGetProperty(stageId.ToString(), out var s) && s.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "";

    private JsonElement StageInfo(long stageId) =>
        Cfg("stage_info_cfg.json") is { ValueKind: JsonValueKind.Object } info && info.TryGetProperty(stageId.ToString(), out var s) ? s : default;

    /// <summary>Fatigue (item 4) per run. Challenge/weekly/rogue/core/boss/teaching ranges are free; main and sub-plot
    /// stages only cost fatigue on the first clear (ChapterSectionInfoView).</summary>
    public int StaminaCost(long stageId, bool firstClear)
    {
        if (stageId is >= 2000 and <= 2999 or >= 3090001 and <= 3093999 or >= 10101 and <= 16208
            or >= 3010101 and <= 3012399 or >= 3026001 and <= 3028041 or 1 or 2) return 0;
        if (!firstClear && Category(stageId) is "MAIN" or "SUB_PLOT") return 0;
        if (StageInfo(stageId) is { ValueKind: JsonValueKind.Object } si && si.TryGetProperty("cost", out var cost)) return cost.GetInt32();
        return Cfg("stage_meta.json") is { ValueKind: JsonValueKind.Object } meta && meta.GetProperty("stages").TryGetProperty(stageId.ToString(), out var st)
            && st.TryGetProperty("stamina_need", out var need) ? need.GetInt32() : 0;
    }

    /// <summary>Player experience per run (stage_info_cfg.user_exp, default = fatigue cost).</summary>
    public int UserExp(long stageId, int staminaCost) =>
        StageInfo(stageId) is { ValueKind: JsonValueKind.Object } si && si.TryGetProperty("user_exp", out var e) ? e.GetInt32() : staminaCost;

    /// <summary>Hero experience per run (stage_info_cfg.hero_exp, default 20).</summary>
    public int HeroExp(long stageId) =>
        StageInfo(stageId) is { ValueKind: JsonValueKind.Object } si && si.TryGetProperty("hero_exp", out var e) ? e.GetInt32() : 20;

    /// <summary>Chapter/sub-plot replays grant no player or hero EXP; every other stage does.</summary>
    public bool GrantsExp(long stageId, bool firstClear) => firstClear || Category(stageId) is not ("MAIN" or "SUB_PLOT");

    /// <summary>
    /// Drops of one run (client DropCfg contract): stage → drop library (daily_stage_drop_cfg) → drop_cfg
    /// {base (first clear only), random [item,num,prob%], weight [item,num,weight] × weight_count}.
    /// Stages without a drop library use the chapter first/repeat rewards (stage_first_clear_rewards, chapter_reward_cfg).
    /// </summary>
    public RunDrops RollDrops(long stageId, bool firstClear, Random rng)
    {
        var gain = new List<(long, long)>();
        var extra = new List<(long, long)>();
        var lib = Cfg("daily_stage_drop_cfg.json") is { ValueKind: JsonValueKind.Object } map && map.TryGetProperty(stageId.ToString(), out var l) ? l.ToString() : null;
        if (lib is not null && Cfg("drop_cfg.json") is { ValueKind: JsonValueKind.Object } drops && drops.TryGetProperty(lib, out var d))
        {
            var bases = firstClear && d.TryGetProperty("base", out var b) ? b.EnumerateArray().ToArray() : [];
            foreach (var e in bases)
                if (e.GetArrayLength() < 3 || e[2].GetInt32() >= 100 || rng.Next(1, 101) <= e[2].GetInt32()) gain.Add((e[0].GetInt64(), e[1].GetInt64()));
            if (bases.Length > 0) return new RunDrops(Merge(gain), []);
            if (d.TryGetProperty("random", out var random))
                foreach (var e in random.EnumerateArray())
                    if (e.GetArrayLength() < 3 || e[2].GetInt32() >= 100 || rng.Next(1, 101) <= e[2].GetInt32()) gain.Add((e[0].GetInt64(), e[1].GetInt64()));
            var weights = d.TryGetProperty("weight", out var w) ? w.EnumerateArray().Where(x => x.GetArrayLength() >= 3).ToArray() : [];
            var count = d.TryGetProperty("weight_count", out var wc) && wc.GetInt32() > 0 ? wc.GetInt32() : weights.Length > 0 ? 1 : 0;
            for (var i = 0; i < count && weights.Length > 0; i++)
            {
                var total = weights.Sum(x => x[2].GetInt32());
                var roll = rng.Next(1, Math.Max(total, 1) + 1);
                var acc = 0;
                foreach (var x in weights)
                {
                    acc += x[2].GetInt32();
                    if (roll <= acc) { gain.Add((x[0].GetInt64(), x[1].GetInt64())); break; }
                }
            }
            return new RunDrops(Merge(gain), []);
        }
        if (firstClear)
        {
            if (Cfg("stage_first_clear_rewards.json") is { ValueKind: JsonValueKind.Object } fr && fr.TryGetProperty(stageId.ToString(), out var first))
                extra.AddRange(Pairs(first));
            else if (ChapterReward(stageId, "first") is { } cf) extra.AddRange(cf);
        }
        else if (ChapterReward(stageId, "second") is { } cs) gain.AddRange(cs);
        return new RunDrops(Merge(gain), Merge(extra));
    }

    private IReadOnlyList<(long, long)>? ChapterReward(long stageId, string key) =>
        Cfg("chapter_reward_cfg.json") is { ValueKind: JsonValueKind.Object } cr && cr.TryGetProperty(stageId.ToString(), out var c) && c.TryGetProperty(key, out var v)
            ? Pairs(v) : null;

    /// <summary>Three-star evaluation (TrialSystem stage_three_stars, default [[8],[13],[14,1]]) against the battle report.</summary>
    public IReadOnlyList<StarResult> EvaluateStars(long stageId, bool win, BattleStats s, IReadOnlyCollection<long> heroes)
    {
        if (!win) return [];
        var stars = Cfg("trial_system_data.json") is { ValueKind: JsonValueKind.Object } t && t.GetProperty("stage_three_stars").TryGetProperty(stageId.ToString(), out var c)
            ? c.EnumerateArray().Select(x => x.EnumerateArray().Select(v => v.GetInt64()).ToArray()).ToArray()
            : [[8], [13], [14, 1]];
        var result = new List<StarResult>();
        for (var i = 0; i < stars.Length; i++)
        {
            var cond = stars[i];
            long P(long fallback) => cond.Length > 1 ? cond[1] : fallback;
            var (ok, now, need) = cond[0] switch
            {
                8 => (true, 1L, 1L),                                                      // clear the stage
                13 => (s.ResurrectTimes == 0, s.ResurrectTimes == 0 ? 1 : 0, 1),          // no resurrection
                14 => (s.ResurrectTimes <= P(1), s.ResurrectTimes <= P(1) ? 1 : 0, 1),    // resurrections ≤ X
                1 => (s.DeadNum <= P(0), s.DeadNum <= P(0) ? 1 : 0, 1),                   // fallen members ≤ X
                2 => (s.DeadNum == 0, s.DeadNum == 0 ? 1 : 0, 1),                         // nobody falls
                3 => (s.HittedNum <= P(0), s.HittedNum <= P(0) ? 1 : 0, 1),               // leader hits ≤ X
                4 => (s.BattleTimeMs <= P(180) * 1000, s.BattleTimeMs <= P(180) * 1000 ? 1 : 0, 1), // clear within X s
                9 => (s.InjuredNum < P(0), s.InjuredNum < P(0) ? 1 : 0, 1),               // damage taken < X
                10 => (s.FallDownNum <= P(0), s.FallDownNum <= P(0) ? 1 : 0, 1),          // knocked down ≤ X
                11 => (s.KnockoutNum >= P(1), s.KnockoutNum, P(1)),                       // knock out ≥ X enemies
                12 => (heroes.Contains(P(0)), heroes.Contains(P(0)) ? 1 : 0, 1),          // team includes hero X
                17 => (s.EnemyDeadNum >= P(1), s.EnemyDeadNum, P(1)),                     // kill ≥ X enemies
                _ => (true, 1L, 1L)
            };
            result.Add(new StarResult(i + 1, (int)now, (int)need, ok));
        }
        return result;
    }

    /// <summary>Cumulative player experience needed to reach each level (user_level_setting: exp of each level).</summary>
    public int LevelForExp(long totalExp)
    {
        if (Cfg("user_level_setting.json") is not { ValueKind: JsonValueKind.Object } levels) return 1;
        var ordered = levels.EnumerateObject().Select(p => (Level: int.Parse(p.Name), Exp: p.Value.GetInt64())).OrderBy(x => x.Level).ToArray();
        long acc = 0;
        var level = 1;
        foreach (var (lv, exp) in ordered)
        {
            if (lv == ordered[^1].Level) break;
            acc += exp;
            if (totalExp >= acc) level = lv + 1; else break;
        }
        return level;
    }

    private static IReadOnlyList<(long, long)> Pairs(JsonElement arr) =>
        arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2).Select(p => (p[0].GetInt64(), p[1].GetInt64())).ToArray()
            : [];

    private static List<(long, long)> Merge(IEnumerable<(long Id, long Count)> items) =>
        items.Where(i => i.Id > 0 && i.Count > 0).GroupBy(i => i.Id).Select(g => (g.Key, g.Sum(x => x.Count))).ToList();
}
