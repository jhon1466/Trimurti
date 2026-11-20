using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 游园街 / back home (port of Alpha-Persei backhome_service.py + account_db dorm functions) on the client configs
/// BackHomeCfg / BackHomeCanteen*Cfg / BackHomeHeroCfg / BackHomeHeroSkillCfg / IdolTrainee*Cfg (exported with
/// scripts/luadump/luacfg.py into Data/Configs/backhome_cfg.json) plus the dorm gift limits.
///  - Dorm: rooms unlock with their configured cost and default furniture, heroes move into the hall / one private room,
///    furniture layouts are trimmed to the owned stock, gifts raise room exp (level = recovery multiplier) and trust,
///    feeding (+20, once per game day), skins, door positions, templates and likes.
///  - Fatigue: 120 natural cap (140 with food), recovery per 600 s: private room 1.00–1.50 by room level, hall / camp 1.0,
///    idle 0.25, working (canteen job or running entrust) 0.
///  - Canteen: jobs with hero skills (cook speed, price, fatigue), signature dishes cook while the staff has fatigue
///    (56 s per guest, 0.05 fatigue each, 14000 pending cap), income claim, manual service, furniture upgrades,
///    entrusts (dispatch / recall / refresh / unlock / submit with the configured duration multipliers and success roll).
///  - Idol trainee: attribute training (20 fatigue, official growth formula), camp, PVE stages scored from the trained
///    attributes against the stage NPC's base attributes, stage tasks (condition 2012), rank rewards, dance DIY.
/// Task conditions: 2003 feed, 2007 dispatch, 2002 entrust done, 2020 income claim, 2008 games, 2009/2017 training,
/// 2011/2010 idol battles. Other players' rooms are read from their saved state (58050 / 58052).
/// </summary>
public sealed class BackHome(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec, TaskSystem tasks)
{
    private const string Kind = "backhome_v1";
    private const long Coin = 39;
    private const double GuestSeconds = 56.0, MealFatigue = 0.05;
    private const int EarningsCap = 14000, NaturalCap = 120, FedCap = 140;
    private static readonly (long Exp, double Mult)[] Levels = [(48000, 1.50), (33000, 1.40), (21000, 1.35), (12000, 1.30), (6000, 1.25), (3000, 1.20), (1800, 1.15), (1000, 1.10), (400, 1.05), (0, 1.00)];
    private static readonly Dictionary<int, double> Quality = new() { [1] = 0.5, [2] = 0.7, [3] = 1.0, [4] = 1.2, [5] = 1.3 };
    private static readonly long[] DefaultTasks = [1, 20001, 30001, 20003];

    private JsonElement Cfg => config.Get("backhome_cfg.json") ?? throw new ModeError("Missing backhome_cfg.json");
    private static void Require(bool ok, string m) { if (!ok) throw new ModeError(m); }
    private sealed class Fail(uint code) : Exception { public uint Code => code; }
    private static void Need(bool ok, uint code) { if (!ok) throw new Fail(code); }

    // ---------- state ----------
    public sealed class Hero { public double Fatigue { get; set; } = FedCap; public long Ts { get; set; } public int FeedTimes { get; set; } public int TotalFeed { get; set; } public long FeedDay { get; set; } public int Lock { get; set; } public long Skin { get; set; } }
    public sealed class FurPos { public uint F { get; set; } public int X { get; set; } public int Y { get; set; } public int R { get; set; } }
    public sealed class FurList { public uint Type { get; set; } public uint Suit { get; set; } public List<FurPos> Pos { get; set; } = []; }
    public sealed class Dorm { public int Pos { get; set; } public long Exp { get; set; } public int Liked { get; set; } public int Visited { get; set; } public List<long> Archives { get; set; } = []; public Dictionary<long, long> Gifts { get; set; } = []; public List<FurList> Layout { get; set; } = []; }
    public sealed class Entrust { public int Pos { get; set; } public long Task { get; set; } public List<long> Heroes { get; set; } = []; public int Refresh { get; set; } public long Start { get; set; } public int Duration { get; set; } = 1200; }
    public sealed class Dish { public int SellNum { get; set; } public int Sold { get; set; } public long Earn { get; set; } public long Ts { get; set; } }
    public sealed class Template { public uint Type { get; set; } public string Name { get; set; } = ""; public uint Pos { get; set; } public List<FurList> Lists { get; set; } = []; }
    public sealed class Dance { public uint Music { get; set; } public uint Scene { get; set; } public List<uint> Actions { get; set; } = []; }
    /// <summary>Weekly counters behind the dorm information boards 1001 (canteen) and 2001 (idol), shown for the previous week.</summary>
    public sealed class Week
    {
        public Dictionary<long, double> Fatigue { get; set; } = [];
        public Dictionary<long, long> Food { get; set; } = [];
        public int Entrusts { get; set; }
        public long Coins { get; set; }
        public Dictionary<long, int> Join { get; set; } = [];
        public Dictionary<long, int> Train { get; set; } = [];
        public Dictionary<int, int> Attr { get; set; } = [];
        public int Pvp { get; set; }
        public int PvpWin { get; set; }
    }
    public sealed class PveRec { public long Score { get; set; } public bool Clear { get; set; } }
    public sealed class Battle { public long Stage { get; set; } public long Atk { get; set; } public long Def { get; set; } public long AtkPec { get; set; } public long DefPec { get; set; } }
    public sealed class State
    {
        public bool Init { get; set; }
        public Dictionary<long, Hero> Heroes { get; set; } = [];
        public Dictionary<int, Dorm> Dorms { get; set; } = [];
        public Dictionary<int, long> Careers { get; set; } = [];
        public List<Entrust> Entrusts { get; set; } = [];
        public Dictionary<long, Dish> Dishes { get; set; } = [];
        public Dictionary<long, long> FoodSold { get; set; } = [];
        public Dictionary<int, int> CanteenFur { get; set; } = [];
        public long Pending { get; set; }
        public long Accruing { get; set; }
        public long LastReceive { get; set; }
        public long LastSettle { get; set; }
        public int Mode { get; set; }
        public Dictionary<long, long> Given { get; set; } = [];
        public Dictionary<uint, Template> Templates { get; set; } = [];
        public Dictionary<long, List<long>> Theatre { get; set; } = [];
        public bool ShareOpen { get; set; } = true;
        public string Picture { get; set; } = "";
        public long LikeDay { get; set; }
        public List<string> LikedToday { get; set; } = [];
        public Dictionary<long, List<uint>> IdolAttr { get; set; } = [];
        public Dictionary<uint, long> CampPos { get; set; } = [];
        public List<long> Attack { get; set; } = [];
        public List<long> Defend { get; set; } = [];
        public int TrainTimes { get; set; }
        public Dictionary<long, PveRec> Pve { get; set; } = [];
        public List<int> RankGot { get; set; } = [];
        public Dictionary<uint, Dance> Dances { get; set; } = [];
        public Dictionary<uint, List<uint>> Viewed { get; set; } = [];
        public long WeekKey { get; set; }
        public Week ThisWeek { get; set; } = new();
        public Week LastWeek { get; set; } = new();
        public Battle? Pending1 { get; set; }
    }

