using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 心象梦魇 (solo heart demon) and 古远虚影 (multi heart demon), built from the client configs SoloHeartDemonCfg /
/// SoloHeartDemonRewardCfg / MultHeartDemonCfg / MultHeartDemonRewardCfg (Data/Configs/heart_demon_cfg.json, extracted
/// with scripts/luadump/luacfg.py) and ConditionCfg types 11001–11008. Alpha-Persei only sends an empty 75017.
/// The running boss rotates every two weeks (Thursday 05:00 UTC+8); its activity is added to the activity list.
/// Solo: 75001 state, 75002 claim, 75005 score. Multi: 75017 state, 75020 save team, 75022 save stage, 75025 score,
/// 75026 reset difficulty, 75028 give up with the boss HP rate.
/// Scores are a private rule (the client carries no formula): 1,000,000 × difficulty + 10,000 × max(0, 300 − seconds).
/// </summary>
public sealed class HeartDemon(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec) : IStageMode
{
    private static readonly DateTimeOffset Anchor = new(2026, 1, 1, 5, 0, 0, GameClock.Offset);
    private JsonElement Cfg => config.Get("heart_demon_cfg.json") ?? throw new ModeError("Missing heart_demon_cfg.json");
    private JsonElement Conditions => config.Get("condition_cfg.json") ?? default;
    private static void Require(bool ok, string m) { if (!ok) throw new ModeError(m); }

    private List<JsonElement> Entries(string table) => Cfg.GetProperty(table).EnumerateObject().Select(p => p.Value).OrderBy(v => v.GetProperty("id").GetInt32()).ToList();

    private (JsonElement Entry, long Start, long Stop) Current(string table)
    {
        var list = Entries(table);
        var n = (long)Math.Floor((GameClock.Now - Anchor).TotalDays / 14);
        var start = Anchor.AddDays(n * 14);
        return (list[(int)(((n % list.Count) + list.Count) % list.Count)], start.ToUnixTimeSeconds(), start.AddDays(14).ToUnixTimeSeconds());
    }

    public IEnumerable<Dictionary<string, object?>> ActivityEntries(Func<uint, (uint Theme, uint Template, uint[] Subs)?> cfg)
    {
        foreach (var table in new[] { "SoloHeartDemonCfg", "MultHeartDemonCfg" })
        {
            var (e, start, stop) = Current(table);
            foreach (var a in e.GetProperty("activity_id").EnumerateArray().Select(x => x.GetUInt32()))
                if (cfg(a) is { } c)
                    yield return new() { ["activity_id"] = a, ["start_time"] = start, ["stop_time"] = stop, ["state"] = 1, ["theme"] = c.Theme, ["template"] = c.Template, ["sub_activity_id_list"] = c.Subs.Cast<object>().ToArray() };
        }
    }

    public static long Score(int difficulty, int battleMs) => 1_000_000L * difficulty + 10_000L * Math.Max(0, 300 - battleMs / 1000);

    // ---------------- state ----------------

    public sealed class Solo
    {
        public int Edition { get; set; }
        public Dictionary<int, int> State { get; set; } = [];   // difficulty → 1 cleared / 2 attempted
        public long MaxScore { get; set; }
        public long ShortestSec { get; set; }
        public int MaxDifficultyTried { get; set; }
        public List<long> Claimed { get; set; } = [];
    }

    public sealed class MultStage { public long StageId { get; set; } public int State { get; set; } public long MaxScore { get; set; } public long HeroHp { get; set; } public long BossHp { get; set; } = 10000; public long HeroId { get; set; } }
    public sealed class MultDiff { public bool Enter { get; set; } public List<long> Heroes { get; set; } = []; public Dictionary<long, MultStage> Stages { get; set; } = []; }
    public sealed class Mult
    {
        public int Edition { get; set; }
        public Dictionary<int, MultDiff> Diffs { get; set; } = [];
        public Dictionary<int, long> MaxScores { get; set; } = [];
        public List<long> Claimed { get; set; } = [];
    }

