using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// Resident minigames (port of Alpha-Persei minigame_point_service, resident_music_service, vehicle_ball_service and
/// kadas_race_service): activity point reward tiers (60054/60105/60107), the music concert (61047/61048/61050),
/// 极限机装 vehicle ball (89701–89708) and 卡达斯 tank race (84331–84334). Each mode's task conditions
/// (50104, 422001–422004, 423004/423006) are projected into the task system.
/// </summary>
public sealed class MiniGames(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec, TaskSystem tasks)
{
    private JsonElement C(string f) => config.Get(f) ?? throw new ModeError($"Missing {f}");
    private static void Require(bool ok, string m) { if (!ok) throw new ModeError(m); }
    private static int I(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    private static object[] Items(IEnumerable<(long, long)> x) => HandlerHelpers.ToItemNetRecList(x);

    public sealed class State
    {
        public Dictionary<long, long> ActivityScores { get; set; } = [];
        public List<long> PointRewards { get; set; } = [];
        public Dictionary<long, long[]> Music { get; set; } = [];       // id → [score, sign, rewarded]
        public List<long> VehicleStages { get; set; } = [];
        public long Vehicle { get; set; } = 50111;
        public Dictionary<long, int> VehicleBuffs { get; set; } = [];
        public long VehicleKills { get; set; }
        public long VehicleMerges { get; set; }
        public List<long> KadasMissions { get; set; } = [];
        public long KadasTank { get; set; }
        public Dictionary<long, List<long>> KadasBuilds { get; set; } = [];
        public long KadasRound { get; set; }
        public long KadasMaxScore { get; set; } = -1;
        public long KadasPoints { get; set; }
        public Dictionary<long, long> KadasKills { get; set; } = [];
    }

    private async Task<State> LoadAsync(long uid, CancellationToken ct) => await accounts.GetJsonAsync<State>(uid, "minigames_v1", ct) ?? new State();
    private Task SaveAsync(long uid, State s, CancellationToken ct) => accounts.SetJsonAsync(uid, "minigames_v1", s, ct);

    /// <summary>Mode-owned activity scores (e.g. osiris study count) feeding the activity point tiers.</summary>
    public async Task SetActivityScoreAsync(long uid, long activity, long score, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        s.ActivityScores[activity] = score;
        await SaveAsync(uid, s, ct);
    }

    // ---------------- payloads ----------------

    private static Dictionary<string, object?> MusicPayload(State s, JsonElement catalog) => new()
    {
        ["challenge_info"] = catalog.EnumerateObject().Select(p => long.Parse(p.Name)).OrderBy(x => x).Select(id =>
        {
            var r = s.Music.GetValueOrDefault(id, [0, 0, 0]);
            return (object)new Dictionary<string, object?> { ["id"] = (uint)id, ["score"] = (uint)r[0], ["sign"] = (uint)r[1] };
        }).ToArray()
    };

    private static Dictionary<string, object?> VehiclePayload(State s) => new()
    {
        ["stages"] = s.VehicleStages.OrderBy(x => x).Select(x => (uint)x).ToArray(), ["using_vehicle"] = (uint)s.Vehicle,
        ["unlock_buffs"] = s.VehicleBuffs.OrderBy(x => x.Key).Select(b => (object)new Dictionary<string, object?> { ["key"] = (uint)b.Key, ["value"] = (uint)b.Value }).ToArray()
    };

    private (HashSet<long> Bodies, HashSet<long> Options) KadasUnlocked(State s)
    {
        var cfg = C("kadas_contract.json");
        var bodies = cfg.GetProperty("bodies").EnumerateObject().Where(p => I(p.Value, "is_unlocked") != 1).Select(p => long.Parse(p.Name)).ToHashSet();
        var options = cfg.GetProperty("options").EnumerateObject().Where(p => I(p.Value, "is_unlocked") != 1).Select(p => long.Parse(p.Name)).ToHashSet();
        foreach (var m in s.KadasMissions)
            foreach (var rw in cfg.GetProperty("stages").GetProperty(m.ToString()).GetProperty("rewards").EnumerateArray())
                (rw[0].GetInt32() == 3 ? bodies : options).Add(rw[1].GetInt64());
        return (bodies, options);
    }

    private Dictionary<string, object?> KadasPayload(State s)
    {
        var cfg = C("kadas_contract.json");
        var (bodies, _) = KadasUnlocked(s);
        var defBody = cfg.GetProperty("bodies").EnumerateObject().Where(p => I(p.Value, "is_unlocked") != 1).OrderBy(p => I(p.Value, "sort")).Select(p => long.Parse(p.Name)).First();
        var tank = bodies.Contains(s.KadasTank) ? s.KadasTank : defBody;
        if (!s.KadasBuilds.ContainsKey(tank))
            s.KadasBuilds[tank] = cfg.GetProperty("options").EnumerateObject().Where(p => I(p.Value, "is_unlocked") != 1).Select(p => long.Parse(p.Name)).ToList();
        s.KadasTank = tank;
        return new()
        {
            ["activity_id"] = cfg.GetProperty("activity_id").GetUInt32(), ["tank_id"] = (uint)tank, ["mission_list"] = s.KadasMissions.OrderBy(x => x).Select(x => (uint)x).ToArray(),
            ["tank_info"] = s.KadasBuilds.Where(b => bodies.Contains(b.Key)).OrderBy(b => b.Key).Select(b => (object)new Dictionary<string, object?> { ["tank_id"] = (uint)b.Key, ["weapon_list"] = b.Value.Select(x => (uint)x).ToArray() }).ToArray(),
            ["challenge_stage_info"] = new Dictionary<string, object?> { ["round"] = (uint)s.KadasRound, ["max_score"] = (int)s.KadasMaxScore }
        };
    }

    private Dictionary<string, object?> ScorePayload(State s) => new()
    {
        ["score_info_list"] = s.ActivityScores.Select(x => (object)new Dictionary<string, object?> { ["key"] = (uint)x.Key, ["score"] = (uint)x.Value }).ToArray()
    };

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        var frames = new List<(uint, byte[])>
        {
            (61047, codec.Encode("sc_61047", MusicPayload(s, C("activity_music_catalog.json")))),
            (89701, codec.Encode("sc_89701", VehiclePayload(s))),
            (84331, codec.Encode("sc_84331", KadasPayload(s))),
            (60105, codec.Encode("sc_60105", ScorePayload(s))),
            (60107, codec.Encode("sc_60107", new Dictionary<string, object?> { ["reward_info_list"] = s.PointRewards.Select(x => (uint)x).ToArray() })),
            // ActivityPointAction 85101: the same claimed point-reward ledger (ActivityPointData.InitData).
            (85101, codec.Encode("sc_85101", new Dictionary<string, object?> { ["reward_list"] = s.PointRewards.Select(x => (uint)x).ToArray() }))
        };
        await SaveAsync(uid, s, ct);
        return frames;
    }

