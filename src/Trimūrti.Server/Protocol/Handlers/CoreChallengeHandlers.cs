using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 迭代校验·挑战关 (core verification challenge modes 1–4), port of Alpha-Persei get_core_verification_challenge_data,
/// the core_verification_cl settle branch, core_mode4_reward_service and operations 89014–89038 / 89432–89438 /
/// 89802–89806. Seasons (main activity X3301 → stage activity X3501) rotate every two weeks from Thursday 05:00 UTC+8.
/// Tasks and badges are judged with the client's ConditionCfg (Data/Configs/condition_cfg.json, extracted from
/// ConditionCfg.lua): 11304 clear stage, 11305 clear stage with ≥ score, 11306 clear stage with the given affixes,
/// 11307 total score of the stage set ≥ X.
/// </summary>
public sealed class CoreChallenge(GameAccountRepository accounts, SourceGameConfig config)
{
    public sealed record Season(uint Main, uint[] Subs, uint StageActivity, int Mode, uint Theme, uint Template);

    private static readonly Dictionary<uint, int> TemplateMode = new() { [393] = 1, [403] = 2, [417] = 3, [433] = 4 };
    private static readonly DateTimeOffset Anchor = new(2026, 1, 1, 5, 0, 0, GameClock.Offset); // a Thursday

    private JsonElement Cl => config.Get("core_verification_cl_catalog.json") ?? default;
    private JsonElement Mode4Cfg => config.Get("core_mode4_rewards_cfg.json") ?? default;
    private JsonElement Conditions => config.Get("condition_cfg.json") ?? default;

    public const uint HubActivity = 111;
    private IReadOnlyList<Season>? _seasons;

    /// <summary>All challenge seasons of activity_content_cfg (latest version), oldest first.</summary>
    public IReadOnlyList<Season> Seasons => _seasons ??= LoadSeasons();

