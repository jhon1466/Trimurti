using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 迭代校验 (core verification, regular mode), port of Alpha-Persei weekly_challenge_service core-verification
/// functions, the settlement branch and operations 75006/75010/75012. 16-cycle weekly rotation; two bosses with
/// difficulties 1..8 (catalog cycle_info); difficulty 8 is the score stage (10000 + 800/affix + time bonus).
/// Clearing a difficulty also clears the lower ones. Rewards (core_verification_rewards_complete.json): type 1/2
/// per boss difficulty, 3 by the lower of both bosses, 4 permanent first-clear.
/// </summary>
public sealed class CoreVerification(GameAccountRepository accounts, SourceGameConfig config)
{
    public JsonElement Catalog => config.Get("core_verification_catalog.json") ?? default;
    public JsonElement Rewards => config.Get("core_verification_rewards_complete.json") ?? default;

    public static int Cycle() => (int)((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1700000000) / 604800 % 16) + 1;
    public static bool IsStage(uint type, long stage) => type == 67 || stage is >= 3090001 and <= 3090999 or >= 10101 and <= 16208;

    public sealed class Record { public int Boss { get; set; } public int Diff { get; set; } public int Sign { get; set; } public int MinTime { get; set; } public int Score { get; set; } }
    public sealed class State
    {
        public int Cycle { get; set; }
        public Dictionary<string, Record> Records { get; set; } = [];
        public Dictionary<string, List<long>> Locks { get; set; } = new() { ["1"] = [], ["2"] = [] };
        public List<int> Claimed { get; set; } = [];
    }

    public async Task<State> LoadAsync(long uid, CancellationToken ct)
    {
        var cycle = Cycle();
        var s = await accounts.GetJsonAsync<State>(uid, "core_verification_v1", ct);
        return s is null || s.Cycle != cycle ? new State { Cycle = cycle } : s;
    }

    public Task SaveAsync(long uid, State s, CancellationToken ct) => accounts.SetJsonAsync(uid, "core_verification_v1", s, ct);

    /// <summary>Stages of the cycle per boss, ordered by difficulty: (info id, stage id, difficulty).</summary>
    public (List<(int Info, long Stage, int Diff)> Boss1, List<(int Info, long Stage, int Diff)> Boss2) Stages(int cycle)
    {
        var b1 = new List<(int, long, int)>();
        var b2 = new List<(int, long, int)>();
        static long N(JsonElement v, string k) => v.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt64() : 0;
        foreach (var p in Catalog.GetProperty("cycle_info").EnumerateObject())
        {
            var v = p.Value;
            if (N(v, "cycle") != cycle || !int.TryParse(p.Name, out var info)) continue;
            var row = (info, N(v, "stage_id"), (int)N(v, "difficult"));
            if (N(v, "boss_type") == 1) b1.Add(row); else if (N(v, "boss_type") == 2) b2.Add(row);
        }
        // Like the original: a cycle without catalog rows gets synthetic ids cycle*1000 + boss*100 + difficulty.
        if (b1.Count == 0) b1.AddRange(Enumerable.Range(1, 8).Select(d => (cycle * 1000 + 100 + d, 0L, d)));
        if (b2.Count == 0) b2.AddRange(Enumerable.Range(1, 8).Select(d => (cycle * 1000 + 200 + d, 0L, d)));
        return (b1.OrderBy(x => x.Item3).ToList(), b2.OrderBy(x => x.Item3).ToList());
    }

    public async Task<List<(int Mode, List<long> Affixes)>> AffixesAsync(long uid, CancellationToken ct) =>
        (await accounts.GetJsonAsync<Dictionary<string, List<long>>>(uid, "core_verification_affix_v1", ct) ?? [])
            .Select(kv => (int.Parse(kv.Key), kv.Value)).ToList();

