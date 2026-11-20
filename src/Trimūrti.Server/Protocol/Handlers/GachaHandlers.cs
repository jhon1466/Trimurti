using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 探测 (gacha), port of Alpha-Persei draw_service.py on the client tables DrawPoolCfg / DrawItemCfg / DrawSettingCfg and the
/// GameSetting draw keys (→ Data/Configs/draw_cfg.json). Rules from the pools' own rule text (detail_note):
///  - item pools by DrawSettingCfg[pool_draw_range_type] = [S, A, B, 3★ servant] (servant range: [5★, 4★, 3★]);
///  - S 1.6 %, A 7.8 % (4★ servants 11.08 %), B heroes 6 %, rest 3★ servants; every 10 pulls guarantee A or better;
///    S at the latest at pull 70 (pull 90 for anchored type-6 pools, whose S is always the chosen UP); a first S by
///    draw_ssr_lucky_num_first_time on the standard pool;
///  - limited (type 3): S is the UP 50 %, a miss guarantees the next one; A is one of the UP A 90 % with the same guarantee;
///    precision pools (types 3 / 6) share their counters; selectable pools use the UP chosen with 16016;
///  - 选择钥从 (type 2): 5★ is the chosen sleeping child, 4★ from its race list;
///  - 新人入职 (type 5): 20 pulls in total, ten pulls cost discount %, the first ten include 夜之寄生·霍德尔;
///  - 共鸣辉芒 (item 36) by rarity from currency_for_draw, duplicates become pieces, draw_num_max per day.
/// 16010 draw, 16012 history, 16016 choose UP, 16018 pool details, login frame 16015.
/// </summary>
public sealed class GachaSystem(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    private const double RateS = 0.016, RateAHero = 0.078, RateAServant = 0.1108, RateB = 0.060;
    private const long Resonance = 36;
    private JsonElement Cfg => config.Get("draw_cfg.json") ?? throw new ModeError("Missing draw_cfg.json");
    private static void Require(bool ok, string m) { if (!ok) throw new ModeError(m); }
    private static List<long> L(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList() : [];

    public sealed class Counter { public int SinceS { get; set; } public int SinceA { get; set; } public bool UpS { get; set; } public bool UpA { get; set; } public bool GotS { get; set; } }
    public sealed class Record { public long Pool { get; set; } public long Item { get; set; } public long Num { get; set; } public long Ts { get; set; } }
    public sealed class State
    {
        public Dictionary<string, Counter> Counters { get; set; } = [];
        public Dictionary<long, long> Up { get; set; } = [];
        public int Newbie { get; set; }
        public long Day { get; set; }
        public int Today { get; set; }
        public bool FirstS { get; set; }
        public bool NewbieClosed { get; set; }
        public Dictionary<long, int> UpTimes { get; set; } = [];
        public List<Record> History { get; set; } = [];
    }

    private JsonElement Pool(long id) { Require(Cfg.GetProperty("pools").TryGetProperty(id.ToString(), out var p), "Unknown pool"); return p; }
    private static int T(JsonElement p) => p.GetProperty("type").GetInt32();
    /// <summary>Pity groups (client): standard and the old newbie pool share; every other pool type keeps its own.</summary>
    private static string Group(JsonElement p) => T(p) is 1 or 5 ? "1" : T(p).ToString();
    private List<long> Items(long sub) => Cfg.GetProperty("items").TryGetProperty(sub.ToString(), out var v) ? L(v) : [];
    private List<long> Subs(JsonElement p) => Cfg.GetProperty("setting").TryGetProperty(p.GetProperty("range").GetInt32().ToString(), out var s) ? L(s) : [];
    private static T Pick<T>(IReadOnlyList<T> list) => list[Random.Shared.Next(list.Count)];

    private async Task<State> LoadAsync(long uid, CancellationToken ct)
    {
        var s = await accounts.GetJsonAsync<State>(uid, "draw_v1", ct) ?? new State();
        if (s.Day != GameClock.TodayStartUnix) { s.Day = GameClock.TodayStartUnix; s.Today = 0; }
        return s;
    }

    private long Up(State s, long poolId, JsonElement p)
    {
        if (p.GetProperty("selected").GetInt32() != 0) return s.Up.GetValueOrDefault(poolId);
        var up = p.GetProperty("up");
        return up.GetArrayLength() > 0 ? L(up[0]).FirstOrDefault() : 0;
    }

    private bool ActivePool(State s, long id) => !(T(Pool(id)) == 5 && s.Newbie >= 20) && !(T(Pool(id)) == 8 && s.NewbieClosed);

    private object Info(State s, long id)
    {
        var p = Pool(id);
        var c = s.Counters.GetValueOrDefault(Group(p)) ?? new Counter();
        return new Dictionary<string, object?> { ["id"] = (uint)id, ["ssr_draw_times"] = (uint)c.SinceS, ["up"] = (uint)Up(s, id, p), ["up_times"] = (uint)s.UpTimes.GetValueOrDefault(id), ["is_new"] = 0u };
    }

    public async Task<(uint, byte[])> LoginFrameAsync(long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        return (16015, codec.Encode("sc_16015", new Dictionary<string, object?>
        {
            ["draw_info_list"] = L(Cfg.GetProperty("active")).Where(id => Cfg.GetProperty("pools").TryGetProperty(id.ToString(), out _) && ActivePool(s, id)).Select(id => Info(s, id)).ToArray(),
            ["first_ssr_draw_flag"] = s.FirstS, ["today_draw_times"] = (uint)s.Today, ["newbie_choose_draw_flag"] = false
        }));
    }

    private int Rarity(long item) => item >= 2000000 ? (item >= 2500000 ? 5 : item >= 2400000 ? 4 : 3)
        : Items(201).Contains(item) ? 4 : Items(301).Contains(item) ? 3 : 5;

    /// <summary>One pull (client rules: TipsCfg DRAW_POOL_PROBABILITY_* rates; S at 70, 90 anchored, 40 directed, first S 40;
    /// S UP 50 % (anchored 55 %, never a guarantee and a miss keeps the counter; servant / directed / return pools always UP);
    /// A UP 90 % (servant 100 %) with a guarantee after a miss).</summary>
    private long PullOne(State s, long poolId, JsonElement p, bool forceA)
    {
        var kind = T(p);
        var c = s.Counters.TryGetValue(Group(p), out var cc) ? cc : s.Counters[Group(p)] = new Counter();
        var subs = Subs(p);
        var servantPool = kind == 2;
        c.SinceS++; c.SinceA++;
        var cap = kind == 6 ? 90 : kind == 8 ? 40 : 70;
        if (kind is 1 or 5 && !s.FirstS) cap = Cfg.GetProperty("first_s").GetInt32();
        if (kind == 9) cap = Cfg.GetProperty("return_s").GetInt32();
        var roll = Random.Shared.NextDouble();
        var up = Up(s, poolId, p);
        var aRate = servantPool ? RateAServant : RateAHero;
        if (c.SinceS >= cap || roll < RateS)
        {
            var upS = up != 0 ? new List<long> { up } : (p.GetProperty("up").GetArrayLength() > 0 ? L(p.GetProperty("up")[0]) : []);
            var others = subs.Count > 0 ? Items(subs[0]).Where(x => !upS.Contains(x)).ToList() : [];
            var guaranteed = c.UpS || kind is 2 or 8 or 9 || (kind == 6 && c.SinceS >= cap);
            var rate = kind == 6 ? 0.55 : 0.5;
            var chooseUp = upS.Count > 0 && (guaranteed || others.Count == 0 || Random.Shared.NextDouble() < rate);
            var item = chooseUp ? Pick(upS) : Pick(others.Count > 0 ? others : upS);
            c.UpS = upS.Count > 0 && !chooseUp && kind != 6;
            c.SinceA = 0;
            if (kind != 6 || chooseUp) c.SinceS = 0;
            if (kind is 1 or 5) s.FirstS = true;
            if (kind == 8) s.NewbieClosed = true;
            return item;
        }
        if (forceA || c.SinceA >= 10 || roll < RateS + aRate)
        {
            c.SinceA = 0;
            List<long> upA;
            if (servantPool)
            {
                var idx = L(p.GetProperty("optional")).IndexOf(up);
                upA = idx >= 0 && idx < p.GetProperty("optional_a").GetArrayLength() ? L(p.GetProperty("optional_a")[idx]) : [];
            }
            else upA = p.GetProperty("up").GetArrayLength() > 1 ? L(p.GetProperty("up")[1]) : [];
            var others = Items(subs[1]).Where(x => !upA.Contains(x)).ToList();
            var chooseUp = upA.Count > 0 && (c.UpA || servantPool || others.Count == 0 || Random.Shared.NextDouble() < 0.9);
            c.UpA = upA.Count > 0 && !chooseUp;
            return chooseUp ? Pick(upA) : Pick(others.Count > 0 ? others : upA);
        }
        if (servantPool) return Pick(Items(subs[2]));
        return roll < RateS + aRate + RateB ? Pick(Items(subs[2])) : Pick(Items(subs[3]));
    }

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var b = r.Body;
        var s = await LoadAsync(uid, ct);
        try
        {
            switch (cmd)
            {
                case 16010:
                    {
                        var poolId = (long)HandlerHelpers.ReadUInt(b, "pool");
                        var ten = HandlerHelpers.ReadUInt(b, "type") is 2 or 10;
                        var p = Pool(poolId);
                        Require(L(Cfg.GetProperty("active")).Contains(poolId) && ActivePool(s, poolId), "Pool closed");
                        var n = ten ? 10 : 1;
                        Require(s.Today + n <= Cfg.GetProperty("daily_max").GetInt32(), "Daily draw limit");
                        if (T(p) == 5) Require(s.Newbie + n <= 20, "Newbie pulls used up");
                        if (p.GetProperty("selected").GetInt32() != 0) Require(s.Up.ContainsKey(poolId), "Choose the UP first");
                        // Accepted costs: the pool's ticket (with the ten-pull discount) or its activity material.
                        var main = L(p.GetProperty(ten ? "ten" : "once"));
                        var alt = L(p.GetProperty(ten ? "ten_alt" : "once_alt"));
                        var mainCost = (main[0], ten ? (long)Math.Ceiling(main[1] * p.GetProperty("discount").GetInt32() / 100.0) : main[1]);
                        var sent = b.TryGetValue("cost", out var cv) && cv is IReadOnlyDictionary<string, object?> cd ? ((long)HandlerHelpers.ReadUInt(cd, "item_id"), (long)HandlerHelpers.ReadUInt(cd, "item_num")) : (0L, 0L);
                        var cost = alt.Count >= 2 && sent.Item1 == alt[0] ? (alt[0], alt[1]) : mainCost;
                        var got = new List<long>();
                        for (var i = 0; i < n; i++)
                        {
                            var item = PullOne(s, poolId, p, false);
                            // 新人入职: the first ten pulls always include 夜之寄生·霍德尔 (1037).
                            if (T(p) == 5 && s.Newbie + i == 9 && !got.Contains(1037) && Rarity(item) < 5) item = 1037;
                            got.Add(item);
                        }
                        var owned = (await accounts.GetOwnedHeroIdsAsync(uid, ct)).ToHashSet();
                        var shown = new List<(long, long)>();
                        var rebate = Cfg.GetProperty("rebate").EnumerateArray().ToDictionary(x => x[0].GetInt32(), x => x[1].GetInt64());
                        long resonance = 0;
                        foreach (var item in got)
                        {
                            resonance += rebate.GetValueOrDefault(Rarity(item));
                            if (ItemKinds.IsHero(item) && !owned.Add(item)) shown.Add((10000 + item, ItemKinds.DuplicatePieces(item)));
                            else shown.Add((item, 1));
                            s.History.Add(new Record { Pool = poolId, Item = item, Num = 1, Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                        }
                        var grants = got.GroupBy(x => x).Select(g => (g.Key, (long)g.Count())).ToList();
                        if (resonance > 0) grants.Add((Resonance, resonance));
                        if (!await accounts.ConsumeAndGrantAsync(uid, [cost], grants, ct)) return [HandlerHelpers.ResultOnly(codec, r, 16011, 2)];
                        s.Today += n;
                        if (T(p) == 5) s.Newbie += n;
                        if (s.History.Count > 500) s.History.RemoveRange(0, s.History.Count - 500);
                        await accounts.SetJsonAsync(uid, "draw_v1", s, ct);
                        var srv = (ushort)(r.ServerIndex + 1);
                        var frames = await InventorySync.FramesAsync(accounts, codec, r, [cost.Item1, Resonance], ct);
                        frames.Add(new GameResponse(14019, 0, srv, codec.Encode("sc_14019", new Dictionary<string, object?>
                        { ["piece_list"] = (await accounts.GetHeroPiecesAsync(uid, ct)).Select(x => (object)new Dictionary<string, object?> { ["id"] = (uint)x.Hero, ["num"] = (uint)x.Num }).ToArray() })));
                        frames.Add(new GameResponse(16015, 0, srv, (await LoginFrameAsync(uid, ct)).Item2));
                        // Achievements: pulls, 5★ servants and ten-pull patterns (80001 S-rank heroes, 80002 same hero,
                        // 80003 same race, 80004 4★ servants, 80005 same servant).
                        var ach = new AchievementSystem(accounts, config, codec);
                        var heroCfg = config.Get("hero_cfg.json")!.Value.GetProperty("heroes");
                        int Race(long h) => heroCfg.TryGetProperty(h.ToString(), out var hc) ? hc.GetProperty("race").GetInt32() : 0;
                        var heroesGot = got.Where(ItemKinds.IsHero).ToList();
                        var servantsGot = got.Where(x => x >= 2000000).ToList();
                        var events = new List<(int, long)> { (700, n), (780, servantsGot.Count(x => x >= 2500000)) };
                        if (n == 10)
                        {
                            events.Add((80001, heroesGot.Count(x => Rarity(x) == 5)));
                            events.Add((80002, heroesGot.GroupBy(x => x).Select(g => (long)g.Count()).DefaultIfEmpty(0).Max()));
                            events.Add((80003, heroesGot.GroupBy(Race).Select(g => (long)g.Count()).DefaultIfEmpty(0).Max()));
                            events.Add((80004, servantsGot.Count(x => x is >= 2400000 and < 2500000)));
                            events.Add((80005, servantsGot.GroupBy(x => x).Select(g => (long)g.Count()).DefaultIfEmpty(0).Max()));
                        }
                        foreach (var (cond, value) in events.Where(e => e.Item2 > 0))
                            if (await ach.EventAsync(uid, srv, cond, value, ct) is { } achDiff) frames.Add(achDiff);
                        frames.Add(HandlerHelpers.Reply(codec, r, 16011, new Dictionary<string, object?>
                        {
                            ["result"] = 0u, ["item"] = HandlerHelpers.ToItemNetRecList(shown), ["first_ssr_draw_flag"] = s.FirstS, ["newbie_choose_draw_flag"] = false,
                            ["ssr_draw_times"] = (uint)(s.Counters.GetValueOrDefault(Group(p))?.SinceS ?? 0)
                        }));
                        return frames;
                    }
                case 16012:
                    {
                        var poolId = (long)HandlerHelpers.ReadUInt(b, "id");
                        var p = Pool(poolId);
                        return [HandlerHelpers.Reply(codec, r, 16013, new Dictionary<string, object?>
                        {
                            ["result"] = 0u, ["ssr_draw_times"] = (uint)(s.Counters.GetValueOrDefault(Group(p))?.SinceS ?? 0),
                            ["draw_record_list"] = s.History.Where(h => Group(Pool(h.Pool)) == Group(p)).Reverse().Take(50).Select(h => (object)new Dictionary<string, object?>
                            { ["item"] = new Dictionary<string, object?> { ["item_id"] = (uint)h.Item, ["item_num"] = (uint)h.Num }, ["draw_timestamp"] = (uint)h.Ts }).ToArray()
                        })];
                    }
                case 16016:
                    {
                        var poolId = (long)HandlerHelpers.ReadUInt(b, "id");
                        var up = (long)HandlerHelpers.ReadUInt(b, "up");
                        var p = Pool(poolId);
                        Require(p.GetProperty("selected").GetInt32() != 0 && L(p.GetProperty("optional")).Contains(up), "UP not selectable");
                        Require(!(T(p) == 8 && s.NewbieClosed), "Directed newbie pool finished");
                        if (s.Up.GetValueOrDefault(poolId) != up)
                        {
                            var change = p.GetProperty("change").GetInt32();
                            Require(change == 0 || s.UpTimes.GetValueOrDefault(poolId) < change, "No UP changes left");
                            s.Up[poolId] = up;
                            s.UpTimes[poolId] = s.UpTimes.GetValueOrDefault(poolId) + 1;
                        }
                        await accounts.SetJsonAsync(uid, "draw_v1", s, ct);
                        return [new GameResponse(16015, 0, (ushort)(r.ServerIndex + 1), (await LoginFrameAsync(uid, ct)).Item2), HandlerHelpers.ResultOnly(codec, r, 16017)];
                    }
                case 16018:
                    {
                        var poolId = (long)HandlerHelpers.ReadUInt(b, "id");
                        var p = Pool(poolId);
                        var subs = Subs(p);
                        var up = Up(s, poolId, p);
                        var upA = p.GetProperty("up").GetArrayLength() > 1 ? L(p.GetProperty("up")[1]) : [];
                        uint[] U(IEnumerable<long> x) => x.Select(v => (uint)v).ToArray();
                        return [HandlerHelpers.Reply(codec, r, 16019, new Dictionary<string, object?>
                        {
                            ["result"] = 0u,
                            ["pool_details"] = new Dictionary<string, object?>
                            {
                                ["s_up_probability"] = up != 0 ? 50u : 0u, ["s_up_item"] = up != 0 ? new[] { (uint)up } : Array.Empty<uint>(),
                                ["s_other_item"] = subs.Count > 0 ? U(Items(subs[0]).Where(x => x != up)) : [],
                                ["a_up_probability"] = upA.Count > 0 ? 90u : 0u, ["a_up_item"] = U(upA),
                                ["a_other_item"] = subs.Count > 1 ? U(Items(subs[1]).Where(x => !upA.Contains(x))) : [],
                                ["b_item"] = subs.Count > 2 ? U(subs.Skip(2).SelectMany(Items)) : []
                            }
                        })];
                    }
            }
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)]; }
        return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)];
    }

    public static readonly uint[] Commands = [16010, 16012, 16016, 16018];
}
