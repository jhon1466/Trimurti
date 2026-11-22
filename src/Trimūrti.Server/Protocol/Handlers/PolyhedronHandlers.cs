using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

public sealed class PolyhedronError(string message) : Exception(message);

/// <summary>
/// 多维变量 (Polyhedron), port of Alpha-Persei polyhedron_service / polyhedron_flow / polyhedron_account /
/// polyhedron_battle on the client contracts Data/Configs/polyhedron_contract.json (stages, terminals, levels,
/// difficulties, beacons, astrolabes, combos, conditions) and polyhedron_cfg.json (events, artifacts, heroes).
/// A run walks tiers 1-1 … 3-10: battles (coin / artifact / typewriter / upgrade / elite / boss) open three-pick
/// rewards, gates choose the next node (shop, recruit, battles), and the end grants decision EXP (45), terminal
/// EXP (46) and points (29). Weekly decision cycle (Monday 05:00 UTC+8) resets item 45 and its claimed levels.
/// </summary>
public sealed class Polyhedron(GameAccountRepository accounts, SourceGameConfig config)
{
    private static readonly long[] TierRoutes = [1001, 1002, 1003, 1004, 1005, 1006, 1007, 2001, 2002, 2003, 2004, 2005, 2006, 2007, 2008,
        3001, 3002, 3003, 3004, 3005, 3006, 3007, 3008, 3009, 3010];
    private static readonly Dictionary<long, long> BossTiers = new() { [1007] = 3035301, [2008] = 3036301, [3010] = 3037301 };
    private static readonly long[] PolicyExp = [60, 120, 180, 240, 300, 400, 500, 600, 700, 800, 920, 1040, 1160, 1280, 1400, 1520, 1640, 1760, 1880, 2000];
    private static (long, long) PolicyReward(int lvl) => lvl switch
    {
        5 or 15 => (41601, 35), 10 or 20 => (1, 800), >= 16 => (28, 70), _ => (28, 60)
    };

    private JsonElement Contract => config.Get("polyhedron_contract.json") ?? throw new PolyhedronError("Missing polyhedron contract");
    private JsonElement Cfg => config.Get("polyhedron_cfg.json") ?? throw new PolyhedronError("Missing polyhedron cfg");
    private static void Require(bool ok, string message) { if (!ok) throw new PolyhedronError(message); }

    /// <summary>Latest polyhedron activity (template 73) of activity_content_cfg.</summary>
    public uint ActivityId
    {
        get
        {
            if (config.Get("activity_content_cfg.json") is not { ValueKind: JsonValueKind.Object } root) return 4407301;
            var latest = root.GetProperty("versions").EnumerateObject().OrderBy(v => int.TryParse(v.Name, out var n) ? n : 0).Last().Value.GetProperty("activities");
            return latest.EnumerateObject().Where(a => a.Value.TryGetProperty("activity_template", out var t) && t.GetInt32() == 73 && a.Name.EndsWith("07301"))
                .Select(a => uint.Parse(a.Name)).DefaultIfEmpty(4407301u).Max();
        }
    }