    // ---------------- task projection ----------------

    private async Task<GameResponse?> ProjectAsync(long uid, ushort srv, State s, CancellationToken ct)
    {
        var music = C("activity_music_catalog.json");
        var vehicle = C("vehicle_ball_contract.json");
        var doneMusicActivities = s.Music.Where(m => m.Value[1] is >= 1 and <= 3 && music.TryGetProperty(m.Key.ToString(), out _))
            .Select(m => (long)I(music.GetProperty(m.Key.ToString()), "activity_id")).Distinct().ToList();
        return await tasks.EventAsync(uid, srv, ts =>
        {
            var c = new List<object>();
            foreach (var a in doneMusicActivities) c.AddRange(tasks.Trigger(ts, 50104, 1, a, set: true));
            foreach (var st in s.VehicleStages) c.AddRange(tasks.Trigger(ts, 422001, 1, st, set: true));
            c.AddRange(tasks.Trigger(ts, 422002, s.VehicleBuffs.Keys.Count(b => vehicle.GetProperty("buffs").GetProperty(b.ToString()).GetProperty("reward").GetArrayLength() > 0), set: true));
            c.AddRange(tasks.Trigger(ts, 422003, s.VehicleMerges, set: true));
            c.AddRange(tasks.Trigger(ts, 422004, s.VehicleKills, set: true));
            c.AddRange(tasks.Trigger(ts, 423006, s.KadasPoints, set: true));
            foreach (var (w, k) in s.KadasKills) c.AddRange(tasks.Trigger(ts, 423004, k, w, set: true));
            return c;
        }, ct);
    }

