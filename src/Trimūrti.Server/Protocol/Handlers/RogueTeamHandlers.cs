using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 虚构推演 (Challenge Rogue Team, p88), port of Alpha-Persei rogueteam_service / rogueteam_flow / rogueteam_battle on
/// Data/Configs/rogueteam_cfg.json (items, skill tree, difficulties, rooms, events, options, effects, conditions, endings,
/// option jumps). A run walks three 7-column floors: battles (gold, healing, three-pick program/relic rewards,
/// mechanism gauge), events with condition-gated options and effects, safe houses (recruit / revive / upgrade /
/// rerolls), shops, reward rooms and the final boss; 88306 settles score → survey (70) and research (71) items.
/// </summary>
public sealed class RogueTeam(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec) : IStageMode
{
    private const int Template = 100001;
    // Attribute ids (RogueTeamConst.ATTR_*)
    private const int Mech = 1, Revive = 4, ReviveLimit = 5, Gold = 6, ResetCnt = 7, RecruitCnt = 8, ShopRate = 11, ShopRelicMax = 12, ShopTreasureMax = 13,
        W3 = 16, HpRecovery = 18, GoldRate = 19, MechThreshold = 20, MaxHp = 23, NoGold = 26, Exempt = 28, ExtraTreasure = 29, GoldBuff = 31, NormalGoldBuff = 32;
    // Node types / states
    private const int Normal = 1, Elite = 2, Boss = 3, LastBoss = 4, Shop = 5, Rest = 6, Event = 7, Plot = 8, Reward = 9, Demon = 11;
    private const int Unclean = 0, Clean = 1, Lock = 2, Over = 3;
    // Pop windows
    private const int PopTreasure = 1, PopRelic = 2, PopRecruit = 3, PopShop = 7, PopMechanism = 11;
    private const int ItemMechanism = 2, ItemRelic = 3, ItemTreasure = 4;
    private static readonly int[] EventPool = [10010, 10020, 10040, 10050, 10060, 10070, 10080, 10090, 10100, 10110, 10120, 10140, 10150, 10160, 20010, 20030, 20040,
        20050, 20060, 20070, 20080, 20090, 20100, 20110, 20120, 20140, 20150, 20160, 30010, 30020, 30030, 30050, 30060, 30070, 30080, 30090, 31100];
    private static readonly Dictionary<long, int> EndingOption = new() { [600101] = 1, [600102] = 2, [600103] = 3, [600104] = 6 };

    private JsonElement Cfg => config.Get("rogueteam_cfg.json") ?? throw new ModeError("Missing rogueteam_cfg.json");
    private static void Require(bool ok, string m) { if (!ok) throw new ModeError(m); }
    private JsonElement? Item(long id) => Cfg.GetProperty("items").TryGetProperty(id.ToString(), out var v) ? v : null;
    private static int I(JsonElement e, string k, int d = 0) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : d;

    // ---------------- state ----------------

    public sealed class Node { public int NodeId { get; set; } public int Col { get; set; } public int Row { get; set; } public int NodeType { get; set; } public List<int> Next { get; set; } = []; public int State { get; set; } public long Param { get; set; } public bool EvRelic { get; set; } }
    public sealed class Hero { public long HeroId { get; set; } public long TempId { get; set; } public int HpRatio { get; set; } = 10000; }
    public sealed class Treasure { public long Id { get; set; } public int Rare { get; set; } = 1; }
    public sealed class Param { public int Index { get; set; } public long Value { get; set; } public int BuyTimes { get; set; } public int Rare { get; set; } public int Discount { get; set; } = 100; public int IsNew { get; set; } }
    public sealed class Pop { public int EventType { get; set; } public List<Param> Params { get; set; } = []; public int DropType { get; set; } = 1; public int ShopRefresh { get; set; } public List<Pop> Queue { get; set; } = []; }
    public sealed class EventWin { public long EventId { get; set; } public List<long> Options { get; set; } = []; public int Trigger { get; set; } }

    public sealed class Run
    {
        public bool InGame { get; set; }
        public int Difficult { get; set; } = 1;
        public int Floor { get; set; } = 1;
        public int FloorState { get; set; }
        public int SelectNode { get; set; }
        public int Rooms { get; set; }
        public int Battles { get; set; }
        public int MechTotal { get; set; }
        public bool MechPending { get; set; }
        public int Silence { get; set; }
        public bool EndingWon { get; set; }
        public int PendingEnding { get; set; }
        public int ScoreRate { get; set; } = 100;
        public long StartTs { get; set; }
        public List<Node> Map { get; set; } = [];
        public List<Treasure> Treasures { get; set; } = [];
        public List<long> Others { get; set; } = [];
        public List<Hero> Heroes { get; set; } = [];
        public Dictionary<int, long> Attr { get; set; } = [];
        public List<long> Affixes { get; set; } = [];
        public List<object> Effects { get; set; } = [];
        public Pop? PopWin { get; set; }
        public Pop? ShopWin { get; set; }
        public EventWin? Event { get; set; }
        public List<long> UsedEvents { get; set; } = [];
        public HashSet<long> HistEvents { get; set; } = [];
        public HashSet<long> HistOptions { get; set; } = [];
        public List<long> Expired { get; set; } = [];
    }

    public sealed class Outside
    {
        public int Difficult { get; set; } = 1;
        public int MaxDifficult { get; set; } = 1;
        public long LastScoreId { get; set; }
        public long Point { get; set; }
        public List<long> Tree { get; set; } = [];
        public Dictionary<long, int> Collection { get; set; } = [];   // id → type
        public Dictionary<long, long> CollectionTs { get; set; } = [];
        public List<long> Viewed { get; set; } = [];
        public Dictionary<int, int> DiffClears { get; set; } = [];
        public Dictionary<int, int> Endings { get; set; } = [];
        public Run Run { get; set; } = new();
    }

    private async Task<Outside> LoadAsync(long uid, CancellationToken ct) => await accounts.GetJsonAsync<Outside>(uid, "rogueteam_v1", ct) ?? new Outside();
    private Task SaveAsync(long uid, Outside o, CancellationToken ct) => accounts.SetJsonAsync(uid, "rogueteam_v1", o, ct);

    // ---------------- items ----------------

    public int Rare(long id)
    {
        if (id == 1420401) return 4;
        var s = id.ToString();
        if (s.StartsWith("144") || s.StartsWith("143")) return 4;
        if (s.StartsWith("142")) return 2;
        if (s.StartsWith("141")) return 1;
        return Item(id) is { } c ? Math.Max(1, I(c, "rare", I(c, "sub_type", 1))) : 1;
    }

    private bool Has(Run r, long id) => !r.Expired.Contains(id) && (r.Others.Contains(id) || r.Treasures.Any(t => t.Id == id));
    private HashSet<long> Owned(Run r) => r.Others.Concat(r.Treasures.Select(t => t.Id)).ToHashSet();

    private List<long> RandomItems(int type, int count, int? rare = null, int pool = 0)
    {
        var camp = pool is >= 401 and <= 406 ? pool - 400 : 0;
        bool Ok(JsonProperty p, bool useRare)
        {
            if (I(p.Value, "type") != type || p.Name.StartsWith("133")) return false;
            var r = p.Value.TryGetProperty("rare", out var rv) && rv.ValueKind == JsonValueKind.Number ? rv.GetInt32() : type == ItemTreasure ? I(p.Value, "sub_type", 1) : (int?)null;
            if (useRare && rare is { } want && r is { } have && have != want) return false;
            return camp == 0 || p.Name.StartsWith($"1410{camp}") || p.Name.StartsWith($"1420{camp}");
        }
        var items = Cfg.GetProperty("items").EnumerateObject().ToList();
        var list = items.Where(p => Ok(p, true)).Select(p => long.Parse(p.Name)).ToList();
        if (list.Count == 0) list = items.Where(p => Ok(p, false)).Select(p => long.Parse(p.Name)).ToList();
        if (list.Count == 0) list = items.Where(p => I(p.Value, "type") == type && !p.Name.StartsWith("133")).Select(p => long.Parse(p.Name)).ToList();
        return list.OrderBy(_ => Random.Shared.Next()).Take(count).ToList();
    }

    private List<long> RollUnowned(Run r, int type, int count = 3, int? rare = null)
    {
        if (type == ItemTreasure)
        {
            if (Has(r, 1310009)) count++;
            if (Has(r, 1310008)) count = Math.Max(1, count - 1);
        }
        var owned = Owned(r);
        var cand = RandomItems(type, Math.Max(count * 6, 18), rare);
        var fresh = cand.Where(i => !owned.Contains(i)).Distinct().Take(count).ToList();
        foreach (var i in cand) { if (fresh.Count >= count) break; if (!fresh.Contains(i)) fresh.Add(i); }
        return fresh;
    }

    private static int RollQuality(long b3, long b4)
    {
        var w = new long[] { 750, 200, Math.Max(10, 50 + b3), Math.Max(5, 10 + b4) };
        var roll = Random.Shared.NextInt64(w.Sum());
        for (var i = 0; i < 4; i++) { if ((roll -= w[i]) < 0) return i + 1; }
        return 1;
    }

    private List<Param> Params(Outside o, IEnumerable<long> ids, int buy = 0) =>
        ids.Select((id, i) => new Param { Index = i + 1, Value = id, BuyTimes = buy, Rare = Rare(id), IsNew = o.Collection.ContainsKey(id) ? 0 : 1 }).ToList();

