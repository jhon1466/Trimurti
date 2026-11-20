using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 誓约 (oath / wedding), port of Alpha-Persei oath_service.py on the client tables WeddingCfg, WeddingAssignmentCfg and
/// WeddingRightCfg (→ Data/Configs/oath_cfg.json). 14514 oath (WeddingCfg conditions: ring owned, hero owned, archive
/// affection 5 / trust level 5; consumes the ring) or photo; 14512 submit stage tasks → oath level 2 / 3 (+ WeddingRightCfg
/// rewards); 14510 oath stories (plot_condition = oath level); 14516 nickname (30-day cooldown).
/// Task progress: 110 proficiency, 2022 own dorm room, 401 stage clear and 800 heart story read are evaluated from state;
/// 410001 furniture gift and 410003 battle wins are pushed by the dorm / battle settlement (<see cref="AdvanceAsync"/>).
/// Frames 14501 (oaths) and 14503 (tasks).
/// </summary>
public sealed class OathSystem(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    private JsonElement Cfg => config.Get("oath_cfg.json") ?? throw new ModeError("Missing oath_cfg.json");
    private static void Require(bool ok, string m) { if (!ok) throw new ModeError(m); }
    private static List<long> L(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList() : [];

    public sealed class Oath { public int Level { get; set; } public string Nick { get; set; } = ""; public string Picture { get; set; } = ""; public long Time { get; set; } public List<long> Plots { get; set; } = []; public List<long> Renames { get; set; } = []; }
    public sealed class Task1 { public long Progress { get; set; } public bool Done { get; set; } }
    public sealed class State
    {
        public Dictionary<long, Oath> Oaths { get; set; } = [];
        public Dictionary<long, Task1> Tasks { get; set; } = [];
        public List<long> Rights { get; set; } = [];
        /// <summary>Lobby assistant (poster girl) terms: closed seconds per hero, plus the current one.</summary>
        public Dictionary<long, long> AssistSeconds { get; set; } = [];
        public long AssistHero { get; set; }
        public long AssistSince { get; set; }
    }

    /// <summary>Records a lobby assistant change (32016 / 32132), for the 410002 "assistant days" tasks.</summary>
    public static async Task RecordAssistantAsync(GameAccountRepository accounts, long uid, long hero, CancellationToken ct)
    {
        if (hero <= 0) return;
        var s = await accounts.GetJsonAsync<State>(uid, "oath_v1", ct) ?? new State();
        if (s.AssistHero == hero) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (s.AssistHero != 0 && s.AssistSince > 0) s.AssistSeconds[s.AssistHero] = s.AssistSeconds.GetValueOrDefault(s.AssistHero) + now - s.AssistSince;
        s.AssistHero = hero;
        s.AssistSince = now;
        await accounts.SetJsonAsync(uid, "oath_v1", s, ct);
    }

    private static long AssistDays(State s, long hero) =>
        (s.AssistSeconds.GetValueOrDefault(hero) + (s.AssistHero == hero && s.AssistSince > 0 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - s.AssistSince : 0)) / 86400;

    private Task<State?> LoadRaw(long uid, CancellationToken ct) => accounts.GetJsonAsync<State>(uid, "oath_v1", ct);
    private Task SaveAsync(long uid, State s, CancellationToken ct) => accounts.SetJsonAsync(uid, "oath_v1", s, ct);

    private IEnumerable<(long Id, JsonElement Cfg)> TasksOf(long hero, int level) =>
        Cfg.GetProperty("tasks").EnumerateObject().Where(p => p.Value.GetProperty("hero_id").GetInt64() == hero && p.Value.GetProperty("wedding_level").GetInt32() == level)
            .Select(p => (long.Parse(p.Name), p.Value));

    /// <summary>Evaluates the state-based task conditions of every active oath.</summary>
    private async Task EvaluateAsync(long uid, State s, CancellationToken ct)
    {
        foreach (var (hero, o) in s.Oaths.Where(x => x.Value.Level is 1 or 2))
            foreach (var (id, t) in TasksOf(hero, o.Level))
            {
                var rec = s.Tasks.TryGetValue(id, out var r) ? r : s.Tasks[id] = new Task1();
                var need = t.GetProperty("need").GetInt64();
                if (rec.Done || rec.Progress >= need) continue;
                var p = L(t.GetProperty("additional_parameter"));
                var ok = t.GetProperty("condition").GetInt32() switch
                {
                    110 => (await accounts.GetHeroRowAsync(uid, hero, ct))?.ClearTimes >= (p.Count > 1 ? p[1] : 30),
                    2022 => (await accounts.GetJsonAsync<BackHome.State>(uid, "backhome_v1", ct)) is { } home && home.Dorms.Any(d => d.Key >= 6 && d.Value.Archives.Contains(Archive(hero))),
                    401 => p.Count > 0 && (await accounts.GetLobbySnapshotAsync(uid, ct)).Chapters.Any(c => c.Id == p[0] && c.ClearTimes > 0),
                    800 => p.Count > 0 && (await accounts.GetArchiveAsync(uid, Archive(hero), ct)).Videos.Contains((int)p[0]),
                    410002 => false,
                    410003 => false,
                    _ => false
                };
                if (ok) rec.Progress = need;
                // Counted tasks (client: assistant term days, hero proficiency = clear times).
                var counted = t.GetProperty("condition").GetInt32() switch
                {
                    410002 => AssistDays(s, hero),
                    410003 => (await accounts.GetHeroRowAsync(uid, hero, ct))?.ClearTimes ?? 0,
                    _ => -1L
                };
                if (counted >= 0) rec.Progress = Math.Min(need, Math.Max(rec.Progress, counted));
            }
    }

    private long Archive(long hero) => config.Get("trust_cfg.json") is { } t && t.GetProperty("hero_to_archive").TryGetProperty(hero.ToString(), out var a) ? a.GetInt64() : hero;

    /// <summary>Event progress (410001 furniture gift, 410003 battle win…) for heroes with an active oath.</summary>
    public async Task<GameResponse?> AdvanceAsync(long uid, ushort srv, long hero, int condition, long delta, CancellationToken ct)
    {
        var s = await LoadRaw(uid, ct);
        if (s is null || !s.Oaths.TryGetValue(hero, out var o) || o.Level is not (1 or 2)) return null;
        var changed = false;
        foreach (var (id, t) in TasksOf(hero, o.Level).Where(x => x.Cfg.GetProperty("condition").GetInt32() == condition))
        {
            var rec = s.Tasks.TryGetValue(id, out var r) ? r : s.Tasks[id] = new Task1();
            var need = t.GetProperty("need").GetInt64();
            if (rec.Done || rec.Progress >= need) continue;
            rec.Progress = Math.Min(need, rec.Progress + delta);
            changed = true;
        }
        if (!changed) return null;
        await SaveAsync(uid, s, ct);
        return new GameResponse(14503, 0, srv, TasksFrame(s));
    }

    private byte[] OathsFrame(State s) => codec.Encode("sc_14501", new Dictionary<string, object?>
    {
        ["hero_oath"] = s.Oaths.OrderBy(x => x.Key).Select(x => (object)new Dictionary<string, object?>
        {
            ["hero_id"] = (uint)x.Key, ["oath"] = x.Value.Level > 0, ["oath_level"] = (uint)x.Value.Level, ["nick"] = x.Value.Nick, ["picture_link"] = x.Value.Picture,
            ["oath_plot"] = new Dictionary<string, object?> { ["hero_id"] = (uint)x.Key, ["text_list"] = x.Value.Plots.Select(p => (uint)p).ToArray() },
            ["time_list"] = x.Value.Renames.Select(t => (uint)t).ToArray(), ["oath_time"] = (uint)x.Value.Time
        }).ToArray()
    });

    private byte[] TasksFrame(State s) => codec.Encode("sc_14503", new Dictionary<string, object?>
    {
        ["assignment_list"] = s.Tasks.OrderBy(t => t.Key).Select(t => (object)new Dictionary<string, object?>
        { ["id"] = (uint)t.Key, ["progress"] = (uint)t.Value.Progress, ["complete_flag"] = t.Value.Done ? 1u : 0u, ["expired_timestamp"] = 0u }).ToArray(),
        ["send_type"] = 0u
    });

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(long uid, CancellationToken ct)
    {
        var s = await LoadRaw(uid, ct) ?? new State();
        await EvaluateAsync(uid, s, ct);
        if (s.Oaths.Count > 0) await SaveAsync(uid, s, ct);
        return [(14501, OathsFrame(s)), (14503, TasksFrame(s))];
    }

    private async Task<bool> ConditionAsync(long uid, long id, State s, CancellationToken ct)
    {
        if (!Cfg.GetProperty("conditions").TryGetProperty(id.ToString(), out var c)) return false;
        var p = L(c.GetProperty("params"));
        return c.GetProperty("type").GetInt32() switch
        {
            330001 => await accounts.GetItemCountAsync(uid, p[0], ct) >= p[1],
            330003 => await accounts.GetHeroRowAsync(uid, p[0], ct) is not null,
            330002 => s.Oaths.TryGetValue(p[0], out var o) && o.Level >= p[1],
            1027 => (await accounts.GetArchiveAsync(uid, Archive(p[0]), ct)).Exp >= 1000,
            1028 => (await accounts.GetTrustAsync(uid, p[0], ct))?.Level >= p[1],
            _ => false
        };
    }

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var b = r.Body;
        var srv = (ushort)(r.ServerIndex + 1);
        var reply = new Dictionary<string, object?> { ["result"] = 0u };
        var s = await LoadRaw(uid, ct) ?? new State();
        var costs = new List<(long, long)>();
        var grants = new List<(long, long)>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            switch (cmd)
            {
                case 14514:
                    {
                        var hero = (long)HandlerHelpers.ReadUInt(b, "hero_id");
                        Require(Cfg.GetProperty("heroes").TryGetProperty(hero.ToString(), out var hc), "Hero has no oath");
                        if (b.ContainsKey("picture_url") && HandlerHelpers.ReadString(b, "picture_url") is { Length: > 0 } url)
                        {
                            Require(s.Oaths.TryGetValue(hero, out var o) && o.Level > 0, "No oath yet");
                            o!.Picture = url;
                            reply["time"] = (uint)now;
                            break;
                        }
                        if (s.Oaths.TryGetValue(hero, out var done) && done.Level > 0) { reply["time"] = (uint)done.Time; break; }
                        foreach (var c in L(hc.GetProperty("conditions"))) Require(await ConditionAsync(uid, c, s, ct), "Oath condition not met");
                        costs.Add((hc.GetProperty("ring").GetInt64(), 1));
                        s.Oaths[hero] = new Oath { Level = 1, Time = now };
                        reply["time"] = (uint)now;
                        break;
                    }
                case 14512:
                    {
                        await EvaluateAsync(uid, s, ct);
                        var ids = HandlerHelpers.ReadIdList(b, "id");
                        Require(ids.Count > 0, "No tasks");
                        var heroes = new HashSet<long>();
                        foreach (var id in ids)
                        {
                            Require(Cfg.GetProperty("tasks").TryGetProperty(id.ToString(), out var t), "Unknown task");
                            var hero = t.GetProperty("hero_id").GetInt64();
                            Require(s.Oaths.TryGetValue(hero, out var o) && o.Level == t.GetProperty("wedding_level").GetInt32(), "Task not active");
                            var rec = s.Tasks.GetValueOrDefault(id);
                            Require(rec is not null && (rec.Done || rec.Progress >= t.GetProperty("need").GetInt64()), "Task not complete");
                            rec!.Done = true;
                            heroes.Add(hero);
                        }
                        foreach (var hero in heroes)
                        {
                            var o = s.Oaths[hero];
                            if (o.Level is 1 or 2 && TasksOf(hero, o.Level).All(t => s.Tasks.GetValueOrDefault(t.Id)?.Done == true)) o.Level++;
                            foreach (var p in Cfg.GetProperty("rights").EnumerateObject().Where(p => p.Value.GetProperty("hero_id").GetInt64() == hero && p.Value.GetProperty("level").GetInt32() <= o.Level))
                            {
                                var rid = long.Parse(p.Name);
                                if (s.Rights.Contains(rid)) continue;
                                s.Rights.Add(rid);
                                if (p.Value.GetProperty("reward").ValueKind == JsonValueKind.Array)
                                    grants.AddRange(p.Value.GetProperty("reward").EnumerateArray().Select(x => (x[0].GetInt64(), x[1].GetInt64())));
                            }
                        }
                        await EvaluateAsync(uid, s, ct);
                        reply["reward_list"] = HandlerHelpers.ToItemNetRecList(grants);
                        break;
                    }
                case 14510:
                    {
                        var plot = b.TryGetValue("oath_plot", out var v) ? v as IReadOnlyDictionary<string, object?> : null;
                        Require(plot is not null, "No plot");
                        var hero = (long)HandlerHelpers.ReadUInt(plot!, "hero_id");
                        Require(Cfg.GetProperty("heroes").TryGetProperty(hero.ToString(), out var hc) && s.Oaths.TryGetValue(hero, out var o) && o.Level > 0, "No oath");
                        var plots = L(hc.GetProperty("plots"));
                        var conds = L(hc.GetProperty("plot_conditions"));
                        foreach (var t in HandlerHelpers.ReadIdList(plot!, "text_list"))
                        {
                            var i = plots.IndexOf(t);
                            Require(i >= 0 && (i >= conds.Count || await ConditionAsync(uid, conds[i], s, ct)), "Story locked");
                            if (!s.Oaths[hero].Plots.Contains(t)) s.Oaths[hero].Plots.Add(t);
                        }
                        break;
                    }
                case 14516:
                    {
                        var hero = (long)HandlerHelpers.ReadUInt(b, "hero_id");
                        var nick = HandlerHelpers.ReadString(b, "nick");
                        if (!s.Oaths.TryGetValue(hero, out var o) || o.Level == 0) { reply["result"] = 1u; break; }
                        if (nick == o.Nick) { reply["result"] = 2u; break; }
                        if (o.Renames.Count > 0 && now < o.Renames[^1] + 30 * 86400) { reply["result"] = 3u; break; }
                        Require(nick.Length <= 16, "Nick too long");
                        o.Nick = nick;
                        o.Renames.Add(now);
                        break;
                    }
            }
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)]; }

        if ((costs.Count > 0 || grants.Count > 0) && !await accounts.ConsumeAndGrantAsync(uid, costs, grants, ct))
            return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)];
        if (cmd == 14514) await EvaluateAsync(uid, s, ct);
        await SaveAsync(uid, s, ct);
        var frames = await InventorySync.FramesAsync(accounts, codec, r, costs.Concat(grants).Select(x => x.Item1), ct);
        frames.Add(new GameResponse(14501, 0, srv, OathsFrame(s)));
        if (cmd is 14512 or 14514) frames.Add(new GameResponse(14503, 0, srv, TasksFrame(s)));
        frames.Add(HandlerHelpers.Reply(codec, r, cmd + 1, reply));
        return frames;
    }

    public static readonly uint[] Commands = [14510, 14512, 14514, 14516];
}