    // ---------------- operations ----------------

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var srv = (ushort)(r.ServerIndex + 1);
        var s = await LoadAsync(uid, ct);
        var frames = new List<GameResponse>();
        IReadOnlyList<(long, long)> granted = [];
        var reply = new Dictionary<string, object?> { ["result"] = 0u };
        try
        {
            switch (cmd)
            {
                case 60054:
                case 85102:
                    {
                        var ids = HandlerHelpers.ReadIdList(r.Body, cmd == 60054 ? "point_reward_id_list" : "reward_id_list");
                        Require(ids.Count > 0 && ids.Distinct().Count() == ids.Count && !ids.Any(s.PointRewards.Contains), "Invalid or claimed point reward");
                        var cat = C("activity_point_reward_cfg.json");
                        var items = new List<(long, long)>();
                        foreach (var id in ids)
                        {
                            Require(cat.TryGetProperty(id.ToString(), out var row), "Unknown point reward");
                            var item = (long)I(row, "need_item_id");
                            var have = item != 0 ? await accounts.GetItemCountAsync(uid, item, ct) : s.ActivityScores.GetValueOrDefault(I(row, "activity_id"));
                            Require(have >= I(row, "need"), "Activity points not reached");
                            items.AddRange(row.GetProperty("reward").EnumerateArray().Select(x => (x[0].GetInt64(), x[1].GetInt64())));
                        }
                        s.PointRewards.AddRange(ids);
                        granted = await accounts.GrantItemsAsync(uid, items.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))), ct);
                        frames.Add(new GameResponse(60107, 0, srv, codec.Encode("sc_60107", new Dictionary<string, object?> { ["reward_info_list"] = ids.Select(x => (uint)x).ToArray() })));
                        if (cmd == 85102)
                        {
                            frames.Add(new GameResponse(85101, 0, srv, codec.Encode("sc_85101", new Dictionary<string, object?> { ["reward_list"] = s.PointRewards.Select(x => (uint)x).ToArray() })));
                            reply["item_list"] = granted.Select(i => (object)new Dictionary<string, object?> { ["item_id"] = (uint)i.Item1, ["item_num"] = (uint)i.Item2 }).ToArray();
                        }
                        else reply["reward_list"] = Items(granted);
                        break;
                    }
                case 61048:
                    {
                        var music = C("activity_music_catalog.json");
                        var id = (long)HandlerHelpers.ReadUInt(r.Body, "id");
                        var score = (long)HandlerHelpers.ReadUInt(r.Body, "score");
                        var sign = (long)HandlerHelpers.ReadUInt(r.Body, "state");
                        Require(music.TryGetProperty(id.ToString(), out _) && sign is >= 1 and <= 3, "Invalid music result");
                        var old = s.Music.GetValueOrDefault(id, [0, 0, 0]);
                        if (score >= old[0]) s.Music[id] = [score, sign, old[2]];
                        break;
                    }
                case 61050:
                    {
                        var music = C("activity_music_catalog.json");
                        var contract = C("resident_music_contract.json");
                        var ids = HandlerHelpers.ReadIdList(r.Body, "id_list");
                        Require(ids.Count > 0 && ids.Distinct().Count() == ids.Count, "Invalid music list");
                        var items = new List<(long, long)>();
                        foreach (var id in ids)
                        {
                            Require(music.TryGetProperty(id.ToString(), out var song) && song.GetProperty("rewards").GetArrayLength() > 0, "Song has no direct reward");
                            var rec = s.Music.GetValueOrDefault(id);
                            Require(rec is { } && rec[1] is >= 1 and <= 3 && rec[2] == 0, "Song not completed or already rewarded");
                            var target = contract.GetProperty("targets").GetProperty(id.ToString()).GetInt32();
                            Require(rec![0] * 100 >= I(song, "total_score") * contract.GetProperty("grades")[target - 1].GetInt64(), "Score below the reward grade");
                            items.AddRange(song.GetProperty("rewards").EnumerateArray().Select(x => (x[0].GetInt64(), x[1].GetInt64())));
                        }
                        foreach (var id in ids) s.Music[id][2] = 1;
                        granted = await accounts.GrantItemsAsync(uid, items.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))), ct);
                        reply["reward_list"] = Items(granted);
                        break;
                    }
                case 89702 or 89704 or 89706 or 89708:
                    {
                        var vb = C("vehicle_ball_contract.json");
                        Require(HandlerHelpers.ReadUInt(r.Body, "activity_id") == vb.GetProperty("activity_id").GetUInt32(), "Unknown vehicle activity");
                        var buffs = vb.GetProperty("buffs");
                        switch (cmd)
                        {
                            case 89702:
                                {
                                    var stage = (long)HandlerHelpers.ReadUInt(r.Body, "stage_id");
                                    Require(vb.GetProperty("stages").TryGetProperty(stage.ToString(), out _), "Unknown vehicle stage");
                                    Require(vb.GetProperty("stages").EnumerateObject().Select(p => long.Parse(p.Name)).Where(i => i < stage).All(s.VehicleStages.Contains), "Previous vehicle stage missing");
                                    foreach (var b in HandlerHelpers.ReadIdList(r.Body, "buffs")) { Require(buffs.TryGetProperty(b.ToString(), out _), "Unknown armament"); s.VehicleBuffs.TryAdd(b, 0); }
                                    if (HandlerHelpers.ReadUInt(r.Body, "is_pass") == 1 && !s.VehicleStages.Contains(stage)) s.VehicleStages.Add(stage);
                                    if (r.Body.GetValueOrDefault("kv_list") is System.Collections.IEnumerable kv)
                                        foreach (var e in kv)
                                            if (e is IReadOnlyDictionary<string, object?> d)
                                            {
                                                var v = HandlerHelpers.ReadUInt(d, "value");
                                                if (HandlerHelpers.ReadUInt(d, "key") == 1) s.VehicleKills += v; else if (HandlerHelpers.ReadUInt(d, "key") == 2) s.VehicleMerges += v;
                                            }
                                    break;
                                }
                            case 89704:
                                {
                                    var v = (long)HandlerHelpers.ReadUInt(r.Body, "vehicle");
                                    Require(vb.GetProperty("vehicles").TryGetProperty(v.ToString(), out var vc) && I(vc, "type") == 1 && (I(vc, "unlock_stage") == 0 || s.VehicleStages.Contains(I(vc, "unlock_stage"))), "Vehicle locked");
                                    s.Vehicle = v;
                                    break;
                                }
                            case 89706:
                                {
                                    var b = (long)HandlerHelpers.ReadUInt(r.Body, "buff_id");
                                    Require(buffs.TryGetProperty(b.ToString(), out var bc) && bc.GetProperty("reward").GetArrayLength() == 2, "No illustration reward");
                                    Require(s.VehicleBuffs.TryGetValue(b, out var st) && st == 0, "Locked or already claimed");
                                    s.VehicleBuffs[b] = 1;
                                    var rw = bc.GetProperty("reward");
                                    granted = await accounts.GrantItemsAsync(uid, [(rw[0].GetInt64(), rw[1].GetInt64())], ct);
                                    reply["reward_list"] = Items(granted);
                                    break;
                                }
                            default:
                                {
                                    var b = (long)HandlerHelpers.ReadUInt(r.Body, "buff_id");
                                    Require(buffs.TryGetProperty(b.ToString(), out _), "Unknown armament");
                                    s.VehicleBuffs.TryAdd(b, 0);
                                    break;
                                }
                        }
                        break;
                    }
                case 84332:
                    {
                        var kc = C("kadas_contract.json");
                        Require(HandlerHelpers.ReadUInt(r.Body, "activity_id") == kc.GetProperty("activity_id").GetUInt32(), "Unknown Kadas activity");
                        var stage = (long)HandlerHelpers.ReadUInt(r.Body, "stage_id");
                        Require(kc.GetProperty("stages").TryGetProperty(stage.ToString(), out var sc), "Unknown Kadas stage");
                        var pre = I(sc, "pre_stage");
                        Require(pre == 0 || s.KadasMissions.Contains(pre), "Kadas prerequisite missing");
                        var mode = I(sc, "mode_type");
                        if (mode == 4) Require(kc.GetProperty("stages").EnumerateObject().Where(p => I(p.Value, "mode_type") == 1).All(p => s.KadasMissions.Contains(long.Parse(p.Name))), "Challenge needs every main race");
                        KadasPayload(s);
                        if (HandlerHelpers.ReadUInt(r.Body, "result") == 0 && !s.KadasMissions.Contains(stage)) s.KadasMissions.Add(stage);
                        if (mode == 4)
                        {
                            s.KadasRound = Math.Max(s.KadasRound, HandlerHelpers.ReadUInt(r.Body, "round"));
                            var score = Math.Min(int.MaxValue, (long)HandlerHelpers.ReadUInt(r.Body, "score"));
                            s.KadasMaxScore = Math.Max(s.KadasMaxScore, score);
                            s.KadasPoints += score;
                        }
                        var kills = (long)HandlerHelpers.ReadUInt(r.Body, "kill_num");
                        if (mode != 5 && kills > 0)
                            foreach (var w in s.KadasBuilds[s.KadasTank].Where(w => I(kc.GetProperty("options").GetProperty(w.ToString()), "item_type") == 1).Distinct())
                                s.KadasKills[w] = s.KadasKills.GetValueOrDefault(w) + kills;
                        frames.Add(HandlerHelpers.Reply(codec, r, 84333, reply));
                        frames.Add(new GameResponse(84331, 0, srv, codec.Encode("sc_84331", KadasPayload(s))));
                        await SaveAsync(uid, s, ct);
                        if (await ProjectAsync(uid, srv, s, ct) is { } d) frames.Add(d);
                        return frames;
                    }
                case 84334:
                    {
                        var kc = C("kadas_contract.json");
                        var body = (long)HandlerHelpers.ReadUInt(r.Body, "tank_id");
                        var parts = HandlerHelpers.ReadIdList(r.Body, "weapon_list");
                        var (bodies, options) = KadasUnlocked(s);
                        Require(bodies.Contains(body) && parts.All(options.Contains), "Tank or part locked");
                        var bc = kc.GetProperty("bodies").GetProperty(body.ToString());
                        var opt = kc.GetProperty("options");
                        Require(parts.Count(p => I(opt.GetProperty(p.ToString()), "item_type") == 1) <= I(bc, "weapon_slots")
                            && parts.Count(p => I(opt.GetProperty(p.ToString()), "item_type") == 2) <= I(bc, "equip_slots")
                            && parts.Sum(p => I(opt.GetProperty(p.ToString()), "cost")) <= I(bc, "cost"), "Too many parts for this tank");
                        s.KadasBuilds[body] = parts.OrderBy(p => I(opt.GetProperty(p.ToString()), "item_type")).ToList();
                        s.KadasTank = body;
                        break;
                    }
            }
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)]; }
        await SaveAsync(uid, s, ct);
        frames.InsertRange(0, await InventorySync.FramesAsync(accounts, codec, r, granted.Select(x => x.Item1), ct));
        if (cmd is 61048 or 89702 or 89706 or 89708 && await ProjectAsync(uid, srv, s, ct) is { } diff) frames.Add(diff);
        frames.Add(HandlerHelpers.Reply(codec, r, cmd + 1, reply));
        return frames;
    }
}