    public async Task<Dictionary<string, object?>> PayloadAsync(long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        var (b1, b2) = Stages(s.Cycle);
        var stageInfo = b1.Concat(b2).Where(x => s.Records.TryGetValue(x.Info.ToString(), out var r) && r.Sign == 1)
            .Select(x => { var r = s.Records[x.Info.ToString()]; return (object)new Dictionary<string, object?> { ["id"] = (uint)x.Info, ["sign"] = 1u, ["min_time"] = (uint)r.MinTime, ["score"] = (uint)r.Score }; })
            .ToArray();
        uint Score(List<(int Info, long Stage, int Diff)> b) => b.Count > 0 && s.Records.TryGetValue(b[^1].Info.ToString(), out var r) ? (uint)r.Score : 0u;
        object Lock(int boss, uint score) => new Dictionary<string, object?> { ["boss_type"] = (uint)boss, ["hero_list"] = s.Locks.GetValueOrDefault(boss.ToString(), []).Select(h => (uint)h).ToArray(), ["score"] = score };
        var locks = new[] { Lock(1, Score(b1)), Lock(2, Score(b2)) };
        return new()
        {
            ["now_cycle"] = (uint)s.Cycle, ["next_cycle"] = (uint)(s.Cycle % 16 + 1), ["stage_info"] = stageInfo,
            ["reward_list"] = s.Claimed.Concat((await accounts.GetClaimsAsync(uid, "core_first", ct)).Select(int.Parse)).Select(x => (uint)x).ToArray(),
            ["lock_list"] = locks, ["refresh_timestamp"] = (uint)BossChallenge.Bounds().Next,
            ["affix_list"] = (await AffixesAsync(uid, ct)).Select(a => (object)new Dictionary<string, object?> { ["id"] = (uint)a.Mode, ["affix_list"] = a.Affixes.Select(x => (uint)x).ToArray() }).ToArray(),
            ["max_score_info"] = new Dictionary<string, object?> { ["lock_list"] = locks }
        };
    }

    /// <summary>Settlement of a won regular stage (record_core_verification_stage_clear).</summary>
    public async Task RecordClearAsync(long uid, long stage, int battleTimeMs, IReadOnlyList<long> heroes, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        static long N(JsonElement v, string k) => v.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt64() : 0;
        var match = Catalog.GetProperty("cycle_info").EnumerateObject()
            .FirstOrDefault(p => N(p.Value, "stage_id") == stage && N(p.Value, "cycle") == s.Cycle);
        if (match.Value.ValueKind != JsonValueKind.Object)
            match = Catalog.GetProperty("cycle_info").EnumerateObject().FirstOrDefault(p => p.Name == stage.ToString());
        if (match.Value.ValueKind != JsonValueKind.Object) return;
        var info = int.Parse(match.Name);
        var boss = (int)Math.Max(1, N(match.Value, "boss_type"));
        var diff = (int)Math.Max(1, N(match.Value, "difficult"));
        var score = 0;
        if (diff == 8)
        {
            var affixes = (await AffixesAsync(uid, ct)).FirstOrDefault(a => a.Mode == boss).Affixes ?? [];
            var seconds = battleTimeMs > 1000 ? battleTimeMs / 1000.0 : battleTimeMs;
            score = 10000 + affixes.Count * 800 + Math.Max(0, (int)(3000 - seconds * 20));
        }
        var key = info.ToString();
        var old = s.Records.GetValueOrDefault(key);
        s.Records[key] = new Record
        {
            Boss = boss, Diff = diff, Sign = 1,
            MinTime = old is { MinTime: > 0 } && (battleTimeMs <= 0 || old.MinTime < battleTimeMs) ? old.MinTime : battleTimeMs,
            Score = Math.Max(old?.Score ?? 0, score)
        };
        // Clearing difficulty D also marks 1..D-1 of the same boss.
        for (var d = 1; d < diff; d++)
        {
            var lower = (s.Cycle * 1000 + boss * 100 + d).ToString();
            if (!s.Records.TryGetValue(lower, out var lr) || lr.Sign != 1)
                s.Records[lower] = new Record { Boss = boss, Diff = d, Sign = 1, MinTime = battleTimeMs };
        }
        var locks = s.Locks.GetValueOrDefault(boss.ToString(), []);
        foreach (var h in heroes) if (!locks.Contains(h)) locks.Add(h);
        s.Locks[boss.ToString()] = locks;
        await SaveAsync(uid, s, ct);
    }