    private async Task<Solo> SoloAsync(long uid, CancellationToken ct)
    {
        var ed = Current("SoloHeartDemonCfg").Entry.GetProperty("id").GetInt32();
        var s = await accounts.GetJsonAsync<Solo>(uid, "heart_solo_v1", ct);
        return s is null || s.Edition != ed ? new Solo { Edition = ed } : s;
    }

    private async Task<Mult> MultAsync(long uid, CancellationToken ct)
    {
        var ed = Current("MultHeartDemonCfg").Entry.GetProperty("id").GetInt32();
        var m = await accounts.GetJsonAsync<Mult>(uid, "heart_mult_v1", ct);
        return m is null || m.Edition != ed ? new Mult { Edition = ed } : m;
    }

    private (string Table, int Difficulty)? Locate(long stage)
    {
        var solo = Current("SoloHeartDemonCfg").Entry;
        foreach (var p in solo.GetProperty("stage_id").EnumerateArray())
            if (p[1].GetInt64() == stage) return ("solo", p[0].GetInt32());
        var mult = Current("MultHeartDemonCfg").Entry;
        foreach (var p in mult.GetProperty("stage_id").EnumerateArray())
            if (p[1].EnumerateArray().Any(x => x.GetInt64() == stage)) return ("mult", p[0].GetInt32());
        return null;
    }

    public bool Reserved(uint type, long stage) => Locate(stage) is not null;

    // ---------------- payloads ----------------

    /// <summary>The client reads open_edition as the running activity id (Cfg.get_id_list_by_activity_id[open_edition]).</summary>
    private uint OpenEdition(string table) => Current(table).Entry.GetProperty("activity_id").EnumerateArray().First().GetUInt32();

    private Dictionary<string, object?> SoloPayload(Solo s) => new()
    {
        ["open_edition"] = OpenEdition("SoloHeartDemonCfg"), ["challenge_stage"] = 1u,
        ["info_list"] = Current("SoloHeartDemonCfg").Entry.GetProperty("stage_id").EnumerateArray().Select(p => (object)new Dictionary<string, object?>
        {
            ["difficulty"] = p[0].GetUInt32(), ["challenge_state"] = (uint)s.State.GetValueOrDefault(p[0].GetInt32()), ["reward_state"] = 0u, ["hurt_state"] = 0u
        }).ToArray(),
        ["obtained_reward_list"] = s.Claimed.Select(x => (uint)x).ToArray()
    };

    private Dictionary<string, object?> MultPayload(Mult m) => new()
    {
        ["open_edition"] = OpenEdition("MultHeartDemonCfg"), ["challenge_stage"] = 1u,
        ["info_list"] = m.Diffs.OrderBy(x => x.Key).Select(d => (object)new Dictionary<string, object?>
        {
            ["difficulty"] = (uint)d.Key, ["enter_flag"] = d.Value.Enter, ["hero_id_list"] = d.Value.Heroes.Select(x => (uint)x).ToArray(),
            ["stage_info_list"] = d.Value.Stages.Values.Select(st => (object)new Dictionary<string, object?>
            {
                ["stage_id"] = (uint)st.StageId, ["challenge_state"] = (uint)st.State, ["max_score"] = (uint)st.MaxScore, ["least_injure_num"] = 0u, ["least_hit_num"] = 0u,
                ["hero_hp"] = (uint)st.HeroHp, ["boss_hp"] = (uint)st.BossHp, ["hero_id"] = (uint)st.HeroId
            }).ToArray()
        }).ToArray(),
        ["max_score_list"] = m.MaxScores.OrderBy(x => x.Key).Select(x => (object)new Dictionary<string, object?> { ["difficulty"] = (uint)x.Key, ["max_score"] = (uint)x.Value }).ToArray()
    };

    public async Task<IReadOnlyList<(uint Cmd, byte[] Payload)>> LoginFramesAsync(ProtoCodec c, long uid, CancellationToken ct) =>
    [
        (75001, codec.Encode("sc_75001", SoloPayload(await SoloAsync(uid, ct)))),
        (75017, codec.Encode("sc_75017", MultPayload(await MultAsync(uid, ct))))
    ];

