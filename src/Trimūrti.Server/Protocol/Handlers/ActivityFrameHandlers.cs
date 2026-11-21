using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// Player-state login frames that used to go out as schema defaults (PlayerAction 81001, CultureGravureAction 12097,
/// FukubukuroAction 17021, ChatAction 19039, CooperationAction 37101/54201, RegressionActionNew 62019,
/// ActivitySkinDrawAction 68155, NewWarChessAction 80115), plus the activity-bound frames, sent only for activities open
/// right now (LoginBootstrap.OpenActivityTemplates, ActivityTemplateConst): 107 HERO_TRIAL → 11035/11071,
/// 231/249/275/340 seven-day skin sign → 11097, 292 ACTIVITY_ACCUMULATIVE → 60201, 221 ACTIVITY_SKIN_DRAW → 68151/68161,
/// 423 ACTIVITY_OATH_DRAW_MAIN → 68185, 243 KAGUTSUCHI_ACTIVITY_PT_SCROLL → 76151, 299 ACTIVITY_MONSTER_COSPLAY → 83401.
/// Commands: 81002 story trigger (client push), 68156 skin pop seen, 11036 hero trial reward.
/// </summary>
public sealed class PlayerFrames(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
    : EventModeBase(accounts, config, codec, "battle_system_cfg.json"), IStageMode
{
    public static readonly uint[] Commands = [81002, 68156, 11036, 64078, 64084, 76152, 76154];
    private const string Kind = "player_frames_v1";

    public sealed class Instance { public uint Id { get; set; } public uint Item { get; set; } public uint Num { get; set; } public List<uint[]> Select { get; set; } = []; }
    public sealed class SkinSign { public List<uint> Unlock { get; set; } = []; public List<uint> Gain { get; set; } = []; public uint Times { get; set; } public uint Gift { get; set; } }
    public sealed class PointRec { public uint Point { get; set; } public List<uint> Rewards { get; set; } = []; }
    public sealed class SkinDraw { public Dictionary<uint, uint> Drops { get; set; } = []; public uint StoryStage { get; set; } public List<uint> Stories { get; set; } = []; public uint Last { get; set; } public List<uint> Already { get; set; } = []; }
    public sealed class PtScroll { public List<uint> Clear { get; set; } = []; public uint Buff { get; set; } public uint Up { get; set; } public uint Index { get; set; } public uint Times { get; set; } }
    public sealed class CultivateRec { public List<uint> ClaimedTasks { get; set; } = []; public string DailyDay { get; set; } = ""; public List<uint> ClaimedPoints { get; set; } = []; }

    public sealed class State
    {
        public Dictionary<uint, List<uint>> StoryTriggers { get; set; } = [];
        public List<Instance> Fukubukuro { get; set; } = [];
        public List<uint> SkinPop { get; set; } = [];
        public Dictionary<uint, Dictionary<uint, uint>> Trials { get; set; } = [];   // activity → trial id → 1 cleared / 2 rewarded
        public Dictionary<uint, SkinSign> SkinSigns { get; set; } = [];
        public Dictionary<uint, PointRec> Points { get; set; } = [];
        public Dictionary<uint, SkinDraw> Draws { get; set; } = [];
        public Dictionary<uint, PtScroll> Scrolls { get; set; } = [];
        public Dictionary<uint, CultivateRec> Cultivate { get; set; } = [];
    }

    private async Task<State> LoadAsync(long uid, CancellationToken ct) => await Accounts.GetJsonAsync<State>(uid, Kind, ct) ?? new State();
    private Task SaveAsync(long uid, State s, CancellationToken ct) => Accounts.SetJsonAsync(uid, Kind, s, ct);
    private static object?[] Arr(IEnumerable<uint> xs) => xs.Select(x => (object?)x).ToArray();

    private static readonly uint[] SkinSignTemplates = [231, 249, 275, 340];

    private IEnumerable<(uint Id, uint Activity, long Stage, List<uint> Templates, JsonElement Reward)> TrialsOf(uint activity) =>
        Cfg.GetProperty("hero_trials").EnumerateObject().Where(t => t.Value.GetProperty("activity_id").GetUInt32() == activity)
            .Select(t => (uint.Parse(t.Name), activity, t.Value.GetProperty("stage_id").GetInt64(),
                t.Value.GetProperty("templates").EnumerateArray().Select(x => x.GetUInt32()).ToList(), t.Value.GetProperty("reward_list")));

    private static IEnumerable<uint> Open(params uint[] templates) =>
        LoginBootstrap.OpenActivityTemplates().Where(a => templates.Contains(a.Value)).Select(a => a.Key).OrderBy(x => x);

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(ProtoCodec c, long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        var frames = new List<(uint, byte[])>
        {
            Frame(81001, new() { ["info_list"] = s.StoryTriggers.OrderBy(g => g.Key).Select(g => (object?)new Dictionary<string, object?>
                { ["trigger_group_id"] = g.Key, ["completed_trigger_list"] = Arr(g.Value) }).ToArray() }),
            Frame(12097, await CultureAsync(ct)),
            Frame(17021, new() { ["instance_list"] = s.Fukubukuro.Select(i => (object?)new Dictionary<string, object?>
            {
                ["instance_id"] = i.Id, ["item"] = new Dictionary<string, object?> { ["item_id"] = i.Item, ["item_num"] = i.Num },
                ["select_list"] = i.Select.Select(x => (object?)new Dictionary<string, object?> { ["item_id"] = x[0], ["item_num"] = x[1] }).ToArray()
            }).ToArray() }),
            // Friend chat and cooperation rooms: this server has no friend links or rooms, so there is nothing pending.
            Frame(19039, new() { ["friend_msg_list"] = Array.Empty<object?>() }),
            Frame(37101, new() { ["invite_list"] = Array.Empty<object?>(), ["type_list"] = Array.Empty<object?>() }),
            Frame(54201, new()),
            // RegressionDataNew: both flags belong to the returning-player (回归) activity, which this server never opens.
            Frame(62019, new() { ["other_sign"] = false, ["draw_over_sign"] = false }),
            Frame(68155, new() { ["skin_id"] = Arr(s.SkinPop) }),
            // 新因果观测 chapters: the mode has no handlers yet, so no chapter has values.
            Frame(80115, new() { ["value_list"] = Array.Empty<object?>() }),
            Frame(11071, new() { ["hero_trial_list"] = Open(107).Select(a => (object?)new Dictionary<string, object?>
                { ["activity_id"] = a, ["template_hero_list"] = Arr(TrialsOf(a).SelectMany(t => t.Templates).Distinct()) }).ToArray() })
        };
        foreach (var a in Open(107))
            frames.Add(Frame(11035, new() { ["activity_id"] = a, ["activity_info"] = TrialsOf(a).Select(t => (object?)new Dictionary<string, object?>
                { ["id"] = t.Id, ["challenge_state"] = s.Trials.GetValueOrDefault(a)?.GetValueOrDefault(t.Id) ?? 0u }).ToArray() }));
        foreach (var a in Open(SkinSignTemplates))
        {
            var g = s.SkinSigns.GetValueOrDefault(a) ?? new SkinSign();
            frames.Add(Frame(11097, new() { ["activity_id"] = a, ["unlock_reward"] = Arr(g.Unlock), ["gain_reward"] = Arr(g.Gain), ["unlock_times"] = g.Times, ["gift_reward"] = g.Gift }));
        }
        foreach (var a in Open(292))
        {
            var p = s.Points.GetValueOrDefault(a) ?? new PointRec();
            frames.Add(Frame(60201, new() { ["activity_id"] = a, ["point"] = p.Point, ["reward_list"] = Arr(p.Rewards) }));
        }
        foreach (var a in Open(221))
        {
            var d = s.Draws.GetValueOrDefault(a) ?? new SkinDraw();
            frames.Add(Frame(68151, new() { ["activity_id"] = a, ["info"] = d.Drops.Select(x => (object?)new Dictionary<string, object?> { ["drop_id"] = x.Key, ["num"] = x.Value }).ToArray() }));
            frames.Add(Frame(68161, new() { ["activity_id"] = a, ["story_stage"] = d.StoryStage, ["finished_story"] = Arr(d.Stories) }));
        }
        foreach (var a in Open(423))
        {
            var d = s.Draws.GetValueOrDefault(a) ?? new SkinDraw();
            frames.Add(Frame(68185, new()
            {
                ["activity_id"] = a, ["info"] = d.Drops.Select(x => (object?)new Dictionary<string, object?> { ["drop_id"] = x.Key, ["num"] = x.Value }).ToArray(),
                ["draw_info2"] = new Dictionary<string, object?> { ["last_drop"] = d.Last, ["already_drop"] = Arr(d.Already) }
            }));
        }
        foreach (var a in Open(243))
        {
            var p = s.Scrolls.GetValueOrDefault(a) ?? new PtScroll();
            frames.Add(Frame(76151, new() { ["activity_id"] = a, ["clear_stage_list"] = Arr(p.Clear), ["buff_id"] = p.Buff, ["up_select"] = p.Up, ["index"] = p.Index, ["clear_times"] = p.Times }));
        }
        foreach (var a in Open(299))
            frames.Add(Frame(83401, new() { ["activity_id"] = a, ["monster_list"] = Array.Empty<object?>() }));
        return frames;
    }

    /// <summary>
    /// 文化图鉴 recommendations (12097): built from the builds players on this server really use. For every hero, the share
    /// (in ten-thousandths) of owners using each astrolabe set and each common / Ω equip set (top three of each).
    /// </summary>
    private async Task<Dictionary<string, object?>> CultureAsync(CancellationToken ct)
    {
        var equips = SharedConfig.Get("sigil_cfg.json") is { } sig && sig.TryGetProperty("equips", out var eq) ? eq : default;
        bool Omega(int prefab) => equips.ValueKind == JsonValueKind.Object && equips.TryGetProperty(prefab.ToString(), out var row)
            && row.TryGetProperty("race", out var race) && race.GetInt32() != 0;
        var astro = new Dictionary<long, Dictionary<string, int>>();
        var common = new Dictionary<long, Dictionary<string, int>>();
        var omega = new Dictionary<long, Dictionary<string, int>>();
        static void Add(Dictionary<long, Dictionary<string, int>> d, long hero, string key)
        {
            if (!d.TryGetValue(hero, out var m)) d[hero] = m = [];
            m[key] = m.GetValueOrDefault(key) + 1;
        }
        foreach (var (hero, astrolabe, sigils) in await Accounts.GetHeroBuildsAsync(ct))
        {
            var set = JsonSerializer.Deserialize<List<long>>(astrolabe) ?? [];
            if (set.Count > 0) Add(astro, hero, string.Join(',', set.OrderBy(x => x)));
            if (sigils.Count == 6) Add(sigils.Any(Omega) ? omega : common, hero, string.Join(',', sigils.OrderByDescending(x => x)));
        }
        static object?[] Rec(Dictionary<long, Dictionary<string, int>> by) => by.OrderBy(h => h.Key).Select(h =>
        {
            var total = h.Value.Values.Sum();
            return (object?)new Dictionary<string, object?>
            {
                ["id"] = (uint)h.Key,
                ["rate_list"] = h.Value.OrderByDescending(x => x.Value).Take(3).OrderBy(x => x.Value).Select(x => (object?)new Dictionary<string, object?>
                {
                    ["rate"] = (uint)(x.Value * 10000L / Math.Max(1, total)),
                    ["id_list"] = x.Key.Split(',').Select(v => (object?)uint.Parse(v)).ToArray()
                }).ToArray()
            };
        }).ToArray();
        return new() { ["astro"] = Rec(astro), ["common_equip"] = Rec(common), ["omega_equip"] = Rec(omega) };
    }

    // ---------------- hero trial battles (rewards come from 11036, the stage itself drops nothing) ----------------
    public bool Reserved(uint type, long stage) => Open(107).Any(a => TrialsOf(a).Any(t => t.Stage == stage));

    public async Task<(List<GameResponse>, IReadOnlyList<(long, long)>)> SettleAsync(ModeBattle b, CancellationToken ct)
    {
        if (!b.Win) return ([], []);
        var s = await LoadAsync(b.Uid, ct);
        foreach (var a in Open(107))
            foreach (var t in TrialsOf(a).Where(t => t.Stage == b.StageId))
            {
                if (!s.Trials.TryGetValue(a, out var m)) s.Trials[a] = m = [];
                if (m.GetValueOrDefault(t.Id) == 0) m[t.Id] = 1;
                await SaveAsync(b.Uid, s, ct);
                return ([Push(b.Srv, 11035, new() { ["activity_id"] = a, ["activity_info"] = TrialsOf(a).Select(x => (object?)new Dictionary<string, object?>
                    { ["id"] = x.Id, ["challenge_state"] = m.GetValueOrDefault(x.Id) }).ToArray() })], []);
            }
        return ([], []);
    }

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var s = await LoadAsync(uid, ct);
        switch (cmd)
        {
            case 81002:
            {
                // PlayerData.AddPlayerStoryTrigger: StoryTriggerCfg[trigger_id].trigger_group collects the trigger.
                var id = HandlerHelpers.ReadUInt(r.Body, "trigger_id");
                if (Cfg.GetProperty("story_triggers").TryGetProperty(id.ToString(), out var g))
                {
                    var group = g.GetUInt32();
                    if (!s.StoryTriggers.TryGetValue(group, out var list)) s.StoryTriggers[group] = list = [];
                    if (!list.Contains(id)) list.Add(id);
                    await SaveAsync(uid, s, ct);
                }
                return [];
            }
            case 68156:
                s.SkinPop.Remove(HandlerHelpers.ReadUInt(r.Body, "skin_id"));
                await SaveAsync(uid, s, ct);
                return [HandlerHelpers.Reply(Codec, r, 68157, new Dictionary<string, object?> { ["result"] = 0u })];
            case 11036:
            {
                var activity = HandlerHelpers.ReadUInt(r.Body, "activity_id");
                var id = HandlerHelpers.ReadUInt(r.Body, "id");
                var trial = Open(107).Contains(activity) ? TrialsOf(activity).FirstOrDefault(t => t.Id == id) : default;
                if (trial.Id == 0 || s.Trials.GetValueOrDefault(activity)?.GetValueOrDefault(id) != 1)
                    return [HandlerHelpers.Reply(Codec, r, 11037, new Dictionary<string, object?> { ["result"] = 1u })];
                s.Trials[activity][id] = 2;
                var granted = await Accounts.GrantItemsAsync(uid, Pairs(trial.Reward), ct);
                await SaveAsync(uid, s, ct);
                var frames = await SyncAsync(r, granted.Select(x => x.Item1), ct);
                frames.Add(HandlerHelpers.Reply(Codec, r, 11037, new Dictionary<string, object?>
                {
                    ["result"] = 0u, ["item_list"] = granted.Select(x => (object?)new Dictionary<string, object?> { ["item_id"] = (uint)x.Item1, ["item_num"] = (uint)x.Item2 }).ToArray()
                }));
                return frames;
            }
            case 64078:
            {
                var aid = HandlerHelpers.ReadUInt(r.Body, "activity_id");
                var taskIds = HandlerHelpers.ReadIdList(r.Body, "task_id_list").Select(x => (uint)x).Distinct().ToList();
                var cultCfg = config.Get("cultivate_hero_cfg.json");
                if (cultCfg is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(aid.ToString(), out var act) || taskIds.Count == 0)
                    return [HandlerHelpers.Reply(Codec, r, 64079, new Dictionary<string, object?> { ["result"] = 2u, ["reward_list"] = Array.Empty<object>() })];
                if (!s.Cultivate.TryGetValue(aid, out var rec)) s.Cultivate[aid] = rec = new CultivateRec();
                var tasks = act.GetProperty("tasks");
                var rewards = new List<(long, long)>();
                foreach (var tid in taskIds)
                {
                    if (rec.ClaimedTasks.Contains(tid) || !tasks.TryGetProperty(tid.ToString(), out var tcfg)) continue;
                    rec.ClaimedTasks.Add(tid);
                    rewards.AddRange(Pairs(tcfg.GetProperty("reward")));
                }
                if (rewards.Count == 0)
                    return [HandlerHelpers.Reply(Codec, r, 64079, new Dictionary<string, object?> { ["result"] = 2u, ["reward_list"] = Array.Empty<object>() })];
                var granted = await Accounts.GrantItemsAsync(uid, rewards, ct);
                await SaveAsync(uid, s, ct);
                var frames = await SyncAsync(r, granted.Select(x => x.Item1), ct);
                frames.Add(HandlerHelpers.Reply(Codec, r, 64079, new Dictionary<string, object?>
                {
                    ["result"] = 0u, ["reward_list"] = HandlerHelpers.ToItemNetRecList(granted)
                }));
                return frames;
            }
            case 64084:
            {
                var aid = HandlerHelpers.ReadUInt(r.Body, "activity_id");
                var cultCfg = config.Get("cultivate_hero_cfg.json");
                if (cultCfg is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(aid.ToString(), out var act))
                    return [HandlerHelpers.Reply(Codec, r, 64085, new Dictionary<string, object?> { ["result"] = 2u, ["reward_list"] = Array.Empty<object>() })];
                if (!s.Cultivate.TryGetValue(aid, out var rec)) s.Cultivate[aid] = rec = new CultivateRec();
                var day = GameClock.Now.AddHours(-5).ToString("yyyy-MM-dd");
                if (rec.DailyDay == day)
                    return [HandlerHelpers.Reply(Codec, r, 64085, new Dictionary<string, object?> { ["result"] = 2u, ["reward_list"] = Array.Empty<object>() })];
                rec.DailyDay = day;
                var granted = await Accounts.GrantItemsAsync(uid, Pairs(act.GetProperty("daily_reward")), ct);
                await SaveAsync(uid, s, ct);
                var frames = await SyncAsync(r, granted.Select(x => x.Item1), ct);
                frames.Add(HandlerHelpers.Reply(Codec, r, 64085, new Dictionary<string, object?>
                {
                    ["result"] = 0u, ["reward_list"] = HandlerHelpers.ToItemNetRecList(granted)
                }));
                return frames;
            }
            case 76152:
            {
                var aid = HandlerHelpers.ReadUInt(r.Body, "activity_id");
                var up = HandlerHelpers.ReadUInt(r.Body, "up_select");
                if (!s.Scrolls.TryGetValue(aid, out var sc)) s.Scrolls[aid] = sc = new PtScroll();
                sc.Up = up;
                await SaveAsync(uid, s, ct);
                return [HandlerHelpers.ResultOnly(Codec, r, 76153, 0u)];
            }
            case 76154:
            {
                var aid = HandlerHelpers.ReadUInt(r.Body, "activity_id");
                var times = HandlerHelpers.ReadUInt(r.Body, "times");
                var ptCfg = config.Get("pt_scroll_cfg.json");
                if (ptCfg is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(aid.ToString(), out var act))
                    return [HandlerHelpers.Reply(Codec, r, 76155, new Dictionary<string, object?> { ["result"] = 2u, ["reward_list"] = Array.Empty<object>() })];
                if (!s.Scrolls.TryGetValue(aid, out var sc)) s.Scrolls[aid] = sc = new PtScroll();
                var available = sc.Times / 2u;
                var rewards = new List<(long, long)>();
                if (times > 0 && times <= available)
                {
                    foreach (var st in act.GetProperty("stages").EnumerateObject())
                    {
                        if (st.Value.GetProperty("mode").GetInt32() == 2)
                        {
                            var cost = st.Value.GetProperty("cost");
                            rewards.Add((cost[0].GetInt64(), cost[1].GetInt64() * times));
                            break;
                        }
                    }
                    sc.Times -= times * 2u;
                }
                var granted = rewards.Count > 0 ? await Accounts.GrantItemsAsync(uid, rewards, ct) : [];
                await SaveAsync(uid, s, ct);
                var frames = await SyncAsync(r, granted.Select(x => x.Item1), ct);
                frames.Add(HandlerHelpers.Reply(Codec, r, 76155, new Dictionary<string, object?>
                {
                    ["result"] = 0u, ["reward_list"] = HandlerHelpers.ToItemNetRecList(granted)
                }));
                return frames;
            }
        }
        return [];
    }
}