    public int MaxDiff(State s, int boss) => s.Records.Values.Where(r => r.Boss == boss && r.Sign == 1).Select(r => r.Diff).DefaultIfEmpty(0).Max();
}

/// <summary>75006 claim regular-mode task rewards (type 1/2 per boss, 3 both bosses, 4 permanent first clear).</summary>
public sealed class CoreVerificationRewardHandler(CoreVerification core, GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 75006;
    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var s = await core.LoadAsync(r.AccountId, ct);
        int d1 = core.MaxDiff(s, 1), d2 = core.MaxDiff(s, 2), dmin = Math.Min(d1, d2);
        var granted = new List<(long, long)>();
        foreach (var id in HandlerHelpers.ReadIdList(r.Body, "reward_list").Select(x => (int)x))
        {
            if (!core.Rewards.TryGetProperty(id.ToString(), out var t)) continue;
            var type = t.GetProperty("reward_type").GetInt32();
            if (type == 4 ? (await accounts.GetClaimsAsync(r.AccountId, "core_first", ct)).Contains(id.ToString()) : s.Claimed.Contains(id)) continue;
            var ok = type switch
            {
                1 => id % 10 > 0 && d1 >= id % 10,
                2 => id % 10 > 0 && d2 >= id % 10,
                3 => id - 1000 > 0 && dmin >= id - 1000,
                4 => id is >= 2001 and <= 2008 ? dmin >= id - 2000 : id is >= 2011 and <= 2018 ? d1 >= id - 2010 : id is >= 2021 and <= 2028 && d2 >= id - 2020,
                _ => true
            };
            if (!ok) continue;
            if (type == 4) await accounts.TryClaimAsync(r.AccountId, "core_first", id.ToString(), ct); else s.Claimed.Add(id);
            granted.AddRange(t.GetProperty("rewards").EnumerateArray().Select(x => (x.GetProperty("id").GetInt64(), x.GetProperty("num").GetInt64())));
        }
        await core.SaveAsync(r.AccountId, s, ct);
        var merged = granted.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).ToArray();
        var given = await accounts.GrantItemsAsync(r.AccountId, merged, ct);
        var frames = await InventorySync.FramesAsync(accounts, codec, r, given.Select(x => x.Item1), ct);
        frames.Insert(0, HandlerHelpers.Reply(codec, r, 75007, new Dictionary<string, object?> { ["result"] = 0u, ["reward_list"] = InventorySync.Rewards(given) }));
        frames.Add(new GameResponse(75009, 0, (ushort)(r.ServerIndex + 1), codec.Encode("sc_75009", await core.PayloadAsync(r.AccountId, ct))));
        return frames;
    }
}

/// <summary>75010 reset: type 0 = all stage records and hero locks of the cycle, otherwise only the hero locks.</summary>
public sealed class CoreVerificationResetHandler(CoreVerification core, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 75010;
    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var s = await core.LoadAsync(r.AccountId, ct);
        if (HandlerHelpers.ReadUInt(r.Body, "type") == 0) s.Records.Clear();
        s.Locks = new() { ["1"] = [], ["2"] = [] };
        await core.SaveAsync(r.AccountId, s, ct);
        return [HandlerHelpers.ResultOnly(codec, r, 75011), new GameResponse(75009, 0, (ushort)(r.ServerIndex + 1), codec.Encode("sc_75009", await core.PayloadAsync(r.AccountId, ct)))];
    }
}

/// <summary>75012 save the chosen affixes of a boss (score stage).</summary>
public sealed class CoreVerificationAffixHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 75012;
    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        if (r.Body.TryGetValue("affix_info", out var raw) && raw is IReadOnlyDictionary<string, object?> info)
        {
            var all = await accounts.GetJsonAsync<Dictionary<string, List<long>>>(r.AccountId, "core_verification_affix_v1", ct) ?? [];
            all[HandlerHelpers.ReadUInt(info, "id", 1).ToString()] = HandlerHelpers.ReadIdList(info, "affix_list").ToList();
            await accounts.SetJsonAsync(r.AccountId, "core_verification_affix_v1", all, ct);
        }
        return [HandlerHelpers.ResultOnly(codec, r, 75013)];
    }
}