    // ---------------- settlement ----------------

    public async Task<(List<GameResponse>, IReadOnlyList<(long, long)>)> SettleAsync(ModeBattle b, CancellationToken ct)
    {
        var (table, diff) = Locate(b.StageId)!.Value;
        var score = b.Win ? Score(diff, b.Stats.BattleTimeMs) : 0;
        var frames = new List<GameResponse>();
        if (table == "solo")
        {
            var s = await SoloAsync(b.Uid, ct);
            s.MaxDifficultyTried = Math.Max(s.MaxDifficultyTried, diff);
            if (b.Win)
            {
                s.State[diff] = 1;
                s.MaxScore = Math.Max(s.MaxScore, score);
                var sec = b.Stats.BattleTimeMs / 1000;
                s.ShortestSec = s.ShortestSec == 0 ? sec : Math.Min(s.ShortestSec, sec);
            }
            else s.State.TryAdd(diff, 2);
            await accounts.SetJsonAsync(b.Uid, "heart_solo_v1", s, ct);
            frames.Add(new GameResponse(75005, 0, b.Srv, codec.Encode("sc_75005", new Dictionary<string, object?> { ["score"] = (uint)score })));
            frames.Add(new GameResponse(75001, 0, b.Srv, codec.Encode("sc_75001", SoloPayload(s))));
        }
        else
        {
            var m = await MultAsync(b.Uid, ct);
            if (!m.Diffs.TryGetValue(diff, out var d)) m.Diffs[diff] = d = new MultDiff { Enter = true, Heroes = b.Heroes.ToList() };
            if (!d.Stages.TryGetValue(b.StageId, out var st)) d.Stages[b.StageId] = st = new MultStage { StageId = b.StageId };
            st.HeroId = b.Heroes.FirstOrDefault();
            if (b.Win) { st.State = 1; st.BossHp = 0; st.MaxScore = Math.Max(st.MaxScore, score); st.HeroHp = 10000; }
            else st.State = Math.Max(st.State, 2);
            m.MaxScores[diff] = Math.Max(m.MaxScores.GetValueOrDefault(diff), d.Stages.Values.Sum(x => x.MaxScore));
            await accounts.SetJsonAsync(b.Uid, "heart_mult_v1", m, ct);
            frames.Add(new GameResponse(75025, 0, b.Srv, codec.Encode("sc_75025", new Dictionary<string, object?> { ["score"] = (uint)score })));
            frames.Add(new GameResponse(75017, 0, b.Srv, codec.Encode("sc_75017", MultPayload(m))));
        }
        return (frames, []);
    }

    // ---------------- operations ----------------

    private bool SoloMet(long condition, Solo s)
    {
        if (condition == 0) return s.MaxScore > 0;   // rank rewards: the local server ranks its only player first
        if (Conditions.ValueKind != JsonValueKind.Object || !Conditions.TryGetProperty(condition.ToString(), out var c)) return false;
        var p = c.GetProperty("params")[0].GetInt64();
        return c.GetProperty("type").GetInt32() switch
        {
            11001 => s.State.Any(x => x.Value == 1 && x.Key >= p),
            11002 => s.MaxDifficultyTried >= p,
            11003 => s.ShortestSec > 0 && s.ShortestSec <= p,
            11005 => s.MaxScore >= p,
            _ => false
        };
    }