    public static (long Start, long Stop) Cycle()
    {
        var now = GameClock.Now;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, 5, 0, 0, GameClock.Offset).AddDays(-(((int)now.DayOfWeek + 6) % 7));
        if (start > now) start = start.AddDays(-7);
        return (start.ToUnixTimeSeconds(), start.AddDays(7).ToUnixTimeSeconds());
    }

    public bool IsStage(uint type, long stage) => type == 52 || Contract.GetProperty("stages").TryGetProperty(stage.ToString(), out _);

    // ---------------- state ----------------

    public sealed class Item { public long Class { get; set; } public List<long> Params { get; set; } = []; }
    public sealed class ShopItem { public Item Item { get; set; } = new(); public long Price { get; set; } public int Available { get; set; } = 1; }
    public sealed class Shop { public List<ShopItem> Items { get; set; } = []; public int Refresh { get; set; } public int Recover { get; set; } }
    public sealed class Ev { public long Id { get; set; } public long StageId { get; set; } public long RewardType { get; set; } }
    public sealed class Gate { public int Index { get; set; } public Ev Event { get; set; } = new(); }
    public sealed class Stage
    {
        public long StageId { get; set; }
        public int SavePoint { get; set; } = 1;
        public int Round { get; set; }
        public List<Item> Rewards { get; set; } = [];
        public List<long> Params { get; set; } = [];
        public List<Gate> Gates { get; set; } = [];
        public Shop? Shop { get; set; }
    }
    public sealed class Hero { public long Id { get; set; } public long Template { get; set; } public long Health { get; set; } = 10000; public long MaxHealth { get; set; } = 10000; }
    public sealed class Art { public long Id { get; set; } public long Level { get; set; } = 1; }

    public sealed class Run
    {
        public int State { get; set; } = 2;
        public long Tier { get; set; } = 1001;
        public int Difficulty { get; set; } = 1;
        public long Leader { get; set; }
        public List<long> LeaderAstrolabes { get; set; } = [];
        public List<long> Beacons { get; set; } = [];
        public List<long> Terminals { get; set; } = [];
        public long Coins { get; set; }
        public int Rolls { get; set; }
        public List<Hero> Heroes { get; set; } = [];
        public List<long> Fight { get; set; } = [];
        public List<Art> Artifacts { get; set; } = [];
        public List<long> Effects { get; set; } = [];
        public long Combo { get; set; }
        public Ev Event { get; set; } = new();
        public Stage Stage { get; set; } = new();
        public List<long> Cleared { get; set; } = [];
        public bool Settled { get; set; }
        public long[]? End { get; set; }
    }

    public sealed class Meta
    {
        public Dictionary<long, List<long>> Heroes { get; set; } = [];
        public List<long> Beacons { get; set; } = [];
        public List<long> Terminals { get; set; } = [];
        public List<int> Difficulties { get; set; } = [];
        public Dictionary<long, int> Manual { get; set; } = [];
        public int ResetTimes { get; set; } = 1;
        public int IsNew { get; set; }
        public long PolicyCycle { get; set; }
        public List<int> PolicyClaimed { get; set; } = [];
        public Dictionary<string, long> Progress { get; set; } = [];
        public List<long> Leaders { get; set; } = [];
        public bool Seeded { get; set; }
        public Run? Run { get; set; }
    }

    public async Task<Meta> LoadAsync(long uid, CancellationToken ct)
    {
        var m = await accounts.GetJsonAsync<Meta>(uid, "polyhedron_v1", ct) ?? new Meta();
        if (!m.Seeded)
        {
            // Unlocked mapped heroes start as the account's own heroes that exist in the polyhedron roster.
            var state = await accounts.GetInitialLoginStateAsync(uid, ct);
            foreach (var h in state?.Heroes ?? [])
                if (Cfg.GetProperty("poly_heroes").TryGetProperty(h.Id.ToString(), out _)) m.Heroes.TryAdd(h.Id, []);
            m.Seeded = true;
        }
        // Weekly decision cycle: item 45 and claimed levels restart; one terminal reset per cycle.
        var (cycle, _) = Cycle();
        if (m.PolicyCycle != 0 && cycle > m.PolicyCycle)
        {
            var exp = await accounts.GetItemCountAsync(uid, 45, ct);
            if (exp > 0) await accounts.AddItemAsync(uid, 45, -exp, ct);
            m.PolicyClaimed.Clear();
            m.ResetTimes = 1;
        }
        m.PolicyCycle = Math.Max(m.PolicyCycle, cycle);
        return m;
    }

    public Task SaveAsync(long uid, Meta m, CancellationToken ct) => accounts.SetJsonAsync(uid, "polyhedron_v1", m, ct);

    private Run Active(Meta m, int? point = null)
    {
        Require(m.Run is { State: 2, Settled: false }, "Polyhedron run is not active");
        if (point is { } p) Require(m.Run!.Stage.SavePoint == p, "Polyhedron stage point mismatch");
        return m.Run!;
    }

    private void AddProgress(Meta m, string key, long delta) => m.Progress[key] = m.Progress.GetValueOrDefault(key) + delta;

    // ---------------- helpers ----------------

    private long TierMajor(long tier) => tier > 1000 ? tier / 1000 : 1;
    private long TierLevel(long tier) => tier > 1000 ? tier % 1000 : 1;
    private static long NextTier(long tier) { var i = Array.IndexOf(TierRoutes, tier); return i >= 0 && i + 1 < TierRoutes.Length ? TierRoutes[i + 1] : tier; }
    private static T Pick<T>(IReadOnlyList<T> list) => list[Random.Shared.Next(list.Count)];
    private static List<T> Sample<T>(IEnumerable<T> src, int n) => src.OrderBy(_ => Random.Shared.Next()).Take(n).ToList();

    private long PickStage(long tier, long eventType)
    {
        var t = 3034 + TierMajor(tier);   // 3035 / 3036 / 3037
        long[] Range(int kind, params int[] ids) => ids.Select(i => t * 1000 + kind * 100 + i).ToArray();
        if (eventType == 5) return t * 1000 + 601;
        if (eventType == 2) return t * 1000 + 401;
        if (eventType is 6 or 7 || BossTiers.ContainsKey(tier)) return Pick(Range(3, 1, 2, 3, 4));
        if (eventType is 1002 or 3002) return Pick(Range(2, 5, 6, 7, 8));
        return Pick(Range(1, 5, 6, 7, 8, 9, 10, 11, 12));
    }

    private long EventType(long eventId)
    {
        if (Cfg.GetProperty("events").TryGetProperty(eventId.ToString(), out var e) && e.GetProperty("event_type").GetInt64() is var t and not 0) return t;
        return eventId switch
        {
            >= 1001 and <= 1003 => 1, >= 1002001 and <= 1002003 => 1002, >= 2001 and <= 2003 => 2, >= 3001 and <= 3003 => 3,
            >= 3002001 and <= 3002003 => 3002, >= 4001 and <= 4003 => 4, >= 5001 and <= 5003 => 5, 6001 or 6002 => 6, 7001 => 7,
            >= 8001 and <= 8003 => 8, _ => 1
        };
    }

    private List<Gate> Gates(long tier, Run run)
    {
        var major = TierMajor(tier); var level = TierLevel(tier);
        if (BossTiers.TryGetValue(tier, out var boss))
            return [new Gate { Index = 1, Event = new Ev { Id = major == 1 ? 6001 : major == 2 ? 6002 : 7001, StageId = boss, RewardType = major < 3 ? 3007 : 0 } }];
        var subs = Sample(new long[] { 3001, 3002, 3003, 3004, 3005, 3006, 3008, 3009 }, 8);
        var gates = new List<Gate> { new() { Index = 1, Event = new Ev { Id = 3000 + major, StageId = PickStage(tier, 3), RewardType = subs[0] } } };
        Ev second = level switch
        {
            3 or 6 => new Ev { Id = 5000 + major, StageId = PickStage(tier, 5), RewardType = 5 },
            2 or 5 when run.Beacons.Contains(10) => new Ev { Id = 3002000 + major, StageId = PickStage(tier, 3002), RewardType = subs[1] },
            2 or 5 => new Ev { Id = 2000 + major, StageId = PickStage(tier, 2), RewardType = 2 },
            4 => new Ev { Id = 4000 + major, StageId = PickStage(tier, 4), RewardType = 4 },
            _ => new Ev { Id = 3002000 + major, StageId = PickStage(tier, 3002), RewardType = subs[1] }
        };
        gates.Add(new Gate { Index = 2, Event = second });
        gates.Add(new Gate { Index = 3, Event = new Ev { Id = 1000 + major, StageId = PickStage(tier, 1), RewardType = 1 } });
        if (run.Beacons.Contains(4) && major == 1)
            gates.Add(new Gate { Index = 4, Event = new Ev { Id = 8000 + major, StageId = PickStage(tier, 8), RewardType = 7 } });
        return gates;
    }

    private IEnumerable<(long Id, long Hero)> AllArtifacts() =>
        Cfg.GetProperty("artifacts").EnumerateObject().Select(p => (long.Parse(p.Name), p.Value.TryGetProperty("exclusive_hero_id", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt64() : 0L));

    private List<long> RewardArtifacts(Run run, long rewardType)
    {
        var team = run.Heroes.Select(h => h.Id).ToHashSet();
        var owned = run.Artifacts.Select(a => a.Id).ToHashSet();
        var all = AllArtifacts().Where(a => !owned.Contains(a.Id)).ToList();
        if (rewardType == 3007)
        {
            var spec = all.Where(a => team.Contains(a.Hero)).Select(a => a.Id).ToList();
            if (spec.Count >= 3) return Sample(spec, 3);
            var other = all.Where(a => a.Hero == 0).Select(a => a.Id).ToList();
            return Sample(spec.Concat(Sample(other, 3 - spec.Count)), 3);
        }
        var candidates = Cfg.GetProperty("artifacts_by_sub_type").TryGetProperty(rewardType.ToString(), out var subs)
            ? subs.EnumerateArray().Select(x => x.GetInt64()).Where(x => !owned.Contains(x)).ToList() : [];
        if (Random.Shared.NextDouble() < 0.2 && candidates.Count > 0 && all.Where(a => team.Contains(a.Hero)).Select(a => a.Id).ToList() is { Count: > 0 } teamSpec)
            return Sample(new[] { Pick(teamSpec) }.Concat(Sample(candidates, 2)), 3);
        if (candidates.Count >= 3) return Sample(candidates, 3);
        return Sample(all.Where(a => a.Hero == 0 || team.Contains(a.Hero)).Select(a => a.Id), 3);
    }

    private Dictionary<long, long> Attributes(Run run)
    {
        var a = new Dictionary<long, long> { [1] = 1, [2] = 1000, [7] = 0, [8] = 3, [15] = 4, [16] = 3, [17] = 1, [18] = 0, [28] = 0, [30] = 1, [33] = 0, [34] = 0 };
        void Add(long k, long v) => a[k] = a.GetValueOrDefault(k) + v;
        var b = run.Beacons; var t = run.Terminals;
        if (b.Contains(3)) Add(6, 200);
        if (b.Contains(4)) Add(34, 1);
        if (b.Contains(5)) { Add(1, 1); Add(30, 1); }
        if (b.Contains(7)) Add(18, 2);
        if (b.Contains(8)) Add(28, 100);
        if (b.Contains(9)) Add(15, 3);
        if (b.Contains(10)) Add(32, -1000);
        if (b.Contains(13)) Add(33, 500);
        if (t.Contains(2001)) { Add(15, 1); Add(16, 1); }
        if (t.Contains(3002)) Add(18, 1);
        if (t.Contains(3008)) Add(17, 1);
        if (t.Contains(3012)) Add(7, 2);
        if (t.Contains(1101)) { Add(1, 1); Add(30, 1); }
        if (t.Contains(3010)) Add(28, 50);
        a[8] = Math.Max(0, run.Rolls);
        return a;
    }

    private Shop MakeShop(Run run)
    {
        var team = run.Heroes.Select(h => h.Id).ToHashSet();
        var owned = run.Artifacts.Select(x => x.Id).ToHashSet();
        var pool = AllArtifacts().Where(x => !owned.Contains(x.Id)).ToList();
        var count = (int)Math.Max(4, Attributes(run)[15]);
        var arts = new List<long>();
        var spec = pool.Where(x => team.Contains(x.Hero)).Select(x => x.Id).ToList();
        if (spec.Count > 0) { arts.Add(Pick(spec)); count--; }
        arts.AddRange(Sample(pool.Where(x => x.Hero == 0).Select(x => x.Id), count));
        var shop = new Shop { Items = Sample(arts, arts.Count).Select(id => new ShopItem { Item = new Item { Class = 6, Params = [id] }, Price = Pick(new long[] { 300, 350, 400, 450 }) }).ToList() };
        shop.Items.Add(new ShopItem { Item = new Item { Class = 4, Params = [201] }, Price = 500 });
        return shop;
    }

    private void AddArtifact(Meta m, Run run, long id, long level = 1)
    {
        var a = run.Artifacts.FirstOrDefault(x => x.Id == id);
        if (a is null) run.Artifacts.Add(new Art { Id = id, Level = level }); else a.Level += level;
        m.Manual[id] = 2;
    }

    private static void Heal(Run run, int percent)
    {
        foreach (var h in run.Heroes) h.Health = Math.Min(h.MaxHealth, h.Health + h.MaxHealth * percent / 100);
    }

    private long StandardId(long hero) =>
        Cfg.GetProperty("poly_heroes").TryGetProperty(hero.ToString(), out var h) ? h.GetProperty("standard_id").GetInt64() : 3051000 + hero % 1000;

    private void OpenGates(Run run) => run.Stage = new Stage { StageId = run.Stage.StageId, SavePoint = 3, Gates = Gates(NextTier(run.Tier), run) };

    public int TerminalLevel(long exp) =>
        Contract.GetProperty("levels").EnumerateObject().Select(p => p.Value).Where(r => r.GetProperty("exp").GetInt64() <= exp).Select(r => r.GetProperty("id").GetInt32()).DefaultIfEmpty(1).Max();

    private int TerminalPoints(int level) => Contract.GetProperty("levels").GetProperty(level.ToString()).GetProperty("point").GetInt32();

    // ---------------- run operations ----------------

    public void Start(Meta m, IReadOnlyList<long> heroes, IReadOnlyList<long> beacons, int difficulty)
    {
        Require(m.Run is null || m.Run.State == 1 || m.Run.Settled, "Finish or reset the current polyhedron run first");
        Require(heroes.Count == 1 && m.Heroes.ContainsKey(heroes[0]), "Polyhedron leader is not unlocked");
        Require(beacons.Distinct().Count() == beacons.Count && beacons.All(m.Beacons.Contains), "Polyhedron beacon is not unlocked");
        Require(Contract.GetProperty("difficulties").TryGetProperty(difficulty.ToString(), out var dcfg), "Invalid polyhedron difficulty");
        var need = dcfg.GetProperty("unlock_difficulty").GetInt32();
        Require(need == 0 || m.Difficulties.DefaultIfEmpty(0).Max() >= need, "Polyhedron difficulty is locked");
        Require(beacons.Count <= m.Terminals.Count(t => t is 3001 or 3005 or 3011), "Too many polyhedron beacons");
        var leader = heroes[0];
        var run = new Run
        {
            Difficulty = difficulty, Leader = leader, LeaderAstrolabes = m.Heroes[leader].ToList(), Beacons = beacons.ToList(), Terminals = m.Terminals.ToList(),
            Coins = 600 + (beacons.Contains(1) ? 200 : 0) + (m.Terminals.Contains(3001) ? 100 : 0),
            Rolls = 3 + (m.Terminals.Contains(3003) ? 2 : 0),
            Heroes = [new Hero { Id = leader, Template = StandardId(leader) }], Fight = [leader]
        };
        var stage = PickStage(1001, 1);
        run.Event = new Ev { Id = 1001, StageId = stage, RewardType = 3001 };
        run.Stage = new Stage { StageId = stage, SavePoint = 1 };
        m.Run = run;
    }

    public void SelectGate(Meta m, int index)
    {
        var run = Active(m, 3);
        var gate = run.Stage.Gates.FirstOrDefault(g => g.Index == index) ?? throw new PolyhedronError("Polyhedron gate does not exist");
        Require(Contract.GetProperty("stages").TryGetProperty(gate.Event.StageId.ToString(), out _), "Polyhedron gate stage does not exist");
        run.Tier = NextTier(run.Tier);
        var type = EventType(gate.Event.Id);
        var after = NextTier(run.Tier);
        run.Event = new Ev { Id = gate.Event.Id, StageId = gate.Event.StageId, RewardType = type == 5 ? 5 : type == 2 ? 2 : gate.Event.RewardType };
        if (type == 5)
            run.Stage = new Stage { StageId = gate.Event.StageId, SavePoint = 3, Gates = Gates(after, run), Shop = MakeShop(run) };
        else if (type == 2)
        {
            var have = run.Heroes.Select(h => h.Id).ToHashSet();
            var roster = Cfg.GetProperty("poly_heroes").EnumerateObject().Select(p => long.Parse(p.Name)).Where(h => !have.Contains(h)).ToList();
            var picks = Sample(roster, 3);
            var kinds = new long[] { 2, 3, 2 };
            run.Stage = new Stage { StageId = gate.Event.StageId, SavePoint = 1, Gates = Gates(after, run), Params = picks.SelectMany((h, i) => new[] { h, kinds[i % 3] }).ToList() };
        }
        else run.Stage = new Stage { StageId = gate.Event.StageId, SavePoint = 1 };
        if (type == 8) AddProgress(m, "upgrade_gates", 1);
    }

    /// <summary>A won fight of the current node: coins, then the node's reward pick or the exit gates.</summary>
    public void BattleWon(Meta m, int kills)
    {
        var run = Active(m, 1);
        var type = EventType(run.Event.Id);
        if (type is 2 or 5) return;   // recruit/shop scenes launch through 54030 too; their exit is not a fight
        Require(!run.Cleared.Contains(run.Tier), "Polyhedron node already settled");
        run.Cleared.Add(run.Tier);
        AddProgress(m, "kills", kills);
        long gain = BossTiers.ContainsKey(run.Tier) || type is 6 or 7 ? 1000 : type is 1002 or 3002 || run.Event.Id >= 1002000 ? 700 : 400;
        run.Coins += gain;
        AddProgress(m, "coins", gain);
        var stageId = run.Event.StageId;
        if (type == 4 || run.Event.Id is 4001 or 4002 or 4003)
            run.Stage = new Stage { StageId = stageId, SavePoint = 2, Rewards = [new Item { Class = 4, Params = [201] }, new Item { Class = 4, Params = [202] }] };
        else if (type == 8 || run.Event.Id is 8001 or 8002 or 8003)
        {
            var owned = run.Artifacts.Select(a => a.Id).ToList();
            run.Stage = new Stage { StageId = stageId, SavePoint = 2, Rewards = Sample(owned.Count > 0 ? owned : RewardArtifacts(run, 3001), 3).Select(a => new Item { Class = 5, Params = [a] }).ToList() };
        }
        else if (type is 3 or 3002 or 6 or 7 or 9 || run.Event.RewardType >= 3000)
        {
            var rt = BossTiers.ContainsKey(run.Tier) || type is 6 or 7 || run.Event.RewardType == 3007 ? 3007 : run.Event.RewardType;
            run.Stage = new Stage { StageId = stageId, SavePoint = 2, Rewards = RewardArtifacts(run, rt).Select(a => new Item { Class = 6, Params = [a] }).ToList() };
        }
        else if (run.Tier == 3010) run.State = 3;
        else OpenGates(run);
    }

    public void SelectReward(Meta m, int index)
    {
        var run = Active(m, 2);
        var items = run.Stage.Rewards;
        Require(items.Count > 0 && index >= 0 && index <= items.Count, "Invalid polyhedron reward index");
        if (index > 0)
        {
            var chosen = items[index - 1];
            switch (chosen.Class)
            {
                case 6: AddArtifact(m, run, chosen.Params[0], run.Beacons.Contains(3) && Random.Shared.NextDouble() < 0.2 ? 2 : 1); break;
                case 5: AddArtifact(m, run, chosen.Params[0]); break;
                case 4: if (!run.Effects.Contains(chosen.Params[0])) run.Effects.Add(chosen.Params[0]); break;
            }
        }
        else
        {
            var coins = 150 + Attributes(run)[28];
            run.Coins += coins;
            AddProgress(m, "coins", coins);
        }
        if (run.Tier == 3010) run.State = 3; else OpenGates(run);
    }

    public void Reroll(Meta m)
    {
        var run = Active(m, 2);
        Require(run.Stage.Rewards.Count > 0 && run.Stage.Rewards.All(i => i.Class == 6), "This reward cannot be rerolled");
        Require(run.Rolls > 0, "No polyhedron rerolls left");
        run.Rolls--;
        run.Stage.Round++;
        run.Stage.Rewards = RewardArtifacts(run, run.Event.RewardType).Select(a => new Item { Class = 6, Params = [a] }).ToList();
    }

    public void Buy(Meta m, int index)
    {
        var run = Active(m, 3);
        var shop = run.Stage.Shop ?? throw new PolyhedronError("Not in a polyhedron shop");
        Require(index >= 1 && index <= shop.Items.Count, "Polyhedron goods do not exist");
        var it = shop.Items[index - 1];
        Require(it.Available == 1, "Polyhedron goods sold out");
        Require(run.Coins >= it.Price, "Not enough polyhedron coins");
        run.Coins -= it.Price;
        it.Available = 0;
        switch (it.Item.Class)
        {
            case 6: AddArtifact(m, run, it.Item.Params[0]); break;
            case 4: if (!run.Effects.Contains(it.Item.Params[0])) run.Effects.Add(it.Item.Params[0]); break;
            case 7: Heal(run, 50); break;
        }
        AddProgress(m, "shop_buys", 1);
    }

    public void RefreshShop(Meta m)
    {
        var run = Active(m, 3);
        var shop = run.Stage.Shop ?? throw new PolyhedronError("Not in a polyhedron shop");
        var attrs = Attributes(run);
        Require(shop.Refresh < Math.Max(3, attrs[16]), "Polyhedron shop refreshes used up");
        var cost = shop.Refresh >= attrs[18] ? 100 * (shop.Refresh - attrs[18] + 1) : 0;
        Require(run.Coins >= cost, "Not enough polyhedron coins");
        run.Coins -= cost;
        var fresh = MakeShop(run);
        shop.Items = fresh.Items;
        shop.Refresh++;
    }

    public void RecoverShop(Meta m)
    {
        var run = Active(m, 3);
        var shop = run.Stage.Shop ?? throw new PolyhedronError("Not in a polyhedron shop");
        Require(shop.Recover < Math.Max(1, Attributes(run)[17]), "Polyhedron shop heals used up");
        var cost = 100 * shop.Recover;
        Require(run.Coins >= cost, "Not enough polyhedron coins");
        run.Coins -= cost;
        shop.Recover++;
        Heal(run, 50);
    }

    public void Enlist(Meta m, long hero)
    {
        var run = Active(m, 1);
        Require(run.Event.RewardType == 2, "Not a polyhedron recruit stage");
        if (hero > 0)
        {
            var p = run.Stage.Params;
            long? kind = null;
            for (var i = 0; i + 1 < p.Count; i += 2) if (p[i] == hero) kind = p[i + 1];
            Require(kind is 2 or 3, "Polyhedron recruit candidate missing");
            Require(run.Heroes.All(h => h.Id != hero), "Polyhedron hero already joined");
            if (kind == 2)
            {
                var cost = Cfg.GetProperty("poly_heroes").TryGetProperty(hero.ToString(), out var hc) ? hc.GetProperty("need_coin").GetInt64() : 800;
                Require(run.Coins >= cost, "Not enough polyhedron coins");
                run.Coins -= cost;
            }
            run.Heroes.Add(new Hero { Id = hero, Template = StandardId(hero) });
            if (run.Fight.Count < 3) run.Fight.Add(hero);
        }
        run.Stage.SavePoint = 3;
        run.Stage.Params = [];
    }

    public void SwitchTeam(Meta m, IReadOnlyList<long> fight)
    {
        var run = Active(m);
        var have = run.Heroes.Select(h => h.Id).ToHashSet();
        Require(fight.Count is >= 1 and <= 3 && fight.Distinct().Count() == fight.Count && fight.All(have.Contains) && fight.Contains(run.Leader), "Invalid polyhedron team");
        run.Fight = fight.ToList();
    }

    public void SetCombo(Meta m, long skill)
    {
        var run = Active(m);
        Require(skill == 0 || Contract.GetProperty("combos").TryGetProperty(skill.ToString(), out var c)
            && c.GetProperty("cooperate_role_ids").EnumerateArray().All(x => run.Fight.Contains(x.GetInt64())), "Polyhedron combo does not match the team");
        run.Combo = skill;
    }

    /// <summary>End of run (3-10 cleared or abandoned via 18018): points (29), decision EXP (45), terminal EXP (46).</summary>
    public async Task<long[]> SettleAsync(long uid, Meta m, CancellationToken ct)
    {
        var run = m.Run;
        Require(run is { State: 2 or 3 }, "No polyhedron run to settle");
        if (run!.Settled && run.End is { } done) return done;
        var major = TierMajor(run.Tier); var level = TierLevel(run.Tier);
        var cleared = (int)Math.Clamp(major == 1 ? level : major == 2 ? 7 + level : 15 + level, 1, 25);
        var full = run.Tier == 3010 && run.Cleared.Contains(3010) && run.State == 3;
        var diffMult = 1.0 + (Math.Max(1, run.Difficulty) - 1) * 0.15;
        var expMult = 1.0 + (Math.Max(1, run.Difficulty) - 1) * 0.10;
        var exclusive = run.Artifacts.Count(a => a.Id < 70000);
        var normal = run.Artifacts.Count - exclusive;
        var point = (long)((cleared * 180 + (full ? 3500 : (major - 1) * 1000) + exclusive * 200 + normal * 100 + run.Coins / 2 + run.Heroes.Count * 300) * diffMult);
        var bounty = run.Beacons.Contains(13) ? 1.5 : 1.0;
        var decision = (long)((cleared * 60 + (full ? 800 : (major - 1) * 250) + run.Artifacts.Count * 20) * expMult * bounty);
        var terminal = (long)((cleared * 80 + (full ? 1000 : (major - 1) * 350) + run.Artifacts.Count * 25) * expMult * bounty);
        await accounts.GrantItemsAsync(uid, [(45, decision), (46, terminal), (29, point)], ct);
        AddProgress(m, "score", point);
        if (full)
        {
            if (!m.Leaders.Contains(run.Leader)) m.Leaders.Add(run.Leader);
            if (!m.Difficulties.Contains(run.Difficulty)) m.Difficulties.Add(run.Difficulty);
        }
        run.State = 3;
        run.Settled = true;
        run.End = [point, decision, terminal];
        return run.End;
    }

    // ---------------- account operations ----------------

    public async Task SetTerminalsAsync(long uid, Meta m, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var terms = Contract.GetProperty("terminals");
        Require(ids.Distinct().Count() == ids.Count && ids.All(i => terms.TryGetProperty(i.ToString(), out _)), "Invalid polyhedron terminal");
        Require(m.Terminals.All(ids.Contains), "Removing terminals needs a reset");
        var lvl = TerminalLevel(await accounts.GetItemCountAsync(uid, 46, ct));
        Require(ids.Sum(i => terms.GetProperty(i.ToString()).GetProperty("cost").GetInt32()) <= TerminalPoints(lvl), "Not enough terminal points");
        Require(ids.All(i => terms.GetProperty(i.ToString()).GetProperty("need_level").GetInt32() <= lvl), "Terminal level too low");
        var reach = new HashSet<long> { 0 };
        while (ids.Where(i => !reach.Contains(i) && terms.GetProperty(i.ToString()).GetProperty("pre_id_list").EnumerateArray().Any(p => reach.Contains(p.GetInt64()))).ToList() is { Count: > 0 } added)
            reach.UnionWith(added);
        Require(ids.All(reach.Contains), "Terminal must connect to an unlocked root");
        m.Terminals = ids.ToList();
    }

    public void ResetTerminals(Meta m)
    {
        Require(m.ResetTimes > 0, "No terminal resets left");
        Require(m.Terminals.Count > 0, "Nothing to reset");
        m.Terminals.Clear();
        m.ResetTimes--;
    }

    public async Task UnlockBeaconAsync(long uid, Meta m, long beacon, CancellationToken ct)
    {
        Require(Contract.GetProperty("beacons").TryGetProperty(beacon.ToString(), out var cfg), "Polyhedron beacon does not exist");
        if (m.Beacons.Contains(beacon)) return;
        var cond = cfg.GetProperty("condition").GetInt64();
        if (cond != 0)
        {
            var c = Contract.GetProperty("conditions").GetProperty(cond.ToString());
            var need = c.GetProperty("params")[0].GetInt64();
            long value = c.GetProperty("type").GetInt32() switch
            {
                3001 => m.Difficulties.DefaultIfEmpty(0).Max(),
                3002 => TerminalLevel(await accounts.GetItemCountAsync(uid, 46, ct)),
                3003 => m.Progress.GetValueOrDefault("coins"),
                3004 => m.Progress.GetValueOrDefault("healthy_difficulty"),
                3005 => m.Progress.GetValueOrDefault("upgrade_gates"),
                3006 => m.Leaders.Count,
                3007 => m.Progress.GetValueOrDefault("score"),
                3008 => m.Progress.GetValueOrDefault("shop_buys"),
                3009 => m.Progress.GetValueOrDefault("kills"),
                _ => -1
            };
            Require(value >= need, "Polyhedron beacon condition not met");
        }
        m.Beacons.Add(beacon);
    }

    public void EquipAstrolabes(Meta m, long hero, IReadOnlyList<long> ids)
    {
        Require(m.Heroes.ContainsKey(hero), "Polyhedron hero is not unlocked");
        Require(ids.Count <= 3 && ids.Distinct().Count() == ids.Count, "Invalid astrolabe count");
        foreach (var id in ids)
        {
            Require(Contract.GetProperty("astrolabes").TryGetProperty(id.ToString(), out var c) && c.GetProperty("hero_astrolabe_suit_id").GetInt64() / 1000 == hero, "Astrolabe does not belong to the hero");
            var pre = c.GetProperty("pre_astrolabe_id").GetInt64();
            Require(pre == 0 || ids.Contains(pre), "Astrolabe prerequisite missing");
        }
        m.Heroes[hero] = ids.ToList();
    }

    public async Task UnlockHeroAsync(long uid, Meta m, long hero, CancellationToken ct)
    {
        Require(Cfg.GetProperty("poly_heroes").TryGetProperty(hero.ToString(), out _), "Polyhedron hero does not exist");
        var state = await accounts.GetInitialLoginStateAsync(uid, ct);
        Require(state?.Heroes.Any(h => h.Id == hero) == true, "Account does not own this hero");
        if (m.Heroes.ContainsKey(hero)) return;
        Require(await accounts.ConsumeAndGrantAsync(uid, [(44, 1)], [], ct), "Not enough mapping devices (44)");
        m.Heroes[hero] = [];
    }

    public async Task<IReadOnlyList<(long, long)>> ClaimPolicyAsync(long uid, Meta m, int type, int level, CancellationToken ct)
    {
        Require(type is 1 or 2, "Invalid claim type");
        var exp = await accounts.GetItemCountAsync(uid, 45, ct);
        var max = PolicyExp.TakeWhile(e => exp >= e).Count();
        var levels = type == 1 ? Enumerable.Range(1, max).Where(l => !m.PolicyClaimed.Contains(l)).ToList()
            : level >= 1 && level <= max && !m.PolicyClaimed.Contains(level) ? [level] : [];
        Require(type == 1 || levels.Count > 0, "Polyhedron decision level is not reached");
        m.PolicyClaimed.AddRange(levels);
        return levels.Select(PolicyReward).GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).ToArray();
    }

    // ---------------- payloads ----------------

    public Dictionary<string, object?> Snapshot(Meta m)
    {
        var game = new Dictionary<string, object?> { ["state"] = (uint)(m.Run is { State: 2 or 3 } r0 ? r0.State : 1) };
        if (m.Run is { State: 2 or 3 } run)
        {
            game["start_info"] = new Dictionary<string, object?>
            {
                ["leader"] = new Dictionary<string, object?>
                {
                    ["hero_id"] = (uint)run.Leader, ["star"] = 500u, ["astrolabe_list"] = run.LeaderAstrolabes.Select(x => (uint)x).ToArray(),
                    ["weapon"] = new Dictionary<string, object?> { ["breakthrough"] = 5u, ["servant"] = new Dictionary<string, object?> { ["id"] = 0u, ["stage"] = 1u } },
                    ["skin"] = 0u, ["break_level"] = 5u, ["weapon_module"] = new Dictionary<string, object?> { ["level"] = 0u }
                },
                ["beacon_id_list"] = run.Beacons.Select(x => (uint)x).ToArray(), ["difficulty"] = (uint)run.Difficulty,
                ["terminal_id_list"] = run.Terminals.Select(x => (uint)x).ToArray()
            };
            game["progress"] = Progress(run);
        }
        return new()
        {
            ["game"] = game,
            ["decision"] = new Dictionary<string, object?> { ["apply_id_list"] = m.PolicyClaimed.Select(x => (uint)x).ToArray() },
            ["terminal"] = new Dictionary<string, object?> { ["reset_times"] = (uint)m.ResetTimes, ["upgrade_id_list"] = m.Terminals.Select(x => (uint)x).ToArray() },
            ["beacon_id_list"] = m.Beacons.Select(x => (uint)x).ToArray(),
            ["unlocked_hero_list"] = m.Heroes.Select(h => (object)new Dictionary<string, object?> { ["hero_id"] = (uint)h.Key, ["astrolabe_id_list"] = h.Value.Select(x => (uint)x).ToArray() }).ToArray(),
            ["activity_id"] = ActivityId,
            ["clear_difficulty_list"] = m.Difficulties.OrderByDescending(x => x).Select(x => (uint)x).ToArray(),
            ["manual"] = new Dictionary<string, object?> { ["sample_list"] = m.Manual.Select(a => (object)new Dictionary<string, object?> { ["id"] = (uint)a.Key, ["state"] = (uint)a.Value }).ToArray() },
            ["already_challenge_times"] = 0u, ["is_new"] = (uint)m.IsNew
        };
    }

    private static object ItemRec(Item i) => new Dictionary<string, object?> { ["class"] = (uint)i.Class, ["params"] = i.Params.Select(x => (uint)x).ToArray() };
    private static Dictionary<string, object?> EvRec(Ev e) => new() { ["id"] = (uint)e.Id, ["stage_id"] = (uint)e.StageId, ["reward_type"] = (uint)e.RewardType };

    private Dictionary<string, object?> Progress(Run run)
    {
        var s = run.Stage;
        var stage = new Dictionary<string, object?>
        {
            ["stage_id"] = (uint)s.StageId, ["save_point"] = (uint)s.SavePoint,
            ["reward"] = new Dictionary<string, object?> { ["round"] = (uint)s.Round, ["item_list"] = s.Rewards.Select(ItemRec).ToArray() },
            ["params"] = s.Params.Select(x => (uint)x).ToArray(),
            ["gate_list"] = s.Gates.Select(g => (object)new Dictionary<string, object?> { ["index"] = (uint)g.Index, ["event"] = EvRec(g.Event) }).ToArray()
        };
        if (s.Shop is { } shop)
            stage["shop"] = new Dictionary<string, object?>
            {
                ["refresh_times"] = (uint)shop.Refresh, ["recover_times"] = (uint)shop.Recover,
                ["item_list"] = shop.Items.Select(i => (object)new Dictionary<string, object?> { ["item"] = ItemRec(i.Item), ["price"] = (uint)i.Price, ["is_available"] = (uint)i.Available }).ToArray()
            };
        return new()
        {
            ["tier_id"] = (uint)run.Tier, ["event"] = EvRec(run.Event), ["stage"] = stage,
            ["hero_list"] = run.Heroes.Select(h => (object)new Dictionary<string, object?>
            {
                ["hero_id"] = (uint)h.Id, ["template_id"] = (uint)h.Template, ["health"] = (uint)h.Health, ["max_health"] = (uint)h.MaxHealth
            }).ToArray(),
            ["fight_hero_id_list"] = run.Fight.Select(x => (uint)x).ToArray(),
            ["artifact_list"] = run.Artifacts.Select(a => (object)new Dictionary<string, object?> { ["id"] = (uint)a.Id, ["level"] = (uint)a.Level, ["life_time"] = 0u }).ToArray(),
            ["effect_list"] = run.Effects.Select(e => (object)new Dictionary<string, object?> { ["id"] = (uint)e }).ToArray(),
            ["attribute_list"] = Attributes(run).Where(a => a.Key is >= 1 and <= 34).Select(a => (object)new Dictionary<string, object?> { ["id"] = (uint)a.Key, ["value"] = (int)a.Value }).ToArray(),
            ["stackable_item_list"] = new object[] { new Dictionary<string, object?> { ["id"] = 1u, ["num"] = (uint)run.Coins } },
            ["cooperate_unique_skill_id"] = (uint)run.Combo
        };
    }

    public List<GameResponse> Frames(ProtoCodec codec, GameRequest r, Meta m, long[]? end)
    {
        var srv = (ushort)(r.ServerIndex + 1);
        var frames = new List<GameResponse>();
        if (end is { } e)
            frames.Add(new GameResponse(18005, 0, srv, codec.Encode("sc_18005", new Dictionary<string, object?>
            {
                ["end_info"] = new Dictionary<string, object?> { ["point"] = (uint)e[0], ["decision_exp"] = (uint)e[1], ["terminal_exp"] = (uint)e[2] }
            })));
        frames.Add(new GameResponse(18001, 0, srv, codec.Encode("sc_18001", Snapshot(m))));
        return frames;
    }

    /// <summary>Stage-52 settlement hook: a won fight advances the node (and may end the run); losses change nothing.</summary>
    public async Task<IReadOnlyList<GameResponse>> SettleBattleAsync(ProtoCodec codec, GameRequest r, bool win, BattleStats stats, CancellationToken ct)
    {
        var m = await LoadAsync(r.AccountId, ct);
        long[]? end = null;
        try
        {
            if (win && m.Run is { State: 2, Settled: false, Stage.SavePoint: 1 })
            {
                BattleWon(m, stats.EnemyDeadNum);
                if (m.Run!.State == 3) end = await SettleAsync(r.AccountId, m, ct);
            }
        }
        catch (PolyhedronError) { }
        await SaveAsync(r.AccountId, m, ct);
        var frames = Frames(codec, r, m, end);
        if (end is not null) frames.InsertRange(0, await InventorySync.FramesAsync(accounts, codec, r, [29, 45, 46], ct));
        return frames;
    }
}

