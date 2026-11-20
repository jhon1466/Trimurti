using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 薇儿丹蒂 daily life (port of Alpha-Persei sphero_schedule_service / sphero_leisure_service on sphero_contract.json):
/// daily schedules (83003/83005), timed entrusts (83007/83009/83011/83013/83018/83040), barbecue (83022) and
/// puzzles (83024); state frames 83016 / 83017 / 83021 / 83026. Daily reset at 05:00 UTC+8.
/// </summary>
public sealed class SpheroLife(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    private const uint Activity = 242841;
    private JsonElement Cfg => config.Get("sphero_contract.json") ?? throw new ModeError("Missing sphero_contract.json");
    private JsonElement Set(string k) => Cfg.GetProperty("settings").GetProperty(k);
    private static void Require(bool ok, string m) { if (!ok) throw new ModeError(m); }
    private static int I(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    public sealed class Sched { public int Index { get; set; } public long ScheduleId { get; set; } public bool IsFinish { get; set; } }
    public sealed class Entrust { public int Index { get; set; } public long EntrustId { get; set; } public long StartTime { get; set; } public long AcceptedAt { get; set; } }
    public sealed class State
    {
        public string Day { get; set; } = "";
        public List<Sched> Schedules { get; set; } = [];
        public List<long>? Pool { get; set; }
        public int Refresh { get; set; }
        public bool Claimed { get; set; }
        public List<Entrust> Entrusts { get; set; } = [];
        public long Earned { get; set; }
        public long EntrustCompleted { get; set; }
        public long Coins { get; set; }
        public long Tickets { get; set; }
        public long BbqCount { get; set; }
        public Dictionary<long, long> Times { get; set; } = [];
        public List<long> Puzzles { get; set; } = [];
    }

    private async Task<State> LoadAsync(long uid, CancellationToken ct)
    {
        var s = await accounts.GetJsonAsync<State>(uid, "sphero_life_v1", ct) ?? new State();
        var day = GameClock.Today.ToString("yyyy-MM-dd");
        if (s.Day != day) { s.Day = day; s.Schedules = []; s.Pool = null; s.Refresh = 0; s.Claimed = false; s.Coins = 0; s.Tickets = 0; }
        s.Pool ??= NewPool();
        return s;
    }

    private List<long> NewPool()
    {
        var all = Cfg.GetProperty("entrusts").EnumerateObject().Select(p => long.Parse(p.Name)).ToList();
        return Enumerable.Range(0, Set("activity_hero_challenge_task_choose_num")[0].GetInt32()).Select(_ => all[Random.Shared.Next(all.Count)]).ToList();
    }

    private long Due(Entrust e) => e.StartTime + Cfg.GetProperty("entrusts").GetProperty(e.EntrustId.ToString()).GetProperty("time").GetInt64() * 60;

    /// <summary>Waiting entrusts (index &gt; 3) move into a free or finished running slot.</summary>
    private void Promote(State s, long now)
    {
        for (var n = 0; n < 6; n++)
        {
            var waiting = s.Entrusts.Where(e => e.StartTime == 0).OrderBy(e => e.Index).ToList();
            if (waiting.Count == 0) break;
            var occupied = s.Entrusts.Where(e => e.Index <= 3).ToDictionary(e => e.Index);
            int? free = Enumerable.Range(1, 3).Cast<int?>().FirstOrDefault(i => !occupied.ContainsKey(i!.Value));
            var finished = occupied.Values.Where(e => e.StartTime > 0 && Due(e) <= now).OrderBy(Due).FirstOrDefault();
            if (free is null && finished is null) break;
            var first = waiting[0];
            var position = free ?? finished!.Index;
            var start = free is not null ? now : Due(finished!);
            var last = waiting.Max(e => e.Index);
            foreach (var e in waiting.Skip(1)) e.Index--;
            if (free is null) finished!.Index = last;
            first.Index = position;
            first.StartTime = Math.Max(start, first.AcceptedAt);
        }
    }

    private int MaxSlots(State s) => 3 + Set("activity_hero_challenge_task_line_num").EnumerateArray()
        .Count(l => Cfg.GetProperty("conditions").TryGetProperty(l[1].GetInt64().ToString(), out var c) && s.Earned >= c.GetProperty("params")[1].GetInt64());

    private Sched? FinishSchedule(State s, int serverType)
    {
        var sc = s.Schedules.OrderBy(x => x.Index).FirstOrDefault(x => !x.IsFinish && I(Cfg.GetProperty("schedules").GetProperty(x.ScheduleId.ToString()), "server_type") == serverType);
        if (sc is not null) sc.IsFinish = true;
        return sc;
    }

    private static object[] EntrustWire(State s) => s.Entrusts.OrderBy(e => e.Index).Select(e => (object)new Dictionary<string, object?> { ["index"] = (uint)e.Index, ["entrust_id"] = (uint)e.EntrustId, ["start_time"] = (uint)e.StartTime }).ToArray();
    private static object SchedWire(Sched x) => new Dictionary<string, object?> { ["index"] = (uint)x.Index, ["schedule_id"] = (uint)x.ScheduleId, ["is_finish"] = x.IsFinish };

    private async Task<Dictionary<string, object?>> SnapshotAsync(long uid, State s, CancellationToken ct)
    {
        var battle = await accounts.GetJsonAsync<SpheroMode.State>(uid, "sphero_v1", ct) ?? new SpheroMode.State();
        int Sub(long st) => Cfg.GetProperty("stages").TryGetProperty(st.ToString(), out var c) ? I(c, "sub_type") : 0;
        return new()
        {
            ["activity_id"] = Activity, ["begin_entrust_list"] = EntrustWire(s), ["entrust_id_list"] = s.Pool!.Select(x => (uint)x).ToArray(), ["entrust_refresh_times"] = (uint)s.Refresh,
            ["passed_chapter_level_list"] = battle.Stories.Where(x => x is >= 5170201 and <= 5170206).OrderBy(x => x).Select(x => (uint)x).ToArray(),
            ["train_list"] = new[] { 2, 3, 4 }.Select(g => (object)new Dictionary<string, object?>
            {
                ["type"] = (uint)g, ["value"] = (uint)battle.GroupValue.GetValueOrDefault(g), ["passed_level"] = battle.Train.Keys.Where(k => Sub(k) == g).OrderBy(x => x).Select(x => (uint)x).ToArray()
            }).ToArray(),
            ["challenge_times"] = (uint)battle.Rewarded
        };
    }

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        Promote(s, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await accounts.SetJsonAsync(uid, "sphero_life_v1", s, ct);
        return
        [
            (83016, codec.Encode("sc_83016", await SnapshotAsync(uid, s, ct))),
            (83017, codec.Encode("sc_83017", new Dictionary<string, object?> { ["activity_id"] = Activity, ["schedule_list"] = s.Schedules.Select(SchedWire).ToArray() })),
            (83021, codec.Encode("sc_83021", new Dictionary<string, object?> { ["stage_completed_list"] = s.Puzzles.Select(x => (uint)x).ToArray() })),
            (83026, codec.Encode("sc_83026", new Dictionary<string, object?> { ["reward_record"] = (uint)s.Coins, ["ticket_record"] = (uint)s.Tickets }))
        ];
    }

    private async Task CostAsync(long uid, IEnumerable<(long, long)> items, CancellationToken ct)
    {
        var merged = items.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).ToArray();
        Require(await accounts.ConsumeAndGrantAsync(uid, merged, [], ct), "Not enough items");
    }

    private static IEnumerable<(long, long)> Pairs(JsonElement arr) => arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().Select(x => (x[0].GetInt64(), x[1].GetInt64())) : [];

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var srv = (ushort)(r.ServerIndex + 1);
        var b = r.Body;
        var s = await LoadAsync(uid, ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var reply = new Dictionary<string, object?> { ["result"] = 0u };
        var gives = new List<(long, long)>();
        Sched? changed = null;
        var touched = new List<long>();
        try
        {
            switch (cmd)
            {
                case 83003:
                    {
                        Require(HandlerHelpers.ReadUInt(b, "activity_id") == Activity, "Unknown activity");
                        var entries = (b.GetValueOrDefault("schedule_info_list") as System.Collections.IEnumerable)?.Cast<object?>().OfType<IReadOnlyDictionary<string, object?>>().ToList() ?? [];
                        Require(entries.Count is > 0 and <= 4, "Bad schedule count");
                        var map = s.Schedules.ToDictionary(x => x.Index);
                        foreach (var e in entries)
                        {
                            var idx = (int)HandlerHelpers.ReadUInt(e, "index");
                            var sid = (long)HandlerHelpers.ReadUInt(e, "schedule_id");
                            Require(idx is >= 1 and <= 4 && Cfg.GetProperty("schedules").TryGetProperty(sid.ToString(), out _), "Bad schedule");
                            Require(!(map.TryGetValue(idx, out var old) && old.IsFinish), "Finished schedules cannot be replaced");
                            map[idx] = new Sched { Index = idx, ScheduleId = sid };
                        }
                        s.Schedules = map.Values.OrderBy(x => x.Index).ToList();
                        break;
                    }
                case 83005:
                    {
                        Require(!s.Claimed, "Today's schedule reward already claimed");
                        var score = s.Schedules.Where(x => x.IsFinish).Sum(x => I(Cfg.GetProperty("schedules").GetProperty(x.ScheduleId.ToString()), "score"));
                        Require(score >= Set("activity_hero_challenge_schedule_award_point_limit")[0].GetInt32(), "Not enough schedule points");
                        s.Claimed = true;
                        gives.AddRange(Pairs(Cfg.GetProperty("activity").GetProperty("daily_award")));
                        break;
                    }
                case 83007:
                    Require(s.Refresh < Set("activity_hero_challenge_task_refresh_num")[0].GetInt32(), "No refreshes left today");
                    if (s.Refresh >= Set("activity_hero_challenge_task_free_refresh_num")[0].GetInt32()) await CostAsync(uid, Pairs(Cfg.GetProperty("activity").GetProperty("refresh_cost")), ct);
                    s.Refresh++;
                    s.Pool = NewPool();
                    reply["entrust_id_list"] = s.Pool.Select(x => (uint)x).ToArray();
                    break;
                case 83009:
                    {
                        var idx = (int)HandlerHelpers.ReadUInt(b, "index");
                        var eid = (long)HandlerHelpers.ReadUInt(b, "entrust_id");
                        Require(idx >= 1 && idx <= MaxSlots(s), "Entrust slot locked");
                        Require(s.Pool!.Contains(eid) && s.Entrusts.All(e => e.Index != idx), "Entrust not available or slot used");
                        await CostAsync(uid, Pairs(Cfg.GetProperty("entrusts").GetProperty(eid.ToString()).GetProperty("cost")), ct);
                        s.Pool.Remove(eid);
                        s.Entrusts.Add(new Entrust { Index = idx, EntrustId = eid, StartTime = idx <= 3 ? now : 0, AcceptedAt = now });
                        Promote(s, now);
                        reply["begin_entrust_list"] = EntrustWire(s);
                        changed = FinishSchedule(s, 8);
                        break;
                    }
                case 83011 or 83018:
                    {
                        var idx = (int)HandlerHelpers.ReadUInt(b, "index");
                        var e = s.Entrusts.FirstOrDefault(x => x.Index == idx) ?? throw new ModeError("Entrust not found");
                        if (cmd == 83011) { s.Entrusts.Remove(e); Promote(s, now); }
                        else
                        {
                            var count = (int)HandlerHelpers.ReadUInt(b, "use_cnt");
                            var remaining = Due(e) - now;
                            var seconds = Set("activity_hero_challenge_certificate_time")[0].GetInt64() * 60;
                            Require(count > 0 && e.StartTime > 0 && remaining > 0 && count <= (remaining + seconds - 1) / seconds, "Nothing to accelerate");
                            await CostAsync(uid, [(Cfg.GetProperty("activity").GetProperty("certificate_cost_id").GetInt64(), count)], ct);
                            e.StartTime = Math.Max(1, e.StartTime - count * seconds);
                        }
                        reply["begin_entrust_list"] = EntrustWire(s);
                        break;
                    }
                case 83013:
                    {
                        var idxs = HandlerHelpers.ReadIdList(b, "index_list");
                        var selected = idxs.Select(i => s.Entrusts.FirstOrDefault(e => e.Index == i)).ToList();
                        Require(idxs.Count > 0 && idxs.Distinct().Count() == idxs.Count && selected.All(e => e is not null && e.StartTime > 0 && Due(e) <= now), "Entrust not finished");
                        foreach (var e in selected) gives.AddRange(Pairs(Cfg.GetProperty("entrusts").GetProperty(e!.EntrustId.ToString()).GetProperty("reward_list")));
                        s.Entrusts.RemoveAll(e => idxs.Contains(e.Index));
                        s.EntrustCompleted += idxs.Count;
                        Promote(s, now);
                        reply["begin_entrust_list"] = EntrustWire(s);
                        break;
                    }
                case 83040:
                    Promote(s, now);
                    reply["begin_entrust_list"] = EntrustWire(s);
                    break;
                case 83022:
                    {
                        var stage = (long)HandlerHelpers.ReadUInt(b, "stage_id");
                        var grade = (int)HandlerHelpers.ReadUInt(b, "grade_id");
                        var result = HandlerHelpers.ReadUInt(b, "result");
                        Require(Cfg.GetProperty("barbecue").TryGetProperty(stage.ToString(), out var bbq) && grade is >= 1 and <= 5, "Bad barbecue stage or grade");
                        Require(HandlerHelpers.ReadUInt(b, "hero_id") == Cfg.GetProperty("activity").GetProperty("hero_id").GetUInt32() && result is >= 1 and <= 3, "Bad barbecue report");
                        if (result == 1)
                        {
                            foreach (var (field, isCoins, limit) in new[] { ("reward_list_coin", true, "activity_hero_challenge_barbecue_coin_limit"), ("reward_list_ticket", false, "activity_hero_challenge_barbecue_ticket_limit") })
                            {
                                if (!bbq.TryGetProperty(field, out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() < grade) continue;
                                var rw = list[grade - 1];
                                if (rw.ValueKind != JsonValueKind.Array || rw.GetArrayLength() < 2) continue;
                                var cur = isCoins ? s.Coins : s.Tickets;
                                var amount = Math.Min(rw[1].GetInt64(), Set(limit)[0].GetInt64() - cur);
                                if (amount <= 0) continue;
                                gives.Add((rw[0].GetInt64(), amount));
                                if (isCoins) s.Coins += amount; else s.Tickets += amount;
                            }
                            s.BbqCount++;
                            changed = FinishSchedule(s, 7);
                        }
                        reply["reward_recode"] = (uint)s.Coins;
                        reply["ticket_recode"] = (uint)s.Tickets;
                        break;
                    }
                case 83024:
                    {
                        var stage = (long)HandlerHelpers.ReadUInt(b, "stage_id");
                        var result = HandlerHelpers.ReadUInt(b, "result");
                        Require(Cfg.GetProperty("puzzles").TryGetProperty(stage.ToString(), out var pz) && result is >= 1 and <= 3, "Bad puzzle report");
                        var pre = I(pz, "pre_stage");
                        Require(pre == 0 || s.Puzzles.Contains(pre), "Previous puzzle missing");
                        if (result == 1)
                        {
                            if (!s.Puzzles.Contains(stage))
                            {
                                if (HandlerHelpers.ReadUInt(b, "auto_completed") == 1) await CostAsync(uid, [(53100, Set("activity_hero_challenge_color_cost")[0].GetInt64())], ct);
                                gives.AddRange(Pairs(pz.GetProperty("reward")));
                                s.Puzzles.Add(stage);
                                changed = FinishSchedule(s, 6);
                            }
                            var secs = (long)HandlerHelpers.ReadUInt(b, "useseconds");
                            s.Times[stage] = s.Times.TryGetValue(stage, out var t) ? Math.Min(t, secs) : secs;
                        }
                        reply["stage_id"] = (uint)stage;
                        reply["finished_list"] = s.Puzzles.OrderBy(x => x).Select(x => (uint)x).ToArray();
                        break;
                    }
            }
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)]; }
        var granted = gives.Count > 0 ? await accounts.GrantItemsAsync(uid, gives.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))), ct) : [];
        s.Earned = Math.Max(s.Earned + granted.Where(x => x.Item1 == 53100).Sum(x => x.Item2), await accounts.GetItemCountAsync(uid, 53100, ct));
        if (cmd is 83005 or 83013 or 83022 or 83024) reply["reward_list"] = granted.Select(x => (object)new Dictionary<string, object?> { ["id"] = (uint)x.Item1, ["num"] = (uint)x.Item2 }).ToArray();
        await accounts.SetJsonAsync(uid, "sphero_life_v1", s, ct);
        var frames = await InventorySync.FramesAsync(accounts, codec, r, granted.Select(x => x.Item1).Concat(touched), ct);
        if (changed is not null) frames.Add(new GameResponse(83017, 0, srv, codec.Encode("sc_83017", new Dictionary<string, object?> { ["activity_id"] = Activity, ["schedule_list"] = new[] { SchedWire(changed) } })));
        frames.Add(HandlerHelpers.Reply(codec, r, cmd + 1, reply));
        return frames;
    }
}