    private void Unlock(Outside o, long id, int type)
    {
        if (id == 0) return;
        if (type == 0) { var s = id.ToString(); type = s.StartsWith("14") ? 1 : s.StartsWith("13") ? 3 : s.StartsWith("12") ? 4 : id is >= 1 and <= 10 ? 5 : 2; }
        if (o.Collection.TryAdd(id, type)) o.CollectionTs[id] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    // ---------------- map ----------------

    private long NodeParam(int type, int floor)
    {
        var f = Math.Clamp(floor, 1, 3);
        int? room = type switch
        {
            Normal => 1000 + f, Elite => 2000 + f, Boss => f == 2 ? 3002 : 3001, LastBoss => 4000 + f, Rest => 6001,
            Event => 7000 + f, Plot => f == 3 ? 8001 : 8002, Reward => 9000 + f, _ => null
        };
        var rooms = Cfg.GetProperty("rooms");
        JsonElement? obj = room is { } rid && rooms.TryGetProperty(rid.ToString(), out var ro) && ro.TryGetProperty("params", out var rp) && rp.GetArrayLength() > 0 ? ro : null;
        if (obj is null)
        {
            var matched = rooms.EnumerateObject().Select(p => p.Value).Where(v => I(v, "room_type") == type && v.TryGetProperty("params", out var pp) && pp.GetArrayLength() > 0).ToList();
            if (matched.Count > 0) obj = matched[Random.Shared.Next(matched.Count)];
        }
        if (obj is { } o)
        {
            var ps = o.GetProperty("params");
            var p = ps[Random.Shared.Next(ps.GetArrayLength())];
            return p.ValueKind == JsonValueKind.Array ? (p.GetArrayLength() > 1 ? p[1].GetInt64() : p[0].GetInt64()) : p.ValueKind == JsonValueKind.Object ? p.GetProperty("stage_id").GetInt64() : p.GetInt64();
        }
        return type switch { Normal => 4101101, Elite => 4101201, Boss => 4101301, LastBoss => 4103301, Event => 10010, Rest => 50010, Reward => 40010, Plot => 60020, Demon => 4106013, _ => 0 };
    }

    private List<Node> GenerateFloor(int floor)
    {
        int[][] layout = [[2, 4], [1, 3, 5], [2, 4], [1, 3, 5], [2, 4], [3], [3]];
        var nodes = new Dictionary<(int, int), Node>();
        var weights = new (int Type, int W)[] { (Normal, 30), (Elite, 18), (Shop, 13), (Event, 14), (Plot, 12), (Rest, 8), (Reward, 5) };
        for (var c = 1; c <= 7; c++)
            foreach (var row in layout[c - 1])
            {
                int type, state = Lock;
                if (c == 1) { type = Normal; state = Unclean; }
                else if (c == 2) type = Elite;
                else if (c == 7) type = floor >= 3 ? LastBoss : Boss;
                else if (c == 6) type = floor >= 3 ? Plot : Random.Shared.Next(2) == 0 ? Rest : Shop;
                else
                {
                    var roll = Random.Shared.Next(weights.Sum(w => w.W));
                    type = weights.First(w => (roll -= w.W) < 0).Type;
                }
                if (floor == 2 && c == 4 && row == 3) type = Plot;
                else if (type == Plot && !(floor >= 3 && c == 6)) type = Event;
                nodes[(c, row)] = new Node { NodeId = c * 100 + row, Col = c, Row = row, NodeType = type, State = state, Param = NodeParam(type, floor) };
            }
        for (var c = 1; c < 7; c++)
        {
            foreach (var r in layout[c - 1])
            {
                var cur = nodes[(c, r)];
                foreach (var n in layout[c].Where(n => Math.Abs(r - n) <= 1)) cur.Next.Add(nodes[(c + 1, n)].NodeId);
                if (cur.Next.Count == 0) cur.Next.Add(nodes[(c + 1, layout[c].OrderBy(n => Math.Abs(n - r)).First())].NodeId);
            }
            foreach (var n in layout[c])
            {
                var id = nodes[(c + 1, n)].NodeId;
                if (!layout[c - 1].Any(r => nodes[(c, r)].Next.Contains(id))) nodes[(c, layout[c - 1].OrderBy(r => Math.Abs(r - n)).First())].Next.Add(id);
            }
        }
        return nodes.Values.OrderBy(n => n.NodeId).ToList();
    }

    private static void ExemptMarks(List<Node> map, long exempt)
    {
        if (exempt <= 0) return;
        foreach (var n in map.Where(n => n.NodeType is Normal or Elite && n.State != Clean)) n.Param = 0;
    }

    private static bool FloorClear(List<Node> map) => map.Any(n => n.State == Over) && !map.Any(n => n.State is Unclean or Clean);

    private static void Finish(List<Node> map, int nodeId)
    {
        var t = map.FirstOrDefault(n => n.NodeId == nodeId);
        if (t is null || t.State == Over) return;
        t.State = Over;
        foreach (var n in map.Where(n => t.Next.Contains(n.NodeId) && n.State == Lock)) n.State = Unclean;
    }

    /// <summary>Room step: counters, upgrade every 5 rooms (tech 11103), mechanism gauge 10–15 per room.</summary>
    private void Step(Outside o, Run r, int type)
    {
        r.Rooms++;
        if (type is Normal or Elite or Boss or LastBoss) r.Battles++;
        if (o.Tree.Contains(11103) && r.Rooms % 5 == 0)
            if (r.Treasures.FirstOrDefault(t => t.Rare == 1 && Item(t.Id) is { } c && I(c, "sub_type") == 1) is { } up) up.Rare = 2;
        var thr = r.Attr.GetValueOrDefault(MechThreshold, 100);
        if (thr <= 0) thr = 100;
        var gain = Random.Shared.Next(10, 16);
        if (Has(r, 1310020)) gain = Math.Max(1, (int)(gain * 1.2));
        var cur = r.Attr.GetValueOrDefault(Mech) + gain;
        r.MechTotal += gain;
        if (cur >= thr) { cur -= thr; r.MechPending = true; }
        r.Attr[Mech] = cur;
        r.Attr[MechThreshold] = thr;
    }

    private Pop? MechPopup(Outside o, Run r)
    {
        if (!r.MechPending) return null;
        r.MechPending = false;
        var ps = Params(o, RollUnowned(r, ItemMechanism));
        return ps.Count == 0 ? null : new Pop { EventType = PopMechanism, Params = ps, DropType = 2 };
    }

    private long GoldGain(Run r, int type, int baseGold)
    {
        if (r.Attr.GetValueOrDefault(NoGold) == 1) return 0;
        var rate = r.Attr.GetValueOrDefault(GoldRate, 1000) / 1000.0;
        var buff = 1.0 + r.Attr.GetValueOrDefault(GoldBuff) / 1000.0 + (type == Normal ? r.Attr.GetValueOrDefault(NormalGoldBuff) / 1000.0 : 0);
        return (long)(baseGold * rate * buff);
    }

    private void HealAfterBattle(Run r)
    {
        var heal = r.Attr.GetValueOrDefault(HpRecovery) + (Has(r, 1310011) ? 100 : 0) - (Has(r, 1320004) ? 300 : 0);
        if (heal <= 0) return;
        foreach (var h in r.Heroes.Where(h => h.HpRatio > 0)) h.HpRatio = (int)Math.Min(10000, h.HpRatio + heal * 10);
    }

    // ---------------- conditions & options ----------------

    private bool Condition(Outside o, Run r, long id)
    {
        if (!Cfg.GetProperty("conditions").TryGetProperty(id.ToString(), out var c)) return true;
        var p = c.TryGetProperty("params", out var pp) && pp.ValueKind == JsonValueKind.Array ? pp.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt64() : 0).ToList() : [];
        long P(int i, long d) => p.Count > i ? p[i] : d;
        switch (I(c, "type"))
        {
            case 101 or 105:
                return r.Treasures.Count(t => (P(0, 0) == 0 || Item(t.Id) is { } ic && I(ic, "sub_type") == P(0, 0))) >= P(1, 1);
            case 102: return r.HistOptions.Contains(P(0, 0));
            case 103: return r.HistEvents.Contains(P(0, 0));
            case 104: return r.Battles >= P(0, 1);
            case 106: return r.Attr.GetValueOrDefault((int)P(0, 1)) >= P(1, 0);
            case 107:
                {
                    var rare = P(0, 1);
                    var n = rare == 99 ? r.Others.Count(i => Item(i) is not { } ic || I(ic, "sub_type") != 3) + r.Treasures.Count
                        : r.Others.Count(i => (Item(i) is { } ic && I(ic, "sub_type") == rare) || i.ToString().StartsWith(rare == 2 ? "132" : "131"));
                    return n >= P(1, 1);
                }
            case 108: return Owned(r).Contains(P(0, 0));
            case 109 or 113: return p.Any(Owned(r).Contains);
            case 110: return r.Attr.GetValueOrDefault((int)P(0, 4)) < r.Attr.GetValueOrDefault((int)P(1, 4), 5);
            case 111: return r.Treasures.Any(t => Item(t.Id) is { } ic && I(ic, "sub_type") == 1 && t.Rare < 3);
            case 112: return r.Treasures.Count(t => t.Rare == P(0, 1)) >= P(1, 1);
            case 115: return r.Heroes.Any(h => (p.Count <= 1 || p.Skip(1).Contains(h.HeroId)) && h.HpRatio <= P(0, 10000));
            case 201: return r.Difficult >= P(0, 1);
            case 202: return o.Endings.GetValueOrDefault((int)P(0, 1)) >= P(1, 1);
            case 204: return o.Tree.Contains(P(0, 0));
            case 205: return ((int)P(0, 1) == Mech ? Math.Max(r.MechTotal, r.Attr.GetValueOrDefault(Mech)) : r.Attr.GetValueOrDefault((int)P(0, 1))) >= P(1, 0);
            default: return true;
        }
    }