    // ---------- config helpers ----------
    private static int I(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetInt64() : 0;
    private static List<long> L(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList() : [];
    private static List<(long, long)> Pairs(JsonElement e) => e.ValueKind == JsonValueKind.Array
        ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Array && x.GetArrayLength() >= 2).Select(x => (x[0].GetInt64(), x[1].GetInt64())).ToList() : [];
    private static int Signed(long v) => unchecked((int)(uint)v);
    private JsonElement? Get(string table, long id) => Cfg.GetProperty(table).TryGetProperty(id.ToString(), out var v) ? v : null;
    private long Arch(long hero) => Cfg.GetProperty("hero_to_archive").TryGetProperty(hero.ToString(), out var a) ? a.GetInt64() : hero;
    private IEnumerable<(long Arch, long Primary)> Archives => Cfg.GetProperty("archives").EnumerateObject().Select(p => (long.Parse(p.Name), p.Value.GetInt64())).OrderBy(x => x.Item1);
    private int RoomType(int id) => Get("rooms", id) is { } r ? I(r, "type") : 0;
    private static double LevelMult(long exp) => Levels.First(l => exp >= l.Exp).Mult;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static long Day => GameClock.TodayStartUnix;

    // ---------- load / save ----------
    public async Task<State> LoadAsync(long uid, CancellationToken ct)
    {
        var s = await accounts.GetJsonAsync<State>(uid, Kind, ct) ?? new State();
        var now = Now;
        foreach (var (arch, _) in Archives) s.Heroes.TryAdd(arch, new Hero { Ts = now });
        if (!s.Init)
        {
            s.Init = true;
            s.Dorms.TryAdd(5, new Dorm { Layout = EmptyLayout() });
            foreach (var p in Cfg.GetProperty("canteen_furniture_ids").EnumerateObject())
                if (Get("canteen_furniture", p.Value.GetInt64()) is { } fc)
                    s.CanteenFur[int.Parse(p.Name)] = fc.GetProperty("cost_material")[0].ValueKind == JsonValueKind.Object ? 1 : 0;
            for (var i = 0; i < DefaultTasks.Length; i++) s.Entrusts.Add(new Entrust { Pos = i + 1, Task = DefaultTasks[i] });
            s.LastReceive = now;
            s.LastSettle = now;
            var hall = Pairs(Get("rooms", 5)!.Value.GetProperty("default_item"));
            if (hall.Count > 0 && await accounts.TryClaimAsync(uid, "backhome_room", "5", ct)) await accounts.GrantItemsAsync(uid, hall, ct);
        }
        foreach (var h in s.Heroes.Values.Where(h => h.FeedDay != Day)) { h.FeedTimes = 0; h.FeedDay = Day; }
        if (s.LikeDay != Day) { s.LikeDay = Day; s.LikedToday.Clear(); }
        return s;
    }

    private Task SaveAsync(long uid, State s, CancellationToken ct) => accounts.SetJsonAsync(uid, Kind, s, ct);

    private static List<FurList> EmptyLayout() => Enumerable.Range(1, 5).Select(t => new FurList { Type = (uint)t }).ToList();

    private async Task<HashSet<long>> OwnedAsync(long uid, CancellationToken ct) => (await accounts.GetOwnedHeroIdsAsync(uid, ct)).Select(Arch).ToHashSet();

    // ---------- simulation ----------
    private bool Working(State s, long arch, long now) =>
        s.Careers.Values.Any(h => h != 0 && Arch(h) == arch) || s.Entrusts.Any(e => e.Start > 0 && now < e.Start + e.Duration * 60L && e.Heroes.Select(Arch).Contains(arch));

    private double RecoveryPer600(State s, long arch, long now)
    {
        if (Working(s, arch, now)) return 0;
        var room = s.Dorms.Where(d => d.Value.Archives.Contains(arch)).Select(d => d.Key).ToList();
        if (room.FirstOrDefault(r => RoomType(r) == 3) is var priv and > 0) return LevelMult(s.Dorms[priv].Exp);
        if (room.Count > 0 || s.CampPos.Values.Select(Arch).Contains(arch)) return 1.0;
        return 0.25;
    }

    /// <summary>Natural fatigue recovery since each hero's last update (account_db.calculate_dorm_hero_fatigue_recovery).</summary>
    private void Recover(State s, long now)
    {
        foreach (var (arch, h) in s.Heroes)
        {
            var dt = Math.Max(0, now - h.Ts);
            if (h.Fatigue < NaturalCap && dt > 0) h.Fatigue = Math.Min(NaturalCap, h.Fatigue + dt * RecoveryPer600(s, arch, now) / 600.0);
            h.Ts = now;
        }
    }

    private sealed record Effects(Dictionary<long, int> Speed, Dictionary<long, int> Price, Dictionary<long, int> Reduce);

    private Effects SkillEffects(State s)
    {
        var e = new Effects([], [], []);
        void Apply(long skillId, int job, long arch)
        {
            if (Get("hero_skills", skillId) is not { } sk) return;
            var cond = L(sk.GetProperty("condition"));
            if (cond.Count >= 2 && cond[0] == 1 && cond[1] != job) return;
            var p = L(sk.GetProperty("param"));
            switch (I(sk, "type"))
            {
                case 1 when p.Count >= 2: e.Speed[p[0]] = e.Speed.GetValueOrDefault(p[0]) + Signed(p[1]); break;
                case 2 when p.Count >= 2: e.Price[p[0]] = e.Price.GetValueOrDefault(p[0]) + Signed(p[1]); break;
                case 3 when p.Count >= 1 && arch != 0: e.Reduce[arch] = e.Reduce.GetValueOrDefault(arch) + Signed(p[0]); break;
            }
        }
        foreach (var (job, hero) in s.Careers.Where(c => c.Value != 0))
        {
            var hc = Get("heroes", hero) ?? Get("heroes", Arch(hero));
            if (hc is { } c && c.TryGetProperty("skill_list", out var sl)) foreach (var id in L(sl)) Apply(id, job, Arch(hero));
        }
        foreach (var (entity, level) in s.CanteenFur.Where(f => f.Value > 0))
            if (Cfg.GetProperty("canteen_furniture_ids").TryGetProperty(entity.ToString(), out var tid) && Get("canteen_furniture", tid.GetInt64()) is { } fc)
            {
                var skills = fc.GetProperty("skill");
                if (level - 1 < skills.GetArrayLength()) foreach (var id in L(skills[level - 1])) Apply(id, 0, 0);
            }
        return e;
    }

    /// <summary>Lazy canteen business (account_db._calculate_canteen_dishes): staff fatigue bounds the working time.</summary>
    /// <summary>Current week's counters, rolling the previous week over at Monday 05:00 (game clock).</summary>
    private static Week W(State s)
    {
        var day = DateOnly.FromDateTime(GameClock.Today);
        var key = day.AddDays(-(((int)day.DayOfWeek + 6) % 7)).DayNumber;
        if (s.WeekKey != key)
        {
            s.LastWeek = s.WeekKey == key - 7 ? s.ThisWeek : new Week();
            s.ThisWeek = new Week();
            s.WeekKey = key;
        }
        return s.ThisWeek;
    }

    private static void AddFatigue(State s, long arch, double spent)
    {
        if (spent <= 0) return;
        var w = W(s);
        w.Fatigue[arch] = w.Fatigue.GetValueOrDefault(arch) + spent;
    }

    private void Settle(State s, long now)
    {
        Recover(s, now);
        var duty = s.Careers.Where(c => c.Value != 0).Select(c => Arch(c.Value)).Distinct().ToList();
        var eff = SkillEffects(s);
        double Cost(long arch) => MealFatigue * (100.0 - Math.Min(90, eff.Reduce.GetValueOrDefault(arch))) / 100.0;
        var maxWork = duty.Count > 0 ? duty.Min(a => s.Heroes.TryGetValue(a, out var h) ? h.Fatigue / Cost(a) * GuestSeconds : 0) : 0;
        long spent = 0;
        foreach (var (food, d) in s.Dishes)
        {
            if (Get("food", food) is not { } fc || d.Sold >= d.SellNum) { d.Ts = now; continue; }
            var type = I(fc, "cook_type");
            var time = Math.Max(1, (int)Math.Ceiling(I(fc, "cost_time") * (100.0 - Math.Min(90, eff.Speed.GetValueOrDefault(type) + eff.Speed.GetValueOrDefault(0))) / 100.0));
            var price = Math.Max(1, (int)Math.Floor(I(fc, "sell") * (100.0 + eff.Price.GetValueOrDefault(type) + eff.Price.GetValueOrDefault(0)) / 100.0));
            var elapsed = (long)Math.Max(0, Math.Min(now - d.Ts, maxWork));
            var inc = (int)Math.Min(Math.Min(d.SellNum - d.Sold, elapsed / time), Math.Max(0, EarningsCap - s.Pending) / price);
            if (inc <= 0) { if (maxWork <= 0) d.Ts = now; continue; }
            d.Sold += inc;
            d.Earn += inc * price;
            s.Pending += inc * price;
            s.FoodSold[food] = s.FoodSold.GetValueOrDefault(food) + inc;
            W(s).Food[food] = W(s).Food.GetValueOrDefault(food) + inc;
            d.Ts += inc * (long)time;
            spent = Math.Max(spent, inc * (long)time);
        }
        foreach (var a in duty.Where(s.Heroes.ContainsKey))
        {
            var before = s.Heroes[a].Fatigue;
            s.Heroes[a].Fatigue = Math.Max(0, before - spent * Cost(a) / GuestSeconds);
            AddFatigue(s, a, before - s.Heroes[a].Fatigue);
        }
        s.LastSettle = now;
    }

    // ---------- payloads ----------
    private static uint F(double v) => (uint)Math.Max(0, Math.Floor(v + 1e-9));

    private static object Entrust1(Entrust e) => new Dictionary<string, object?>
    {
        ["pos"] = (uint)e.Pos, ["id"] = (uint)e.Task, ["hero_list"] = e.Heroes.Select(x => (uint)x).ToArray(), ["tags"] = Array.Empty<object>(),
        ["num_max"] = 3u, ["refresh_times"] = (uint)e.Refresh, ["start_time"] = (uint)e.Start, ["duration"] = (uint)e.Duration
    };

    private static object[] Dishes(State s) => s.Dishes.OrderBy(d => d.Key).Select(d => (object)new Dictionary<string, object?>
    { ["food_id"] = (uint)d.Key, ["sell_num"] = (uint)d.Value.SellNum, ["sold_num"] = (uint)d.Value.Sold, ["sell_earnings"] = (uint)d.Value.Earn }).ToArray();

    private static object[] Careers(State s) => s.Careers.Where(c => c.Value != 0).OrderBy(c => c.Key).Select(c => (object)new Dictionary<string, object?> { ["type"] = (uint)c.Key, ["hero_id"] = (uint)c.Value }).ToArray();

    private static object[] Items(IEnumerable<(long, long)> items) => HandlerHelpers.ToItemNetRecList(items);

    private static object Layout(List<FurList> lists, uint temp = 0) => new Dictionary<string, object?> { ["temp_id"] = temp, ["furniture_pos_list"] = PosLists(lists) };

    private static object[] PosLists(List<FurList> lists) => lists.Select(l => (object)new Dictionary<string, object?>
    {
        ["type"] = l.Type, ["default_suit_id"] = l.Suit,
        ["furniture_pos"] = l.Pos.Select(p => (object)new Dictionary<string, object?> { ["furniture_id"] = p.F, ["x"] = p.X, ["y"] = p.Y, ["rotation"] = p.R }).ToArray()
    }).ToArray();

    private async Task<(object[] Ingredients, object[] Furniture)> StockAsync(long uid, State s, CancellationToken ct)
    {
        var inv = await accounts.GetInventoryAsync(uid, ct);
        var ing = inv.Where(i => i.Id is >= 51000 and < 52000 && i.Count > 0).Select(i => (i.Id, i.Count));
        var fur = inv.Where(i => i.Id is >= 950000 and < 970000 && i.Count > 0).Select(i => (object)new Dictionary<string, object?>
        { ["furniture_id"] = (uint)i.Id, ["num"] = (uint)i.Count, ["give_num"] = (uint)s.Given.GetValueOrDefault(i.Id) }).ToArray();
        return (Items(ing), fur);
    }

    private object[] Fatigues(State s, IEnumerable<long>? only = null) =>
        (only ?? s.Heroes.Keys).Where(s.Heroes.ContainsKey).OrderBy(a => a).Select(a => (object)new Dictionary<string, object?> { ["archives_id"] = (uint)a, ["fatigue"] = F(s.Heroes[a].Fatigue) }).ToArray();

    private async Task<Dictionary<string, object?>> ConciseAsync(long uid, State s, CancellationToken ct)
    {
        var owned = await OwnedAsync(uid, ct);
        var (ing, fur) = await StockAsync(uid, s, ct);
        return new()
        {
            ["canteens"] = new object[] { new Dictionary<string, object?> { ["id"] = 4u, ["entrust"] = s.Entrusts.OrderBy(e => e.Pos).Select(Entrust1).ToArray(), ["signature_dish"] = Dishes(s), ["careers"] = Careers(s), ["accruing_earnings"] = (uint)s.Accruing } },
            ["canteen_furnitures"] = s.CanteenFur.Where(f => f.Value > 0).OrderBy(f => f.Key).Select(f => (object)new Dictionary<string, object?> { ["uid"] = (uint)f.Key, ["level"] = (uint)f.Value }).ToArray(),
            ["backhome_hero"] = Archives.Select(a => (object)new Dictionary<string, object?>
            {
                ["archives_id"] = (uint)a.Arch, ["hero_id"] = (uint)a.Primary, ["fatigue"] = F(s.Heroes[a.Arch].Fatigue), ["feed_times"] = (uint)s.Heroes[a.Arch].FeedTimes,
                ["total_feed_times"] = (uint)s.Heroes[a.Arch].TotalFeed, ["is_lock"] = owned.Contains(a.Arch) ? (uint)s.Heroes[a.Arch].Lock : 1u
            }).ToArray(),
            ["last_fatigue_update_time"] = (uint)Now,
            ["dorm"] = s.Dorms.OrderBy(d => d.Key).Select(d => (object)new Dictionary<string, object?>
            {
                ["id"] = (uint)d.Key, ["pos_id"] = (uint)d.Value.Pos, ["archives_id"] = d.Value.Archives.Select(x => (uint)x).ToArray(), ["exp"] = (uint)d.Value.Exp,
                ["give_furnitures"] = Items(d.Value.Gifts.Select(g => (g.Key, g.Value))), ["liked_num"] = (uint)d.Value.Liked
            }).ToArray(),
            ["ingredients"] = ing,
            ["theatrical_list"] = s.Theatre.OrderBy(t => t.Key).Select(t => (object)new Dictionary<string, object?> { ["id"] = (uint)t.Key, ["timestamp_list"] = t.Value.Select(x => (uint)x).ToArray() }).ToArray(),
            ["hero_setting"] = s.Heroes.Where(h => h.Value.Skin != 0).OrderBy(h => h.Key).Select(h => (object)new Dictionary<string, object?> { ["hero_id"] = (uint)h.Key, ["skin_id"] = (uint)h.Value.Skin }).ToArray(),
            ["daily_game_currency_num"] = await accounts.HasClaimAsync(uid, "backhome_rhythm", Day.ToString(), ct) ? 1000u : 0u,
            ["exhibition_id"] = 5u,
            ["furnitures"] = fur,
            ["backhome_suit_id_list"] = Array.Empty<uint>(),
            ["share_is_open"] = s.ShareOpen,
            ["received_be_visited_gift_num"] = 0u
        };
    }

    private async Task<Dictionary<string, object?>> DetailAsync(long uid, State s, CancellationToken ct)
    {
        var (ing, fur) = await StockAsync(uid, s, ct);
        var now = Now;
        return new()
        {
            ["result"] = 0u,
            ["canteens"] = new object[] { new Dictionary<string, object?>
            {
                ["id"] = 4u, ["entrust"] = s.Entrusts.OrderBy(e => e.Pos).Select(Entrust1).ToArray(), ["accruing_earnings"] = (uint)s.Accruing,
                ["careers"] = Careers(s), ["signature_dish"] = Dishes(s),
                ["business"] = new Dictionary<string, object?> { ["earnings"] = (uint)s.Pending, ["last_earnings_update_time"] = (uint)s.LastSettle, ["last_receive_earnings_time"] = (uint)s.LastReceive },
                ["attractive"] = new Dictionary<string, object?> { ["dynamic"] = 0u, ["dynamic_update_time"] = (uint)now }
            } },
            ["ingredients"] = ing,
            ["food"] = Cfg.GetProperty("food").EnumerateObject().Select(p => long.Parse(p.Name)).OrderBy(x => x)
                .Select(id => (object)new Dictionary<string, object?> { ["id"] = (uint)id, ["proficiency"] = 100u, ["num"] = (uint)s.FoodSold.GetValueOrDefault(id) }).ToArray(),
            ["dorms"] = s.Dorms.OrderBy(d => d.Key).Select(d => (object)new Dictionary<string, object?>
            {
                ["id"] = (uint)d.Key, ["pos_id"] = (uint)d.Value.Pos, ["archives_id"] = d.Value.Archives.Select(x => (uint)x).ToArray(), ["layout"] = Layout(d.Value.Layout),
                ["liked_num"] = (uint)d.Value.Liked, ["give_furnitures"] = Items(d.Value.Gifts.Select(g => (g.Key, g.Value))), ["exp"] = (uint)d.Value.Exp
            }).ToArray(),
            ["exhibition_id"] = 5u,
            ["template"] = s.Templates.OrderBy(t => t.Key).Select(t => (object)new Dictionary<string, object?>
            { ["id"] = t.Key, ["type"] = t.Value.Type, ["name"] = t.Value.Name, ["furniture_pos_list"] = PosLists(t.Value.Lists), ["pos"] = t.Value.Pos }).ToArray(),
            ["furnitures"] = fur
        };
    }

    private object IdolOverview(State s) => new Dictionary<string, object?>
    {
        ["attack_hero_id_list"] = s.Attack.Select(x => (uint)x).ToArray(),
        ["defend_hero_id_list"] = s.Defend.Select(x => (uint)x).ToArray(),
        ["hero_list"] = s.IdolAttr.OrderBy(h => h.Key).Select(h => (object)new Dictionary<string, object?> { ["hero_id"] = (uint)h.Key, ["attribute_list"] = h.Value.ToArray() }).ToArray(),
        ["exercise_times_info"] = new Dictionary<string, object?> { ["use_times"] = (uint)s.TrainTimes, ["camp_list"] = Array.Empty<uint>() },
        ["pvp_stage_info"] = PvpStage(),
        ["hero_pos_list"] = s.CampPos.OrderBy(p => p.Key).Select(p => (object)new Dictionary<string, object?> { ["hero_id"] = (uint)p.Value, ["pos"] = p.Key }).ToArray()
    };

    private object PveChapters(State s) => new Dictionary<string, object?>
    {
        ["chapter_list"] = new object[] { new Dictionary<string, object?>
        {
            ["chapter_id"] = 1u,
            ["stage_list"] = Cfg.GetProperty("idol_pve").EnumerateObject().Select(p => long.Parse(p.Name)).OrderBy(x => x).Select(id => (object)new Dictionary<string, object?>
            { ["stage_id"] = (uint)id, ["score"] = (uint)(s.Pve.GetValueOrDefault(id)?.Score ?? 0), ["is_clear"] = s.Pve.GetValueOrDefault(id)?.Clear ?? false }).ToArray()
        } }
    };

    private object Rank(State s, long point) => new Dictionary<string, object?> { ["get_id_list"] = s.RankGot.OrderBy(x => x).Select(x => (uint)x).ToArray(), ["point"] = (uint)point };

    private object Sequences(State s) => new Dictionary<string, object?>
    {
        ["sequence_list"] = s.Dances.OrderBy(d => d.Key).Select(d => (object)new Dictionary<string, object?>
        { ["pos"] = d.Key, ["base_sequence"] = new Dictionary<string, object?> { ["music_id"] = d.Value.Music, ["scene_id"] = d.Value.Scene, ["action_id_list"] = d.Value.Actions.ToArray() } }).ToArray(),
        ["shared_sequence_list"] = Array.Empty<object>()
    };

    private long RankItem => L(Cfg.GetProperty("idol_rank").GetProperty("1").GetProperty("item_id"))[0];

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        Settle(s, Now);
        await SaveAsync(uid, s, ct);
        return
        [
            (58001, codec.Encode("sc_58001", await ConciseAsync(uid, s, ct))),
            (58027, codec.Encode("sc_58027", new Dictionary<string, object?> { ["fatigue_list"] = Fatigues(s) })),
            (58169, codec.Encode("sc_58169", (IReadOnlyDictionary<string, object?>)IdolOverview(s))),
            (58189, codec.Encode("sc_58189", (IReadOnlyDictionary<string, object?>)PveChapters(s))),
            (58191, codec.Encode("sc_58191", (IReadOnlyDictionary<string, object?>)Rank(s, await accounts.GetItemCountAsync(uid, RankItem, ct)))),
            (58195, codec.Encode("sc_58195", (IReadOnlyDictionary<string, object?>)Sequences(s))),
            (58173, codec.Encode("sc_58173", new Dictionary<string, object?> { ["pvp_stage_info"] = PvpStage() })),
            (58203, codec.Encode("sc_58203", new Dictionary<string, object?>
            {
                ["collection_list"] = s.Viewed.OrderBy(v => v.Key).Select(v => (object)new Dictionary<string, object?> { ["type"] = v.Key, ["viewed_id_list"] = v.Value.ToArray() }).ToArray()
            })),
            (58063, codec.Encode("sc_58063", new Dictionary<string, object?> { ["content_list"] = Notices(s) }))
        ];
    }