/// <summary>18010–18034 run/account mutations and 66002/66004/66034/66036/66038; 66000 decision rewards.</summary>
public sealed class PolyhedronHandler(Polyhedron poly, GameAccountRepository accounts, ProtoCodec codec, uint command) : IGameCommandHandler
{
    public uint Command => command;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var uid = r.AccountId;
        var b = r.Body;
        int I(string k) => (int)HandlerHelpers.ReadUInt(b, k);
        var m = await poly.LoadAsync(uid, ct);
        long[]? end = null;
        IReadOnlyList<(long, long)> rewards = [];
        try
        {
            switch (command)
            {
                case 18010: poly.Start(m, HandlerHelpers.ReadIdList(b, "hero_id_list"), HandlerHelpers.ReadIdList(b, "beacon_id_list"), Math.Max(1, I("difficulty"))); break;
                case 18012:
                    poly.SelectReward(m, I("index"));
                    if (m.Run!.State == 3) end = await poly.SettleAsync(uid, m, ct);
                    break;
                case 18014: poly.SelectGate(m, I("index")); break;
                case 18016: m.Run = null; break;
                case 18018: end = await poly.SettleAsync(uid, m, ct); break;
                case 18020: poly.Enlist(m, I("hero_id")); break;
                case 18022: poly.Buy(m, I("index")); break;
                case 18024: poly.RefreshShop(m); break;
                case 18026: poly.RecoverShop(m); break;
                case 18028: poly.SwitchTeam(m, HandlerHelpers.ReadIdList(b, "fight_id_list")); break;
                case 18030: poly.Reroll(m); break;
                case 18032: await poly.SetTerminalsAsync(uid, m, HandlerHelpers.ReadIdList(b, "upgrade_id_list"), ct); break;
                case 18034: m.IsNew = 0; break;
                case 66000: rewards = await poly.ClaimPolicyAsync(uid, m, I("type"), I("level"), ct); break;
                case 66002: poly.ResetTerminals(m); break;
                case 66004: poly.EquipAstrolabes(m, I("hero_id"), HandlerHelpers.ReadIdList(b, "astrolabe_id_list")); break;
                case 66034: await poly.UnlockBeaconAsync(uid, m, I("beacon_id"), ct); break;
                case 66036: await poly.UnlockHeroAsync(uid, m, I("hero_id"), ct); break;
                case 66038: poly.SetCombo(m, I("cooperate_unique_skill_id")); break;
            }
        }
        catch (PolyhedronError)
        {
            return [HandlerHelpers.ResultOnly(codec, r, command + 1, 2)];
        }
        await poly.SaveAsync(uid, m, ct);
        var frames = new List<GameResponse>();
        var granted = rewards.Count > 0 ? await accounts.GrantItemsAsync(uid, rewards, ct) : [];
        var touched = granted.Select(x => x.Item1).Concat(end is null ? [] : new long[] { 29, 45, 46 }).Concat(command == 66036 ? new long[] { 44 } : []).ToArray();
        if (touched.Length > 0) frames.AddRange(await InventorySync.FramesAsync(accounts, codec, r, touched, ct));
        frames.AddRange(poly.Frames(codec, r, m, end));
        frames.Add(command == 66000
            ? HandlerHelpers.Reply(codec, r, 66001, new Dictionary<string, object?>
            {
                ["result"] = 0u, ["reward_list"] = HandlerHelpers.ToItemNetRecList(granted)
            })
            : HandlerHelpers.ResultOnly(codec, r, command + 1));
        return frames;
    }
}