    private JsonElement? Option(long id) => Cfg.GetProperty("event_options").TryGetProperty(id.ToString(), out var v) ? v : null;
    private bool OptionUnlocked(Outside o, Run r, long id) =>
        Option(id) is not { } opt || !opt.TryGetProperty("condition_list", out var cl) || cl.EnumerateArray().All(c => Condition(o, r, c.GetInt64()));

    private List<long> UnlockedOptions(Outside o, Run r, long eventId) =>
        Cfg.GetProperty("events").TryGetProperty(eventId.ToString(), out var ev) && ev.TryGetProperty("option_list", out var ol)
            ? ol.EnumerateArray().Select(x => x.GetInt64()).Where(x => OptionUnlocked(o, r, x)).ToList() : [];

    private long Jump(long opt) =>
        Option(opt) is { } c && I(c, "jump_event_id") != 0 ? I(c, "jump_event_id")
        : Cfg.TryGetProperty("option_jumps", out var j) && j.TryGetProperty(opt.ToString(), out var v) ? v.GetInt64() : 0;

    private Dictionary<string, object?> OptionEntry(long opt)
    {
        var items = new List<object>();
        if (Option(opt) is { } c && c.TryGetProperty("effect_ids", out var effs))
            foreach (var e in effs.EnumerateArray())
                if (Cfg.GetProperty("event_effects").TryGetProperty(e.GetInt64().ToString(), out var ef) && ef.GetProperty("params") is { ValueKind: JsonValueKind.Array } ps && ps.GetArrayLength() > 0
                    && I(ef, "action") is 6 or 8 or 1008 or 1053 && Item(ps[0].GetInt64()) is not null)
                    items.Add(new Dictionary<string, object?> { ["id"] = (uint)ps[0].GetInt64(), ["rare"] = (uint)Rare(ps[0].GetInt64()) });
        return new() { ["opt_id"] = (uint)opt, ["item_id_list"] = items.ToArray(), ["stage_id"] = 0u, ["jump_battle_room_id"] = 0u };
    }

    private sealed class Delta
    {
        public List<Treasure> NewTreasures { get; } = [];
        public List<long> NewOthers { get; } = [];
        public List<object> Updates { get; } = [];
        public int MapSwitch;
        public int Ending;
        public long FinalRoom;
        public void Add(long id, int rare) => Updates.Add(Upd(1, id, 1, rare, 0));
        public static Dictionary<string, object?> Upd(int op, long id, int isNew, int rare, int src) => new()
        {
            ["operate"] = (uint)op, ["id"] = (uint)id, ["is_new"] = (uint)isNew, ["rare"] = (uint)rare, ["source_rare"] = (uint)src, ["source_type"] = 2u, ["source_item_id"] = 0u
        };
    }

    private void GiveOther(Outside o, Run r, Delta d, long id, int type = ItemRelic, bool unique = true)
    {
        if (unique && r.Others.Contains(id)) return;
        r.Others.Add(id);
        d.NewOthers.Add(id);
        d.Add(id, Rare(id));
        Unlock(o, id, type);
    }

    private void GiveTreasure(Outside o, Run r, Delta d, long id, int? rare = null)
    {
        var t = new Treasure { Id = id, Rare = rare ?? Rare(id) };
        r.Treasures.Add(t);
        d.NewTreasures.Add(t);
        d.Add(id, t.Rare);
        Unlock(o, id, 1);
    }