    private IReadOnlyList<Season> LoadSeasons()
    {
        var list = new List<Season>();
        if (config.Get("activity_content_cfg.json") is not { ValueKind: JsonValueKind.Object } root) return list;
        var latest = root.GetProperty("versions").EnumerateObject().OrderBy(v => int.TryParse(v.Name, out var n) ? n : 0).Last().Value.GetProperty("activities");
        // The client only reaches seasons listed under the hub activity (ActivityConst.CORE_VERIFICATION = 111);
        // CoreVerificationChallengeTools caches them from ActivityCfg[111].sub_activity_list. Older seasons (modes 1-3)
        // are not reachable and their mode frames crash the client (InitTaskInfo indexes a nil activity).
        var hub = latest.TryGetProperty(HubActivity.ToString(), out var h) && h.TryGetProperty("sub_activity_list", out var hs) && hs.ValueKind == JsonValueKind.Array
            ? hs.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetUInt32()).ToHashSet() : [];
        foreach (var a in latest.EnumerateObject())
        {
            if (!uint.TryParse(a.Name, out var hid) || !hub.Contains(hid)) continue;
            var v = a.Value;
            var template = v.TryGetProperty("activity_template", out var t) ? t.GetUInt32() : 0;
            if (!TemplateMode.TryGetValue(template, out var mode) || !uint.TryParse(a.Name, out var id)) continue;
            var subs = v.TryGetProperty("sub_activity_list", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetUInt32()).ToArray() : [];
            var stageAct = subs.FirstOrDefault(x => StagesOf(x).Count > 0);
            if (stageAct == 0) continue;
            list.Add(new Season(id, subs, stageAct, mode, v.GetProperty("activity_theme").GetUInt32(), template));
        }
        return list.OrderBy(x => x.Main).ToList();
    }

    public (Season? Season, long Start, long Stop) Current() => Current(0);

    /// <summary>Running season of a mode (0 = any). Every mode has one season open at a time: the client initialises all four modes.</summary>
    public (Season? Season, long Start, long Stop) Current(int mode)
    {
        var pool = Seasons.Where(x => mode == 0 || x.Mode == mode).ToList();
        if (pool.Count == 0) return (null, 0, 0);
        var n = (long)Math.Floor((GameClock.Now - Anchor).TotalDays / 14);
        var start = Anchor.AddDays(n * 14);
        return (pool[(int)(((n % pool.Count) + pool.Count) % pool.Count)], start.ToUnixTimeSeconds(), start.AddDays(14).ToUnixTimeSeconds());
    }

    public IEnumerable<Season> CurrentAll() =>
        new[] { 1, 2, 3, 4 }.Select(m => Current(m).Season).Where(x => x is not null)!;

    /// <summary>Activity list entries (main + subs) of the running season, for sc_11001.</summary>
    public IEnumerable<Dictionary<string, object?>> ActivityEntries(Func<uint, (uint Theme, uint Template, uint[] Subs)?> cfg)
    {
        if (Seasons.Count > 0 && cfg(HubActivity) is { } hubCfg)
        {
            var (_, hs, he) = Current();
            yield return new()
            {
                ["activity_id"] = HubActivity, ["start_time"] = hs, ["stop_time"] = he, ["state"] = 1,
                ["theme"] = hubCfg.Theme, ["template"] = hubCfg.Template, ["sub_activity_id_list"] = hubCfg.Subs.Cast<object>().ToArray()
            };
        }
        foreach (var mode in new[] { 1, 2, 3, 4 })
        {
        var (season, start, stop) = Current(mode);
        if (season is null) continue;
        foreach (var id in new[] { season.Main }.Concat(season.Subs))
        {
            if (cfg(id) is not { } e) continue;
            yield return new()
            {
                ["activity_id"] = id, ["start_time"] = start, ["stop_time"] = stop, ["state"] = 1,
                ["theme"] = e.Theme, ["template"] = e.Template, ["sub_activity_id_list"] = e.Subs.Cast<object>().ToArray()
            };
        }
        }
    }

    public List<JsonElement> StagesOf(uint activity)
    {
        var list = new List<JsonElement>();
        if (Cl.ValueKind != JsonValueKind.Object) return list;
        foreach (var m in new[] { "mode1", "mode2", "mode3", "mode4" })
            if (Cl.TryGetProperty(m, out var stages))
                list.AddRange(stages.EnumerateObject().Select(p => p.Value).Where(s => s.GetProperty("activity_id").GetUInt32() == activity));
        return list;
    }

    public (uint Activity, int Mode, JsonElement Cfg)? StageCfg(long stageId)
    {
        if (Cl.ValueKind != JsonValueKind.Object) return null;
        foreach (var m in new[] { "mode1", "mode2", "mode3", "mode4" })
            if (Cl.TryGetProperty(m, out var stages) && stages.TryGetProperty(stageId.ToString(), out var s) && s.GetProperty("activity_id").GetUInt32() != 0)
                return (s.GetProperty("activity_id").GetUInt32(), s.GetProperty("mode").GetInt32(), s);
        return null;
    }

    public static bool IsStage(uint stageType, long stageId) => stageType is 88 or 90 || stageId is >= 3092001 and <= 3093099;

    // ---------------- state ----------------

    public sealed class Team
    {
        public int Index { get; set; }
        public List<long> Heroes { get; set; } = [];
        public int Value { get; set; }
        public int Seconds { get; set; }
    }

    public sealed class StageRec
    {
        public bool Cleared { get; set; }
        public int Best { get; set; }
        public int Recent { get; set; }
        public int MinSeconds { get; set; }
        public List<long> Heroes { get; set; } = [];
        public List<long> Buffs { get; set; } = [];
        public List<Team> Teams { get; set; } = [];
    }

    public sealed class State
    {
        public Dictionary<long, StageRec> Stages { get; set; } = [];
        public List<long> Selected { get; set; } = [];
        public List<long> Claimed { get; set; } = [];
        public bool Tipped { get; set; }
        public int BestPoint { get; set; }
    }

    private static string Kind(uint activity) => $"core_cl_{activity}";

    public async Task<State> LoadAsync(long uid, uint activity, CancellationToken ct) =>
        await accounts.GetJsonAsync<State>(uid, Kind(activity), ct) ?? new State();

    public Task SaveAsync(long uid, uint activity, State s, CancellationToken ct) => accounts.SetJsonAsync(uid, Kind(activity), s, ct);

    public async Task<Dictionary<long, long>> BadgesAsync(long uid, CancellationToken ct) =>
        await accounts.GetJsonAsync<Dictionary<long, long>>(uid, "core_cl_badges", ct) ?? [];

    // ---------------- scoring ----------------

    /// <summary>Challenge value (calc_core_verification_super_score): 10000 + 800 per selected affix + time bonus
    /// max(0, 3000 − 20·s); ordinary stages of modes 3/4 score 3000 + the same time bonus.</summary>
    public static int Score(bool challenge, int battleTimeMs, int affixes)
    {
        var sec = battleTimeMs > 1000 ? battleTimeMs / 1000.0 : battleTimeMs;
        var bonus = Math.Max(0, (int)(3000 - sec * 20));
        return challenge ? 10000 + affixes * 800 + bonus : 3000 + bonus;
    }

    public static int StageTotal(State s) =>
        s.Stages.Values.Sum(r => r.Teams.Count > 0 ? r.Teams.Sum(t => t.Value) : r.Recent);

    public async Task RecordAsync(long uid, long stageId, int battleTimeMs, IReadOnlyList<long> heroes, CancellationToken ct)
    {
        if (StageCfg(stageId) is not { } info) return;
        var s = await LoadAsync(uid, info.Activity, ct);
        var challenge = info.Cfg.GetProperty("stage_type").GetInt32() == 2;
        var score = Score(challenge, battleTimeMs, challenge ? s.Selected.Count : 0);
        var sec = battleTimeMs > 1000 ? battleTimeMs / 1000 : battleTimeMs;
        if (!s.Stages.TryGetValue(stageId, out var rec)) s.Stages[stageId] = rec = new StageRec();
        rec.Cleared = true;
        rec.Recent = score;
        rec.Best = Math.Max(rec.Best, score);
        rec.MinSeconds = rec.MinSeconds == 0 ? sec : Math.Min(rec.MinSeconds, sec);
        rec.Heroes = heroes.ToList();
        if (challenge) rec.Buffs = s.Selected.ToList();
        if (info.Mode == 4)
        {
            var teams = Math.Max(1, info.Cfg.TryGetProperty("stage_team", out var st) ? st.GetInt32() : 1);
            var team = rec.Teams.FirstOrDefault(t => t.Heroes.OrderBy(x => x).SequenceEqual(heroes.OrderBy(x => x)))
                ?? (rec.Teams.Count < teams ? null : rec.Teams.OrderBy(t => t.Value).First());
            if (team is null) rec.Teams.Add(team = new Team { Index = Enumerable.Range(0, teams).First(i => rec.Teams.All(t => t.Index != i)) });
            team.Heroes = heroes.ToList();
            if (score >= team.Value) { team.Value = score; team.Seconds = sec; }
        }
        s.BestPoint = Math.Max(s.BestPoint, StageTotal(s));
        await SaveAsync(uid, info.Activity, s, ct);
        await UnlockBadgesAsync(uid, ct);
    }

    // ---------------- tasks & badges ----------------

    public sealed record TaskDef(long Id, int RewardType, int CondType, JsonElement Params, IReadOnlyList<(long, long)> Items);

    public IReadOnlyList<TaskDef> Tasks(uint activity)
    {
        var list = new List<TaskDef>();
        if (Cl.ValueKind == JsonValueKind.Object && Cl.TryGetProperty("rewards", out var rw))
            foreach (var r in rw.EnumerateObject().Select(p => p.Value).Where(r => r.GetProperty("activity_id").GetUInt32() == activity))
            {
                var (type, prm) = Condition(r.GetProperty("condition").GetInt64());
                list.Add(new TaskDef(r.GetProperty("id").GetInt64(), r.GetProperty("reward_type").GetInt32(), type, prm, Items(r.GetProperty("rewards"))));
            }
        if (list.Count == 0 && Mode4Cfg.ValueKind == JsonValueKind.Object)
            foreach (var p in Mode4Cfg.GetProperty("profiles").EnumerateObject().Select(x => x.Value).Where(p => p.GetProperty("activity").GetUInt32() == activity))
                foreach (var t in p.GetProperty("tasks").EnumerateObject())
                    list.Add(new TaskDef(long.Parse(t.Name), 1, t.Value.GetProperty("condition_type").GetInt32(), t.Value.GetProperty("params"), Items(t.Value.GetProperty("items"))));
        return list;
    }

    private static IReadOnlyList<(long, long)> Items(JsonElement arr) =>
        arr.EnumerateArray().Select(x => (x.GetProperty("id").GetInt64(), x.GetProperty("num").GetInt64())).Where(x => x.Item1 > 0 && x.Item2 > 0).ToArray();

    private (int Type, JsonElement Params) Condition(long id) =>
        id > 0 && Conditions.ValueKind == JsonValueKind.Object && Conditions.TryGetProperty(id.ToString(), out var c)
            ? (c.GetProperty("type").GetInt32(), c.TryGetProperty("params", out var p) ? p : default) : (0, default);

    private static long P(JsonElement prm, int i) =>
        prm.ValueKind == JsonValueKind.Array && prm.GetArrayLength() > i && prm[i].ValueKind == JsonValueKind.Number ? prm[i].GetInt64() : 0;

    /// <summary>Evaluates one client condition against a season state (stages belong to that season's activity).</summary>
    public bool Met(int type, JsonElement prm, State s)
    {
        StageRec? Rec(long id) => s.Stages.TryGetValue(id, out var r) && r.Cleared ? r : null;
        switch (type)
        {
            case 11304: return Rec(P(prm, 0)) is not null;
            case 11305: return Rec(P(prm, 0)) is { } r5 && Math.Max(r5.Best, r5.Teams.Sum(t => t.Value)) >= P(prm, 1);
            case 11306:
                {
                    var need = prm.ValueKind == JsonValueKind.Array && prm.GetArrayLength() > 0 && prm[0].ValueKind == JsonValueKind.Array
                        ? prm[0].EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToArray() : [];
                    return Rec(P(prm, 1)) is { } r6 && need.All(r6.Buffs.Contains);
                }
            case 11307: return Rec(P(prm, 0)) is not null && Math.Max(s.BestPoint, StageTotal(s)) >= P(prm, 1);
            default: return false;
        }
    }

    /// <summary>finish_assignment_list: 2 claimed, 1 claimable (condition met / all tasks claimed for the final chest).</summary>
    public object[] TaskStates(uint activity, State s)
    {
        var tasks = Tasks(activity);
        var normal = tasks.Where(t => t.RewardType != 0).ToArray();
        return tasks.Select(t =>
        {
            var state = s.Claimed.Contains(t.Id) ? 2u : CanClaim(t, normal, s) ? 1u : 0u;
            return (object)new Dictionary<string, object?> { ["assignment_id"] = (uint)t.Id, ["state"] = state };
        }).Where(x => ((Dictionary<string, object?>)x)["state"] is not 0u).ToArray();
    }

    private bool CanClaim(TaskDef t, IReadOnlyList<TaskDef> normal, State s) =>
        t.RewardType == 0 ? normal.Count > 0 && normal.All(n => s.Claimed.Contains(n.Id)) : Met(t.CondType, t.Params, s);

    public async Task<IReadOnlyList<(long, long)>> ClaimAsync(long uid, uint activity, IEnumerable<long> ids, CancellationToken ct)
    {
        var s = await LoadAsync(uid, activity, ct);
        var tasks = Tasks(activity);
        var normal = tasks.Where(t => t.RewardType != 0).ToArray();
        var items = new List<(long, long)>();
        // Final chest last, so a batch claim of every task also grants it.
        foreach (var t in ids.Distinct().Select(id => tasks.FirstOrDefault(x => x.Id == id)).OfType<TaskDef>().OrderBy(t => t.RewardType == 0))
        {
            if (s.Claimed.Contains(t.Id) || !CanClaim(t, normal, s)) continue;
            s.Claimed.Add(t.Id);
            items.AddRange(t.Items);
        }
        await SaveAsync(uid, activity, s, ct);
        return items.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).ToArray();
    }

    /// <summary>Badges (89021 illustrated): unlocked once their condition holds in the season of their main activity.</summary>
    public async Task UnlockBadgesAsync(long uid, CancellationToken ct)
    {
        if (Cl.ValueKind != JsonValueKind.Object || !Cl.TryGetProperty("badges", out var badges)) return;
        var owned = await BadgesAsync(uid, ct);
        var changed = false;
        var states = new Dictionary<uint, State>();
        foreach (var b in badges.EnumerateObject().Select(p => p.Value))
        {
            var id = b.GetProperty("id").GetInt64();
            if (owned.ContainsKey(id)) continue;
            var season = Seasons.FirstOrDefault(x => x.Main == b.GetProperty("activity_id").GetUInt32());
            if (season is null) continue;
            if (!states.TryGetValue(season.StageActivity, out var st)) states[season.StageActivity] = st = await LoadAsync(uid, season.StageActivity, ct);
            var (type, prm) = Condition(b.GetProperty("condition").GetInt64());
            if (!Met(type, prm, st)) continue;
            owned[id] = GameClock.Now.ToUnixTimeSeconds();
            changed = true;
        }
        if (changed) await accounts.SetJsonAsync(uid, "core_cl_badges", owned, ct);
    }

    // ---------------- payloads ----------------

    public static uint CmdOf(int mode) => mode switch { 1 => 89013, 2 => 89025, 3 => 89431, _ => 89801 };

    public async Task<IReadOnlyList<(uint Cmd, byte[] Payload)>> FramesAsync(ProtoCodec codec, long uid, uint? activity, CancellationToken ct)
    {
        var targets = activity is { } a ? Seasons.Where(x => x.StageActivity == a || x.Main == a).Take(1) : CurrentAll();
        var frames = new List<(uint, byte[])>();
        foreach (var season in targets)
        {
            var cmd = CmdOf(season.Mode);
            frames.Add((cmd, codec.Encode($"sc_{cmd}", await PayloadAsync(uid, season, ct))));
        }
        var badges = await BadgesAsync(uid, ct);
        frames.Add((89021, codec.Encode("sc_89021", new Dictionary<string, object?>
        {
            ["illustrated"] = badges.Select(b => (object)new Dictionary<string, object?> { ["illustrated_id"] = (uint)b.Key, ["time"] = (uint)b.Value }).ToArray()
        })));
        return frames;
    }

    public async Task<IReadOnlyList<GameResponse>> PushAsync(ProtoCodec codec, GameRequest r, uint? activity, CancellationToken ct) =>
        (await FramesAsync(codec, r.AccountId, activity, ct)).Select(f => new GameResponse(f.Cmd, 0, (ushort)(r.ServerIndex + 1), f.Payload)).ToArray();

    private async Task<Dictionary<string, object?>> PayloadAsync(long uid, Season season, CancellationToken ct)
    {
        var act = season.StageActivity;
        var s = await LoadAsync(uid, act, ct);
        var stages = StagesOf(act);
        var commons = stages.Where(x => x.GetProperty("stage_type").GetInt32() == 1).OrderBy(x => x.GetProperty("stage_id").GetInt64()).ToArray();
        var challenge = stages.FirstOrDefault(x => x.GetProperty("stage_type").GetInt32() == 2);
        var challengeId = challenge.ValueKind == JsonValueKind.Object ? challenge.GetProperty("stage_id").GetInt64() : 0;
        StageRec R(long id) => s.Stages.TryGetValue(id, out var r) ? r : new StageRec();
        uint[] Locks(StageRec r) => r.Cleared ? r.Heroes.Select(x => (uint)x).ToArray() : [];
        var ch = R(challengeId);
        var tasks = TaskStates(act, s);

        switch (season.Mode)
        {
            case 1:
                {
                    var buffs = new Dictionary<long, (long Level, int Type)>();
                    void Add(JsonElement cfg, string key, int type)
                    {
                        if (!cfg.TryGetProperty(key, out var b) || b.ValueKind != JsonValueKind.Array || b.GetArrayLength() == 0) return;
                        var id = b[0].GetInt64();
                        if (id > 0) buffs[id] = (b.GetArrayLength() > 1 ? b[1].GetInt64() : 0, type);
                    }
                    if (challenge.ValueKind == JsonValueKind.Object) { Add(challenge, "stage_buff", 1); Add(challenge, "stage_debuff", 2); }
                    foreach (var c in commons.Where(c => R(c.GetProperty("stage_id").GetInt64()).Cleared)) { Add(c, "stage_buff", 1); Add(c, "stage_debuff", 2); }
                    return new()
                    {
                        ["challenge_stage"] = new Dictionary<string, object?>
                        {
                            ["stage_id"] = (uint)challengeId, ["max_challenge_value"] = (uint)ch.Best, ["challenge_lock"] = Locks(ch),
                            ["challenge_buff"] = s.Selected.Select(x => (uint)x).ToArray(), ["recently_challenge_value"] = (uint)ch.Recent
                        },
                        ["common_stage"] = commons.Select(c => (object)new Dictionary<string, object?>
                        {
                            ["stage_id"] = (uint)c.GetProperty("stage_id").GetInt64(), ["common_lock_id"] = Locks(R(c.GetProperty("stage_id").GetInt64()))
                        }).ToArray(),
                        ["buff_list"] = buffs.Select(b => (object)new Dictionary<string, object?> { ["buff_id"] = (uint)b.Key, ["level"] = (uint)b.Value.Level, ["type"] = (uint)b.Value.Type }).ToArray(),
                        ["finish_assignment_list"] = tasks, ["first_enter"] = !s.Tipped
                    };
                }
            case 2:
                return new()
                {
                    ["challenge_stage"] = new Dictionary<string, object?>
                    {
                        ["stage_id"] = (uint)challengeId, ["max_challenge_value"] = (uint)ch.Best, ["challenge_lock"] = Locks(ch),
                        ["challenge_buff"] = s.Selected.Select(x => (uint)x).ToArray(), ["recently_challenge_value"] = (uint)ch.Recent
                    },
                    ["common_stage"] = commons.Select(c => (object)new Dictionary<string, object?>
                    {
                        ["stage_id"] = (uint)c.GetProperty("stage_id").GetInt64(), ["common_lock_id"] = Locks(R(c.GetProperty("stage_id").GetInt64()))
                    }).ToArray(),
                    ["finish_assignment_list"] = tasks, ["first_enter"] = !s.Tipped
                };
            case 3:
                return new()
                {
                    ["challenge_stage"] = new Dictionary<string, object?>
                    {
                        ["stage_id"] = (uint)challengeId, ["max_challenge_value"] = (uint)ch.Best, ["min_seconds"] = (uint)ch.MinSeconds,
                        ["challenge_lock"] = Locks(ch), ["recently_challenge_value"] = (uint)ch.Recent, ["reset_challenge_value"] = 0u
                    },
                    ["common_stage"] = commons.Select(c =>
                    {
                        var r = R(c.GetProperty("stage_id").GetInt64());
                        return (object)new Dictionary<string, object?>
                        {
                            ["stage_id"] = (uint)c.GetProperty("stage_id").GetInt64(), ["common_lock_id"] = Locks(r), ["max_value"] = (uint)r.Best,
                            ["min_seconds"] = (uint)r.MinSeconds, ["recently_normal_value"] = (uint)r.Recent, ["reset_normal_value"] = 0u
                        };
                    }).ToArray(),
                    ["finish_assignment_list"] = tasks, ["first_enter"] = !s.Tipped,
                    ["cur_point"] = (uint)StageTotal(s), ["max_point"] = (uint)s.BestPoint
                };
            default:
                return new()
                {
                    ["common_stages"] = stages.OrderBy(x => x.GetProperty("stage_id").GetInt64()).Select(c => (object)new Dictionary<string, object?>
                    {
                        ["stage_id"] = (uint)c.GetProperty("stage_id").GetInt64(),
                        ["common_locks"] = R(c.GetProperty("stage_id").GetInt64()).Teams.OrderBy(t => t.Index).Select(t => (object)new Dictionary<string, object?>
                        {
                            ["index"] = (uint)t.Index, ["hero_id_list"] = t.Heroes.Select(x => (uint)x).ToArray(), ["cur_value"] = (uint)t.Value, ["seconds"] = (uint)t.Seconds
                        }).ToArray()
                    }).ToArray(),
                    ["finish_assignment_list"] = tasks, ["first_enter"] = !s.Tipped, ["max_point"] = (uint)s.BestPoint
                };
        }
    }

    public uint ResolveActivity(GameRequest r)
    {
        var a = HandlerHelpers.ReadUInt(r.Body, "activity_id");
        var season = Seasons.FirstOrDefault(x => x.StageActivity == a || x.Main == a || x.Subs.Contains(a)) ?? Current().Season;
        return season?.StageActivity ?? a;
    }
}

