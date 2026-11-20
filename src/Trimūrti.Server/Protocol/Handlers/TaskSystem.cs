using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// Task / assignment system (port of Alpha-Persei task_listener, task_cycle_notifications and TaskSubmit / OneKey /
/// activity-point operations) on the client AssignmentCfg (Data/Configs/assignment_cfg.json, extracted with
/// scripts/luadump/luacfg.py) and activity_pt_cfg.json. Progress comes from game events (login, stage clears, fatigue,
/// shop, level…); daily tasks (types 6, 7, 901, 1101, 3001) reset at 05:00 UTC+8 and weekly ones (5, 8, 902) on
/// Monday 05:00. Rewards are the configured ones; items 22 / 23 / 35 feed the daily / newbie / weekly activity points
/// whose boxes are claimed with 28016.
/// </summary>
public sealed class TaskSystem(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    private static readonly int[] DailyTypes = [6, 7, 901, 1101, 3001];
    private static readonly int[] WeeklyTypes = [5, 8, 902];
    private static readonly Dictionary<long, int> PointItems = new() { [22] = 1, [23] = 2, [35] = 3 };

    private JsonElement Catalog => config.Get("assignment_cfg.json") ?? throw new InvalidOperationException("Missing assignment_cfg.json");
    private JsonElement PtCfg => config.Get("activity_pt_cfg.json") ?? default;

    public sealed class Rec { public long P { get; set; } public bool Claimed { get; set; } public long Exp { get; set; } }
    public sealed class Pt { public long Point { get; set; } public List<long> Got { get; set; } = []; }
    public sealed class State
    {
        public Dictionary<long, Rec> Tasks { get; set; } = [];
        public Dictionary<int, Pt> Points { get; set; } = [];
        public long LoginDay { get; set; }
        public long DayStart { get; set; }
        public long WeekStart { get; set; }
    }

    private static long DayStart() => GameClock.TodayStartUnix;
    private static long WeekStart()
    {
        var day = GameClock.Today;
        var monday = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        return new DateTimeOffset(monday.AddHours(5), GameClock.Offset).ToUnixTimeSeconds();
    }

    private JsonElement? Cfg(long id) => Catalog.TryGetProperty(id.ToString(), out var v) ? v : null;
    private static int I(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    private static List<long> Params(JsonElement c) => c.TryGetProperty("param", out var p) && p.ValueKind == JsonValueKind.Array
        ? p.EnumerateArray().SelectMany(x => x.ValueKind == JsonValueKind.Array ? x.EnumerateArray().ToList() : new List<JsonElement> { x }).Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList() : [];

    private long ExpiryFor(int type) => DailyTypes.Contains(type) ? DayStart() + 86400 : WeeklyTypes.Contains(type) ? WeekStart() + 7 * 86400 : 0;

    public async Task<State> LoadAsync(long uid, CancellationToken ct)
    {
        var s = await accounts.GetJsonAsync<State>(uid, "tasks_v1", ct) ?? new State();
        var day = DayStart();
        var week = WeekStart();
        if (s.DayStart != day || s.WeekStart != week)
        {
            foreach (var (id, r) in s.Tasks)
            {
                if (Cfg(id) is not { } c) continue;
                var type = I(c, "type");
                if ((DailyTypes.Contains(type) && s.DayStart != day) || (WeeklyTypes.Contains(type) && s.WeekStart != week))
                { r.P = 0; r.Claimed = false; r.Exp = ExpiryFor(type); }
            }
            if (s.DayStart != day) { s.Points.Remove(1); }
            if (s.WeekStart != week) { s.Points.Remove(3); }
            s.DayStart = day;
            s.WeekStart = week;
        }
        return s;
    }

    public Task SaveAsync(long uid, State s, CancellationToken ct) => accounts.SetJsonAsync(uid, "tasks_v1", s, ct);

    private static Dictionary<string, object?> Info(long id, Rec r) => new()
    {
        ["id"] = (uint)id, ["progress"] = (uint)Math.Max(0, r.P), ["complete_flag"] = r.Claimed ? 1u : 0u, ["expired_timestamp"] = (uint)r.Exp
    };

    /// <summary>Advances every unclaimed task with this condition whose parameters match (task_listener._trigger_task_condition).</summary>
    public List<object> Trigger(State s, int condition, long delta = 1, long? param = null, bool set = false, IReadOnlyCollection<long>? candidates = null)
    {
        var changes = new List<object>();
        foreach (var p in Catalog.EnumerateObject())
        {
            var c = p.Value;
            if (I(c, "condition") != condition) continue;
            var id = long.Parse(p.Name);
            var rec = s.Tasks.GetValueOrDefault(id);
            if (rec?.Claimed == true) continue;
            var ps = Params(c);
            if (condition is 401 or 400 or 800)
            {
                if (param is not { } stage || stage <= 0 || !ps.Contains(stage)) continue;
            }
            else if (ps.Any(x => x != 0))
            {
                var cand = candidates ?? (param is { } pv ? [pv] : []);
                if (!cand.Any(ps.Contains)) continue;
            }
            var need = Math.Max(1, I(c, "need"));
            var old = rec?.P ?? 0;
            var next = Math.Min(need, set ? delta : old + delta);
            if (next <= old) continue;
            if (rec is null) s.Tasks[id] = rec = new Rec { Exp = ExpiryFor(I(c, "type")) };
            rec.P = next;
            changes.Add(Info(id, rec));
        }
        return changes;
    }

    /// <summary>Marks configured tasks as fulfilled (progress = need), for checks evaluated by the caller (e.g. 2012 idol PVE scores).</summary>
    public List<object> Fulfil(State s, IEnumerable<long> ids)
    {
        var changes = new List<object>();
        foreach (var id in ids)
        {
            if (Cfg(id) is not { } c) continue;
            var rec = s.Tasks.GetValueOrDefault(id);
            var need = Math.Max(1, I(c, "need"));
            if (rec?.Claimed == true || (rec?.P ?? 0) >= need) continue;
            if (rec is null) s.Tasks[id] = rec = new Rec { Exp = ExpiryFor(I(c, "type")) };
            rec.P = need;
            changes.Add(Info(id, rec));
        }
        return changes;
    }

    public GameResponse? Diff(ushort srv, List<object> changes) =>
        changes.Count == 0 ? null : new GameResponse(28007, 0, srv, codec.Encode("sc_28007", new Dictionary<string, object?> { ["progress_list"] = changes.ToArray() }));

    /// <summary>Applies one event and returns the 28007 diff push (if any).</summary>
    public async Task<GameResponse?> EventAsync(long uid, ushort srv, Func<State, List<object>> apply, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        var changes = apply(s);
        await SaveAsync(uid, s, ct);
        return Diff(srv, changes);
    }

    /// <summary>Stage win: 401 configured stage cleared, 402 any clear, 405 regular, 453 black zone, 1001 captain, 350 fatigue spent.</summary>
    public Task<GameResponse?> StagePassAsync(long uid, ushort srv, long stageId, int times, IReadOnlyList<long> heroes, int fatigue, CancellationToken ct) =>
        EventAsync(uid, srv, s =>
        {
            var c = Trigger(s, 401, 1, stageId, set: true);
            c.AddRange(Trigger(s, 400, 1, stageId));
            c.AddRange(Trigger(s, 402, times));
            if (stageId is >= 100000 and < 200000) c.AddRange(Trigger(s, 405, times));
            if (stageId is >= 3026000 and < 3028000) c.AddRange(Trigger(s, 453, times));
            if (heroes.Count > 0) c.AddRange(Trigger(s, 1001, times, heroes[0]));
            if (fatigue > 0) c.AddRange(Trigger(s, 350, fatigue, 4));
            return c;
        }, ct);

    /// <summary>Login (once per game day): 1 daily login; 2 player level; 401 back-fill of cleared chapter stages.</summary>
    public async Task<State> OnLoginAsync(long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        if (s.LoginDay != DayStart())
        {
            s.LoginDay = DayStart();
            Trigger(s, 1);
        }
        if (await accounts.GetProfileAsync(uid, ct) is { } profile) Trigger(s, 2, profile.Level, set: true);
        if (await accounts.GetLobbySnapshotAsync(uid, ct) is { } lobby)
            foreach (var ch in lobby.Chapters.Where(c => c.ClearTimes > 0)) Trigger(s, 401, 1, ch.Id, set: true);
        await SaveAsync(uid, s, ct);
        return s;
    }

    public Dictionary<string, object?> ListPayload(State s)
    {
        var list = new List<object>();
        foreach (var p in Catalog.EnumerateObject())
        {
            var id = long.Parse(p.Name);
            var activity = I(p.Value, "activity_id");
            if (activity != 0 && !s.Tasks.ContainsKey(id)) continue;
            var rec = s.Tasks.GetValueOrDefault(id) ?? new Rec { Exp = ExpiryFor(I(p.Value, "type")) };
            list.Add(Info(id, rec));
        }
        return new() { ["assignment_list"] = list.ToArray(), ["send_type"] = 0u };
    }

    public Dictionary<string, object?> PointPayload(State s) => new()
    {
        ["pt_list"] = s.Points.OrderBy(x => x.Key).Select(x => (object)new Dictionary<string, object?>
        {
            ["activity_pt_id"] = (uint)x.Key, ["active_point"] = (uint)x.Value.Point, ["get_id_list"] = x.Value.Got.Select(g => (uint)g).ToArray()
        }).ToArray()
    };

    // ---------------- claims ----------------

    private bool Claimable(State s, long id) =>
        Cfg(id) is { } c && s.Tasks.TryGetValue(id, out var r) && !r.Claimed && r.P >= Math.Max(1, I(c, "need"))
        && (I(c, "pre_id") == 0 || (s.Tasks.TryGetValue(I(c, "pre_id"), out var pre) && pre.Claimed));

    private (List<(long, long)> Items, List<object> Changes) Claim(State s, IEnumerable<long> ids)
    {
        var items = new List<(long, long)>();
        var changes = new List<object>();
        foreach (var id in ids.Distinct())
        {
            if (!Claimable(s, id)) continue;
            var rec = s.Tasks[id];
            rec.Claimed = true;
            changes.Add(Info(id, rec));
            foreach (var rw in Cfg(id)!.Value.GetProperty("reward").EnumerateArray())
            {
                long item = rw[0].GetInt64(), num = rw[1].GetInt64();
                if (PointItems.TryGetValue(item, out var pt))
                {
                    var p = s.Points.TryGetValue(pt, out var v) ? v : s.Points[pt] = new Pt();
                    p.Point += num;
                    changes.AddRange(Trigger(s, 300, num, candidates: [item, pt]));
                }
                else items.Add((item, num));
            }
        }
        return (items, changes);
    }

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var srv = (ushort)(r.ServerIndex + 1);
        var s = await LoadAsync(uid, ct);
        List<(long, long)> items;
        List<object> changes = [];
        var touchedPoints = false;
        switch (cmd)
        {
            case 28010:
                {
                    var id = (long)HandlerHelpers.ReadUInt(r.Body, "id");
                    if (!Claimable(s, id)) return [HandlerHelpers.ResultOnly(codec, r, 28011, 2)];
                    (items, changes) = Claim(s, [id]);
                    touchedPoints = true;
                    break;
                }
            case 28014:
                {
                    var ids = HandlerHelpers.ReadIdList(r.Body, "id_list");
                    var targets = ids.Count > 0 ? ids : s.Tasks.Keys.Where(k => Claimable(s, k)).ToList();
                    (items, changes) = Claim(s, targets);
                    touchedPoints = true;
                    break;
                }
            default:
                {
                    var key = (int)HandlerHelpers.ReadUInt(r.Body, "activity_pt_id", 1);
                    var need = (long)HandlerHelpers.ReadUInt(r.Body, "need_active_point");
                    var pt = s.Points.GetValueOrDefault(key);
                    if (pt is null || pt.Point < need || need <= 0) return [HandlerHelpers.ResultOnly(codec, r, 28017, 2)];
                    items = [];
                    if (!pt.Got.Contains(need))
                    {
                        pt.Got.Add(need);
                        if (PtCfg.ValueKind == JsonValueKind.Object && PtCfg.TryGetProperty(key.ToString(), out var pc) && pc.GetProperty("rewards").TryGetProperty(need.ToString(), out var rw))
                            items = rw.EnumerateArray().Select(x => (x.GetProperty("id").GetInt64(), x.GetProperty("num").GetInt64())).ToList();
                    }
                    touchedPoints = true;
                    break;
                }
        }
        await SaveAsync(uid, s, ct);
        var merged = items.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).ToArray();
        var granted = merged.Length > 0 ? await accounts.GrantItemsAsync(uid, merged, ct) : [];
        var frames = await InventorySync.FramesAsync(accounts, codec, r, granted.Select(x => x.Item1), ct);
        if (Diff(srv, changes) is { } d) frames.Add(d);
        if (touchedPoints) frames.Add(new GameResponse(28019, 0, srv, codec.Encode("sc_28019", PointPayload(s))));
        frames.Add(HandlerHelpers.Reply(codec, r, cmd + 1, new Dictionary<string, object?>
        {
            ["result"] = 0u, ["reward_list"] = granted.Select(x => (object)new Dictionary<string, object?> { ["id"] = (uint)x.Item1, ["num"] = (uint)x.Item2 }).ToArray()
        }));
        return frames;
    }
}