    /// <summary>Event effect actions (_execute_event_effects): attributes, HP, relics, items, removals, upgrades, pools,
    /// full heal, map switch, endings, world line, silence, mechanisms, max HP.</summary>
    private void Effects(Outside o, Run r, IEnumerable<long> effectIds, Delta d, long optId = 0)
    {
        foreach (var effId in effectIds)
        {
            if (!Cfg.GetProperty("event_effects").TryGetProperty(effId.ToString(), out var eff)) continue;
            var p = eff.GetProperty("params") is { ValueKind: JsonValueKind.Array } pa ? pa.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt64() : 0).ToList() : [];
            long P(int i, long def) => p.Count > i ? p[i] : def;
            switch (I(eff, "action"))
            {
                case 1 or 1001:
                    if (p.Count >= 4)
                    {
                        var aid = (int)p[1];
                        var cur = r.Attr.GetValueOrDefault(aid);
                        if (p[0] == 1)
                        {
                            if (aid == Gold && p[3] > 0 && r.Attr.GetValueOrDefault(NoGold) == 1) break;
                            r.Attr[aid] = Math.Max(0, cur + p[3]);
                            if (aid == Revive) r.Attr[aid] = Math.Min(r.Attr.GetValueOrDefault(ReviveLimit, 3), r.Attr[aid]);
                            if (aid == Mech && p[3] > 0) r.MechTotal += (int)p[3];
                        }
                        else r.Attr[aid] = Math.Max(0, cur - Math.Abs(p[3]));
                    }
                    else if (p.Count == 2) r.Attr[(int)p[0]] = Math.Max(0, p[1]);
                    break;
                case 2 or 1002:
                    if (p.Count >= 2)
                    {
                        var delta = Math.Abs(p[1]) <= 1000 ? p[1] * 10 : p[1];
                        foreach (var h in r.Heroes)
                            h.HpRatio = p[0] == 1 || delta > 0 ? (h.HpRatio > 0 ? (int)Math.Min(10000, h.HpRatio + Math.Abs(delta)) : 0) : (int)Math.Max(1, h.HpRatio - Math.Abs(delta));
                    }
                    break;
                case 6 or 1006:
                    if (p.Count > 0)
                    {
                        GiveOther(o, r, d, p[0]);
                        foreach (var sub in new[] { p[0] * 10 + 1, p[0] * 10 + 2 })
                            if (Cfg.GetProperty("event_effects").TryGetProperty(sub.ToString(), out var se) && I(se, "action") is 6 or 1006 && se.GetProperty("params")[0].GetInt64() is var sid && sid != p[0])
                                GiveOther(o, r, d, sid);
                    }
                    break;
                case 8 or 1008:
                    if (p.Count > 0)
                    {
                        var cnt = (int)P(1, 1);
                        if (Item(p[0]) is { } ic)
                        {
                            if (I(ic, "type") == ItemTreasure) for (var i = 0; i < cnt; i++) GiveTreasure(o, r, d, p[0]);
                            else
                            {
                                for (var i = 0; i < cnt; i++) GiveOther(o, r, d, p[0], I(ic, "type"), unique: false);
                                RelicOnGet(r, p[0]);
                            }
                        }
                        else if (p[0] is > 0 and < 100000)
                            foreach (var id in RandomItems(ItemRelic, Math.Max(1, cnt))) GiveOther(o, r, d, id, unique: false);
                        else foreach (var id in RandomItems(ItemTreasure, cnt)) GiveTreasure(o, r, d, id);
                    }
                    break;
                case 9:
                    {
                        var removed = 0;
                        for (var i = r.Treasures.Count - 1; i >= 0 && removed < P(2, 1); i--)
                            if (r.Treasures[i].Rare >= P(0, 1) && r.Treasures[i].Rare <= P(1, P(0, 1)))
                            {
                                d.Updates.Add(Delta.Upd(2, r.Treasures[i].Id, 0, r.Treasures[i].Rare, r.Treasures[i].Rare));
                                r.Treasures.RemoveAt(i);
                                removed++;
                            }
                        break;
                    }
                case 10:
                    {
                        var removed = 0;
                        for (var i = r.Others.Count - 1; i >= 0 && removed < P(1, 1); i--)
                            if (Item(r.Others[i]) is { } ic && I(ic, "sub_type", 1) == P(0, 1))
                            {
                                d.Updates.Add(Delta.Upd(2, r.Others[i], 0, 1, 1));
                                r.Others.RemoveAt(i);
                                removed++;
                            }
                        break;
                    }
                case 12:
                    foreach (var t in r.Treasures.Where(t => t.Rare < 3).Take((int)P(0, 1)).ToList())
                    {
                        var old = t.Rare;
                        t.Rare = (int)Math.Min(3, t.Rare + P(1, 1));
                        d.Updates.Add(Delta.Upd(3, t.Id, 0, t.Rare, old));
                    }
                    break;
                case 14 or 16 or 1016:
                    {
                        var cnt = (int)(p.Count >= 4 ? p[3] : p.Count >= 3 ? p[2] : p.Count >= 2 ? p[1] : 1);
                        int? rare = p.Count >= 2 && p[1] is >= 1 and <= 4 ? (int)p[1] : null;
                        var items = RandomItems(ItemTreasure, cnt, rare, (int)P(0, 0));
                        if (items.Count == 0) items = RandomItems(ItemTreasure, cnt);
                        foreach (var id in items) GiveTreasure(o, r, d, id);
                        break;
                    }
                case 15:
                    {
                        var pool = P(0, 301);
                        List<long> cands = pool switch
                        {
                            315 => [1310056], 316 => [1310025], 317 => [1310021],
                            _ => Cfg.GetProperty("items").EnumerateObject().Where(x => I(x.Value, "type") == ItemRelic && I(x.Value, "sub_type") == (pool == 314 ? 2 : 1)).Select(x => long.Parse(x.Name)).ToList()
                        };
                        if (cands.Count > 0) GiveOther(o, r, d, cands[Random.Shared.Next(cands.Count)], unique: false);
                        break;
                    }
                case 17: foreach (var h in r.Heroes) h.HpRatio = 10000; break;
                case 19: d.MapSwitch = 2; break;
                case 20: d.MapSwitch = 3; break;
                case 21: d.Ending = EndingOption.TryGetValue(optId, out var e) ? e : p.Count > 0 && p[0] is >= 1 and <= 5 ? (int)p[0] : 0; break;
                case 22: if (p.Count > 0) d.FinalRoom = p[0]; break;
                case 1012:
                    foreach (var t in r.Treasures.Where(t => t.Id.ToString().StartsWith("14") && t.Rare < 4).OrderBy(_ => Random.Shared.Next()).Take((int)P(1, 1)).ToList())
                    {
                        var old = t.Rare;
                        t.Rare = Math.Min(4, t.Rare + 1);
                        d.Updates.Add(Delta.Upd(3, t.Id, 0, t.Rare, old));
                    }
                    break;
                case 1056: r.Silence = (int)P(0, 1); break;
                case 1051: if (p.Count > 0) d.Ending = (int)p[0]; break;
                case 1053: if (p.Count > 0) GiveOther(o, r, d, p[0], ItemMechanism); break;
                case 1054: if (p.Count > 0) r.Attr[MaxHp] = r.Attr.GetValueOrDefault(MaxHp, 1000) + p[0]; break;
            }
        }
    }

    /// <summary>Relic "on get" attribute effect (effect id = item × 10 + 1, action 1).</summary>
    private void RelicOnGet(Run r, long relic)
    {
        if (!Cfg.GetProperty("event_effects").TryGetProperty((relic * 10 + 1).ToString(), out var eff) || I(eff, "action") is not (1 or 1001)) return;
        var p = eff.GetProperty("params").EnumerateArray().Select(x => x.GetInt64()).ToList();
        if (p.Count < 4) return;
        var aid = (int)p[1];
        var cur = r.Attr.GetValueOrDefault(aid);
        r.Attr[aid] = p[0] == 1 ? Math.Max(0, cur + p[3]) : Math.Max(0, cur - Math.Abs(p[3]));
        if (aid == Revive) r.Attr[aid] = Math.Min(r.Attr.GetValueOrDefault(ReviveLimit, 3), r.Attr[aid]);
    }

    private static bool BattleOption(long opt, JsonElement c) => opt == 300701 || (c.TryGetProperty("name", out var n) && (n.GetString() ?? "").Contains("战斗"));

    // ---------------- wire ----------------

    private static Dictionary<string, object?> NodeRec(Node n) => new()
    {
        ["node_id"] = (uint)n.NodeId, ["col"] = (uint)n.Col, ["row"] = (uint)n.Row, ["node_type"] = (uint)n.NodeType,
        ["next_id_list"] = n.Next.Select(x => (uint)x).ToArray(), ["state"] = (uint)n.State, ["param"] = (uint)n.Param
    };
    private static object[] MapRec(Run r) => r.Map.Select(n => (object)NodeRec(n)).ToArray();
    private static object[] AttrRec(Run r) => r.Attr.OrderBy(a => a.Key).Select(a => (object)new Dictionary<string, object?> { ["attr_id"] = (uint)a.Key, ["value"] = (uint)Math.Max(0, a.Value) }).ToArray();
    private static object[] HeroRec(Run r) => r.Heroes.Select(h => (object)new Dictionary<string, object?> { ["hero_id"] = (uint)h.HeroId, ["temp_id"] = (uint)h.TempId, ["hp_ratio"] = (uint)h.HpRatio }).ToArray();
    private object[] TreasureRec(IEnumerable<Treasure> ts) => ts.Select(t => (object)new Dictionary<string, object?> { ["id"] = (uint)t.Id, ["rare"] = (uint)Math.Min(4, Math.Max(t.Rare, Rare(t.Id))) }).ToArray();

    private static Dictionary<string, object?> PopRec(Pop? p) => p is null ? new() { ["event_type"] = 0u, ["param_list"] = Array.Empty<object>(), ["drop_type"] = 0u } : new()
    {
        ["event_type"] = (uint)p.EventType, ["drop_type"] = (uint)p.DropType, ["shop_refresh_times"] = (uint)p.ShopRefresh,
        ["param_list"] = p.Params.Select(x => (object)new Dictionary<string, object?>
        {
            ["index"] = (uint)x.Index, ["param"] = (uint)x.Value, ["buy_times"] = (uint)x.BuyTimes, ["rare"] = (uint)x.Rare, ["discount"] = (uint)x.Discount, ["is_new"] = (uint)x.IsNew
        }).ToArray()
    };

    private Dictionary<string, object?> EventRec(Outside o, Run r, EventWin? e) => e is null ? new() { ["event_id"] = 0u, ["opt_list"] = Array.Empty<object>(), ["trigger_type"] = 0u } : new()
    {
        ["event_id"] = (uint)e.EventId, ["opt_list"] = e.Options.Select(x => (object)OptionEntry(x)).ToArray(), ["trigger_type"] = (uint)e.Trigger
    };

    private Dictionary<string, object?> Session(Run r) => new()
    {
        ["template_id"] = (uint)Template, ["floor_num"] = (uint)r.Floor, ["floor_state"] = (uint)r.FloorState, ["select_node_id"] = (uint)r.SelectNode,
        ["map_info"] = MapRec(r), ["second_map_info"] = Array.Empty<object>(), ["treasure_list"] = TreasureRec(r.Treasures),
        ["other_item_list"] = r.Others.Select(x => (uint)x).ToArray(), ["effect_list"] = Array.Empty<object>(), ["affix_list"] = r.Affixes.Select(x => (uint)x).ToArray(),
        ["plot_id"] = 0u, ["other_info"] = PopRec(r.PopWin), ["shop_info"] = PopRec(r.ShopWin)
    };

    private Dictionary<string, object?> OutsideRec(Outside o) => new()
    {
        ["template_id"] = (uint)Template, ["difficult"] = (uint)(o.Run.InGame ? o.Run.Difficult : 0),
        ["collection_list"] = o.Collection.GroupBy(c => c.Value).Select(g => (object)new Dictionary<string, object?> { ["type"] = (uint)g.Key, ["item_list"] = g.Select(x => (uint)x.Key).ToArray() }).ToArray(),
        ["his_difficult"] = o.DiffClears.Select(x => (object)new Dictionary<string, object?> { ["key"] = (uint)x.Key, ["value"] = (uint)x.Value }).ToArray(),
        ["his_avg"] = o.Endings.Select(x => (object)new Dictionary<string, object?> { ["key"] = (uint)x.Key, ["value"] = (uint)x.Value }).ToArray(),
        ["unlock_collection"] = Cfg.GetProperty("items").EnumerateObject().Where(p => I(p.Value, "type") == ItemRelic).Select(p => long.Parse(p.Name))
            .Union(o.Collection.Keys.Where(k => Item(k) is not null)).OrderBy(x => x).Select(x => (uint)x).ToArray(),
        ["view_collection"] = o.Viewed.Where(v => Item(v) is not null).Select(x => (uint)x).ToArray(),
        ["tree_list"] = o.Tree.Select(x => (uint)x).ToArray(), ["last_id"] = (uint)o.LastScoreId
    };

    private GameResponse F(ushort srv, uint cmd, Dictionary<string, object?> body) => new(cmd, 0, srv, codec.Encode($"sc_{cmd}", body));

    private List<GameResponse> SnapshotFrames(ushort srv, Run r) =>
    [
        F(srv, 88001, Session(r)), F(srv, 88013, new() { ["attr_list"] = AttrRec(r) }), F(srv, 88019, new() { ["hero_list"] = HeroRec(r), ["modify_type"] = 1u })
    ];

    private static (long, long) Fetters()
    {
        var week = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (7 * 86400);
        return (week % 4 + 1, (week + 1) * 7 * 86400);
    }

    public async Task<IReadOnlyList<(uint Cmd, byte[] Payload)>> LoginFramesAsync(ProtoCodec c, long uid, CancellationToken ct)
    {
        var o = await LoadAsync(uid, ct);
        var (fid, next) = Fetters();
        var frames = new List<(uint, byte[])>
        {
            (88305, codec.Encode("sc_88305", OutsideRec(o))),
            (88315, codec.Encode("sc_88315", new Dictionary<string, object?> { ["activity_id"] = 1000u, ["next_timestamps"] = (uint)next, ["fetters_id"] = (uint)fid })),
            // ChallengeRogueTeamData: guide done once the player has started a run; score points and rewarded score tiers.
            (88029, codec.Encode("sc_88029", new Dictionary<string, object?> { ["finish_guide"] = o.Run.InGame || o.DiffClears.Count > 0 || o.Point > 0 ? 1u : 0u, ["template_id"] = (uint)Template })),
            (88309, codec.Encode("sc_88309", new Dictionary<string, object?> { ["template_id"] = (uint)Template, ["point"] = (uint)o.Point, ["reward_list"] = Array.Empty<uint>() }))
        };
        if (o.Run.InGame)
        {
            var r = o.Run;
            frames.Add((88001, codec.Encode("sc_88001", Session(r))));
            frames.Add((88013, codec.Encode("sc_88013", new Dictionary<string, object?> { ["attr_list"] = AttrRec(r) })));
            frames.Add((88019, codec.Encode("sc_88019", new Dictionary<string, object?> { ["hero_list"] = HeroRec(r), ["modify_type"] = 1u })));
            if (r.Event is { } e) frames.Add((88023, codec.Encode("sc_88023", EventRec(o, r, e))));
        }
        return frames;
    }

    // ---------------- battle settlement (stage type 78) ----------------

    public bool Reserved(uint type, long stage) => type == 78 || stage is >= 4100000 and <= 4199999;

    public async Task<(List<GameResponse>, IReadOnlyList<(long, long)>)> SettleAsync(ModeBattle b, CancellationToken ct)
    {
        var o = await LoadAsync(b.Uid, ct);
        var r = o.Run;
        Require(r.InGame, "Rogue run not active");
        var node = r.Map.FirstOrDefault(n => n.NodeId == r.SelectNode && n.State == Unclean && n.NodeType is Normal or Elite or Boss or LastBoss or Demon);
        Require(node is not null, "Rogue node is not a pending battle");
        if (!b.Win) return ([], []);
        var srv = b.Srv;
        node!.State = Clean;
        foreach (var n in r.Map.Where(n => n.Col == node.Col && n.NodeId != node.NodeId && n.State == Unclean)) n.State = Lock;
        foreach (var n in r.Map.Where(n => node.Next.Contains(n.NodeId) && n.State == Lock)) n.State = Unclean;
        Step(o, r, node.NodeType);
        var baseGold = node.NodeType switch { Elite => 80, Boss => 150, LastBoss => 300, _ => 40 };
        var gold = GoldGain(r, node.NodeType, baseGold);
        r.Attr[Gold] = r.Attr.GetValueOrDefault(Gold) + gold;
        if (r.Silence > 0 && --r.Silence == 0 && r.Attr.GetValueOrDefault(NoGold) == 0) r.Attr[Gold] += 300;
        if (b.Stats.ResurrectTimes > 0) r.Attr[Revive] = Math.Max(0, r.Attr.GetValueOrDefault(Revive) - b.Stats.ResurrectTimes);
        HealAfterBattle(r);
        var b3 = r.Attr.GetValueOrDefault(W3, 50) - 50;
        var b4 = r.Attr.GetValueOrDefault(17, 10) - 10;
        var pop = PopTreasure;
        List<long> items;
        var extra = new List<(int, int)>();
        if (node.NodeType == Elite)
        {
            if (Random.Shared.Next(2) == 0) { pop = PopRelic; items = RollUnowned(r, ItemRelic, 3, 2); }
            else items = RollUnowned(r, ItemTreasure, 3, Math.Max(2, RollQuality(b3, b4)));
            if (Has(r, 1310017)) extra.Add((PopRelic, 1));
        }
        else if (node.NodeType is Boss or LastBoss)
        {
            items = RollUnowned(r, ItemTreasure, 3, Math.Max(3, RollQuality(b3 + 50, b4 + 30)));
            extra.Add((PopRelic, 1));
        }
        else items = RollUnowned(r, ItemTreasure, 3, RollQuality(b3, b4));
        if (r.Attr.GetValueOrDefault(ExtraTreasure) == 1 && r.Battles <= 2) extra.Add((PopTreasure, 2));
        if (node.EvRelic) { pop = PopTreasure; items = RollUnowned(r, ItemTreasure); }
        var final = node.Param is >= 4103301 and <= 4103304;
        if (final)
        {
            r.EndingWon = true;
            r.FloorState = 1;
            items = []; extra.Clear();
            r.Attr[Gold] = Math.Max(0, r.Attr[Gold] - gold);
            gold = 0;
        }
        var cats = new[] { pop }.Concat(extra.Select(e => e.Item1)).ToList();
        var frames = new List<GameResponse>
        {
            F(srv, 88033, new() { ["treasure_num"] = (uint)cats.Count(c => c == PopTreasure), ["relic_num"] = (uint)cats.Count(c => c == PopRelic), ["coin_num"] = (uint)gold })
        };
        var win = new Pop { EventType = pop, Params = Params(o, items), Queue = extra.Select(e => new Pop { EventType = e.Item1, DropType = e.Item2 }).ToList() };
        if (!final && MechPopup(o, r) is { } mp) win.Queue.Add(mp);
        if (win.Params.Count == 0)
        {
            Finish(r.Map, node.NodeId);
            r.PopWin = win.Queue.Count > 0 ? win : null;
        }
        else r.PopWin = win;
        if (r.PopWin is not { Params.Count: > 0 }) r.SelectNode = 0;
        await SaveAsync(b.Uid, o, ct);
        if (r.PopWin is { Params.Count: > 0 }) frames.Add(F(srv, 88015, new() { ["other_info"] = PopRec(r.PopWin) }));
        frames.AddRange(SnapshotFrames(srv, r));
        if (r.PopWin is null && (final || FloorClear(r.Map))) frames.Add(F(srv, 88221, new() { ["sign"] = 1u }));
        return (frames, []);
    }

    // ---------------- operations ----------------

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest req, uint cmd, CancellationToken ct)
    {
        var uid = req.AccountId;
        var srv = (ushort)(req.ServerIndex + 1);
        var o = await LoadAsync(uid, ct);
        var r = o.Run;
        var frames = new List<GameResponse>();
        uint result = 0;
        try
        {
            switch (cmd)
            {
                case 88100: Start(o, req, frames, srv); break;
                case 88102: Recruit(o, req, frames, srv); break;
                case 88004: SelectNode(o, (int)HandlerHelpers.ReadUInt(req.Body, "node_id"), frames, srv); break;
                case 88008:
                    Require(r.InGame, "Rogue run not active");
                    if (r.Floor < 3)
                    {
                        r.Floor++;
                        r.FloorState = 0;
                        r.SelectNode = 0;
                        r.Map = GenerateFloor(r.Floor);
                        ExemptMarks(r.Map, r.Attr.GetValueOrDefault(Exempt));
                    }
                    else { r.FloorState = 1; r.SelectNode = 0; }
                    frames.Add(F(srv, 88001, Session(r)));
                    break;
                case 88024: Choose(o, HandlerHelpers.ReadUInt(req.Body, "opt_id"), frames, srv); break;
                case 88030: Require(r.InGame, "Rogue run not active"); frames.AddRange(SnapshotFrames(srv, r)); break;
                case 88200: Commit(o, HandlerHelpers.ReadUInt(req.Body, "param_arg"), frames, srv, req); return frames;
                case 88202:
                    {
                        Require(r.PopWin is { EventType: > 0 }, "No pending pick window");
                        var left = r.Attr.GetValueOrDefault(ResetCnt);
                        Require(left > 0, "No reroll left");
                        var type = r.PopWin!.EventType switch { PopRelic => ItemRelic, PopMechanism => ItemMechanism, _ => ItemTreasure };
                        r.PopWin.Params = Params(o, RollUnowned(r, type));
                        r.Attr[ResetCnt] = left - 1;
                        frames.Add(F(srv, 88015, new() { ["other_info"] = PopRec(r.PopWin) }));
                        frames.Add(F(srv, 88013, new() { ["attr_list"] = AttrRec(r) }));
                        break;
                    }
                case 88208 or 88218: break;
                case 88212:
                    {
                        var del = HandlerHelpers.ReadIdList(req.Body, "item_id_list").ToHashSet();
                        var dt = r.Treasures.Where(t => del.Contains(t.Id)).ToList();
                        var dO = r.Others.Where(del.Contains).ToList();
                        r.Treasures.RemoveAll(t => del.Contains(t.Id));
                        r.Others.RemoveAll(del.Contains);
                        frames.Add(F(srv, 88021, new() { ["item_list"] = new object[] { new Dictionary<string, object?> { ["opt"] = 2u, ["treasure_list"] = TreasureRec(dt), ["other_list"] = dO.Select(x => (uint)x).ToArray() } } }));
                        break;
                    }
                case 88222: ShopBuy(o, (int)HandlerHelpers.ReadUInt(req.Body, "index"), frames, srv); break;
                case 88224:
                    {
                        Require(r.ShopWin is not null, "Not in a shop");
                        var cost = 20L << r.ShopWin!.ShopRefresh;
                        Require(r.Attr.GetValueOrDefault(Gold) >= cost, "Not enough gold");
                        r.Attr[Gold] -= cost;
                        var times = r.ShopWin.ShopRefresh + 1;
                        r.ShopWin = MakeShop(o, r);
                        r.ShopWin.ShopRefresh = times;
                        frames.Add(F(srv, 88013, new() { ["attr_list"] = AttrRec(r) }));
                        frames.Add(F(srv, 88027, new() { ["shop_info"] = PopRec(r.ShopWin) }));
                        break;
                    }
                case 88300:
                    {
                        var tree = (long)HandlerHelpers.ReadUInt(req.Body, "tree_id");
                        Require(Cfg.GetProperty("skill_tree").TryGetProperty(tree.ToString(), out var nodeCfg), "Unknown tech node");
                        if (!o.Tree.Contains(tree))
                        {
                            Require(nodeCfg.GetProperty("pre_nodes").EnumerateArray().All(p => o.Tree.Contains(p.GetInt64())), "Tech prerequisite locked");
                            var cost = I(nodeCfg, "cost");
                            if (cost > 0) Require(await accounts.ConsumeAndGrantAsync(uid, [(71, cost)], [], ct), "Not enough research points");
                            o.Tree.Add(tree);
                            frames.AddRange(await InventorySync.FramesAsync(accounts, codec, req, [71], ct));
                        }
                        frames.Add(F(srv, 88305, OutsideRec(o)));
                        break;
                    }
                case 88306:
                    {
                        await SaveAsync(uid, o, ct);
                        frames.AddRange(await SettleRunAsync(o, req, srv, ct));
                        await SaveAsync(uid, o, ct);
                        return frames;
                    }
                case 88312:
                    foreach (var id in HandlerHelpers.ReadIdList(req.Body, "colllection_id"))
                    {
                        Unlock(o, id, (int)HandlerHelpers.ReadUInt(req.Body, "type"));
                        if (!o.Viewed.Contains(id)) o.Viewed.Add(id);
                    }
                    break;
                case 88316: o.LastScoreId = HandlerHelpers.ReadUInt(req.Body, "id"); break;
            }
        }
        catch (ModeError) { result = 2; frames.Clear(); }
        if (result == 0) await SaveAsync(uid, o, ct);
        frames.Add(cmd == 88200
            ? HandlerHelpers.Reply(codec, req, 88201, new Dictionary<string, object?> { ["result"] = result, ["window_opt"] = false })
            : HandlerHelpers.ResultOnly(codec, req, cmd + 1, result));
        return frames;
    }

    private void Start(Outside o, GameRequest req, List<GameResponse> frames, ushort srv)
    {
        var diff = Math.Clamp((int)HandlerHelpers.ReadUInt(req.Body, "difficult", 1), 1, Math.Min(4, o.MaxDifficult));
        var heroes = new List<Hero>();
        if (req.Body.GetValueOrDefault("hero_list") is System.Collections.IEnumerable list)
            foreach (var h in list)
                if (h is IReadOnlyDictionary<string, object?> hd && HandlerHelpers.ReadUInt(hd, "hero_id") is var id and > 0)
                    heroes.Add(new Hero { HeroId = id, TempId = HandlerHelpers.ReadUInt(hd, "temp_id") });
        Require(heroes.Count > 0, "Rogue team is empty");
        var affixes = HandlerHelpers.ReadIdList(req.Body, "affix_pool_id_list").ToList();
        var dcfg = Cfg.GetProperty("difficulties").EnumerateArray().FirstOrDefault(d => I(d, "id") == diff);
        var rate = dcfg.ValueKind == JsonValueKind.Object ? I(dcfg, "score", 100) : 100;
        if (diff == 4 && affixes.Count > 0)
            rate = Math.Min(250, rate + affixes.Sum(a => Cfg.GetProperty("optional_affixes").TryGetProperty(a.ToString(), out var af) ? I(af, "point") : 0) * 5);
        var t = o.Tree;
        var exempt = t.Contains(10699) ? 2 : 0;
        var r = new Run
        {
            InGame = true, Difficult = diff, ScoreRate = rate, StartTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Heroes = heroes, Affixes = affixes,
            Map = GenerateFloor(1),
            Attr = new()
            {
                [Gold] = 100 + 80 * new long[] { 10202, 10502, 10802, 11102 }.Count(t.Contains), [3] = Math.Max(1, heroes.Count), [Revive] = 0,
                [ReviveLimit] = 2 + (t.Contains(10999) ? 1 : 0), [ResetCnt] = 2 + (t.Contains(10203) ? 2 : 0), [RecruitCnt] = 1, [9] = 3, [10] = 3, [ShopRate] = 1000,
                [ShopTreasureMax] = 2 + (t.Contains(10503) ? 2 : 0), [ShopRelicMax] = 2 + (t.Contains(10803) ? 2 : 0), [14] = 750, [15] = 200,
                [16] = 50 + (t.Contains(11299) ? 20 : 0), [17] = 10 + (t.Contains(11299) ? 20 : 0),
                [HpRecovery] = 20 * new long[] { 10201, 10501, 10801, 11101 }.Count(t.Contains), [GoldRate] = 1000, [Mech] = 0, [MechThreshold] = 100,
                [MaxHp] = 1000 + 100 * new long[] { 10101, 10401, 10701, 11001 }.Count(t.Contains), [Exempt] = exempt, [ExtraTreasure] = t.Contains(10399) ? 1 : 0
            }
        };
        ExemptMarks(r.Map, exempt);
        o.Run = r;
        o.Difficult = diff;
        var (fid, next) = Fetters();
        frames.AddRange(SnapshotFrames(srv, r));
        frames.Add(F(srv, 88029, new() { ["finish_guide"] = 1u, ["template_id"] = (uint)Template }));
        frames.Add(F(srv, 88315, new() { ["activity_id"] = 1000u, ["next_timestamps"] = (uint)next, ["fetters_id"] = (uint)fid }));
        frames.Add(F(srv, 88023, EventRec(o, r, null)));
    }

    private void Recruit(Outside o, GameRequest req, List<GameResponse> frames, ushort srv)
    {
        var r = o.Run;
        Require(r.InGame, "Rogue run not active");
        var added = 0;
        if (req.Body.GetValueOrDefault("hero_list") is System.Collections.IEnumerable list)
            foreach (var h in list)
                if (h is IReadOnlyDictionary<string, object?> hd && HandlerHelpers.ReadUInt(hd, "hero_id") is var id and > 0 && r.Heroes.All(x => x.HeroId != id))
                {
                    r.Heroes.Add(new Hero { HeroId = id, TempId = HandlerHelpers.ReadUInt(hd, "temp_id"), HpRatio = (int)HandlerHelpers.ReadUInt(hd, "hp_ratio", 10000) });
                    added++;
                }
        if (added > 0 && Has(r, 1310041)) r.Attr[MaxHp] = r.Attr.GetValueOrDefault(MaxHp, 1000) + 120 * added;
        var cur = r.Map.FirstOrDefault(n => n.NodeId == r.SelectNode);
        r.Event = cur?.NodeType == Rest ? new EventWin { EventId = 50011, Options = [500111, 500112, 500113], Trigger = 1 } : null;
        r.PopWin = null;
        frames.Add(F(srv, 88019, new() { ["hero_list"] = HeroRec(r), ["modify_type"] = 1u }));
        if (r.Event is { } e) frames.Add(F(srv, 88023, EventRec(o, r, e)));
    }

    private Pop MakeShop(Outside o, Run r)
    {
        var owned = Owned(r);
        List<long> PickShop(string key, long count)
        {
            var pool = Cfg.GetProperty(key).EnumerateArray().Select(x => x.GetInt64()).OrderBy(_ => Random.Shared.Next()).ToList();
            var fresh = pool.Where(i => !owned.Contains(i)).Take((int)count).ToList();
            foreach (var i in pool) { if (fresh.Count >= count) break; if (!fresh.Contains(i)) fresh.Add(i); }
            return fresh;
        }
        var ids = PickShop("shop_treasures", r.Attr.GetValueOrDefault(ShopTreasureMax, 2)).Concat(PickShop("shop_relics", r.Attr.GetValueOrDefault(ShopRelicMax, 2)));
        return new Pop { EventType = PopShop, DropType = 0, Params = Params(o, ids, buy: 1) };
    }

    private void SelectNode(Outside o, int nodeId, List<GameResponse> frames, ushort srv)
    {
        var r = o.Run;
        Require(r.InGame, "Rogue run not active");
        var active = r.Map.Where(n => n.State is Unclean or Clean).Select(n => n.NodeId).ToList();
        if (!active.Contains(nodeId) && active.Count > 0) nodeId = active[0];
        var node = r.Map.FirstOrDefault(n => n.NodeId == nodeId) ?? throw new ModeError("Unknown rogue node");
        r.SelectNode = nodeId;
        var battle = node.NodeType is Normal or Elite or Boss or LastBoss or Demon;
        if (battle && node.NodeType == Normal && Has(r, 1310056) && Random.Shared.NextDouble() < 0.8) { node.Param = 4105101; r.Expired.Add(1310056); }
        if (battle && r.Silence > 0 && node.Param is not (0 or 4101101)) node.Param = 4101101;
        var exempt = node.NodeType is Normal or Elite && node.Param == 0 && r.Attr.GetValueOrDefault(Exempt) > 0;
        if (battle && node.Param == 0 && !exempt)
        {
            foreach (var n in r.Map.Where(n => n.NodeType is Normal or Elite && n.State != Clean && n.Param == 0)) n.Param = n.NodeType == Normal ? 4101101 : 4101201;
            frames.Add(F(srv, 88003, new() { ["map_info"] = MapRec(r), ["second_map_info"] = Array.Empty<object>() }));
            return;
        }
        var realBattle = battle && node.Param != 0;
        if (!realBattle) node.State = Clean;
        foreach (var n in r.Map.Where(n => n.Col == node.Col && n.NodeId != node.NodeId && n.State is Lock or Unclean)) n.State = Lock;
        if (!realBattle) foreach (var n in r.Map.Where(n => node.Next.Contains(n.NodeId) && n.State == Lock)) n.State = Unclean;
        r.ShopWin = null;
        r.Event = null;
        if (exempt)
        {
            r.Attr[Exempt] = Math.Max(0, r.Attr[Exempt] - 1);
            if (r.Attr[Exempt] == 0)
                foreach (var n in r.Map.Where(n => n.NodeType is Normal or Elite && n.State != Clean && n.Param == 0)) n.Param = n.NodeType == Normal ? 4101101 : 4101201;
            Step(o, r, node.NodeType);
            r.Attr[Gold] = r.Attr.GetValueOrDefault(Gold) + GoldGain(r, node.NodeType, node.NodeType == Elite ? 80 : 40);
            HealAfterBattle(r);
            var b3 = r.Attr.GetValueOrDefault(16, 50) - 50;
            var b4 = r.Attr.GetValueOrDefault(17, 10) - 10;
            var relic = Random.Shared.NextDouble() < (node.NodeType == Elite ? 0.45 : 0.30);
            var items = relic ? RollUnowned(r, ItemRelic, 3, node.NodeType == Elite ? 2 : null)
                : RollUnowned(r, ItemTreasure, 3, node.NodeType == Elite ? Math.Max(2, RollQuality(b3, b4)) : RollQuality(b3, b4));
            r.PopWin = new Pop { EventType = relic ? PopRelic : PopTreasure, Params = Params(o, items) };
            if (r.Attr.GetValueOrDefault(ExtraTreasure) == 1 && r.Battles <= 2) r.PopWin.Queue.Add(new Pop { EventType = PopTreasure, DropType = 2 });
            if (MechPopup(o, r) is { } mp) r.PopWin.Queue.Add(mp);
        }
        else switch (node.NodeType)
        {
            case Shop:
                Step(o, r, Shop);
                r.ShopWin = MakeShop(o, r);
                break;
            case Rest:
                if (Has(r, 1310012)) foreach (var h in r.Heroes.Where(h => h.HpRatio > 0)) h.HpRatio = Math.Min(10000, h.HpRatio + 1000);
                if (Has(r, 1310018)) r.Attr[RecruitCnt] = 3;
                OpenEvent(o, r, node.Param == 0 ? 50010 : node.Param);
                break;
            case Event or Plot:
                {
                    long ev;
                    if (node.NodeType == Event)
                    {
                        var cand = EventPool.Where(e => !r.UsedEvents.Contains(e)).ToList();
                        ev = EventPool.Contains((int)node.Param) && !r.UsedEvents.Contains(node.Param) ? node.Param : (cand.Count > 0 ? cand : EventPool.ToList())[Random.Shared.Next(Math.Max(1, cand.Count > 0 ? cand.Count : EventPool.Length))];
                        if (!r.UsedEvents.Contains(ev)) r.UsedEvents.Add(ev);
                        node.Param = ev;
                    }
                    else ev = node.Param == 0 ? 60020 : node.Param;
                    Unlock(o, ev, 2);
                    OpenEvent(o, r, ev);
                    r.MechPending = false;
                    break;
                }
            case Reward:
                {
                    Step(o, r, Reward);
                    var relic = Random.Shared.Next(2) == 0;
                    r.PopWin = new Pop { EventType = relic ? PopRelic : PopTreasure, Params = Params(o, RollUnowned(r, relic ? ItemRelic : ItemTreasure)) };
                    break;
                }
        }
        frames.Add(F(srv, 88003, new() { ["map_info"] = MapRec(r), ["second_map_info"] = Array.Empty<object>() }));
        if (r.ShopWin is not null) frames.Add(F(srv, 88027, new() { ["shop_info"] = PopRec(r.ShopWin) }));
        if (r.Event is { } e) frames.Add(F(srv, 88023, EventRec(o, r, e)));
        else if (r.PopWin is { } p) frames.Add(F(srv, 88015, new() { ["other_info"] = PopRec(p) }));
        frames.Add(F(srv, 88013, new() { ["attr_list"] = AttrRec(r) }));
    }

    private void OpenEvent(Outside o, Run r, long ev)
    {
        r.HistEvents.Add(ev);
        r.Event = new EventWin { EventId = ev, Options = UnlockedOptions(o, r, ev), Trigger = 1 };
    }

    private void Choose(Outside o, long opt, List<GameResponse> frames, ushort srv)
    {
        var r = o.Run;
        Require(r.InGame, "Rogue run not active");
        var d = new Delta();
        void Tail()
        {
            if (d.NewTreasures.Count > 0 || d.NewOthers.Count > 0)
                frames.Add(F(srv, 88021, new() { ["item_list"] = new object[] { new Dictionary<string, object?> { ["opt"] = 1u, ["treasure_list"] = TreasureRec(d.NewTreasures), ["other_list"] = d.NewOthers.Select(x => (uint)x).ToArray() } } }));
            if (d.Updates.Count > 0) frames.Add(F(srv, 88205, new() { ["update_list"] = d.Updates.ToArray() }));
            frames.Add(F(srv, 88019, new() { ["hero_list"] = HeroRec(r), ["modify_type"] = 1u }));
            frames.Add(F(srv, 88013, new() { ["attr_list"] = AttrRec(r) }));
            frames.Add(F(srv, 88023, EventRec(o, r, r.Event)));
            if (r.Event is null)
            {
                frames.Add(F(srv, 88003, new() { ["map_info"] = MapRec(r), ["second_map_info"] = Array.Empty<object>() }));
                if (r.PopWin is { } p) frames.Add(F(srv, 88015, new() { ["other_info"] = PopRec(p) }));
                else if (FloorClear(r.Map)) frames.Add(F(srv, 88221, new() { ["sign"] = 1u }));
            }
        }
        if (opt == 0)
        {
            Finish(r.Map, r.SelectNode);
            Step(o, r, Event);
            r.SelectNode = 0;
            r.Event = null;
            Tail();
            return;
        }
        switch (opt)
        {
            case 500101:
                r.Event = null;
                r.PopWin = new Pop { EventType = PopRecruit };
                frames.Add(F(srv, 88015, new() { ["other_info"] = PopRec(r.PopWin) }));
                return;
            case 500102:
                r.Event = new EventWin { EventId = 50011, Options = [500111, 500112, 500113], Trigger = 1 };
                Tail();
                return;
            case 500111 or 500131:
                r.Attr[Revive] = Math.Min(r.Attr.GetValueOrDefault(ReviveLimit, 5), r.Attr.GetValueOrDefault(Revive) + 1);
                foreach (var h in r.Heroes.Where(h => h.HpRatio > 0)) h.HpRatio = 10000;
                r.Event = new EventWin { EventId = 50012, Trigger = 1 };
                Tail();
                return;
            case 500112 or 500132:
                if (r.Treasures.Where(t => Item(t.Id) is { } ic && I(ic, "sub_type") == 1 && t.Rare < 3).OrderBy(_ => Random.Shared.Next()).FirstOrDefault() is { } up)
                {
                    var old = up.Rare;
                    up.Rare++;
                    d.Updates.Add(Delta.Upd(3, up.Id, 0, up.Rare, old));
                }
                r.Event = new EventWin { EventId = 50012, Trigger = 1 };
                Tail();
                return;
            case 500113 or 500133:
                r.Attr[ResetCnt] = r.Attr.GetValueOrDefault(ResetCnt) + 2;
                r.Event = new EventWin { EventId = 50012, Trigger = 1 };
                Tail();
                return;
        }
        var curEvent = r.Event?.EventId ?? 0;
        if (curEvent != 0 && Cfg.GetProperty("events").TryGetProperty(curEvent.ToString(), out var evCfg)
            && evCfg.GetProperty("option_list").EnumerateArray().Select(x => x.GetInt64()).ToList() is { Count: > 0 } valid && !valid.Contains(opt))
        {
            r.Event = new EventWin { EventId = curEvent, Options = UnlockedOptions(o, r, curEvent), Trigger = 1 };
            Tail();
            return;
        }
        r.HistOptions.Add(opt);
        var optCfg = Option(opt);
        long jump;
        var battleConv = 0;
        if (optCfg is { } oc)
        {
            Require(OptionUnlocked(o, r, opt), "Rogue option locked");
            if (BattleOption(opt, oc))
            {
                var target = r.Map.FirstOrDefault(n => n.NodeId == r.SelectNode) ?? r.Map.FirstOrDefault(n => n.State == Clean);
                if (target is not null)
                {
                    target.NodeType = Normal;
                    target.Param = 4101101;
                    target.State = Unclean;
                    target.EvRelic = opt == 300701;
                    battleConv = target.NodeId;
                }
            }
            var effs = oc.TryGetProperty("effect_ids", out var ei) ? ei.EnumerateArray().Select(x => x.GetInt64()).ToList() : [];
            if (opt == 600211) effs.Add(6002111);
            Effects(o, r, effs, d, opt);
            if (battleConv == 0 && effs.Count == 0 && RandomItems(ItemTreasure, 1) is [var fb]) GiveTreasure(o, r, d, fb);
            if (d.FinalRoom != 0 && Cfg.GetProperty("rooms").TryGetProperty(d.FinalRoom.ToString(), out var fr) && fr.GetProperty("params") is { ValueKind: JsonValueKind.Array } fp && fp.GetArrayLength() > 0
                && r.Map.Where(n => n.NodeType is LastBoss or Boss).OrderByDescending(n => n.Col).FirstOrDefault() is { } bossNode)
                bossNode.Param = fp[0].GetInt64();
            if (d.Ending != 0) r.PendingEnding = d.Ending;
            jump = battleConv != 0 || d.MapSwitch != 0 ? 0 : Jump(opt);
        }
        else jump = Jump(opt);
        r.Event = null;
        if (jump != 0 && Cfg.GetProperty("events").TryGetProperty(jump.ToString(), out _))
        {
            Unlock(o, jump, 2);
            OpenEvent(o, r, jump);
        }
        else if (battleConv == 0)
        {
            Step(o, r, Event);
            if (MechPopup(o, r) is { } mp) r.PopWin = mp;
            else
            {
                Finish(r.Map, r.SelectNode);
                r.SelectNode = 0;
            }
        }
        Tail();
    }

    private void Commit(Outside o, long arg, List<GameResponse> frames, ushort srv, GameRequest req)
    {
        var r = o.Run;
        Require(r.InGame, "Rogue run not active");
        var win = r.PopWin;
        var pick = win?.Params.FirstOrDefault(p => p.Index == arg || p.Value == arg);
        var id = pick?.Value ?? (win is not null && arg >= 1 && arg <= win.Params.Count ? win.Params[(int)arg - 1].Value : arg);
        if (id == 0)
        {
            r.PopWin = null;
            frames.Add(HandlerHelpers.Reply(codec, req, 88201, new Dictionary<string, object?> { ["result"] = 0u, ["window_opt"] = false }));
            return;
        }
        var d = new Delta();
        var type = Item(id) is { } ic ? I(ic, "type") : (id.ToString().StartsWith("14") ? ItemTreasure : ItemRelic);
        if (type == ItemTreasure) GiveTreasure(o, r, d, id, Math.Max(pick?.Rare ?? 1, Rare(id)));
        else GiveOther(o, r, d, id, type, unique: false);
        if (id == 1100001) r.Attr[Gold] = r.Attr.GetValueOrDefault(Gold) + 150;
        if (id == 1100004) r.Attr[Mech] = r.Attr.GetValueOrDefault(Mech) + 40;
        if (type == ItemRelic) Effects(o, r, [id * 10 + 1], d);
        Pop? next = null;
        if (win?.Queue is { Count: > 0 } q)
        {
            var cur = q[0];
            var t = cur.EventType switch { PopRelic => ItemRelic, PopMechanism => ItemMechanism, _ => ItemTreasure };
            var ps = cur.Params.Count > 0 ? cur.Params : Params(o, RollUnowned(r, t));
            if (ps.Count > 0) next = new Pop { EventType = cur.EventType, Params = ps, DropType = Math.Max(2, cur.DropType), Queue = q.Skip(1).ToList() };
        }
        if (next is null && r.SelectNode != 0)
        {
            var node = r.Map.FirstOrDefault(n => n.NodeId == r.SelectNode);
            if (node is { State: Clean }) Finish(r.Map, node.NodeId);
            r.SelectNode = 0;
        }
        r.PopWin = next;
        frames.Add(F(srv, 88021, new() { ["item_list"] = new object[] { new Dictionary<string, object?> { ["opt"] = 1u, ["treasure_list"] = TreasureRec(d.NewTreasures), ["other_list"] = d.NewOthers.Select(x => (uint)x).ToArray() } } }));
        frames.Add(F(srv, 88205, new() { ["update_list"] = d.Updates.ToArray() }));
        frames.Add(F(srv, 88013, new() { ["attr_list"] = AttrRec(r) }));
        frames.Add(F(srv, 88003, new() { ["map_info"] = MapRec(r), ["second_map_info"] = Array.Empty<object>() }));
        if (next is not null) frames.Add(F(srv, 88015, new() { ["other_info"] = PopRec(next) }));
        frames.Add(HandlerHelpers.Reply(codec, req, 88201, new Dictionary<string, object?> { ["result"] = 0u, ["window_opt"] = true }));
        if (next is null && (FloorClear(r.Map) || r.FloorState == 1 && r.Floor >= 3)) frames.Add(F(srv, 88221, new() { ["sign"] = 1u }));
    }

    private void ShopBuy(Outside o, int index, List<GameResponse> frames, ushort srv)
    {
        var r = o.Run;
        Require(r.InGame && r.ShopWin is not null, "Not in a shop");
        if (index == 0)
        {
            Finish(r.Map, r.SelectNode);
            r.SelectNode = 0;
            r.ShopWin = null;
            frames.Add(F(srv, 88003, new() { ["map_info"] = MapRec(r), ["second_map_info"] = Array.Empty<object>() }));
            frames.Add(F(srv, 88027, new() { ["shop_info"] = PopRec(null) }));
            if (FloorClear(r.Map)) frames.Add(F(srv, 88221, new() { ["sign"] = 1u }));
            return;
        }
        var offer = r.ShopWin!.Params.FirstOrDefault(p => p.Index == index && p.BuyTimes > 0) ?? throw new ModeError("Sold out");
        var id = offer.Value;
        var ic = Item(id) ?? throw new ModeError("Unknown goods");
        var prices = ic.TryGetProperty("prices", out var pr) && pr.ValueKind == JsonValueKind.Array && pr.GetArrayLength() > 0 ? pr.EnumerateArray().Select(x => x.GetInt64()).ToList() : [I(ic, "price")];
        var rare = I(ic, "type") == ItemTreasure && I(ic, "sub_type") == 1 ? Math.Clamp(offer.Rare, 1, 3) : 1;
        var basePrice = rare <= prices.Count ? prices[rare - 1] : prices[0];
        var cost = (long)Math.Ceiling(Math.Ceiling(basePrice * r.Attr.GetValueOrDefault(ShopRate, 1000) / 1000.0) * offer.Discount / 100.0);
        Require(r.Attr.GetValueOrDefault(Gold) >= cost, "Not enough gold");
        r.Attr[Gold] -= cost;
        offer.BuyTimes = 0;
        offer.IsNew = 0;
        var d = new Delta();
        var isTreasure = I(ic, "type") == ItemTreasure || id.ToString().StartsWith("14");
        if (Has(r, 1310004) && RollUnowned(r, isTreasure ? ItemTreasure : ItemRelic, 4).Where(i => i != id).FirstOrDefault() is var re and > 0)
        {
            offer.Value = re; offer.Rare = Rare(re); offer.BuyTimes = 1; offer.Discount = new[] { 60, 70, 80, 90 }[Random.Shared.Next(4)]; offer.IsNew = o.Collection.ContainsKey(re) ? 0 : 1;
        }
        if (isTreasure) GiveTreasure(o, r, d, id, offer.Rare > 0 ? Math.Max(offer.Rare, Rare(id)) : null);
        else
        {
            GiveOther(o, r, d, id, unique: false);
            Effects(o, r, [id * 10 + 1], d);
        }
        frames.Add(F(srv, 88013, new() { ["attr_list"] = AttrRec(r) }));
        frames.Add(F(srv, 88021, new() { ["item_list"] = new object[] { new Dictionary<string, object?> { ["opt"] = 1u, ["treasure_list"] = TreasureRec(d.NewTreasures), ["other_list"] = d.NewOthers.Select(x => (uint)x).ToArray() } } }));
        frames.Add(F(srv, 88205, new() { ["update_list"] = d.Updates.ToArray() }));
        frames.Add(F(srv, 88027, new() { ["shop_info"] = PopRec(r.ShopWin) }));
    }

    /// <summary>88306 final settlement: score × difficulty rate, next difficulty on victory endings, survey (70, ×6/1000)
    /// and research (71, ×1/1000) items.</summary>
    private async Task<List<GameResponse>> SettleRunAsync(Outside o, GameRequest req, ushort srv, CancellationToken ct)
    {
        var r = o.Run;
        Require(r.InGame, "No rogue run to settle");
        var win = r.EndingWon && r.PendingEnding is 1 or 2 or 3 or 6 || r.EndingWon;
        var ending = win ? (r.PendingEnding is 1 or 2 or 3 or 6 ? r.PendingEnding : 1) : 4;
        var clean = r.Map.Where(n => n.State is Clean or Over).ToList();
        int normal = clean.Count(n => n.NodeType == Normal), elite = clean.Count(n => n.NodeType == Elite), boss = clean.Count(n => n.NodeType is Boss or LastBoss);
        int mech = r.Others.Count(x => x.ToString().StartsWith("12")), relics = r.Others.Count(x => x.ToString().StartsWith("13"));
        long baseScore = r.Floor * 1000 + clean.Count * 100 + r.Treasures.Count * 100 + relics * 200 + mech * 100 + normal * 100 + elite * 150 + boss * 1000;
        var finalScore = baseScore * r.ScoreRate / 100;
        var victory = win && ending is 1 or 2 or 3 or 6;
        if (victory)
        {
            if (r.Difficult == o.MaxDifficult && o.MaxDifficult < 4) o.MaxDifficult++;
            Unlock(o, ending, 5);
            o.DiffClears[r.Difficult] = o.DiffClears.GetValueOrDefault(r.Difficult) + 1;
            o.Endings[ending] = o.Endings.GetValueOrDefault(ending) + 1;
        }
        o.Point += finalScore;
        var coef = Cfg.GetProperty("reward_coefficients");
        var survey = finalScore * coef.GetProperty("survey").GetInt64() / 1000;
        var research = finalScore * coef.GetProperty("research").GetInt64() / 1000;
        var granted = await accounts.GrantItemsAsync(req.AccountId, new[] { (70L, survey), (71L, research) }.Where(x => x.Item2 > 0), ct);
        var unlocked = o.CollectionTs.Where(c => c.Value >= r.StartTs && Item(c.Key) is not null).Select(c => (uint)c.Key).ToArray();
        r.InGame = false;
        r.SelectNode = 0;
        r.FloorState = win ? 1 : 2;
        r.PopWin = null; r.ShopWin = null; r.Event = null;
        var frames = await InventorySync.FramesAsync(accounts, codec, req, granted.Select(x => x.Item1), ct);
        object Kv(int k, long v) => new Dictionary<string, object?> { ["key"] = (uint)k, ["value"] = (uint)v };
        frames.Add(HandlerHelpers.Reply(codec, req, 88307, new Dictionary<string, object?>
        {
            ["result"] = 0u,
            ["end_info_list"] = new[] { Kv(1, win ? r.Floor : Math.Max(0, r.Floor - 1)), Kv(2, clean.Count), Kv(3, r.Treasures.Count), Kv(4, relics), Kv(5, mech), Kv(6, normal), Kv(7, elite), Kv(8, boss), Kv(100, baseScore), Kv(101, finalScore) },
            ["collection_list"] = o.Collection.GroupBy(c => c.Value).Select(g => (object)new Dictionary<string, object?> { ["type"] = (uint)g.Key, ["item_list"] = g.Select(x => (uint)x.Key).ToArray() }).ToArray(),
            ["unlock_collection"] = unlocked,
            ["total_time"] = (uint)Math.Max(1, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - r.StartTs)
        }));
        frames.Add(F(srv, 88305, OutsideRec(o)));
        frames.Add(F(srv, 88001, Session(r)));
        return frames;
    }
}