/// <summary>89014 (mode 1, buff_info list) / 89026 (mode 2, id list): save the challenge-stage affixes.</summary>
public sealed class CoreChallengeAffixHandler(CoreChallenge cl, ProtoCodec codec, uint command) : IGameCommandHandler
{
    public uint Command => command;
    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var act = cl.ResolveActivity(r);
        var ids = new List<long>();
        if (r.Body.TryGetValue("buff_list", out var raw) && raw is System.Collections.IEnumerable seq && raw is not string)
            foreach (var e in seq)
                if (e is IReadOnlyDictionary<string, object?> d) { var id = (long)HandlerHelpers.ReadUInt(d, "buff_id"); if (id > 0) ids.Add(id); }
                else { try { var id = Convert.ToInt64(e); if (id > 0) ids.Add(id); } catch { } }
        var s = await cl.LoadAsync(r.AccountId, act, ct);
        s.Selected = ids.Distinct().ToList();
        await cl.SaveAsync(r.AccountId, act, s, ct);
        return [HandlerHelpers.ResultOnly(codec, r, command + 1), .. await cl.PushAsync(codec, r, act, ct)];
    }
}

/// <summary>89018 / 89030 / 89434 / 89804: claim task rewards.</summary>
public sealed class CoreChallengeRewardHandler(CoreChallenge cl, GameAccountRepository accounts, ProtoCodec codec, uint command) : IGameCommandHandler
{
    public uint Command => command;
    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var act = cl.ResolveActivity(r);
        var items = await cl.ClaimAsync(r.AccountId, act, HandlerHelpers.ReadIdList(r.Body, "assignment_id"), ct);
        var granted = await accounts.GrantItemsAsync(r.AccountId, items, ct);
        var frames = await InventorySync.FramesAsync(accounts, codec, r, granted.Select(x => x.Item1), ct);
        frames.Insert(0, HandlerHelpers.Reply(codec, r, command + 1, new Dictionary<string, object?>
        {
            ["result"] = 0u,
            ["reward_list"] = HandlerHelpers.ToItemNetRecList(granted)
        }));
        frames.AddRange(await cl.PushAsync(codec, r, act, ct));
        return frames;
    }
}