    /// <summary>
    /// 偶像竞技 PVP stage of the day: IdolTraineePvpBattleCfg type-1 stages rotate daily at 05:00 (battle_system_cfg.json).
    /// </summary>
    private Dictionary<string, object?> PvpStage()
    {
        var stages = (config.Get("battle_system_cfg.json") is { } c ? c.GetProperty("idol_pvp_stages").EnumerateArray().Select(x => x.GetUInt32()).ToList() : []);
        var day = (long)Math.Floor((GameClock.Now - TimeSpan.FromHours(5)).ToUnixTimeSeconds() / 86400.0);
        return new()
        {
            ["stage_id"] = stages.Count == 0 ? 0u : stages[(int)(day % stages.Count)],
            ["refresh_timestamp"] = (uint)((day + 1) * 86400 + 5 * 3600)
        };
    }

    /// <summary>
    /// Dorm information board (DormInfomationData, BackhomeContentNoticeCfg): last week's canteen board 1001 (most tired
    /// hero, best-selling dish, dishes sold, entrusts done, coins earned, most dispatched hero, dispatches) and idol board
    /// 2001 (most trained hero and times, most trained attribute, PVP battles and wins), then the update notices
    /// 3001-6001 with their configured params.
    /// </summary>
    private object[] Notices(State s)
    {
        W(s);
        var w = s.LastWeek;
        static int Top<TK, TV>(Dictionary<TK, TV> d) where TK : notnull where TV : IComparable<TV> =>
            d.Count == 0 ? 0 : Convert.ToInt32(d.OrderByDescending(x => x.Value).First().Key);
        var list = new List<object>
        {
            new Dictionary<string, object?> { ["id"] = 1001u, ["content_params"] = new object[]
            {
                Top(w.Fatigue), Top(w.Food), (int)Math.Min(int.MaxValue, w.Food.Values.Sum()), w.Entrusts,
                (int)Math.Min(int.MaxValue, w.Coins), Top(w.Join), w.Join.Values.Sum()
            } },
            new Dictionary<string, object?> { ["id"] = 2001u, ["content_params"] = new object[]
            {
                Top(w.Train), w.Train.Count == 0 ? 0 : w.Train.Values.Max(), Top(w.Attr), w.Pvp, w.PvpWin
            } }
        };
        if (config.Get("battle_system_cfg.json") is { } c)
            list.AddRange(c.GetProperty("dorm_notices").EnumerateObject().OrderBy(n => uint.Parse(n.Name)).Select(n => (object)new Dictionary<string, object?>
            {
                ["id"] = uint.Parse(n.Name), ["content_params"] = n.Value.EnumerateArray().Select(x => (object)x.GetInt32()).ToArray()
            }));
        return list.ToArray();
    }

