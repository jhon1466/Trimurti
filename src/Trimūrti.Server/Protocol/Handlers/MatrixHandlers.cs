using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 多维变量 / 矩阵 (matrix roguelike, 48xxx). Config: matrix_cfg.json, extracted from the client MatrixTierCfg, MatrixEventCfg,
/// MatrixChanceCfg, MatrixItemCfg, MatrixPriceTemplateCfg, MatrixEffectCfg, MatrixPointRankCfg, MatrixBeaconCfg,
/// MatrixTerminalGiftCfg, MatrixTerminalLevelCfg, MatrixEvaluateCfg and GameSetting matrix_shop_refresh_cost_item_list.
///
/// Semantics follow MatrixAction/MatrixData/MatrixDataTemplate (MatrixConst):
///   game_state 1 NOTSTARTED, 2 STARTED, 3 SUCCESS, 4 FAIL; phase 1 CHOSE (event_id_list), 2 ACTION (event + envent_params),
///   3 REWARD (matrix_item_id_list + give_up_matrix_item_id).
///   A run walks the tiers 1..4, levels 1..10 (MatrixTierCfg ids tier·1000+level). Level 10 is the tier boss (event type 9,
///   tier 4: final boss type 10). Other levels offer 3 events (+attribute 7) of that tier (MatrixEventCfg id type·1000+tier).
///   Battle events: envent_params = [stage]; the client launches BattleMatrixStageCfg[stage] and settlement opens the REWARD
///   phase with items of the event's kind (or ends the run). Shop (11): envent_params = [refreshes, item…] priced through
///   MatrixPriceTemplateCfg (difficulty template) or MatrixItemCfg.price, paid in 喵喵宝藏币 (item 26).
///   Incident (12): envent_params = [title]; the chosen MatrixChanceCfg option applies its toll effect.
/// 48010 params by phase: CHOSE [event_id]; ACTION shop [1] refresh / [2, index, hero, replace] buy / [0] leave;
///   ACTION incident [chance_id]; REWARD [item_id (or give-up item), hero, replace].
/// 48012 start, 48014/48016 point rank rewards, 48018 unlock beacon, 48020 settle a finished run (48005 end info),
/// 48022 set terminal gifts, 48024 give up. Terminal level comes from item 34 (MATRIX_TERMINAL_EXP); PT is item 29.
/// The server-side tables the client does not ship (point rank reward items, end-of-run PT formula, ready trial heroes,
/// per-difficulty affix lists) are not invented: rank rewards carry no items, PT/EXP equal the run score (coins gained),
/// the ready list is empty (the player brings own heroes) and difficulties expose only their price template.
/// </summary>
public sealed class MatrixMode(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
    : EventModeBase(accounts, config, codec, "matrix_cfg.json"), IStageMode
{
    public const long Coin = 26, Pt = 29, TerminalExp = 34;
    private const uint NotStarted = 1, Started = 2, Success = 3, Fail = 4;
    private const uint Chose = 1, Action = 2, RewardPhase = 3;
    private const uint EvBoss = 9, EvFinal = 10, EvShop = 11, EvIncident = 12;
    private const uint AttrMaxEquip = 2, AttrMaxAstrolabe = 3, AttrExtraEvent = 7, AttrShopGoods = 8, AttrShopFree = 10,
        AttrFirstDiscount = 11, AttrGiveUpAddition = 14, AttrScoreAddition = 17;
    private const uint FullHp = 10000;
    private const string Kind = "matrix_v1";

    public static readonly uint[] Commands = [48010, 48012, 48014, 48016, 48018, 48020, 48022, 48024];

    public sealed class Hero
    {
        public uint Id { get; set; }
        public uint Hp { get; set; } = FullHp;
        public uint MaxHp { get; set; } = FullHp;
        public List<uint> Astrolabes { get; set; } = [];
        public List<uint> Equips { get; set; } = [];
        public uint Servant { get; set; }
        public List<uint> Effects { get; set; } = [];
    }

    public sealed class Run
    {
        public uint Tier { get; set; }
        public uint Difficulty { get; set; }
        public List<uint> Affixes { get; set; } = [];
        public List<uint> Beacons { get; set; } = [];
        public List<Hero> Heroes { get; set; } = [];
        public Dictionary<uint, uint> Artifacts { get; set; } = [];
        public Dictionary<uint, int> Attributes { get; set; } = [];
        public Dictionary<uint, uint> Gifts { get; set; } = [];
        public uint Phase { get; set; }
        public List<uint> Events { get; set; } = [];
        public uint Event { get; set; }
        public List<uint> EventParams { get; set; } = [];
        public List<uint> RewardItems { get; set; } = [];
        public uint GiveUpItem { get; set; }
        public long StartTime { get; set; }
        public long EndTime { get; set; }
        public long CoinsGained { get; set; }
        public uint Losses { get; set; }
        public Dictionary<uint, uint> Picks { get; set; } = [];
        public List<uint> ItemKinds { get; set; } = [];
    }

    public sealed class State
    {
        public uint GameState { get; set; } = NotStarted;
        public Run? Run { get; set; }
        public List<uint> RankClaimed { get; set; } = [];
        public List<uint> Beacons { get; set; } = [];
        public Dictionary<uint, uint> Gifts { get; set; } = [];
        public Dictionary<uint, uint> Completes { get; set; } = [];
        public Dictionary<uint, uint> MinClear { get; set; } = [];
    }

    private readonly Random _rng = new();

    // ---------------------------------------------------------------- config access
    private JsonElement Section(string name) => Cfg.GetProperty(name);
    private JsonElement? Row(string section, uint id) => Section(section).TryGetProperty(id.ToString(), out var v) ? v : null;
    private static uint U(JsonElement e) => e.ValueKind == JsonValueKind.Number ? (uint)e.GetInt64() : 0;
    private static int I(JsonElement e) => unchecked((int)(uint)e.GetInt64());
    private static List<uint> UList(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(U).ToList() : [];

    private HashSet<long>? _stages;
    private HashSet<long> Stages => _stages ??= Section("events").EnumerateObject()
        .Where(e => U(e.Value.GetProperty("event_type")) <= EvFinal)
        .SelectMany(e => e.Value.GetProperty("params").EnumerateArray().Select(p => p.GetInt64())).ToHashSet();

    private uint EventType(uint ev) => Row("events", ev) is { } r ? U(r.GetProperty("event_type")) : 0;
    private uint ItemType(uint item) => Row("items", item) is { } r ? U(r.GetProperty("type")) : 0;

    private uint TerminalLevel(long exp)
    {
        uint level = 1;
        foreach (var l in Section("terminal_levels").EnumerateObject())
            if (exp >= l.Value.GetProperty("exp").GetInt64()) level = Math.Max(level, uint.Parse(l.Name));
        return level;
    }

    private JsonElement LevelRow(uint level) => Row("terminal_levels", level) ?? Row("terminal_levels", 1)!.Value;

    private static long NextWeekly()
    {
        var now = GameClock.Now;
        var day = new DateTimeOffset(now.Date, now.Offset) + TimeSpan.FromHours(5);
        var monday = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        if (monday <= now) monday = monday.AddDays(7);
        return monday.ToUnixTimeSeconds();
    }

    // ---------------------------------------------------------------- state
    private async Task<State> LoadAsync(long uid, CancellationToken ct)
    {
        var s = await Accounts.GetJsonAsync<State>(uid, Kind, ct) ?? new State();
        foreach (var b in Section("beacons").EnumerateObject())
            if (U(b.Value.GetProperty("new_condition")) == 0 && !s.Beacons.Contains(uint.Parse(b.Name))) s.Beacons.Add(uint.Parse(b.Name));
        return s;
    }

    private Task SaveAsync(long uid, State s, CancellationToken ct) => Accounts.SetJsonAsync(uid, Kind, s, ct);

    private int Attr(Run r, uint id) => r.Attributes.GetValueOrDefault(id);

    // ---------------------------------------------------------------- records
    private static object?[] Arr(IEnumerable<uint> xs) => xs.Select(x => (object?)x).ToArray();
    private static Dictionary<string, object?> GiftList(IEnumerable<(uint Id, uint Level)> gifts) => new()
    {
        ["gift_list"] = gifts.OrderBy(g => g.Id).Select(g => (object?)new Dictionary<string, object?> { ["id"] = g.Id, ["level"] = g.Level }).ToArray()
    };

    private Dictionary<string, object?> DifficultyRec(uint d) => new()
    {
        ["difficulty"] = d, ["regular_affix_list"] = Array.Empty<object?>(), ["ai_chip_list"] = Array.Empty<object?>(),
        ["custom_affix_list"] = Array.Empty<object?>(),
        ["price_template_id"] = Section("price_templates").TryGetProperty(d.ToString(), out _) ? d : 0u
    };

    private Dictionary<string, object?> SystemRec(uint terminalLevel) => new()
    {
        ["ready_hero_list"] = Array.Empty<object?>(),
        ["difficulty_list"] = UList(LevelRow(Math.Max(terminalLevel, LastLevel)).GetProperty("difficulty_open_list")).Select(d => (object?)DifficultyRec(d)).ToArray(),
        ["next_refresh_timestamp"] = (uint)NextWeekly()
    };

    private uint LastLevel => Section("terminal_levels").EnumerateObject().Max(l => uint.Parse(l.Name));

    private static Dictionary<string, object?> HeroRec(Hero h) => new()
    {
        ["hero_id"] = h.Id, ["health_point"] = h.Hp, ["max_health_point"] = h.MaxHp,
        ["astrolabe_list"] = Arr(h.Astrolabes), ["equip_list"] = Arr(h.Equips), ["weapon_servant"] = h.Servant,
        ["effect_list"] = Arr(h.Effects), ["total_damage"] = 0u, ["total_heal"] = 0u, ["total_injured"] = 0u
    };

    private Dictionary<string, object?> ProgressRec(Run? r) => r is null
        ? new()
        {
            ["tier_id"] = 0u, ["artifact_list"] = Array.Empty<object?>(), ["phase"] = new Dictionary<string, object?> { ["phase_id"] = 0u },
            ["hero_list"] = Array.Empty<object?>(), ["difficulty"] = DifficultyRec(0), ["custom_affix_id_list"] = Array.Empty<object?>(),
            ["attribute_list"] = Array.Empty<object?>(), ["beacon_id_list"] = Array.Empty<object?>(), ["current_terminal"] = GiftList([])
        }
        : new()
        {
            ["tier_id"] = r.Tier,
            ["artifact_list"] = r.Artifacts.OrderBy(a => a.Key).Select(a => (object?)new Dictionary<string, object?> { ["id"] = a.Key, ["wins"] = a.Value }).ToArray(),
            ["phase"] = PhaseRec(r),
            ["hero_list"] = r.Heroes.Select(h => (object?)HeroRec(h)).ToArray(),
            ["difficulty"] = DifficultyRec(r.Difficulty),
            ["custom_affix_id_list"] = Arr(r.Affixes),
            ["attribute_list"] = r.Attributes.OrderBy(a => a.Key).Select(a => (object?)new Dictionary<string, object?> { ["id"] = a.Key, ["value"] = a.Value }).ToArray(),
            ["beacon_id_list"] = Arr(r.Beacons),
            ["current_terminal"] = GiftList(r.Gifts.Select(g => (g.Key, g.Value)))
        };

    private static Dictionary<string, object?> PhaseRec(Run r)
    {
        var p = new Dictionary<string, object?> { ["phase_id"] = r.Phase, ["event_id_list"] = Arr(r.Phase == Chose ? r.Events : []) };
        if (r.Phase == Action) p["event"] = new Dictionary<string, object?> { ["event_id"] = r.Event, ["params"] = Arr(r.EventParams) };
        if (r.Phase == RewardPhase)
        {
            var rw = new Dictionary<string, object?> { ["matrix_item_id_list"] = Arr(r.RewardItems) };
            if (r.GiveUpItem != 0) rw["give_up_matrix_item_id"] = r.GiveUpItem;
            p["reward"] = rw;
        }
        return p;
    }

    private static IReadOnlyList<(long ItemId, long Count)> PointRankRewards(uint rank) => rank switch
    {
        1 => [(2, 10000), (40103, 5)],
        2 => [(40803, 5), (40503, 5)],
        3 => [(2, 15000), (40203, 5)],
        4 => [(40603, 5), (40803, 6)],
        5 => [(1, 100), (2, 20000)],
        6 => [(40103, 8), (40503, 6)],
        7 => [(2, 20000), (40803, 8)],
        8 => [(40203, 8), (40603, 6)],
        9 => [(2, 25000), (40503, 8)],
        10 => [(1, 150), (40803, 10)],
        11 => [(2, 25000), (40103, 10)],
        12 => [(40603, 8), (40203, 10)],
        13 => [(2, 30000), (40503, 10)],
        14 => [(40803, 12), (40603, 10)],
        15 => [(1, 200), (2, 30000)],
        16 => [(40103, 12), (40203, 12)],
        17 => [(2, 35000), (40803, 15)],
        18 => [(40503, 12), (40603, 12)],
        19 => [(2, 40000), (40803, 15)],
        _ => [(1, 300), (2, 50000)]
    };

    private Dictionary<string, object?> UserRec(State s) => new()
    {
        ["game_state"] = s.GameState,
        ["current_progress"] = ProgressRec(s.Run),
        ["point_reward_list"] = Section("point_ranks").EnumerateObject().OrderBy(p => uint.Parse(p.Name)).Select(p =>
        {
            var rank = uint.Parse(p.Name);
            return (object?)new Dictionary<string, object?>
            {
                ["rank"] = rank,
                ["item_list"] = InventorySync.Rewards(PointRankRewards(rank)),
                ["is_got_reward"] = s.RankClaimed.Contains(rank) ? 1u : 0u,
                ["need_level"] = 1u
            };
        }).ToArray(),
        ["terminal"] = GiftList(s.Gifts.Select(g => (g.Key, g.Value))),
        ["unlocked_beacon_id_list"] = Arr(s.Beacons.OrderBy(x => x))
    };

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(ProtoCodec codec, long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        var level = TerminalLevel(await Accounts.GetItemCountAsync(uid, TerminalExp, ct));
        return [Frame(48001, new() { ["matrix_system"] = SystemRec(level) }), Frame(48003, new() { ["user_matrix"] = UserRec(s) })];
    }

    // ---------------------------------------------------------------- run flow
    private void OpenChoice(Run r)
    {
        var tier = r.Tier / 1000;
        var level = r.Tier % 1000;
        r.Phase = Chose;
        r.Event = 0;
        r.EventParams = [];
        r.RewardItems = [];
        r.GiveUpItem = 0;
        if (level >= 10)
        {
            var boss = tier >= 4 ? EvFinal * 1000 + 1 : EvBoss * 1000 + tier;
            r.Events = Row("events", boss) is not null ? [boss] : [];
            return;
        }
        var pool = Section("events").EnumerateObject().Select(e => uint.Parse(e.Name))
            .Where(id => id % 1000 == tier && EventType(id) is not (EvBoss or EvFinal)).ToList();
        var count = 3 + Math.Max(0, Attr(r, AttrExtraEvent));
        r.Events = pool.OrderBy(_ => _rng.Next()).Take(count).OrderBy(x => x).ToList();
    }

    private void Advance(State s, Run r)
    {
        var next = r.Tier % 1000 >= 10 ? (r.Tier / 1000 + 1) * 1000 + 1 : r.Tier + 1;
        if (Row("tiers", next) is null) { Finish(s, r, true); return; }
        r.Tier = next;
        OpenChoice(r);
    }

    private void Finish(State s, Run r, bool win)
    {
        s.GameState = win ? Success : Fail;
        r.EndTime = GameClock.Now.ToUnixTimeSeconds();
        r.Phase = 0;
        r.Events = [];
        r.EventParams = [];
        r.RewardItems = [];
    }

    private List<(uint Item, uint Price, long Currency)> ShopGoods(Run r) =>
        r.EventParams.Skip(1).Select(id => id == 0 ? (0u, 0u, 0L) : (id, Price(r, id).Num, Price(r, id).Id)).ToList();

    private (long Id, uint Num) Price(Run r, uint item)
    {
        JsonElement? price = null;
        if (Section("price_templates").TryGetProperty(r.Difficulty.ToString(), out var t) && t.TryGetProperty(item.ToString(), out var p)) price = p;
        else if (Row("items", item) is { } row) price = row.GetProperty("price");
        if (price is not { ValueKind: JsonValueKind.Array } a || a.GetArrayLength() == 0) return (Coin, 0);
        return (a[0][0].GetInt64(), U(a[0][1]));
    }

    private List<uint> RollShop(Run r, JsonElement ev)
    {
        var weights = ev.GetProperty("params").EnumerateArray().Select(x => (Cat: U(x[0]), W: U(x[1]))).ToList();
        var count = 6 + Math.Max(0, Attr(r, AttrShopGoods));
        var goods = new List<uint>();
        for (var i = 0; i < count && weights.Count > 0; i++)
        {
            var roll = _rng.Next((int)weights.Sum(w => w.W));
            var cat = weights.First(w => (roll -= (int)w.W) < 0).Cat;
            var pick = ItemsOf(cat / 100, cat % 100).Where(x => !goods.Contains(x)).ToList();
            if (pick.Count > 0) goods.Add(pick[_rng.Next(pick.Count)]);
        }
        return goods;
    }

    private List<uint> ItemsOf(uint type, uint rare) => Section("items").EnumerateObject()
        .Where(i => U(i.Value.GetProperty("type")) == type && (rare == 0 || U(i.Value.GetProperty("rare")) == rare)).Select(i => uint.Parse(i.Name)).ToList();

    private static readonly Dictionary<uint, (uint ItemType, bool Elite)> BattleReward = new()
    {
        [1] = (1, false), [2] = (2, false), [3] = (3, false), [4] = (3, true), [5] = (4, false), [6] = (4, true),
        [7] = (5, false), [8] = (5, true), [9] = (5, true)
    };

    private void OpenReward(Run r, uint eventType)
    {
        var (type, elite) = BattleReward[eventType];
        var pool = ItemsOf(type, 0).Where(i => elite ? Rare(i) >= 2 : Rare(i) <= 2).ToList();
        if (pool.Count == 0) pool = ItemsOf(type, 0);
        r.Phase = RewardPhase;
        r.RewardItems = pool.OrderBy(_ => _rng.Next()).Take(3).ToList();
        var giveUp = ItemsOf(4, 1);
        r.GiveUpItem = type == 4 || giveUp.Count == 0 ? 0 : giveUp.Min();
        r.Events = [];
        r.EventParams = [];
    }

    private uint Rare(uint item) => Row("items", item) is { } x ? U(x.GetProperty("rare")) : 0;

    /// <summary>Applies one MatrixEffectCfg entry (actions 1 attribute, 2 team HP ‰, 7 coins; others stay on the heroes).</summary>
    private async Task ApplyEffectAsync(long uid, Run r, uint effect, CancellationToken ct)
    {
        if (effect == 0 || Row("effects", effect) is not { } e) return;
        var p = e.GetProperty("params");
        switch (U(e.GetProperty("action")))
        {
            case 1:
                var id = U(p[0]);
                r.Attributes[id] = r.Attributes.GetValueOrDefault(id) + I(p[1]);
                break;
            case 2:
                var rate = I(p[0]);
                foreach (var h in r.Heroes)
                    h.Hp = (uint)Math.Clamp(h.Hp + (long)h.MaxHp * rate / 1000, 1, h.MaxHp);
                break;
            case 7:
                foreach (var pair in p.EnumerateArray())
                {
                    var item = pair[0].GetInt64();
                    var delta = (long)I(pair[1]);
                    var have = await Accounts.GetItemCountAsync(uid, item, ct);
                    delta = Math.Max(-have, delta);
                    if (delta != 0) await Accounts.AddItemAsync(uid, item, delta, ct);
                    if (item == Coin && delta > 0) r.CoinsGained += delta;
                }
                break;
            default:
                foreach (var h in r.Heroes) h.Effects.Add(effect);
                break;
        }
    }

    /// <summary>Gives one matrix item to the run: 1 神格 / 2 刻印 / 3 钥从 on a hero, 4 coins, 5 treasure, 6 effect.</summary>
    private async Task<bool> GiveItemAsync(long uid, Run r, uint item, uint heroId, uint replace, CancellationToken ct)
    {
        if (Row("items", item) is not { } row) return false;
        var p = row.GetProperty("params");
        var type = U(row.GetProperty("type"));
        Hero? Target() => r.Heroes.FirstOrDefault(h => h.Id == heroId) ?? (heroId == 0 ? r.Heroes.FirstOrDefault() : null);
        switch (type)
        {
            case 1:
            case 2:
            {
                if (Target() is not { } h) return false;
                var list = type == 1 ? h.Astrolabes : h.Equips;
                var max = type == 1 ? 3 + Attr(r, AttrMaxAstrolabe) : 2 + Attr(r, AttrMaxEquip);
                var val = U(p[0]);
                if (list.Count >= max)
                {
                    var at = list.IndexOf(replace);
                    if (at < 0) return false;
                    list[at] = val;
                }
                else list.Add(val);
                break;
            }
            case 3:
                if (Target() is not { } hs) return false;
                hs.Servant = U(p[0]);
                break;
            case 4:
                foreach (var pair in p.EnumerateArray())
                {
                    var n = pair[1].GetInt64();
                    if (pair[0].GetInt64() == Coin) r.CoinsGained += n;
                    await Accounts.AddItemAsync(uid, pair[0].GetInt64(), n, ct);
                }
                break;
            case 5:
                r.Artifacts[item] = 0;
                foreach (var eff in UList(p)) await ApplyEffectAsync(uid, r, eff, ct);
                break;
            case 6:
                foreach (var eff in UList(p)) await ApplyEffectAsync(uid, r, eff, ct);
                break;
        }
        r.ItemKinds.Add(type);
        return true;
    }

    // ---------------------------------------------------------------- battles
    public bool Reserved(uint type, long stage) => Stages.Contains(stage);

    public async Task<(List<GameResponse>, IReadOnlyList<(long, long)>)> SettleAsync(ModeBattle b, CancellationToken ct)
    {
        var s = await LoadAsync(b.Uid, ct);
        var r = s.Run;
        Require(s.GameState == Started && r is not null && r.Phase == Action && r.EventParams.FirstOrDefault() == b.StageId, "Matrix battle not active");
        var type = EventType(r!.Event);
        var drops = new List<(long, long)>();
        if (!b.Win)
        {
            r.Losses++;
            Finish(s, r, false);
        }
        else
        {
            foreach (var art in r.Artifacts.Keys.ToList()) r.Artifacts[art]++;
            if (Row("tiers", r.Tier) is { } tier && tier.GetProperty("level_reward") is { ValueKind: JsonValueKind.Array } lr && lr.GetArrayLength() > 0)
            {
                var idx = type is EvBoss or EvFinal ? 2 : type is 4 or 6 or 8 ? 1 : 0;
                var coins = lr[Math.Min(idx, lr.GetArrayLength() - 1)].GetInt64();
                coins += coins * Attr(r, AttrScoreAddition) / 1000;
                if (coins > 0)
                {
                    await Accounts.AddItemAsync(b.Uid, Coin, coins, ct);
                    r.CoinsGained += coins;
                    drops.Add((Coin, coins));
                }
            }
            if (type == EvFinal) Finish(s, r, true);
            else OpenReward(r, type);
        }
        await SaveAsync(b.Uid, s, ct);
        var frames = await SyncAsync(b.Request, drops.Select(d => d.Item1), ct);
        frames.Add(Push(b.Srv, 48003, new() { ["user_matrix"] = UserRec(s) }));
        return (frames, drops);
    }

    // ---------------------------------------------------------------- commands
    private static List<uint> ReadList(GameRequest r, string name) =>
        r.Body.TryGetValue(name, out var v) && v is IEnumerable<object?> e ? e.Select(x => Convert.ToUInt32(x)).ToList() : [];

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest req, uint cmd, CancellationToken ct)
    {
        var uid = req.AccountId;
        var srv = (ushort)(req.ServerIndex + 1);
        var s = await LoadAsync(uid, ct);
        var frames = new List<GameResponse>();
        var touched = new List<long>();
        uint result = 0;
        var userPush = false;
        try
        {
            switch (cmd)
            {
                case 48010: result = await ProgressAsync(uid, s, ReadList(req, "params"), touched, ct); userPush = result == 0; break;
                case 48012: result = await StartAsync(uid, s, req, ct); userPush = result == 0; break;
                case 48014: result = await ClaimRanksAsync(uid, s, [HandlerHelpers.ReadUInt(req.Body, "rank")], touched, ct); userPush = result == 0; break;
                case 48016: result = await ClaimRanksAsync(uid, s, ReadList(req, "rank_list"), touched, ct); userPush = result == 0; break;
                case 48018:
                {
                    var beacon = HandlerHelpers.ReadUInt(req.Body, "beacon_id");
                    if (Row("beacons", beacon) is not { } row || s.Beacons.Contains(beacon)) { result = 1; break; }
                    var need = UList(row.GetProperty("require_terminal_list"));
                    if (need.Any(g => s.Gifts.GetValueOrDefault(g) == 0)) { result = 1; break; }
                    s.Beacons.Add(beacon);
                    userPush = true;
                    break;
                }
                case 48020:
                {
                    if (s.GameState is not (Success or Fail) || s.Run is null) { result = 1; break; }
                    var end = EndInfo(s, s.Run);
                    var gain = (long)(uint)end["score"]!;
                    if (gain > 0)
                    {
                        await Accounts.AddItemAsync(uid, Pt, gain, ct);
                        await Accounts.AddItemAsync(uid, TerminalExp, gain, ct);
                        touched.Add(Pt);
                        touched.Add(TerminalExp);
                    }
                    var coins = await Accounts.GetItemCountAsync(uid, Coin, ct);
                    if (coins > 0) { await Accounts.AddItemAsync(uid, Coin, -coins, ct); touched.Add(Coin); }
                    s.GameState = NotStarted;
                    s.Run = null;
                    frames.Add(Push(srv, 48005, new() { ["end_info"] = end }));
                    userPush = true;
                    break;
                }
                case 48022:
                {
                    var gifts = new Dictionary<uint, uint>();
                    if (req.Body.TryGetValue("gift_list", out var gl) && gl is IEnumerable<object?> list)
                        foreach (var g in list.OfType<IReadOnlyDictionary<string, object?>>())
                            gifts[HandlerHelpers.ReadUInt(g, "id")] = HandlerHelpers.ReadUInt(g, "level");
                    var level = TerminalLevel(await Accounts.GetItemCountAsync(uid, TerminalExp, ct));
                    if (!ValidGifts(gifts, level)) { result = 1; break; }
                    s.Gifts = gifts.Where(g => g.Value > 0).ToDictionary(g => g.Key, g => g.Value);
                    break;
                }
                case 48024:
                    if (s.GameState != Started || s.Run is null) { result = 1; break; }
                    Finish(s, s.Run, false);
                    userPush = true;
                    break;
            }
        }
        catch (ModeError) { result = 1; }
        if (result == 0) await SaveAsync(uid, s, ct);
        else s = await LoadAsync(uid, ct);
        if (touched.Count > 0) frames.InsertRange(0, await SyncAsync(req, touched, ct));
        if (userPush) frames.Add(Push(srv, 48003, new() { ["user_matrix"] = UserRec(s) }));
        var reply = new Dictionary<string, object?> { ["result"] = result };
        if (cmd == 48010 && result == 0) reply["user_matrix"] = UserRec(s);
        frames.Add(HandlerHelpers.Reply(Codec, req, cmd + 1, reply));
        return frames;
    }

    private bool ValidGifts(Dictionary<uint, uint> gifts, uint level)
    {
        long spent = 0;
        foreach (var (id, lv) in gifts)
        {
            if (lv == 0) continue;
            if (Row("terminal_gifts", id) is not { } g) return false;
            var cost = UList(g.GetProperty("cost"));
            if (lv > cost.Count || U(g.GetProperty("limit_level")) > level) return false;
            var pre = U(g.GetProperty("pre_id"));
            if (pre != 0 && gifts.GetValueOrDefault(pre) == 0) return false;
            spent += cost.Take((int)lv).Sum(c => (long)c);
        }
        return spent <= LevelRow(level).GetProperty("point").GetInt64();
    }

    private async Task<uint> StartAsync(long uid, State s, GameRequest req, CancellationToken ct)
    {
        if (s.GameState != NotStarted) return 1;
        var heroes = ReadList(req, "hero_id_list");
        var difficulty = HandlerHelpers.ReadUInt(req.Body, "difficulty");
        var beacons = ReadList(req, "beacon_id_list");
        var level = TerminalLevel(await Accounts.GetItemCountAsync(uid, TerminalExp, ct));
        var lv = LevelRow(level);
        var owned = (await Accounts.GetOwnedHeroIdsAsync(uid, ct)).ToHashSet();
        if (heroes.Count == 0 || heroes.Count > 3 || heroes.Distinct().Count() != heroes.Count || heroes.Any(h => !owned.Contains(h))) return 1;
        if (!UList(lv.GetProperty("difficulty_open_list")).Contains(difficulty)) return 1;
        if (beacons.Count > U(lv.GetProperty("beacon_number")) || beacons.Any(b => !s.Beacons.Contains(b))) return 1;
        var run = new Run
        {
            Tier = 1001, Difficulty = difficulty, Affixes = ReadList(req, "custom_affix_id_list"), Beacons = beacons,
            Heroes = heroes.Select(h => new Hero { Id = h }).ToList(), Gifts = new Dictionary<uint, uint>(s.Gifts),
            StartTime = GameClock.Now.ToUnixTimeSeconds()
        };
        foreach (var b in beacons)
            foreach (var eff in UList(Row("beacons", b)!.Value.GetProperty("effect_id_list"))) await ApplyEffectAsync(uid, run, eff, ct);
        foreach (var (gift, glv) in run.Gifts)
            if (Row("terminal_gifts", gift) is { } g && UList(g.GetProperty("effect_id_list")) is { } effs && glv <= effs.Count)
                await ApplyEffectAsync(uid, run, effs[(int)glv - 1], ct);
        var coins = await Accounts.GetItemCountAsync(uid, Coin, ct);
        if (coins > 0) await Accounts.AddItemAsync(uid, Coin, -coins, ct);
        s.Run = run;
        s.GameState = Started;
        OpenChoice(run);
        return 0;
    }

    private async Task<uint> ClaimRanksAsync(long uid, State s, List<uint> ranks, List<long> touched, CancellationToken ct)
    {
        var pt = await Accounts.GetItemCountAsync(uid, Pt, ct);
        if (ranks.Count == 0) return 1;
        var rewards = new List<(long, long)>();
        foreach (var rank in ranks.Distinct())
        {
            if (s.RankClaimed.Contains(rank) || !Section("point_ranks").TryGetProperty(rank.ToString(), out var need) || pt < need.GetInt64()) return 1;
            rewards.AddRange(PointRankRewards(rank));
        }
        var merged = rewards.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).ToList();
        if (merged.Count > 0)
        {
            await Accounts.GrantItemsAsync(uid, merged, ct);
            touched.AddRange(merged.Select(x => x.Item1));
        }
        s.RankClaimed.AddRange(ranks.Distinct());
        return 0;
    }

    private async Task<uint> ProgressAsync(long uid, State s, List<uint> p, List<long> touched, CancellationToken ct)
    {
        if (s.GameState != Started || s.Run is not { } r) return 1;
        var first = p.Count > 0 ? p[0] : 0;
        switch (r.Phase)
        {
            case Chose:
            {
                if (!r.Events.Contains(first) || Row("events", first) is not { } ev) return 1;
                var type = U(ev.GetProperty("event_type"));
                r.Picks[type] = r.Picks.GetValueOrDefault(type) + 1;
                r.Phase = Action;
                r.Event = first;
                r.Events = [];
                var pars = ev.GetProperty("params");
                r.EventParams = type switch
                {
                    EvShop => [0, .. RollShop(r, ev)],
                    _ => pars.GetArrayLength() == 0 ? [] : [U(pars[_rng.Next(pars.GetArrayLength())])]
                };
                return 0;
            }
            case Action:
            {
                var type = EventType(r.Event);
                if (type == EvIncident)
                {
                    var title = r.EventParams.FirstOrDefault();
                    if (Row("chances", first) is not { } ch || U(ch.GetProperty("title_id")) != title) return 1;
                    await ApplyEffectAsync(uid, r, U(ch.GetProperty("toll")), ct);
                    touched.Add(Coin);
                    Advance(s, r);
                    return 0;
                }
                if (type != EvShop) return 1;
                if (first == 1)
                {
                    var used = r.EventParams[0];
                    var free = (uint)Math.Max(0, Attr(r, AttrShopFree));
                    if (used >= free)
                    {
                        var costs = Cfg.GetProperty("settings").GetProperty("matrix_shop_refresh_cost_item_list");
                        var c = costs[(int)Math.Min(used - free, (uint)costs.GetArrayLength() - 1)];
                        if (!await Accounts.ConsumeAndGrantAsync(uid, [(c[0].GetInt64(), c[1].GetInt64())], [], ct)) return 1;
                        touched.Add(c[0].GetInt64());
                    }
                    r.EventParams = [used + 1, .. RollShop(r, Row("events", r.Event)!.Value)];
                    return 0;
                }
                if (first == 2)
                {
                    var index = p.Count > 1 ? (int)p[1] : 0;
                    var goods = ShopGoods(r);
                    if (index < 1 || index > goods.Count || goods[index - 1].Item == 0) return 1;
                    var (item, price, currency) = goods[index - 1];
                    var bought = r.EventParams.Skip(1).Count(x => x == 0);
                    if (bought == 0 && Attr(r, AttrFirstDiscount) > 0)
                        price -= (uint)Math.Floor(price * Attr(r, AttrFirstDiscount) / 1000.0);
                    if (price > 0 && !await Accounts.ConsumeAndGrantAsync(uid, [(currency, price)], [], ct)) return 1;
                    touched.Add(currency);
                    if (!await GiveItemAsync(uid, r, item, p.Count > 2 ? p[2] : 0, p.Count > 3 ? p[3] : 0, ct))
                    {
                        if (price > 0) await Accounts.AddItemAsync(uid, currency, price, ct);
                        return 1;
                    }
                    touched.Add(Coin);
                    r.EventParams[index] = 0;
                    return 0;
                }
                Advance(s, r);
                return 0;
            }
            case RewardPhase:
            {
                if (first != 0 && first == r.GiveUpItem)
                {
                    var giveUp = Row("items", first)!.Value.GetProperty("params");
                    foreach (var pair in giveUp.EnumerateArray())
                    {
                        var n = pair[1].GetInt64();
                        n += n * Attr(r, AttrGiveUpAddition) / 1000;
                        await Accounts.AddItemAsync(uid, pair[0].GetInt64(), n, ct);
                        if (pair[0].GetInt64() == Coin) r.CoinsGained += n;
                        touched.Add(pair[0].GetInt64());
                    }
                }
                else
                {
                    if (!r.RewardItems.Contains(first)) return 1;
                    if (!await GiveItemAsync(uid, r, first, p.Count > 1 ? p[1] : 0, p.Count > 2 ? p[2] : 0, ct)) return 1;
                    touched.Add(Coin);
                }
                Advance(s, r);
                return 0;
            }
        }
        return 1;
    }

    private Dictionary<string, object?> EndInfo(State s, Run r)
    {
        var win = s.GameState == Success;
        var clear = (uint)Math.Max(0, (r.EndTime == 0 ? GameClock.Now.ToUnixTimeSeconds() : r.EndTime) - r.StartTime);
        if (win)
        {
            s.Completes[r.Difficulty] = s.Completes.GetValueOrDefault(r.Difficulty) + 1;
            s.MinClear[r.Difficulty] = s.MinClear.TryGetValue(r.Difficulty, out var m) && m > 0 ? Math.Min(m, clear) : clear;
        }
        var score = (uint)Math.Min(uint.MaxValue, r.CoinsGained);
        var info = new Dictionary<string, object?>
        {
            ["currency_list"] = score == 0 ? Array.Empty<object?>() : new object?[]
            {
                new Dictionary<string, object?> { ["id"] = (uint)Pt, ["num"] = score },
                new Dictionary<string, object?> { ["id"] = (uint)TerminalExp, ["num"] = score }
            },
            ["clear_time"] = clear,
            ["difficulty_complete_times"] = s.Completes.GetValueOrDefault(r.Difficulty),
            ["evaluate_id_list"] = Arr(win ? Evaluates(r, clear) : []),
            ["score"] = score
        };
        if (s.MinClear.TryGetValue(r.Difficulty, out var min)) info["min_clear_time"] = min;
        return info;
    }

    /// <summary>MatrixEvaluateCfg conditions the server can check from the run record.</summary>
    private List<uint> Evaluates(Run r, uint clear)
    {
        var ids = new List<uint> { 101 };
        if (r.Difficulty >= 2) ids.Add(102);
        foreach (var (id, min) in new (uint, uint)[] { (103, 50), (104, 30), (105, 20), (106, 15) })
            if (clear <= min * 60) ids.Add(id);
        foreach (var (id, n) in new (uint, long)[] { (201, 3000), (202, 4000), (203, 5000), (204, 6000) })
            if (r.CoinsGained >= n) ids.Add(id);
        uint Picks(params uint[] types) => (uint)types.Sum(t => r.Picks.GetValueOrDefault(t));
        void Count(uint none, (uint Id, uint N)[] steps, params uint[] types)
        {
            var n = Picks(types);
            if (n == 0) ids.Add(none);
            foreach (var (id, need) in steps) if (n >= need) ids.Add(id);
        }
        Count(303, [(301, 5), (302, 8)], 1);
        Count(403, [(401, 4), (402, 7)], 2);
        Count(503, [(501, 4)], 3, 4);
        if (r.Picks.GetValueOrDefault(4u) >= 2) ids.Add(502);
        Count(603, [(601, 5)], 5, 6);
        if (r.Picks.GetValueOrDefault(6u) >= 4) ids.Add(602);
        Count(703, [(701, 4), (702, 7)], EvIncident);
        foreach (var (id, n) in new (uint, int)[] { (1101, 10), (1102, 15), (1103, 20) })
            if (r.Artifacts.Count >= n) ids.Add(id);
        if (!r.ItemKinds.Contains(3)) ids.Add(1201);
        if (!r.ItemKinds.Contains(1)) ids.Add(1301);
        if (!r.ItemKinds.Contains(2)) ids.Add(1401);
        if (r.Gifts.Count == 0) ids.Add(1601);
        if (r.Losses == 0) ids.Add(1003);
        return ids.Where(id => Row("evaluates", id) is not null).ToList();
    }
}
