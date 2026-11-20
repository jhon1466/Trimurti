using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 弥弥尔探索 (admin cat exploration, system 2801), port of Alpha-Persei admin_cat_explore_service on the client
/// ExploreLevel/Area/Meow/MeowSkill/Event/MeowInborn configs (Data/Configs/explore_cfg.json). Level from item 53;
/// timed dispatches per area/cat with narrative logs; finishing grants area rewards × hours × (skill + recommended
/// area + random event) bonuses; skill upgrades and unlocks cost item 56; weekly login-day chest on weekends.
/// Ops 67002–67012, state 67001.
/// </summary>
public sealed class Explore(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec, TaskSystem tasks)
{
    private static readonly int[] WeeklyRate = [0, 1, 2, 3, 4, 6, 8, 10];
    private JsonElement Cfg => config.Get("explore_cfg.json") ?? throw new ModeError("Missing explore_cfg.json");
    private JsonElement T(string table, long id) => Cfg.GetProperty(table).GetProperty(id.ToString());
    private static int I(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    private static List<long> L(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList() : [];

    public sealed class Ev { public long Time { get; set; } public long AddressId { get; set; } public long ContentId { get; set; } }
    public sealed class Queue { public long AreaId { get; set; } public long MimirId { get; set; } public long Start { get; set; } public long Stop { get; set; } public int Hours { get; set; } public List<Ev> Events { get; set; } = []; }
    public sealed class State
    {
        public Dictionary<long, int> Cats { get; set; } = new() { [10001] = 1 };
        public List<Queue> Queues { get; set; } = [];
        public int WeeklyTime { get; set; }
        public long DailyTime { get; set; }
        public int WeeklyRewardState { get; set; }
        public int WeeklySceneOpen { get; set; }
        public long TotalExploreC { get; set; }
        public long WeekStart { get; set; }
    }

    private static long WeekStart()
    {
        var day = GameClock.Today;
        return new DateTimeOffset(day.AddDays(-(((int)day.DayOfWeek + 6) % 7)).AddHours(5), GameClock.Offset).ToUnixTimeSeconds();
    }

    private async Task<State> LoadAsync(long uid, CancellationToken ct)
    {
        var s = await accounts.GetJsonAsync<State>(uid, "explore_v1", ct) ?? new State();
        if (s.WeekStart != WeekStart()) { s.WeeklyTime = 0; s.WeeklyRewardState = 0; s.WeeklySceneOpen = 0; s.WeekStart = WeekStart(); }
        if (s.Cats.Count == 0) s.Cats[10001] = 1;
        return s;
    }

    private async Task<int> LevelAsync(long uid, CancellationToken ct)
    {
        var rem = await accounts.GetItemCountAsync(uid, 53, ct);
        var level = 1;
        foreach (var lv in Cfg.GetProperty("ExploreLevelCfg").EnumerateObject().Select(p => p.Value).OrderBy(v => I(v, "id")))
        {
            var need = I(lv, "exp");
            if (need <= 0 || rem < need) break;
            rem -= need;
            level++;
        }
        return Math.Min(level, Cfg.GetProperty("ExploreLevelCfg").EnumerateObject().Count());
    }

    private Dictionary<string, object?> Payload(State s) => new()
    {
        ["mimir"] = s.Cats.OrderBy(c => c.Key).Select(c => (object)new Dictionary<string, object?>
        {
            ["mimir_id"] = (uint)c.Key, ["skill"] = new object[] { new Dictionary<string, object?> { ["skill_id"] = (uint)I(T("ExploreMeowCfg", c.Key), "skill"), ["skill_level"] = (uint)c.Value } }
        }).ToArray(),
        ["explore_queue"] = s.Queues.Select(q => (object)new Dictionary<string, object?>
        {
            ["area_id"] = (uint)q.AreaId, ["mimir_id"] = (uint)q.MimirId, ["start_time"] = (uint)q.Start, ["stop_time"] = (uint)q.Stop, ["target_explore_hour"] = (uint)q.Hours,
            ["explore_event"] = q.Events.Select(e => (object)new Dictionary<string, object?> { ["time"] = (uint)e.Time, ["address_id"] = (uint)e.AddressId, ["content_id"] = (uint)e.ContentId }).ToArray()
        }).ToArray(),
        ["weekly_time"] = (uint)s.WeeklyTime, ["daily_time"] = (uint)s.DailyTime, ["weekly_reward_state"] = (uint)s.WeeklyRewardState,
        ["weekly_scene_open"] = (uint)s.WeeklySceneOpen, ["total_explore_c"] = (uint)s.TotalExploreC
    };

    public async Task<(uint, byte[])> LoginFrameAsync(long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        await accounts.SetJsonAsync(uid, "explore_v1", s, ct);
        return (67001, codec.Encode("sc_67001", Payload(s)));
    }

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var srv = (ushort)(r.ServerIndex + 1);
        var s = await LoadAsync(uid, ct);
        var level = await LevelAsync(uid, ct);
        var lv = T("ExploreLevelCfg", level);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        uint result = 0;
        var reply = new Dictionary<string, object?>();
        var gives = new List<(long, long)>();
        var costs = new List<(long, long)>();
        GameResponse? taskDiff = null;
        switch (cmd)
        {
            case 67002:
                {
                    var mid = (long)HandlerHelpers.ReadUInt(r.Body, "mimir_id");
                    var sid = (long)HandlerHelpers.ReadUInt(r.Body, "skill_id");
                    if (!s.Cats.TryGetValue(mid, out var cur)) { result = 23000; break; }
                    if (sid != I(T("ExploreMeowCfg", mid), "skill")) { result = 2; break; }
                    var consume = T("ExploreMeowSkillCfg", sid).GetProperty("skill_up_consume");
                    if (cur >= 10 || cur - 1 >= consume.GetArrayLength()) { result = 2; break; }
                    var c = consume[cur - 1][0];
                    costs.Add((c[0].GetInt64(), c[1].GetInt64()));
                    if (!await accounts.ConsumeAndGrantAsync(uid, costs, [], ct)) { result = 402; costs.Clear(); break; }
                    s.Cats[mid] = cur + 1;
                    break;
                }
            case 67004:
                {
                    var area = (long)HandlerHelpers.ReadUInt(r.Body, "area_id");
                    var hours = (int)HandlerHelpers.ReadUInt(r.Body, "hour_time");
                    var mid = (long)HandlerHelpers.ReadUInt(r.Body, "mimir_id");
                    if (!Cfg.GetProperty("ExploreAreaCfg").TryGetProperty(area.ToString(), out _) || !L(lv, "area").Contains(area)) result = 23005;
                    else if (hours <= 0 || hours > I(lv, "time")) result = 23001;
                    else if (!s.Cats.ContainsKey(mid)) result = 23000;
                    else if (s.Queues.Any(q => q.AreaId == area)) result = 23003;
                    else if (s.Queues.Any(q => q.MimirId == mid)) result = 23002;
                    else if (s.Queues.Count >= I(lv, "amount")) result = 23004;
                    if (result != 0) break;
                    var log = Cfg.GetProperty("log").GetProperty("events");
                    var address = log.EnumerateObject().Where(p => I(p.Value, "event_type") == 1).Select(p => long.Parse(p.Name)).ToList();
                    var content = log.EnumerateObject().Where(p => I(p.Value, "event_type") is 2 or 3).Select(p => long.Parse(p.Name)).ToList();
                    var q = new Queue { AreaId = area, MimirId = mid, Start = now, Stop = now + hours * 3600, Hours = hours };
                    for (var t = Random.Shared.Next(60, 301); t < hours * 3600 - 120; t += Random.Shared.Next(1800, 3601))
                        q.Events.Add(new Ev { Time = t, AddressId = address[Random.Shared.Next(address.Count)], ContentId = content[Random.Shared.Next(content.Count)] });
                    s.Queues.Add(q);
                    if (s.DailyTime == 0 || GameClock.GameDay(GameClock.At(s.DailyTime)) != GameClock.Today)
                    {
                        s.DailyTime = now;
                        s.WeeklyTime = Math.Min(7, s.WeeklyTime + 1);
                    }
                    taskDiff = await tasks.EventAsync(uid, srv, ts => tasks.Trigger(ts, 9), ct);
                    break;
                }
            case 67006:
                {
                    var area = (long)HandlerHelpers.ReadUInt(r.Body, "area_id");
                    var q = s.Queues.FirstOrDefault(x => x.AreaId == area);
                    if (q is null || now < q.Stop) { result = 2; break; }
                    var areaCfg = T("ExploreAreaCfg", area);
                    var meow = T("ExploreMeowCfg", q.MimirId);
                    var skill = T("ExploreMeowSkillCfg", I(meow, "skill"));
                    var type = I(skill, "skill_type");
                    var skillBonus = 0;
                    if ((type is 1 or 2 && q.Hours <= 8) || (type is 3 or 4 && q.Hours > 8) || type == 5)
                    {
                        var eff = L(skill, "skill_effect");
                        skillBonus = (int)eff[Math.Clamp(s.Cats.GetValueOrDefault(q.MimirId, 1) - 1, 0, eff.Count - 1)];
                    }
                    var inborn = L(meow, "area_recommend").Contains(area) ? 10 : 0;
                    long eventId = 0;
                    var eventBonus = 0;
                    if (Random.Shared.Next(1, 101) <= I(areaCfg, "event_probability") && L(areaCfg, "event") is { Count: > 0 } evs)
                    {
                        eventId = evs[Random.Shared.Next(evs.Count)];
                        eventBonus = (int)L(T("ExploreEventCfg", eventId), "effect").ElementAtOrDefault(1);
                    }
                    var total = 100 + skillBonus + inborn + eventBonus;
                    long currency = 0;
                    foreach (var rw in areaCfg.GetProperty("reward").EnumerateArray())
                    {
                        var amount = (rw[1].GetInt64() * q.Hours * total + 99) / 100;
                        if (amount <= 0) continue;
                        gives.Add((rw[0].GetInt64(), amount));
                        if (rw[0].GetInt64() is 54 or 55 or 56) currency += amount;
                    }
                    s.Queues.Remove(q);
                    s.TotalExploreC += currency;
                    reply["event_id"] = (uint)eventId;
                    break;
                }
            case 67008:
                {
                    var weekday = GameClock.Today.DayOfWeek;
                    if (weekday is not (DayOfWeek.Saturday or DayOfWeek.Sunday) || s.WeeklyRewardState != 0 || s.WeeklyTime <= 0) { result = 2; break; }
                    var mult = WeeklyRate[Math.Min(WeeklyRate.Length - 1, s.WeeklyTime)];
                    gives.AddRange(lv.GetProperty("accumulate_rewards").EnumerateArray().Select(x => (x[0].GetInt64(), x[1].GetInt64() * mult)));
                    s.WeeklyRewardState = 1;
                    break;
                }
            case 67010:
                {
                    var mid = (long)HandlerHelpers.ReadUInt(r.Body, "mimir_id");
                    if (!Cfg.GetProperty("ExploreMeowCfg").TryGetProperty(mid.ToString(), out var mc)) { result = 2; break; }
                    if (s.Cats.ContainsKey(mid)) break;
                    if (!L(lv, "meow").Contains(mid)) { result = 2; break; }
                    if (mc.TryGetProperty("unlock_condition", out var uc) && uc.ValueKind == JsonValueKind.Array && uc.GetArrayLength() > 0 && s.TotalExploreC < uc[0][1].GetInt64()) { result = 2; break; }
                    if (mc.TryGetProperty("consume", out var co) && co.ValueKind == JsonValueKind.Array && co.GetArrayLength() > 0)
                    {
                        costs.Add((co[0][0].GetInt64(), co[0][1].GetInt64()));
                        if (!await accounts.ConsumeAndGrantAsync(uid, costs, [], ct)) { result = 402; costs.Clear(); break; }
                    }
                    s.Cats[mid] = 1;
                    break;
                }
            case 67012: s.WeeklySceneOpen = 1; break;
        }
        if (result != 0) return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, result)];
        await accounts.SetJsonAsync(uid, "explore_v1", s, ct);
        var granted = gives.Count > 0 ? await accounts.GrantItemsAsync(uid, gives.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))), ct) : [];
        var frames = await InventorySync.FramesAsync(accounts, codec, r, granted.Select(x => x.Item1).Concat(costs.Select(c => c.Item1)), ct);
        reply["result"] = 0u;
        if (cmd is 67006 or 67008) reply["reward_list"] = granted.Select(x => (object)new Dictionary<string, object?> { ["id"] = (uint)x.Item1, ["num"] = (uint)x.Item2 }).ToArray();
        frames.Add(HandlerHelpers.Reply(codec, r, cmd + 1, reply));
        frames.Add(new GameResponse(67001, 0, srv, codec.Encode("sc_67001", Payload(s))));
        if (taskDiff is not null) frames.Add(taskDiff);
        return frames;
    }
}