    // ---------- request helpers ----------
    private static uint U(object? o) { try { return o is null ? 0 : unchecked((uint)Convert.ToInt64(o)); } catch { return 0; } }
    private static int Int(object? o) { try { return o is null ? 0 : unchecked((int)Convert.ToInt64(o)); } catch { return 0; } }
    private static IReadOnlyDictionary<string, object?>? Msg(IReadOnlyDictionary<string, object?> b, string k) => b.TryGetValue(k, out var v) ? v as IReadOnlyDictionary<string, object?> : null;
    private static IEnumerable<IReadOnlyDictionary<string, object?>> Msgs(IReadOnlyDictionary<string, object?>? b, string k) =>
        b is not null && b.TryGetValue(k, out var v) && v is System.Collections.IEnumerable e && v is not string ? e.OfType<IReadOnlyDictionary<string, object?>>() : [];
    private static uint G(IReadOnlyDictionary<string, object?> b, string k) => b.TryGetValue(k, out var v) ? U(v) : 0;

    private static List<FurList> ReadLists(IEnumerable<IReadOnlyDictionary<string, object?>> lists) => lists.Select(l => new FurList
    {
        Type = G(l, "type"), Suit = G(l, "default_suit_id"),
        Pos = Msgs(l, "furniture_pos").Select(p => new FurPos { F = G(p, "furniture_id"), X = Int(p.GetValueOrDefault("x")), Y = Int(p.GetValueOrDefault("y")), R = Int(p.GetValueOrDefault("rotation")) }).ToList()
    }).ToList();

    /// <summary>Trims a layout so that no furniture is placed more times than owned across all rooms (validate_and_sanitize_dorm_layout).</summary>
    private static List<FurList> Trim(State s, int room, List<FurList> lists, IReadOnlyDictionary<long, long> stock)
    {
        var used = new Dictionary<long, long>();
        foreach (var (id, d) in s.Dorms.Where(d => d.Key != room))
            foreach (var p in d.Layout.SelectMany(l => l.Pos).Where(p => p.F != 0)) used[p.F] = used.GetValueOrDefault(p.F) + 1;
        foreach (var l in lists)
            l.Pos = l.Pos.Where(p =>
            {
                if (p.F == 0) return true;
                var free = stock.GetValueOrDefault(p.F) - s.Given.GetValueOrDefault(p.F) - used.GetValueOrDefault(p.F);
                if (free <= 0) return false;
                used[p.F] = used.GetValueOrDefault(p.F) + 1;
                return true;
            }).ToList();
        return lists;
    }

    private long RandomTask(int level)
    {
        var ids = Cfg.GetProperty("tasks").EnumerateObject().Where(p => I(p.Value, "task_level") == level).Select(p => long.Parse(p.Name)).ToList();
        return ids.Count == 0 ? DefaultTasks[0] : ids[Random.Shared.Next(ids.Count)];
    }

    private List<uint> IdolAttributes(State s, long hero)
    {
        if (s.IdolAttr.TryGetValue(hero, out var a)) return a;
        var cfg = (Get("heroes", hero) ?? Get("heroes", Arch(hero)))!.Value;
        a = cfg.GetProperty("idol_base_attribute").EnumerateArray().Select(x => (uint)x[0].GetInt64()).ToList();
        s.IdolAttr[hero] = a;
        return a;
    }