    private bool MultMet(long condition, Mult m)
    {
        if (Conditions.ValueKind != JsonValueKind.Object || !Conditions.TryGetProperty(condition.ToString(), out var c)) return false;
        var p = c.GetProperty("params")[0].GetInt64();
        return c.GetProperty("type").GetInt32() switch
        {
            11006 => m.Diffs.Any(d => d.Key >= p && d.Value.Stages.Count > 0 && d.Value.Stages.Values.All(st => st.State == 1)),
            11007 => m.Diffs.Keys.Any(d => d >= p),
            11008 => m.MaxScores.Values.DefaultIfEmpty(0).Max() >= p,
            _ => false
        };
    }

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var srv = (ushort)(r.ServerIndex + 1);
        try
        {
            if (cmd == 75002)
            {
                var s = await SoloAsync(uid, ct);
                var items = new List<(long, long)>();
                foreach (var id in HandlerHelpers.ReadIdList(r.Body, "reward_list").Distinct())
                {
                    if (s.Claimed.Contains(id) || !Cfg.GetProperty("SoloHeartDemonRewardCfg").TryGetProperty(id.ToString(), out var rw)) continue;
                    if (rw.GetProperty("reward_type").GetInt32() == 0 && rw.GetProperty("reward_rank") is { ValueKind: JsonValueKind.Array } rank && rank[0].GetInt32() != 1) continue;
                    if (!SoloMet(rw.GetProperty("condition").GetInt64(), s)) continue;
                    s.Claimed.Add(id);
                    items.AddRange(rw.GetProperty("reward_list").EnumerateArray().Select(x => (x[0].GetInt64(), x[1].GetInt64())));
                }
                await accounts.SetJsonAsync(uid, "heart_solo_v1", s, ct);
                var granted = await accounts.GrantItemsAsync(uid, items.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))), ct);
                var frames = await InventorySync.FramesAsync(accounts, codec, r, granted.Select(x => x.Item1), ct);
                frames.Add(HandlerHelpers.Reply(codec, r, 75003, new Dictionary<string, object?>
                {
                    ["result"] = 0u, ["reward_list"] = granted.Select(x => (object)new Dictionary<string, object?> { ["id"] = (uint)x.Item1, ["num"] = (uint)x.Item2 }).ToArray()
                }));
                frames.Add(new GameResponse(75001, 0, srv, codec.Encode("sc_75001", SoloPayload(s))));
                return frames;
            }
            var m = await MultAsync(uid, ct);
            var diff = (int)HandlerHelpers.ReadUInt(r.Body, "difficulty");
            switch (cmd)
            {
                case 75020:
                    {
                        var heroes = HandlerHelpers.ReadIdList(r.Body, "hero_id_list");
                        Require(heroes.Count > 0 && diff > 0, "Invalid heart demon team");
                        if (!m.Diffs.TryGetValue(diff, out var d)) m.Diffs[diff] = d = new MultDiff();
                        d.Enter = true;
                        d.Heroes = heroes.ToList();
                        break;
                    }
                case 75022:
                    {
                        var stage = (long)HandlerHelpers.ReadUInt(r.Body, "stage_id");
                        var loc = Locate(stage);
                        Require(loc is { Table: "mult" }, "Unknown heart demon stage");
                        var dd = m.Diffs.TryGetValue(loc!.Value.Difficulty, out var d) ? d : m.Diffs[loc.Value.Difficulty] = new MultDiff { Enter = true };
                        dd.Stages.TryAdd(stage, new MultStage { StageId = stage });
                        break;
                    }
                case 75026:
                    m.Diffs.Remove(diff);
                    break;
                case 75028:
                    {
                        var stage = (long)HandlerHelpers.ReadUInt(r.Body, "stage_id");
                        var loc = Locate(stage);
                        Require(loc is { Table: "mult" } && m.Diffs.ContainsKey(loc!.Value.Difficulty), "Unknown heart demon stage");
                        var d = m.Diffs[loc!.Value.Difficulty];
                        if (!d.Stages.TryGetValue(stage, out var st)) d.Stages[stage] = st = new MultStage { StageId = stage };
                        st.State = 2;
                        st.BossHp = Math.Min(10000, HandlerHelpers.ReadUInt(r.Body, "boss_hp_rate"));
                        break;
                    }
            }
            await accounts.SetJsonAsync(uid, "heart_mult_v1", m, ct);
            return [new GameResponse(75017, 0, srv, codec.Encode("sc_75017", MultPayload(m))), HandlerHelpers.ResultOnly(codec, r, cmd + 1)];
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)]; }
    }
}