/// <summary>
/// Resets: 89016 / 89028 / 89432 all stages, 89022 / 89034 / 89436 one stage, 89806 mode-4 stage (0 = all),
/// 89802 one mode-4 team. Clears stage clears and hero locks; best values and claimed tasks stay.
/// </summary>
public sealed class CoreChallengeResetHandler(CoreChallenge cl, ProtoCodec codec, uint command) : IGameCommandHandler
{
    public uint Command => command;
    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var act = cl.ResolveActivity(r);
        var s = await cl.LoadAsync(r.AccountId, act, ct);
        var stage = (long)HandlerHelpers.ReadUInt(r.Body, "stage_id");
        IEnumerable<CoreChallenge.StageRec> targets = command is 89016 or 89028 or 89432 || stage == 0
            ? s.Stages.Values : s.Stages.TryGetValue(stage, out var one) ? [one] : [];
        foreach (var rec in targets)
        {
            if (command == 89802)
            {
                var idx = (int)HandlerHelpers.ReadUInt(r.Body, "team_index");
                rec.Teams.RemoveAll(t => t.Index == idx);
                rec.Cleared = rec.Teams.Count > 0;
                continue;
            }
            rec.Cleared = false; rec.Recent = 0; rec.Heroes.Clear(); rec.Teams.Clear();
        }
        await cl.SaveAsync(r.AccountId, act, s, ct);
        return [HandlerHelpers.ResultOnly(codec, r, command + 1), .. await cl.PushAsync(codec, r, act, ct)];
    }
}

/// <summary>89036 / 89038 / 89438: season tip seen (first_enter false from then on).</summary>
public sealed class CoreChallengeTipHandler(CoreChallenge cl, ProtoCodec codec, uint command) : IGameCommandHandler
{
    public uint Command => command;
    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var act = cl.ResolveActivity(r);
        var s = await cl.LoadAsync(r.AccountId, act, ct);
        s.Tipped = true;
        await cl.SaveAsync(r.AccountId, act, s, ct);
        return [HandlerHelpers.ResultOnly(codec, r, command + 1)];
    }
}