    private bool IdolUsable(long hero) => (Get("heroes", hero) ?? Get("heroes", Arch(hero))) is { } c && I(c, "idol_usable") == 1 && c.GetProperty("idol_base_attribute").ValueKind == JsonValueKind.Array;

    // ---------- operations ----------
    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var srv = (ushort)(r.ServerIndex + 1);
        var b = r.Body;
        var now = Now;
        var s = await LoadAsync(uid, ct);
        Settle(s, now);
        var owned = await OwnedAsync(uid, ct);
        var reply = new Dictionary<string, object?> { ["result"] = 0u };
        var costs = new List<(long, long)>();
        var grants = new List<(long, long)>();
        var pushes = new List<GameResponse>();
        Func<TaskSystem.State, List<object>>? task = null;
        long trustArch = 0, trustExp = 0;
        bool save = true;
        try
        {
            switch (cmd)
            {
                case 58002:
                    reply = await DetailAsync(uid, s, ct);
                    break;

                // ----- dorm rooms -----
                case 58130:
                    {
                        var room = (int)G(b, "architecture_id");
                        var pos = (int)G(b, "pos_id");
                        Require(Get("rooms", room) is { } rc && I(rc, "type") == 3 && !s.Dorms.ContainsKey(room), "Room cannot be unlocked");
                        Require(pos > 0 && s.Dorms.Values.All(d => d.Pos != pos), "Door position taken");
                        var rcfg = Get("rooms", room)!.Value;
                        costs.AddRange(Pairs(rcfg.GetProperty("unlock_cost")));
                        grants.AddRange(Pairs(rcfg.GetProperty("default_item")));
                        s.Dorms[room] = new Dorm { Pos = pos, Layout = EmptyLayout() };
                        break;
                    }
                case 58132:
                    {
                        var room = (int)G(b, "architecture_id");
                        var heroes = HandlerHelpers.ReadIdList(b, "hero_id").Select(Arch).Distinct().ToList();
                        Require(s.Dorms.TryGetValue(room, out var d) && heroes.Count > 0 && heroes.All(owned.Contains), "Bad deploy");
                        if (RoomType(room) == 3)
                        {
                            var h = heroes[0];
                            Need(heroes.Count == 1, 2);
                            Need(d!.Archives.Count == 0 || d.Archives[0] == h, 7130);
                            Need(!s.Dorms.Any(o => o.Key != room && RoomType(o.Key) == 3 && o.Value.Archives.Contains(h)), 210099);
                            d.Archives = [h];
                        }
                        else
                        {
                            Require(RoomType(room) == 2, "Not a living room");
                            foreach (var h in heroes.Where(x => !d!.Archives.Contains(x))) d!.Archives.Add(h);
                        }
                        break;
                    }
                case 58134:
                    {
                        var room = (int)G(b, "architecture_id");
                        var h = Arch(G(b, "hero_id"));
                        Require(s.Dorms.TryGetValue(room, out var d) && d.Archives.Remove(h), "Hero not in room");
                        break;
                    }
                case 58146:
                    {
                        var list = Msgs(b, "dorm_pos_list").Select(x => ((int)G(x, "architecture_id"), (int)G(x, "pos_id"))).ToList();
                        Require(list.Count > 0 && list.All(x => s.Dorms.ContainsKey(x.Item1) && x.Item2 > 0), "Bad positions");
                        foreach (var (room, pos) in list) s.Dorms[room].Pos = pos;
                        var taken = s.Dorms.Where(d => RoomType(d.Key) == 3).Select(d => d.Value.Pos).ToList();
                        Require(taken.Count == taken.Distinct().Count(), "Duplicate door positions");
                        break;
                    }
                case 58010:
                    {
                        var room = (int)G(b, "architecture_id");
                        Require(s.Dorms.ContainsKey(room) || room == 4, "Room locked");
                        var layout = Msg(b, "furniture_layout");
                        var lists = ReadLists(Msgs(layout, "furniture_pos_list"));
                        var temp = layout is null ? 0u : G(layout, "temp_id");
                        if (lists.Count == 0 && temp != 0 && s.Templates.TryGetValue(temp, out var t)) lists = JsonSerializer.Deserialize<List<FurList>>(JsonSerializer.Serialize(t.Lists))!;
                        var stock = (await accounts.GetInventoryAsync(uid, ct)).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.Sum(i => i.Count));
                        if (s.Dorms.TryGetValue(room, out var d)) d.Layout = Trim(s, room, lists, stock);
                        break;
                    }
                case 58136:
                    {
                        var h = Arch(G(b, "hero_id"));
                        Require(owned.Contains(h), "Hero not owned");
                        var room = s.Dorms.Where(d => RoomType(d.Key) == 3 && d.Value.Archives.Contains(h)).Select(d => d.Key).ToList();
                        Require(room.Count == 1, "Hero needs its own room");
                        var d = s.Dorms[room[0]];
                        var items = HandlerHelpers.ReadItemList(b, "furniture").GroupBy(x => x.ItemId).Select(g => (g.Key, g.Sum(x => x.Count))).ToList();
                        Require(items.Count > 0, "Nothing to give");
                        foreach (var (fid, num) in items)
                        {
                            Require(Cfg.GetProperty("gifts").TryGetProperty(fid.ToString(), out var lim), "Furniture cannot be given");
                            Require(d.Gifts.GetValueOrDefault(fid) + num <= lim.GetInt64(), "Gift limit reached");
                            Require(await accounts.GetItemCountAsync(uid, fid, ct) - s.Given.GetValueOrDefault(fid) >= num, "Not enough furniture");
                        }
                        foreach (var (fid, num) in items) { d.Gifts[fid] = d.Gifts.GetValueOrDefault(fid) + num; s.Given[fid] = s.Given.GetValueOrDefault(fid) + num; }
                        var total = items.Sum(x => x.Item2);
                        d.Exp += 1000 * total;
                        trustArch = h; trustExp = 500 * total;
                        break;
                    }
                case 58138:
                    {
                        var type = G(b, "type");
                        var hero = G(b, "hero_id");
                        var targets = type == 1 || hero == 0 ? owned.Where(s.Heroes.ContainsKey).ToList() : [Arch(hero)];
                        var fed = new List<long>();
                        foreach (var a in targets.Where(a => owned.Contains(a) && s.Heroes.ContainsKey(a)))
                        {
                            var h = s.Heroes[a];
                            if (h.FeedTimes >= 1 || h.Fatigue > NaturalCap) continue;
                            h.Fatigue = Math.Min(FedCap, h.Fatigue + 20);
                            h.FeedTimes++; h.TotalFeed++;
                            fed.Add(a);
                        }
                        Require(fed.Count > 0, "Nobody can eat now");
                        reply["fatigue_list"] = Fatigues(s, fed);
                        foreach (var a in fed) await accounts.AddHeroArchiveExpAsync(uid, a, 50, ct);
                        task = t => tasks.Trigger(t, 2003, fed.Count);
                        break;
                    }
                case 58126:
                    {
                        var h = Arch(G(b, "hero_id"));
                        Require(owned.Contains(h), "Hero not owned");
                        s.Heroes[h].Skin = G(b, "skin_id");
                        break;
                    }
                case 58218:
                    {
                        var h = Arch(G(b, "hero_id"));
                        var type = (int)G(b, "type");
                        Require(owned.Contains(h) && type is 0 or 1, "Bad lock");
                        if (type == 1) Need(!Working(s, h, now) && !s.Careers.Values.Any(x => x != 0 && Arch(x) == h), 7130);
                        s.Heroes[h].Lock = type;
                        break;
                    }
                case 58052:
                    {
                        var target = (long)HandlerHelpers.ReadULong(b, "user_id");
                        var room = (int)G(b, "architecture_id");
                        Require(target != uid && target > 0, "Cannot like yourself");
                        var key = $"{target}:{room}";
                        Require(!s.LikedToday.Contains(key), "Already liked today");
                        var other = await accounts.GetJsonAsync<State>(target, Kind, ct);
                        Require(other is not null && other.Dorms.TryGetValue(room, out var od), "Room not found");
                        other!.Dorms[room].Liked++;
                        await accounts.SetJsonAsync(target, Kind, other, ct);
                        s.LikedToday.Add(key);
                        break;
                    }
                case 58054:
                    {
                        var d = s.Dorms.GetValueOrDefault((int)G(b, "architecture_id"));
                        reply["liked_num"] = (uint)(d?.Liked ?? 0);
                        reply["be_visited_num"] = (uint)(d?.Visited ?? 0);
                        save = false;
                        break;
                    }
                case 58050:
                    {
                        var target = (long)HandlerHelpers.ReadULong(b, "user_id");
                        var other = await accounts.GetJsonAsync<State>(target, Kind, ct);
                        var room = other?.Dorms.Where(d => RoomType(d.Key) == 3).OrderBy(d => d.Key).Select(d => d.Key).FirstOrDefault() ?? 0;
                        Require(other is not null && room != 0, "Nothing to visit");
                        var d = other!.Dorms[room];
                        d.Visited++;
                        await accounts.SetJsonAsync(target, Kind, other, ct);
                        reply["furniture_pos_list"] = PosLists(d.Layout);
                        reply["layout_uid"] = 0u;
                        reply["architecture_id"] = (uint)room;
                        reply["skin_id_list"] = d.Archives.Select(a => (uint)(other.Heroes.GetValueOrDefault(a)?.Skin ?? 0)).ToArray();
                        reply["liked_num"] = (uint)d.Liked;
                        reply["own_today_liked_num"] = (uint)s.LikedToday.Count;
                        reply["visited_num"] = (uint)d.Visited;
                        reply["visited_user_list"] = Array.Empty<object>();
                        save = false;
                        break;
                    }
                case 58056: save = false; break;
                case 58058: reply["visited_user_list"] = Array.Empty<object>(); reply["is_have_gift"] = false; save = false; break;
                case 58060: reply["be_visited_reward_list"] = Array.Empty<object>(); save = false; break;
                case 58148: reply["exhibition_brief"] = Array.Empty<object>(); save = false; break;
                case 58152:
                    Require(s.Dorms.ContainsKey((int)G(b, "architecture_id")), "Room locked");
                    s.Picture = HandlerHelpers.ReadString(b, "picture_link");
                    break;
                case 58158:
                    s.ShareOpen = b.TryGetValue("is_open", out var open) && open is true;
                    break;

                // ----- templates -----
                case 58040:
                    {
                        var id = G(b, "id");
                        var name = HandlerHelpers.ReadString(b, "name");
                        Require(id is > 0 and <= 20 && name.Length is > 0 and <= 32, "Bad template");
                        var lists = ReadLists(Msgs(b, "furniture_pos_list"));
                        var room = (int)G(b, "architecture_id");
                        if (lists.Count == 0 && s.Dorms.TryGetValue(room, out var d)) lists = JsonSerializer.Deserialize<List<FurList>>(JsonSerializer.Serialize(d.Layout))!;
                        s.Templates[id] = new Template { Type = G(b, "type"), Name = name, Pos = G(b, "pos"), Lists = lists };
                        break;
                    }
                case 58142:
                    {
                        var name = HandlerHelpers.ReadString(b, "name");
                        Require(s.Templates.TryGetValue(G(b, "id"), out var t) && name.Length is > 0 and <= 32, "Bad template");
                        t!.Name = name;
                        break;
                    }
                case 58144:
                    Require(s.Templates.Remove(G(b, "id")), "Template not found");
                    break;

                // ----- canteen -----
                case 58104:
                    {
                        var job = (int)G(b, "type");
                        var hero = (long)G(b, "hero_id");
                        Require(job is >= 1 and <= 3, "Bad job");
                        if (hero != 0)
                        {
                            var a = Arch(hero);
                            Require(owned.Contains(a), "Hero not owned");
                            Need(s.Heroes[a].Lock == 0, 210297);
                            Need(!s.Entrusts.Any(e => e.Start > 0 && e.Heroes.Select(Arch).Contains(a)), 7130);
                            foreach (var k in s.Careers.Where(c => c.Key != job && c.Value != 0 && Arch(c.Value) == a).Select(c => c.Key).ToList()) s.Careers[k] = 0;
                        }
                        s.Careers[job] = hero;
                        break;
                    }
                case 58106:
                    {
                        var earn = Math.Min(EarningsCap, s.Pending);
                        reply["earnings"] = (uint)earn;
                        if (earn > 0)
                        {
                            grants.Add((Coin, earn));
                            s.Pending -= earn;
                            s.Accruing += earn;
                            W(s).Coins += earn;
                            task = t => tasks.Trigger(t, 2020);
                        }
                        s.LastReceive = now;
                        break;
                    }
                case 58108:
                    s.Mode = (int)G(b, "cmd");
                    break;
                case 58114:
                    {
                        var food = (long)G(b, "food_id");
                        var sell = (int)G(b, "sell_num");
                        Require(Get("food", food) is { } fc, "Unknown dish");
                        var d = s.Dishes.GetValueOrDefault(food);
                        if (d is not null && d.Sold >= d.SellNum) { s.Dishes.Remove(food); d = null; }
                        var oldLeft = d is null ? 0 : d.SellNum - d.Sold;
                        var sold = d?.Sold ?? 0;
                        var newLeft = sell == 0 ? 0 : sell - sold;
                        Require(newLeft >= 0, "Cannot sell fewer than already sold");
                        var ingredients = Pairs(Get("food", food)!.Value.GetProperty("ingredient_list"));
                        var delta = newLeft - oldLeft;
                        if (delta > 0) costs.AddRange(ingredients.Select(x => (x.Item1, x.Item2 * delta)));
                        else if (delta < 0) grants.AddRange(ingredients.Select(x => (x.Item1, x.Item2 * -delta)));
                        if (sell == 0) s.Dishes.Remove(food);
                        else if (d is null) s.Dishes[food] = new Dish { SellNum = sell, Ts = now };
                        else d.SellNum = sell;
                        break;
                    }
                case 58116:
                    {
                        var id = (long)G(b, "uid");
                        var ids = Cfg.GetProperty("canteen_furniture_ids");
                        var entity = ids.TryGetProperty(id.ToString(), out _) ? (int)id : ids.EnumerateObject().Where(p => p.Value.GetInt64() == id).Select(p => int.Parse(p.Name)).FirstOrDefault();
                        Require(entity != 0 && Get("canteen_furniture", ids.GetProperty(entity.ToString()).GetInt64()) is { } fc, "Unknown canteen furniture");
                        var cost = Get("canteen_furniture", ids.GetProperty(entity.ToString()).GetInt64())!.Value.GetProperty("cost_material");
                        var level = s.CanteenFur.GetValueOrDefault(entity);
                        Need(level < cost.GetArrayLength(), 2);
                        costs.AddRange(Pairs(cost[level]));
                        s.CanteenFur[entity] = level + 1;
                        break;
                    }
                case 58120:
                    {
                        var opers = Msgs(b, "oper_list").ToList();
                        Require(opers.Count > 0, "No guests");
                        var eff = SkillEffects(s);
                        long income = 0;
                        foreach (var o in opers)
                        {
                            var food = (long)G(o, "food_id");
                            Require(Get("food", food) is { } fc, "Unknown dish");
                            var fcfg = Get("food", food)!.Value;
                            var type = I(fcfg, "cook_type");
                            var price = Math.Floor(I(fcfg, "sell") * (100.0 + eff.Price.GetValueOrDefault(type) + eff.Price.GetValueOrDefault(0)) / 100.0);
                            income += (long)(price * Quality.GetValueOrDefault((int)G(o, "quality"), 1.0));
                            costs.AddRange(Pairs(fcfg.GetProperty("ingredient_list")));
                            s.FoodSold[food] = s.FoodSold.GetValueOrDefault(food) + 1;
                            W(s).Food[food] = W(s).Food.GetValueOrDefault(food) + 1;
                        }
                        if (income > 0) { grants.Add((Coin, income)); s.Accruing += income; W(s).Coins += income; }
                        task = t => tasks.Trigger(t, 2008);
                        break;
                    }

                // ----- entrusts -----
                case 58100:
                case 58102:
                    {
                        var orders = cmd == 58100
                            ? Msgs(b, "entrust_list").Select(x => ((int)G(x, "pos"), HandlerHelpers.ReadIdList(x, "hero_list").ToList(), (int)G(x, "duration"))).ToList()
                            : [((int)G(b, "pos"), HandlerHelpers.ReadIdList(b, "hero_list").ToList(), (int)G(b, "duration"))];
                        Require(orders.Count > 0, "Nothing to dispatch");
                        var dispatched = 0;
                        foreach (var (pos, heroes, duration) in orders)
                        {
                            var e = s.Entrusts.FirstOrDefault(x => x.Pos == pos);
                            Require(e is not null, "Entrust slot locked");
                            if (heroes.Count == 0)
                            {
                                Require(cmd == 58102 && e!.Start > 0 && now < e.Start + e.Duration * 60L, "Nothing to recall");
                                e!.Start = 0; e.Heroes = [];
                                continue;
                            }
                            Require(e!.Start == 0, "Entrust already running");
                            var tc = Get("tasks", e.Task)!.Value;
                            var need = L(tc.GetProperty("need"));
                            var archs = heroes.Select(Arch).ToList();
                            Require(archs.Distinct().Count() == archs.Count && archs.Count >= need[0] && archs.Count <= need[^1] && archs.All(owned.Contains), "Bad team");
                            Need(archs.All(a => s.Heroes[a].Lock == 0), 210297);
                            Need(archs.All(a => !Working(s, a, now)), 7130);
                            Require(Pairs(tc.GetProperty("time")).Any(t => t.Item1 == duration), "Bad duration");
                            e.Heroes = heroes; e.Start = now; e.Duration = duration;
                            dispatched++;
                        }
                        if (dispatched > 0) task = t => tasks.Trigger(t, 2007, dispatched);
                        break;
                    }
                case 58012:
                    {
                        var e = s.Entrusts.FirstOrDefault(x => x.Pos == (int)G(b, "pos"));
                        Require(e is not null && e.Start == 0, "Entrust cannot be refreshed");
                        e!.Task = RandomTask(I(Get("tasks", e.Task)!.Value, "task_level"));
                        e.Refresh++;
                        reply["entrust"] = Entrust1(e);
                        task = t => tasks.Trigger(t, 2007);
                        break;
                    }
                case 58024:
                    {
                        var pos = (int)G(b, "pos");
                        Require(pos == s.Entrusts.Count + 1 && pos <= 6, "Entrust slot cannot be unlocked");
                        var e = new Entrust { Pos = pos, Task = RandomTask(1) };
                        s.Entrusts.Add(e);
                        reply["entrust"] = Entrust1(e);
                        break;
                    }
                case 58018:
                    {
                        var positions = HandlerHelpers.ReadIdList(b, "pos").Select(x => (int)x).Distinct().ToList();
                        Require(positions.Count > 0, "Nothing to submit");
                        var extra = new List<object>();
                        var fresh = new List<object>();
                        var freed = new List<long>();
                        foreach (var pos in positions)
                        {
                            var e = s.Entrusts.FirstOrDefault(x => x.Pos == pos);
                            Require(e is not null && e.Start > 0 && now >= e.Start + e.Duration * 60L, "Entrust not finished");
                            var tc = Get("tasks", e!.Task)!.Value;
                            var mult = Pairs(tc.GetProperty("time")).Where(t => t.Item1 == e.Duration).Select(t => t.Item2).DefaultIfEmpty(100).First();
                            var items = Pairs(tc.GetProperty("reward_list")).Select(x => (x.Item1, x.Item2 * mult / 100)).ToList();
                            var big = Random.Shared.Next(100) < I(tc, "base_success");
                            if (big) items = items.Select(x => (x.Item1, (long)Math.Ceiling(x.Item2 * 1.5))).ToList();
                            grants.AddRange(items);
                            extra.Add(new Dictionary<string, object?> { ["pos"] = (uint)pos, ["extra_reward"] = big ? 1u : 0u, ["reward_list"] = Items(items) });
                            freed.AddRange(e.Heroes.Select(Arch));
                            W(s).Entrusts++;
                            foreach (var eh in e.Heroes.Select(Arch)) W(s).Join[eh] = W(s).Join.GetValueOrDefault(eh) + 1;
                            e.Task = RandomTask(I(tc, "task_level"));
                            e.Heroes = []; e.Start = 0; e.Refresh = 0; e.Duration = 1200;
                            fresh.Add(Entrust1(e));
                        }
                        reply["extra_reward"] = extra.ToArray();
                        reply["entrust"] = fresh.ToArray();
                        reply["fatigue_list"] = Fatigues(s, freed.Distinct());
                        task = t => tasks.Trigger(t, 2002, positions.Count);
                        break;
                    }

                // ----- mini games -----
                case 58128:
                    {
                        var id = (long)G(b, "theatrical_id");
                        Require(id > 0, "Bad theatre");
                        if (!s.Theatre.TryGetValue(id, out var list)) s.Theatre[id] = list = [];
                        list.Add(now);
                        task = t => tasks.Trigger(t, 2008);
                        break;
                    }
                case 58154:
                    {
                        var a = Arch(G(b, "hero_id"));
                        Require(owned.Contains(a), "Hero not owned");
                        var h = s.Heroes[a];
                        var rewards = new List<(long, long)>();
                        if (await accounts.TryClaimAsync(uid, "backhome_rhythm", Day.ToString(), ct))
                        {
                            var was = h.Fatigue;
                            h.Fatigue = Math.Max(0, h.Fatigue - 10);
                            AddFatigue(s, a, was - h.Fatigue);
                            rewards.Add((Coin, 1000));
                            grants.AddRange(rewards);
                        }
                        reply["reward_list"] = Items(rewards);
                        reply["fatigue"] = F(h.Fatigue);
                        task = t => tasks.Trigger(t, 2008);
                        break;
                    }

                // ----- idol trainee -----
                case 58166:
                    {
                        var hero = (long)G(b, "hero_id");
                        var idx = (int)G(b, "attribute_index");
                        var a = Arch(hero);
                        Require(owned.Contains(a) && IdolUsable(hero) && idx is >= 1 and <= 5, "Bad training");
                        var h = s.Heroes[a];
                        var attrs = IdolAttributes(s, hero);
                        var max = (uint)(Get("heroes", hero) ?? Get("heroes", a))!.Value.GetProperty("idol_base_attribute")[idx - 1][1].GetInt64();
                        Need(h.Fatigue >= 20, 210168);
                        var bonus = h.Fatigue >= 40 ? 100 : 0;
                        h.Fatigue -= 20;
                        AddFatigue(s, a, 20);
                        W(s).Train[a] = W(s).Train.GetValueOrDefault(a) + 1;
                        W(s).Attr[idx] = W(s).Attr.GetValueOrDefault(idx) + 1;
                        var lo = (int)Math.Floor(10 * (100 + bonus) / 10000.0 * 90.0);
                        var hi = (int)Math.Floor(10 * (100 + bonus) / 10000.0 * 110.0);
                        attrs[idx - 1] = Math.Min(max, attrs[idx - 1] + (uint)Random.Shared.Next(lo, Math.Max(lo, hi) + 1));
                        s.TrainTimes++;
                        reply["fatigue"] = F(h.Fatigue);
                        reply["attribute_value"] = attrs[idx - 1];
                        task = t => { var c = tasks.Trigger(t, 2009); c.AddRange(tasks.Trigger(t, 2017, 1, idx)); return c; };
                        pushes.Add(new GameResponse(58171, 0, srv, codec.Encode("sc_58171", new Dictionary<string, object?> { ["exercise_times_info"] = ((Dictionary<string, object?>)IdolOverview(s))["exercise_times_info"] })));
                        break;
                    }
                case 58174:
                    {
                        var list = Msgs(b, "hero_pos_list").Select(x => (G(x, "pos"), (long)G(x, "hero_id"))).Where(x => x.Item2 != 0).ToList();
                        Require(list.All(x => x.Item1 is >= 1 and <= 10 && owned.Contains(Arch(x.Item2))) && list.Select(x => x.Item1).Distinct().Count() == list.Count
                            && list.Select(x => Arch(x.Item2)).Distinct().Count() == list.Count, "Bad camp");
                        s.CampPos = list.ToDictionary(x => x.Item1, x => x.Item2);
                        break;
                    }
                case 58176:
                case 58178:
                    {
                        var ids = HandlerHelpers.ReadIdList(b, cmd == 58176 ? "attack_hero_id_list" : "defend_hero_id_list").ToList();
                        Require(ids.Count is >= 1 and <= 3 && ids.All(x => owned.Contains(Arch(x)) && IdolUsable(x)), "Bad idol team");
                        if (cmd == 58176) s.Attack = ids; else s.Defend = ids;
                        break;
                    }
                case 58186:
                    {
                        var stage = (long)G(b, "stage_id");
                        Require(Get("idol_pve", stage) is { } pc, "Unknown stage");
                        var pcfg = Get("idol_pve", stage)!.Value;
                        var hero = s.Attack.FirstOrDefault(x => owned.Contains(Arch(x)));
                        if (hero == 0) hero = s.CampPos.OrderBy(p => p.Key).Select(p => p.Value).FirstOrDefault(x => IdolUsable(x));
                        Require(hero != 0, "No idol team");
                        var npc = I(pcfg, "npc_id");
                        var npcHero = 1000 + npc / 100 % 1000;
                        var npcAttrs = (Get("heroes", npcHero) is { } nc && nc.GetProperty("idol_base_attribute").ValueKind == JsonValueKind.Array
                            ? nc.GetProperty("idol_base_attribute").EnumerateArray().Select(x => (uint)x[0].GetInt64()).ToList() : [200u, 200u, 200u, 200u, 200u]);
                        var attrs = IdolAttributes(s, hero);
                        var rate = L(pcfg.GetProperty("peculiarity_rate"));
                        var rounds = new List<object>();
                        long atk = 0, def = 0;
                        for (var i = 1; i <= 5; i++)
                        {
                            var k = (i - 1) % 5;
                            var a = attrs[k] * rate[k] / 100;
                            var d = npcAttrs[k] * rate[k] / 100;
                            atk += a; def += d;
                            object Score(long v) => new Dictionary<string, object?> { ["peculiarity_score"] = (uint)v, ["skill_score"] = 0, ["sum_score"] = (uint)v };
                            object Act() => new Dictionary<string, object?> { ["atk_style_id"] = (uint)(k % 3 + 1), ["skill_id"] = 0u, ["index_list"] = new[] { 1u } };
                            rounds.Add(new Dictionary<string, object?> { ["round_index"] = (uint)i, ["attack_user_action"] = Act(), ["defend_user_action"] = Act(), ["attack_user_score"] = Score(a), ["defend_user_score"] = Score(d) });
                        }
                        s.Pending1 = new Battle { Stage = stage, Atk = atk, Def = def };
                        reply["prepare_info"] = new Dictionary<string, object?> { ["attacker_active_buff_list"] = Array.Empty<ulong>(), ["defender_active_buff_list"] = Array.Empty<ulong>() };
                        reply["round_list"] = rounds.ToArray();
                        var skin = s.Heroes.GetValueOrDefault(Arch(hero))?.Skin ?? 0;
                        reply["attacker_skin_id"] = (uint)(skin > 0 ? skin : hero);
                        reply["defender_skin_id"] = (uint)npc;
                        break;
                    }
                case 58164:
                    {
                        Require(s.Pending1 is not null, "No battle in progress");
                        var bt = s.Pending1!;
                        s.Pending1 = null;
                        var win = bt.Atk > bt.Def;
                        var prev = s.Pve.GetValueOrDefault(bt.Stage) ?? new PveRec();
                        s.Pve[bt.Stage] = new PveRec { Score = Math.Max(prev.Score, bt.Atk), Clear = prev.Clear || win };
                        object Side(long v) => new Dictionary<string, object?> { ["sum_score"] = (uint)v, ["peculiarity_score"] = (uint)v, ["skill_add_score"] = 0u, ["skill_decrease_score"] = 0u };
                        reply["battle_result"] = win ? 1u : 2u;
                        reply["attacker_data"] = Side(bt.Atk);
                        reply["defender_data"] = Side(bt.Def);
                        var best = s.Pve[bt.Stage].Score;
                        var catalog = config.Get("assignment_cfg.json");
                        var reached = L(Get("idol_pve", bt.Stage)!.Value.GetProperty("taskIDList")).Where(id => catalog is { } cat && cat.TryGetProperty(id.ToString(), out var tc)
                            && tc.TryGetProperty("param", out var p) && L(p) is { Count: >= 2 } ps && ps[0] == bt.Stage && best >= ps[1]).ToList();
                        task = t => { var c = tasks.Fulfil(t, reached); c.AddRange(tasks.Trigger(t, 2011)); if (win) c.AddRange(tasks.Trigger(t, 2010)); return c; };
                        pushes.Add(new GameResponse(58189, 0, srv, codec.Encode("sc_58189", (IReadOnlyDictionary<string, object?>)PveChapters(s))));
                        break;
                    }
                case 58192:
                    {
                        var id = (int)G(b, "id");
                        Require(Get("idol_rank", id) is { } rc && !s.RankGot.Contains(id), "Rank reward unavailable");
                        var rcfg = Get("idol_rank", id)!.Value;
                        var gate = L(rcfg.GetProperty("item_id"));
                        Require(await accounts.GetItemCountAsync(uid, gate[0], ct) >= gate[1], "Not enough points");
                        var pool = L(rcfg.GetProperty("reward"));
                        var num = I(rcfg, "num");
                        var picks = HandlerHelpers.ReadItemList(b, "select_list").ToList();
                        List<(long, long)> rewards;
                        if (I(rcfg, "type") == 2)
                        {
                            Require(picks.Count > 0 && picks.All(x => pool.Contains(x.ItemId)) && picks.Sum(x => x.Count) == num, "Pick the reward pieces");
                            rewards = picks.Select(x => (x.ItemId, x.Count)).ToList();
                            costs.Add((gate[0], gate[1]));
                            s.RankGot.Clear();
                        }
                        else
                        {
                            rewards = [(pool[Random.Shared.Next(pool.Count)], num)];
                            s.RankGot.Add(id);
                        }
                        grants.AddRange(rewards);
                        reply["get_id_list"] = s.RankGot.OrderBy(x => x).Select(x => (uint)x).ToArray();
                        reply["reward_list"] = Items(rewards);
                        break;
                    }
                case 58196:
                    {
                        // IdolDanceCollectionAction.ViewCollectionRecord: marks one collection record viewed (58203 at login).
                        var type = G(b, "type");
                        var id = G(b, "id");
                        Require(type != 0 && id != 0, "Bad collection record");
                        if (!s.Viewed.TryGetValue(type, out var seen)) s.Viewed[type] = seen = [];
                        if (!seen.Contains(id)) seen.Add(id);
                        break;
                    }
                case 58198:
                    {
                        var seq = Msg(b, "sequence");
                        var bs = seq is null ? null : Msg(seq, "base_sequence");
                        var pos = seq is null ? 0 : G(seq, "pos");
                        Require(pos is >= 1 and <= 10 && bs is not null, "Bad sequence");
                        s.Dances[pos] = new Dance { Music = G(bs!, "music_id"), Scene = G(bs!, "scene_id"), Actions = HandlerHelpers.ReadIdList(bs!, "action_id_list").Select(x => (uint)x).ToList() };
                        pushes.Add(new GameResponse(58195, 0, srv, codec.Encode("sc_58195", (IReadOnlyDictionary<string, object?>)Sequences(s))));
                        break;
                    }
                case 58200:
                    Require(s.Dances.Remove(G(b, "pos")), "Sequence not found");
                    pushes.Add(new GameResponse(58195, 0, srv, codec.Encode("sc_58195", (IReadOnlyDictionary<string, object?>)Sequences(s))));
                    break;
                case 58180: reply["pvp_fight_list"] = Array.Empty<object>(); save = false; break;
                case 58182: reply["history_mini_battle_list"] = Array.Empty<object>(); save = false; break;
                case 58160:
                case 58184:
                    throw new ModeError("No opponents on a private server");
            }
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)]; }
        catch (Fail f) { return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, f.Code)]; }

        if (costs.Count > 0 || grants.Count > 0)
        {
            var c = costs.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).Where(x => x.Item2 > 0).ToList();
            var g2 = grants.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).Where(x => x.Item2 > 0).ToList();
            if (!await accounts.ConsumeAndGrantAsync(uid, c, g2, ct)) return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)];
            costs = c; grants = g2;
        }
        if (save) await SaveAsync(uid, s, ct);
        if (trustExp > 0) await accounts.AddHeroArchiveExpAsync(uid, trustArch, trustExp, ct);
        if (cmd == 58136)
        {
            var oath = new OathSystem(accounts, config, codec);
            foreach (var form in await accounts.GetOwnedHeroIdsAsync(uid, ct))
                if (Arch(form) == trustArch && await oath.AdvanceAsync(uid, srv, form, 410001, 1, ct) is { } of) pushes.Add(of);
        }
        var frames = await InventorySync.FramesAsync(accounts, codec, r, costs.Concat(grants).Select(x => x.Item1).Distinct(), ct);
        if (cmd is 58130 or 58132 or 58134 or 58136 or 58146 or 58104 or 58114 or 58116 or 58106 or 58018 or 58100 or 58102 or 58024 or 58012 or 58126 or 58218)
            frames.Add(new GameResponse(58001, 0, srv, codec.Encode("sc_58001", await ConciseAsync(uid, s, ct))));
        frames.AddRange(pushes);
        if (task is not null && await tasks.EventAsync(uid, srv, task, ct) is { } diff) frames.Add(diff);
        var achCond = cmd switch { 58018 => 2002, 58138 => 2003, 58010 => 2005, _ => 0 };
        var achDelta = cmd == 58018 ? HandlerHelpers.ReadIdList(b, "pos").Distinct().Count() : 1;
        if (achCond != 0 && await new AchievementSystem(accounts, config, codec).EventAsync(uid, srv, achCond, achDelta, ct) is { } ach) frames.Add(ach);
        frames.Add(HandlerHelpers.Reply(codec, r, cmd + 1, reply));
        return frames;
    }

    public static readonly uint[] Commands =
    [
        58002, 58010, 58012, 58018, 58024, 58040, 58050, 58052, 58054, 58056, 58058, 58060, 58100, 58102, 58104, 58106, 58108,
        58114, 58116, 58120, 58126, 58128, 58130, 58132, 58134, 58136, 58138, 58142, 58144, 58146, 58148, 58152, 58154, 58158,
        58160, 58164, 58166, 58174, 58176, 58178, 58180, 58182, 58184, 58186, 58192, 58196, 58198, 58200, 58218
    ];
}