/// <summary>
/// Rhythm game (ActivityRhythmGameAction): 83124 stage stars / read stories at login, 83126 story read (→ 83127).
/// Stage results reach the client as sc_83125; they are kept here when recorded.
/// </summary>
public sealed class RhythmGame(GameAccountRepository accounts, ProtoCodec codec)
{
    public sealed class Stage { public List<uint> Stars { get; set; } = []; public uint Seconds { get; set; } }
    public sealed class State { public Dictionary<uint, Stage> Stages { get; set; } = []; public List<uint> Stories { get; set; } = []; }

    private Task<State?> LoadAsync(long uid, CancellationToken ct) => accounts.GetJsonAsync<State>(uid, "rhythm_v1", ct);

    public async Task<(uint, byte[])> LoginFrameAsync(long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct) ?? new State();
        return (83124, codec.Encode("sc_83124", new Dictionary<string, object?>
        {
            ["rhythm_stage_list"] = s.Stages.OrderBy(x => x.Key).Select(x => (object)new Dictionary<string, object?> { ["id"] = x.Key, ["star_id_list"] = x.Value.Stars.ToArray(), ["use_seconds"] = x.Value.Seconds }).ToArray(),
            ["story_id_list"] = s.Stories.ToArray()
        }));
    }

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var story = HandlerHelpers.ReadUInt(r.Body, "story_id");
        if (story == 0) return [HandlerHelpers.ResultOnly(codec, r, 83127, 2)];
        var s = await LoadAsync(r.AccountId, ct) ?? new State();
        if (!s.Stories.Contains(story)) s.Stories.Add(story);
        await accounts.SetJsonAsync(r.AccountId, "rhythm_v1", s, ct);
        return [HandlerHelpers.ResultOnly(codec, r, 83127)];
    }
}
