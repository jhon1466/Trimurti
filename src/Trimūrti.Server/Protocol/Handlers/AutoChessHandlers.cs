using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 人机决斗王 (AutoChess PVE, first activity 3740601), port of Alpha-Persei autochess_service / autochess_combat /
/// autochess_preparation / autochess_special / autochess_progress on Data/Configs/autochess_cfg.json and
/// autochess_settings.json. Rounds: shop (chess by star rates, props), buy / sell / merge / lock / arrange, then a
/// server-side battle simulation driven by the native buff contracts (moments, selectors, actions) whose action stream
/// the client replays; win/defeat counts decide the stage, first clears record medals and the stage drop.
/// </summary>
public sealed class AutoChess(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    private const int Activity = 3740601;
    private const int None = 0, Prepare = 1, PrepareEnd = 2, RoundSettle = 3, TotalWin = 4, TotalLose = 5;
    private static readonly int[] SupportedProps = [101, 102, 103, 104, 201, 202, 203, 204, 205, 301, 302, 303, 304, 401, 402, 404, 405];

    private JsonElement Cfg => config.Get("autochess_cfg.json") ?? throw new ModeError("Missing autochess_cfg.json");
    private JsonElement Settings => (config.Get("autochess_settings.json") ?? throw new ModeError("Missing autochess_settings.json")).GetProperty("settings");
    private static void Require(bool ok, string m) { if (!ok) throw new ModeError(m); }
    private int Setting(string name, int def) => Settings.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 ? v[0].GetInt32() : def;

    internal JsonElement? Chess(long id) => Cfg.GetProperty("chess").TryGetProperty(id.ToString(), out var v) ? v : null;
    internal JsonElement? Buff(long id) => Cfg.GetProperty("buffs").TryGetProperty(id.ToString(), out var v) ? v : null;
    private JsonElement? Stage(long id) => Cfg.GetProperty("stages").TryGetProperty(id.ToString(), out var v) ? v : null;
    internal static int I(JsonElement e, string k, int d = 0) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : d;
    internal static List<long> Ints(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList() : [];

    // ---------------- state ----------------

    public sealed class BuffRec { public long UniqueId { get; set; } public long BuffId { get; set; } public int OwnerType { get; set; } public int SourceType { get; set; } = 1; public long SourceUid { get; set; } public long SourceCfgId { get; set; } }
    public sealed class Piece { public long UniqueId { get; set; } public long ChessId { get; set; } public int Index { get; set; } public int Exp { get; set; } = 1; public long Atk { get; set; } public long Hp { get; set; } public List<BuffRec> Buffs { get; set; } = []; public int PropRepeatUsed { get; set; } }
    public sealed class ShopItem { public int ShopType { get; set; } public long ShopUniqueId { get; set; } public long Id { get; set; } public int Index { get; set; } public int Exp { get; set; } = 1; public long Atk { get; set; } public long Hp { get; set; } public int IsLock { get; set; } }
    public sealed class Pending { public long StageId { get; set; } public int Round { get; set; } public long BattleUid { get; set; } public int Result { get; set; } public int State { get; set; } public bool Consumed { get; set; } }

    public sealed class Session
    {
        public int State { get; set; }
        public long StageId { get; set; }
        public int Round { get; set; } = 1;
        public long Gold { get; set; } = 10;
        public int Hp { get; set; } = 3;
        public int Win { get; set; }
        public int Defeat { get; set; }
        public int ShopLevel { get; set; } = 1;
        public List<Piece> Board { get; set; } = [];
        public List<Piece> Bench { get; set; } = [];
        public List<ShopItem> Shop { get; set; } = [];
        public int FreeRefresh { get; set; }
        public int FreeProp { get; set; }
        public int BuyCount311 { get; set; }
        public Dictionary<string, long> Effects { get; set; } = [];
        public long[] TeamBonus { get; set; } = [0, 0];
        public Dictionary<string, long[]> ChessBonus { get; set; } = [];
        public int RestartTimes { get; set; }
        public string? Snapshot { get; set; }
        public Pending? Pending { get; set; }
        public string? ReplayInfo { get; set; }
        public string? ReplayActions { get; set; }
        public long NextUid { get; set; } = 100000;
    }

    public sealed class Progress { public Dictionary<long, long> Stages { get; set; } = []; public Dictionary<long, long> Medals { get; set; } = []; public Session Session { get; set; } = new(); }

    private async Task<Progress> LoadAsync(long uid, CancellationToken ct) => await accounts.GetJsonAsync<Progress>(uid, "autochess_v1", ct) ?? new Progress();
    private Task SaveAsync(long uid, Progress p, CancellationToken ct) => accounts.SetJsonAsync(uid, "autochess_v1", p, ct);

    internal static long NewUid(Session s) => ++s.NextUid;

    internal int Level(long chessId, int exp) => 1 + (Chess(chessId) is { } c && c.TryGetProperty("exp", out var e) ? Ints(e).Count(v => exp >= v) : 0);

    private int BaseExp(Session s) => Stage(s.StageId) is { } st && st.TryGetProperty("scene_buff", out var sb) && Ints(sb).Contains(622) ? 3 : 1;

    private void ApplyBonus(Session s, long chessId, ref long atk, ref long hp)
    {
        if (s.ChessBonus.TryGetValue(chessId.ToString(), out var b)) { atk += b[0]; hp += b[1]; }
    }

    internal List<long> LevelBuffIds(long chessId, int level)
    {
        if (Chess(chessId) is not { } c || !c.TryGetProperty("level_buffs", out var lb) || lb.ValueKind != JsonValueKind.Array || lb.GetArrayLength() == 0) return [];
        var entry = lb[Math.Min(level, lb.GetArrayLength()) - 1];
        return entry.ValueKind == JsonValueKind.Number ? [entry.GetInt64()] : Ints(entry);
    }

    private List<BuffRec> PieceBuffs(Piece p, int owner = 0) =>
        LevelBuffIds(p.ChessId, Level(p.ChessId, p.Exp)).Select((b, i) => new BuffRec { UniqueId = 300_000_000 + p.UniqueId * 10 + i + 1, BuffId = b, OwnerType = owner, SourceUid = p.UniqueId, SourceCfgId = p.ChessId })
            .Concat(p.Buffs).ToList();

    // ---------------- wire ----------------

    private static object Kv(int k, long v) => new Dictionary<string, object?> { ["key"] = (uint)k, ["value"] = (int)v };
    internal static Dictionary<string, object?> BuffWire(BuffRec b) => new()
    {
        ["unique_id"] = (uint)b.UniqueId, ["buff_id"] = (uint)b.BuffId, ["owner_type"] = (uint)b.OwnerType, ["source_type"] = (uint)b.SourceType, ["source_uid"] = (uint)b.SourceUid, ["source_cfg_id"] = (uint)b.SourceCfgId
    };

    private Dictionary<string, object?> PieceWire(Piece p, int index) => new()
    {
        ["unique_id"] = (uint)p.UniqueId, ["chess_id"] = (uint)p.ChessId, ["index"] = (uint)index,
        ["chess_attr_list"] = new[] { Kv(1, p.Atk), Kv(2, p.Hp), Kv(3, p.Exp) }, ["buff_list"] = PieceBuffs(p).Select(b => (object)BuffWire(b)).ToArray()
    };

    private Dictionary<string, object?> SessionWire(Session s)
    {
        var attrs = new List<object>
        {
            Kv(1, s.Gold), Kv(2, 10 + s.Effects.GetValueOrDefault("restart_money_bonus")), Kv(3, 10 + s.Effects.GetValueOrDefault("money_ceiling_bonus")), Kv(4, 0), Kv(6, 4), Kv(7, 2), Kv(8, 2),
            Kv(9, 1), Kv(10, 1), Kv(11, s.ShopLevel), Kv(12, BaseExp(s)), Kv(13, 0), Kv(14, 0), Kv(15, 0), Kv(16, s.FreeRefresh), Kv(17, s.FreeProp), Kv(18, 0)
        };
        return new()
        {
            ["game_type"] = 0u,
            ["auto_chessboard_info"] = new Dictionary<string, object?>
            {
                ["base_info_list"] = new object[]
                {
                    new Dictionary<string, object?> { ["key"] = 10u, ["value"] = 0, ["value2"] = s.Pending?.BattleUid ?? 0L },
                    Kv(11, 1), Kv(1, s.Hp), Kv(4, s.Round), Kv(5, s.StageId), Kv(2, s.Win), Kv(6, 0)
                },
                ["chess_list"] = s.Board.OrderBy(p => p.Index).Select(p => (object)PieceWire(p, p.Index)).Concat(s.Bench.Select(p => (object)PieceWire(p, 0))).ToArray()
            },
            ["shop_items"] = s.Shop.Select(i => (object)new Dictionary<string, object?>
            {
                ["shop_type"] = (uint)i.ShopType, ["shop_unique_id"] = (uint)i.ShopUniqueId, ["id"] = (uint)i.Id, ["index"] = (uint)i.Index,
                ["chess_attr_list"] = new[] { Kv(1, i.Atk), Kv(2, i.Hp), Kv(3, i.Exp) }, ["is_lock"] = (uint)i.IsLock
            }).ToArray(),
            ["buff_list"] = Array.Empty<object>(), ["attr_list"] = attrs.ToArray(), ["pve_restart_times"] = (uint)s.RestartTimes
        };
    }

    private Dictionary<string, object?> StagesWire(Progress p) => new()
    {
        ["stage_list"] = p.Stages.OrderByDescending(x => x.Value).Select(x => (object)new Dictionary<string, object?> { ["id"] = (uint)x.Key, ["time"] = (uint)x.Value }).ToArray(),
        ["medal"] = p.Medals.Select(x => (object)new Dictionary<string, object?> { ["id"] = (uint)x.Key, ["level"] = 1u, ["unlock_time"] = (uint)x.Value, ["upgrade_time"] = 0u }).ToArray()
    };

    private GameResponse F(ushort srv, uint cmd, Dictionary<string, object?> body) => new(cmd, 0, srv, codec.Encode($"sc_{cmd}", body));
    private Dictionary<string, object?> StateWire(Session s) => new() { ["game_type"] = 0u, ["state"] = (uint)s.State };

    /// <summary>89207 ranked battle records: this server runs the PVE mode only, so no ranked match has been played.</summary>
    private static Dictionary<string, object?> BattleRecords() => new() { ["result"] = 0u, ["record"] = Array.Empty<object>(), ["medal_record"] = Array.Empty<object>() };

    public async Task<IReadOnlyList<(uint Cmd, byte[] Payload)>> LoginFramesAsync(long uid, CancellationToken ct)
    {
        var p = await LoadAsync(uid, ct);
        return
        [
            (89201, codec.Encode("sc_89201", StagesWire(p))), (89125, codec.Encode("sc_89125", StateWire(p.Session))),
            (89101, codec.Encode("sc_89101", SessionWire(p.Session))), (90007, codec.Encode("sc_90007", new Dictionary<string, object?> { ["can_reload"] = false })),
            (89207, codec.Encode("sc_89207", BattleRecords()))
        ];
    }

    // ---------------- shop ----------------

    private List<ShopItem> ChessShop(Session s)
    {
        var all = Cfg.GetProperty("chess").EnumerateObject().Select(p => p.Value)
            .Where(c => I(c, "type") == 0 && I(c, "shop_price") > 0 && c.TryGetProperty("level_buffs", out var lb) && lb.ValueKind == JsonValueKind.Array && lb.GetArrayLength() > 0
                && Ints(c.GetProperty("activity_id")).Contains(Activity)).ToList();
        Require(all.Count > 0, "No purchasable chess");
        var rates = s.ShopLevel switch { 1 => new[] { (1, 100) }, 2 => [(1, 65), (2, 35)], 3 => [(1, 40), (2, 40), (3, 20)], _ => [(1, 25), (2, 25), (3, 30), (4, 20)] };
        var baseExp = BaseExp(s);
        var items = new List<ShopItem>();
        for (var idx = 1; idx <= 4; idx++)
        {
            var roll = Random.Shared.Next(1, 101);
            var star = rates.First(r => (roll -= r.Item2) <= 0).Item1;
            var pool = all.Where(c => I(c, "star", 1) == star).ToList();
            if (pool.Count == 0) pool = all;
            var c = pool[Random.Shared.Next(pool.Count)];
            var body = Ints(c.GetProperty("body"));
            long atk = body.Count > 0 ? body[0] : 2, hp = body.Count > 1 ? body[1] : 3;
            ApplyBonus(s, I(c, "id"), ref atk, ref hp);
            items.Add(new ShopItem { ShopUniqueId = NewUid(s), Id = I(c, "id"), Index = idx, Exp = baseExp, Atk = atk, Hp = hp });
        }
        return items;
    }

    private List<ShopItem> PropShop(Session s)
    {
        if (s.StageId < Setting("auto_chess_item_unlock", 201)) return [];
        var pool = Cfg.GetProperty("items").EnumerateObject().Select(p => p.Value)
            .Where(c => SupportedProps.Contains(I(c, "id")) && Ints(c.GetProperty("activity_id")).Contains(Activity) && I(c, "unlock_stage") <= s.StageId
                && I(c, "star") > 0 && I(c, "star") <= s.ShopLevel && I(c, "shop_price") > 0).ToList();
        Require(pool.Count > 0, "No prop goods for this stage");
        return Enumerable.Range(1, 2).Select(i => new ShopItem { ShopType = 1, ShopUniqueId = NewUid(s), Id = I(pool[Random.Shared.Next(pool.Count)], "id"), Index = i }).ToList();
    }

    private void Restock(Session s)
    {
        var locked = s.Shop.Where(i => i.IsLock == 1).ToDictionary(i => (i.ShopType, i.Index));
        var fresh = ChessShop(s).Concat(PropShop(s)).Select(i => locked.TryGetValue((i.ShopType, i.Index), out var l) ? l : i).ToList();
        s.Shop = fresh.Concat(s.Shop.Where(i => i.ShopType == 2)).ToList();
    }

    // ---------------- preparation hooks (moments 3 buy, 4 prop, 5 sell, 9 round start, 10 prepare end) ----------------

    private List<JsonElement> PieceBuffCfgs(Piece p, bool legacySell = false)
    {
        if (Chess(p.ChessId) is not { } c || (!Ints(c.GetProperty("activity_id")).Contains(Activity) && !(legacySell && I(c, "id") is 211 or 311))) return [];
        return LevelBuffIds(p.ChessId, Level(p.ChessId, p.Exp)).Concat(p.Buffs.Select(b => b.BuffId)).Distinct().Select(Buff).OfType<JsonElement>().ToList();
    }

    private List<Piece> PrepTargets(Session s, Piece owner, int sel)
    {
        var own = s.Board.OrderBy(p => p.Index).ToList();
        var others = own.Where(p => p.UniqueId != owner.UniqueId).ToList();
        return sel switch
        {
            111 => own.Where(p => p.UniqueId == owner.UniqueId).ToList(),
            112 => own,
            114 => own.OrderBy(_ => Random.Shared.Next()).Take(1).ToList(),
            117 => others.OrderBy(_ => Random.Shared.Next()).Take(1).ToList(),
            120 => others.Where(p => Math.Abs(p.Index - owner.Index) == 1).ToList(),
            124 => own.Take(1).ToList(),
            151 => own.Where(p => PieceBuffCfgs(p).Any(b => I(b, "moment") == 1 && I(b, "action_type") == 7)).ToList(),
            _ => throw new ModeError("Unsupported preparation target")
        };
    }

    private void PrepEffect(Session s, Piece owner, JsonElement buff, int depth = 0)
    {
        Require(depth <= 8, "Preparation effect chain too deep");
        var p = buff.GetProperty("param");
        switch (I(buff, "action_type"))
        {
            case 5:
                foreach (var t in PrepTargets(s, owner, p[0].GetInt32())) { t.Atk += p[2][0].GetInt64(); t.Hp += p[2][1].GetInt64(); }
                break;
            case 6:
                {
                    int mode = p[0].GetInt32(), key = p[1].GetInt32();
                    var value = p[2].GetInt64();
                    switch (key)
                    {
                        case 1: s.Gold = value + (mode == 1 ? s.Gold : 0); break;
                        case 16: s.FreeRefresh = (int)(value + (mode == 1 ? s.FreeRefresh : 0)); break;
                        case 17: s.FreeProp = (int)(value + (mode == 1 ? s.FreeProp : 0)); break;
                        default: throw new ModeError("Unsupported economy attribute");
                    }
                    break;
                }
            case 4 when p[0].GetInt32() == 2:
                foreach (var bid in Ints(p[1])) PrepEffect(s, owner, Buff(bid) ?? throw new ModeError("Missing child buff"), depth + 1);
                break;
            case 24:
                foreach (var i in s.Shop.Where(i => i.ShopType is 0 or 2)) { i.Atk += p[1][0].GetInt64(); i.Hp += p[1][1].GetInt64(); }
                break;
            case 30:
                {
                    int rarity = p[0].GetInt32(), count = p[1].GetInt32();
                    var free = Enumerable.Range(1, 5).Where(i => s.Board.All(x => x.Index != i)).ToList();
                    Require(free.Count >= count, "No free cell for the summon");
                    var pool = Cfg.GetProperty("chess").EnumerateObject().Select(x => x.Value).Where(c => I(c, "type") == 0 && I(c, "star") == rarity && I(c, "shop_price") > 0 && Ints(c.GetProperty("activity_id")).Contains(Activity)).ToList();
                    Require(pool.Count > 0, "Missing summon pool");
                    foreach (var index in free.Take(count))
                    {
                        var c = pool[Random.Shared.Next(pool.Count)];
                        var body = Ints(c.GetProperty("body"));
                        long atk = body[0], hp = body[1];
                        ApplyBonus(s, I(c, "id"), ref atk, ref hp);
                        s.Board.Add(new Piece { UniqueId = NewUid(s), ChessId = I(c, "id"), Index = index, Exp = BaseExp(s), Atk = atk, Hp = hp });
                    }
                    break;
                }
            default: throw new ModeError("Unsupported preparation effect");
        }
    }

    private void Trigger(Session s, int moment, Piece? actor = null)
    {
        var owners = s.Board.OrderBy(p => p.Index).ToList();
        if (moment == 5 && actor is not null && owners.All(p => p.UniqueId != actor.UniqueId)) owners.Add(actor);
        var planned = owners.SelectMany(p => PieceBuffCfgs(p, moment is 3 or 5).Where(b => I(b, "moment") == moment).Select(b => (p, b))).ToList();
        var repeats = 1 + planned.Where(x => moment == 10 && I(x.b, "action_type") == 14).Sum(x => x.b.GetProperty("param")[0].GetInt32());
        if (moment == 3 && actor is not null && planned.Any(x => x.p.ChessId == 311 && x.p.UniqueId != actor.UniqueId)) s.BuyCount311++;
        foreach (var (owner, buff) in planned)
        {
            if (I(buff, "action_type") is 13 or 14) continue;
            var cond = I(buff, "enum");
            if (cond == 111 && (actor is null || owner.UniqueId != actor.UniqueId)) continue;
            if (cond == 113 && (actor is null || owner.UniqueId == actor.UniqueId)) continue;
            Require(cond is 0 or 1 or 2 or 111 or 113, "Unsupported preparation event");
            if (owner.ChessId == 311 && I(buff, "gap") > 0 && s.BuyCount311 % I(buff, "gap") != 0) continue;
            for (var i = 0; i < repeats; i++) PrepEffect(s, owner, buff);
        }
    }

    private int ConsumePropRepeats(Session s)
    {
        var extra = 0;
        foreach (var owner in s.Board.OrderBy(p => p.Index))
            foreach (var b in PieceBuffCfgs(owner).Where(b => I(b, "moment") == 4 && I(b, "action_type") == 13))
            {
                var limit = I(b, "effective_time");
                if (limit > 0 && owner.PropRepeatUsed >= limit) continue;
                owner.PropRepeatUsed++;
                extra += b.GetProperty("param")[0].GetInt32();
            }
        return extra;
    }

    // ---------------- props ----------------

    private void PropEffect(Session s, int iid, JsonElement cfg, int toIndex)
    {
        Piece? target = null;
        var rel = I(cfg, "relevant_type");
        if (rel is 6 or 15)
        {
            target = s.Board.FirstOrDefault(p => p.Index == toIndex) ?? throw new ModeError("Choose a chess on the board");
            Require(!(rel == 15 && Chess(target.ChessId) is { } tc && I(tc, "type") is 1 or 3), "This prop cannot target the administrator");
        }
        else Require(toIndex == 0, "Untargeted prop takes no cell");
        var normals = s.Shop.Where(i => i.ShopType == 0).ToList();
        var all = s.Board.Concat(s.Bench).ToList();
        JsonElement P() => Buff(cfg.GetProperty("buff_list")[0].GetInt64())!.Value.GetProperty("param");
        switch (iid)
        {
            case 101 or 103 or 104 or 304: { var p = P(); target!.Atk += p[0][1].GetInt64(); target.Hp += p[1][1].GetInt64(); break; }
            case 203 or 402:
                {
                    Require(all.Count > 0, "No chess to use the prop on");
                    var p = P();
                    foreach (var x in all) { x.Atk += p[0][1].GetInt64(); x.Hp += p[1][1].GetInt64(); }
                    break;
                }
            case 102:
                {
                    Require(normals.Count > 0, "No chess in the shop");
                    var occupied = s.Shop.Where(i => i.ShopType == 2).Select(i => i.Index).ToHashSet();
                    var slot = new[] { 1, 2 }.FirstOrDefault(i => !occupied.Contains(i));
                    Require(slot != 0, "Claim the reward chess first");
                    var taken = normals[Random.Shared.Next(normals.Count)];
                    taken.ShopType = 2; taken.IsLock = 0; taken.Index = slot; taken.Atk++; taken.Hp++;
                    break;
                }
            case 201:
                s.Effects["restart_money_bonus"] = s.Effects.GetValueOrDefault("restart_money_bonus") + 1;
                s.Effects["money_ceiling_bonus"] = s.Effects.GetValueOrDefault("money_ceiling_bonus") + 1;
                break;
            case 202:
                Require(normals.Count > 0, "No chess in the shop");
                foreach (var x in normals) { x.Atk++; x.Hp++; }
                break;
            case 204:
                {
                    var level = Math.Min(4, s.ShopLevel + 1);
                    var pool = Cfg.GetProperty("chess").EnumerateObject().Select(x => x.Value).Where(c => I(c, "type") == 0 && I(c, "star") == level && I(c, "shop_price") > 0 && Ints(c.GetProperty("activity_id")).Contains(Activity)).ToList();
                    Require(pool.Count > 0, "Missing higher chess");
                    var generated = ChessShop(s);
                    var chosen = pool[Random.Shared.Next(pool.Count)];
                    var body = Ints(chosen.GetProperty("body"));
                    long atk = body[0], hp = body[1];
                    ApplyBonus(s, I(chosen, "id"), ref atk, ref hp);
                    generated[0].Id = I(chosen, "id"); generated[0].Atk = atk; generated[0].Hp = hp; generated[0].Exp = BaseExp(s);
                    s.Shop = s.Shop.Where(i => i.ShopType != 0).Concat(generated).ToList();
                    break;
                }
            case 205: s.Effects["next_round_gold"] = s.Effects.GetValueOrDefault("next_round_gold") + 2; break;
            case 301: s.TeamBonus = [s.TeamBonus[0] + 1, s.TeamBonus[1] + 1]; break;
            case 302:
                {
                    var th = Chess(target!.ChessId) is { } c && c.TryGetProperty("exp", out var e) ? Ints(e) : [];
                    Require(th.Count > 0 && target.Exp < th.Max(), "Chess already at max level");
                    target.Exp++;
                    break;
                }
            case 303:
                {
                    var old = Chess(target!.ChessId)!.Value;
                    var wanted = I(old, "star") + 1;
                    var pool = Cfg.GetProperty("chess").EnumerateObject().Select(x => x.Value).Where(c => I(c, "type") == 0 && I(c, "star") == wanted && I(c, "shop_price") > 0 && Ints(c.GetProperty("activity_id")).Contains(Activity)).ToList();
                    Require(pool.Count > 0, "Chess already at the highest rarity");
                    var nw = pool[Random.Shared.Next(pool.Count)];
                    var ob = Ints(old.GetProperty("body"));
                    var nb = Ints(nw.GetProperty("body"));
                    var oldBonus = s.ChessBonus.GetValueOrDefault(target.ChessId.ToString(), [0, 0]);
                    target.ChessId = I(nw, "id");
                    target.Atk = target.Atk - ob[0] - oldBonus[0] + nb[0];
                    target.Hp = target.Hp - ob[1] - oldBonus[1] + nb[1];
                    long a = target.Atk, h = target.Hp;
                    ApplyBonus(s, target.ChessId, ref a, ref h);
                    target.Atk = a; target.Hp = h;
                    break;
                }
            case 401:
                {
                    var shields = Buff(401) is { } sb && sb.TryGetProperty("repeated", out var rp) ? Ints(rp).ToHashSet() : [];
                    var active = LevelBuffIds(target!.ChessId, Level(target.ChessId, target.Exp)).Concat(target.Buffs.Select(b => b.BuffId)).ToHashSet();
                    Require(!active.Overlaps(shields), "Chess already has this shield");
                    target.Buffs.Add(new BuffRec { UniqueId = NewUid(s), BuffId = 401, SourceUid = target.UniqueId, SourceCfgId = target.ChessId });
                    break;
                }
            case 404:
                {
                    var cands = all.Where(x => x != target).ToList();
                    Require(cands.Count > 0, "No other chess to inherit");
                    var rec = cands[Random.Shared.Next(cands.Count)];
                    rec.Atk += target!.Atk; rec.Hp += target.Hp;
                    s.Board.Remove(target);
                    break;
                }
            case 405:
                {
                    var cands = s.Shop.Where(x => x.ShopType is 0 or 2 && Chess(x.Id) is { } c && x.Exp < Ints(c.GetProperty("exp")).DefaultIfEmpty(1).Max()).ToList();
                    Require(cands.Count > 0, "No upgradeable chess in the shop");
                    cands[Random.Shared.Next(cands.Count)].Exp += 5;
                    break;
                }
        }
    }

    private Session BuyProp(Session session, ShopItem item, int toIndex)
    {
        var s = JsonSerializer.Deserialize<Session>(JsonSerializer.Serialize(session))!;
        var cfg = Cfg.GetProperty("items").TryGetProperty(item.Id.ToString(), out var c) ? c : throw new ModeError("Unknown prop");
        Require(SupportedProps.Contains((int)item.Id) && I(cfg, "unlock_stage") <= s.StageId, "Prop not available");
        var price = s.FreeProp > 0 ? 0 : I(cfg, "shop_price");
        Require(s.Gold >= price, "Not enough gold");
        var extra = ConsumePropRepeats(s);
        PropEffect(s, (int)item.Id, cfg, toIndex);
        for (var i = 0; i < extra; i++)
        {
            var copy = JsonSerializer.Deserialize<Session>(JsonSerializer.Serialize(s))!;
            try { PropEffect(copy, (int)item.Id, cfg, toIndex); s = copy; } catch (ModeError) { }
        }
        Trigger(s, 4);
        s.Effects["used_count"] = s.Effects.GetValueOrDefault("used_count") + 1;
        s.Gold -= price;
        if (s.FreeProp > 0) s.FreeProp--;
        s.Shop.RemoveAll(i => i.ShopUniqueId == item.ShopUniqueId);
        return s;
    }

    // ---------------- enemies ----------------

    private long EnemyTeam(Session s, int? round = null)
    {
        if (s.StageId == Setting("auto_chess_boss_stage", 303) && s.Win >= 4) return Setting("auto_chess_boss_team_id", 130399);
        var gid = Stage(s.StageId) is { } st ? I(st, "group_id") : 0;
        var ids = Cfg.GetProperty("stage_group_map").TryGetProperty(gid.ToString(), out var m) ? Ints(m).Where(t => Cfg.GetProperty("enemy_teams").TryGetProperty(t.ToString(), out var tc) && I(tc, "round") < 99).ToList() : [];
        Require(ids.Count > 0, "Missing enemy teams");
        var rnd = round ?? s.Round;
        return ids[Math.Min(Math.Max(1, rnd), ids.Count) - 1];
    }

    internal sealed record Record(long UniqueId, long ChessId, int Index, long Atk, long Hp, int Exp, List<BuffRec> Buffs);

    private List<Record> EnemyRecords(long team)
    {
        var t = Cfg.GetProperty("enemy_teams").GetProperty(team.ToString());
        var list = new List<Record>();
        var pos = 0;
        foreach (var e in t.GetProperty("team_info").EnumerateArray())
        {
            pos++;
            long cid = e[0].GetInt64();
            var exp = e[1].GetInt32();
            var c = Chess(cid) ?? throw new ModeError("Missing enemy chess");
            var body = Ints(c.GetProperty("body"));
            long atk = body[0] + e[2].GetInt64(), hp = body[1] + e[3].GetInt64();
            var extra = new List<long>();
            foreach (var att in e[4].EnumerateArray())
            {
                if (att.ValueKind != JsonValueKind.Array || att.GetArrayLength() != 2) continue;
                if (att[0].GetInt64() == 1) { var pp = Ints(att[1]); if (pp.Count >= 2) { atk += pp[0]; hp += pp[1]; } }
                else extra.Add(att[0].GetInt64());
            }
            var uid = 200000 + pos;
            var buffs = LevelBuffIds(cid, Level(cid, exp)).Concat(extra).Select((b, i) => new BuffRec { UniqueId = uid * 100L + i + 1, BuffId = b, OwnerType = 1, SourceUid = uid, SourceCfgId = cid }).ToList();
            list.Add(new Record(uid, cid, pos, Math.Max(0, atk), Math.Max(1, hp), exp, buffs));
        }
        return list;
    }

    private Dictionary<string, object?> RecordWire(Record r) => new()
    {
        ["unique_id"] = (uint)r.UniqueId, ["chess_id"] = (uint)r.ChessId, ["index"] = (uint)r.Index,
        ["chess_attr_list"] = new[] { Kv(1, r.Atk), Kv(2, r.Hp), Kv(3, r.Exp) }, ["buff_list"] = r.Buffs.Select(b => (object)BuffWire(b)).ToArray()
    };

    // ---------------- operations ----------------

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest req, uint cmd, CancellationToken ct)
    {
        var uid = req.AccountId;
        var srv = (ushort)(req.ServerIndex + 1);
        var prog = await LoadAsync(uid, ct);
        var s = prog.Session;
        var frames = new List<GameResponse>();
        void RequireState(params int[] states) => Require(states.Contains(s.State), "Invalid autochess state");
        try
        {
            Require(HandlerHelpers.ReadUInt(req.Body, "game_type") == 0, "Only PVE autochess is open");
            switch (cmd)
            {
                case 89116:
                    {
                        var stage = (long)HandlerHelpers.ReadUInt(req.Body, "other_param");
                        if (stage == 0) stage = 101;
                        var st = Stage(stage) ?? throw new ModeError("Unknown autochess stage");
                        Require(I(st, "area") <= 3, "Later areas are closed");
                        var prev = I(st, "prev_stage");
                        Require(prev == 0 || prog.Stages.ContainsKey(prev), "Previous autochess stage not cleared");
                        if (s.State == TotalLose && s.StageId == stage && s.Snapshot is { } snap && s.RestartTimes < Setting("auto_chess_pve_restart_times", 999))
                        {
                            var restored = JsonSerializer.Deserialize<Session>(snap)!;
                            restored.RestartTimes = s.RestartTimes + 1;
                            restored.Gold += Setting("auto_chess_pve_restart_add_coin", 10);
                            restored.State = Prepare;
                            restored.NextUid = Math.Max(restored.NextUid, s.NextUid);
                            prog.Session = s = restored;
                        }
                        else
                        {
                            Require(s.State is None or TotalWin or TotalLose, "A game is already running");
                            var next = s.NextUid;
                            prog.Session = s = new Session { State = Prepare, StageId = stage, Hp = I(st, "defeat_num", 3), NextUid = next };
                            Restock(s);
                        }
                        frames.Add(F(srv, 89125, StateWire(s)));
                        frames.Add(F(srv, 89101, SessionWire(s)));
                        break;
                    }
                case 89118:
                    RequireState(Prepare);
                    if (s.FreeRefresh > 0) s.FreeRefresh--;
                    else { Require(s.Gold >= 1, "Not enough gold"); s.Gold--; }
                    Restock(s);
                    frames.Add(F(srv, 89101, SessionWire(s)));
                    break;
                case 89102:
                    {
                        RequireState(Prepare);
                        var item = s.Shop.FirstOrDefault(i => i.ShopUniqueId == HandlerHelpers.ReadUInt(req.Body, "shop_unique_id")) ?? throw new ModeError("Goods not found");
                        var shopType = (int)HandlerHelpers.ReadUInt(req.Body, "shop_type");
                        var to = Convert.ToInt32(req.Body.GetValueOrDefault("to_index") ?? 0);
                        Require(item.ShopType == shopType, "Goods type mismatch");
                        if (shopType == 1) { prog.Session = s = BuyProp(s, item, to); frames.Add(F(srv, 89101, SessionWire(s))); break; }
                        var c = Chess(item.Id) ?? throw new ModeError("Unknown chess");
                        var price = shopType == 2 ? 0 : I(c, "shop_price");
                        Require(shopType == 2 || price > 0, "Chess not for sale");
                        Require(s.Gold >= price, "Not enough gold");
                        Require(to is >= 1 and <= 5, "Invalid cell");
                        var at = s.Board.FirstOrDefault(p => p.Index == to);
                        Piece piece;
                        if (at is not null)
                        {
                            Require(at.ChessId == item.Id, "Cell occupied by another chess");
                            var limits = Ints(c.GetProperty("exp"));
                            Require(limits.Count == 0 || at.Exp < limits.Max(), "Chess already at max level");
                            at.Exp += item.Exp; at.Atk += item.Atk; at.Hp += item.Hp;
                            piece = at;
                        }
                        else s.Board.Add(piece = new Piece { UniqueId = NewUid(s), ChessId = item.Id, Index = to, Exp = item.Exp, Atk = item.Atk, Hp = item.Hp });
                        s.Gold -= price;
                        s.Shop.Remove(item);
                        Trigger(s, 3, piece);
                        frames.Add(F(srv, 89101, SessionWire(s)));
                        break;
                    }
                case 89122:
                    {
                        RequireState(Prepare);
                        var id = (long)HandlerHelpers.ReadUInt(req.Body, "uid");
                        var target = s.Board.Concat(s.Bench).FirstOrDefault(p => p.UniqueId == id) ?? throw new ModeError("Chess not found");
                        s.Board.Remove(target); s.Bench.Remove(target);
                        var prices = Chess(target.ChessId) is { } c && c.TryGetProperty("sell_price", out var sp) ? Ints(sp) : [];
                        var lvl = Level(target.ChessId, target.Exp);
                        Require(prices.Count >= lvl, "Missing sell price");
                        s.Gold += prices[lvl - 1];
                        Trigger(s, 5, target);
                        frames.Add(F(srv, 89111, new() { ["game_type"] = 0u, ["attr_list"] = new[] { Kv(1, s.Gold) } }));
                        frames.Add(F(srv, 89101, SessionWire(s)));
                        break;
                    }
                case 89104:
                    {
                        RequireState(Prepare);
                        var moves = new Dictionary<long, int>();
                        if (req.Body.GetValueOrDefault("chess_list") is System.Collections.IEnumerable list)
                            foreach (var e in list)
                                if (e is IReadOnlyDictionary<string, object?> d) moves[HandlerHelpers.ReadUInt(d, "key")] = Convert.ToInt32(d.GetValueOrDefault("value") ?? 0);
                        var all = s.Board.Concat(s.Bench).ToList();
                        Require(moves.Keys.All(k => all.Any(p => p.UniqueId == k)) && moves.Values.All(v => v is >= 1 and <= 5), "Invalid formation");
                        var pos = all.ToDictionary(p => p.UniqueId, p => p.Index);
                        foreach (var (id, np) in moves)
                        {
                            var old = pos[id];
                            if (np != old && all.FirstOrDefault(p => pos[p.UniqueId] == np && !moves.ContainsKey(p.UniqueId)) is { } occ) pos[occ.UniqueId] = old;
                            pos[id] = np;
                        }
                        var positive = pos.Values.Where(v => v > 0).ToList();
                        Require(positive.Count == positive.Distinct().Count(), "Two chess on one cell");
                        foreach (var p in all) p.Index = pos[p.UniqueId];
                        s.Board = all.Where(p => p.Index > 0).OrderBy(p => p.Index).ToList();
                        s.Bench = all.Where(p => p.Index == 0).OrderBy(p => p.UniqueId).ToList();
                        await SaveAsync(uid, prog, ct);
                        return [HandlerHelpers.Reply(codec, req, 89105, new Dictionary<string, object?> { ["result"] = 0u, ["chess_list"] = s.Board.Select(p => Kv((int)p.UniqueId, p.Index)).ToArray() })];
                    }
                case 89120:
                    {
                        RequireState(Prepare);
                        var lockType = HandlerHelpers.ReadUInt(req.Body, "type") != 0 ? 1 : 0;
                        var ids = new HashSet<long>();
                        if (req.Body.GetValueOrDefault("info_list") is System.Collections.IEnumerable list)
                            foreach (var e in list) if (e is IReadOnlyDictionary<string, object?> d) ids.UnionWith(HandlerHelpers.ReadIdList(d, "uid_list"));
                        Require(ids.All(i => s.Shop.Any(x => x.ShopUniqueId == i)), "Unknown goods");
                        foreach (var i in s.Shop.Where(x => ids.Contains(x.ShopUniqueId))) i.IsLock = lockType;
                        break;
                    }
                case 89130:
                    {
                        RequireState(Prepare);
                        var all = s.Board.Concat(s.Bench).ToList();
                        var src = all.FirstOrDefault(p => p.UniqueId == HandlerHelpers.ReadUInt(req.Body, "source_uid"));
                        var loss = all.FirstOrDefault(p => p.UniqueId == HandlerHelpers.ReadUInt(req.Body, "loss_uid"));
                        Require(src is not null && loss is not null && src != loss && src.ChessId == loss.ChessId, "Invalid merge");
                        var limits = Ints(Chess(src!.ChessId)!.Value.GetProperty("exp"));
                        Require(limits.Count == 0 || src.Exp < limits.Max(), "Chess already at max level");
                        src.Exp += loss!.Exp; src.Atk += loss.Atk; src.Hp += loss.Hp;
                        s.Board.Remove(loss); s.Bench.Remove(loss);
                        frames.Add(F(srv, 89101, SessionWire(s)));
                        break;
                    }
                case 89144:
                    RequireState(Prepare);
                    Require(s.Board.Count <= 5 && s.Bench.Count == 0, "Move bench chess back to the board or sell them");
                    s.Snapshot = JsonSerializer.Serialize(s);
                    s.State = PrepareEnd;
                    Trigger(s, 10);
                    frames.Add(F(srv, 89125, StateWire(s)));
                    break;
                case 89108:
                    await LaunchAsync(prog, uid, req, srv, frames, ct);
                    await SaveAsync(uid, prog, ct);
                    return frames;
                case 89126:
                    {
                        RequireState(RoundSettle, TotalWin, TotalLose);
                        var pend = s.Pending;
                        Require(pend is { Consumed: false } && pend.Round == s.Round && pend.StageId == s.StageId, "No pending round report");
                        pend!.Consumed = true;
                        var stateNow = pend.Result is 0 or 3 ? RoundSettle : s.State;
                        if (pend.Result == 1) s.Win++;
                        else if (pend.Result == 2) { s.Defeat++; s.Hp = Math.Max(0, s.Hp - 1); }
                        if (stateNow == TotalWin)
                        {
                            s.State = None;
                            var first = !prog.Stages.ContainsKey(s.StageId);
                            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            if (first) prog.Stages[s.StageId] = now;
                            foreach (var m in Cfg.GetProperty("medals").EnumerateObject())
                                if (m.Value.TryGetProperty("unlock_stage", out var us) && Ints(us) is { Count: > 0 } need && need.All(prog.Stages.ContainsKey)) prog.Medals.TryAdd(long.Parse(m.Name), now);
                            if (first && Stage(s.StageId) is { } st && st.TryGetProperty("drop_item", out var drop) && Ints(drop) is [var di, var dn])
                            {
                                var granted = await accounts.GrantItemsAsync(uid, [(di, dn)], ct);
                                frames.AddRange(await InventorySync.FramesAsync(accounts, codec, req, granted.Select(x => x.Item1), ct));
                            }
                            frames.Add(F(srv, 89201, StagesWire(prog)));
                            frames.Add(F(srv, 89125, StateWire(s)));
                        }
                        else if (stateNow == TotalLose)
                        {
                            s.State = None;
                            frames.Add(F(srv, 89125, StateWire(s)));
                        }
                        else
                        {
                            s.Round++;
                            s.State = Prepare;
                            s.ShopLevel = Math.Min(4, s.Round);
                            s.Gold = 10 + s.Effects.GetValueOrDefault("restart_money_bonus") + s.Effects.GetValueOrDefault("next_round_gold");
                            s.Effects.Remove("next_round_gold");
                            s.FreeRefresh = 0;
                            foreach (var p in s.Board.Concat(s.Bench)) p.PropRepeatUsed = 0;
                            Trigger(s, 9);
                            Restock(s);
                            frames.Add(F(srv, 89125, StateWire(s)));
                            frames.Add(HandlerHelpers.ResultOnly(codec, req, 89127));
                            frames.Add(F(srv, 89101, SessionWire(s)));
                            await SaveAsync(uid, prog, ct);
                            return frames;
                        }
                        break;
                    }
                case 89128:
                    RequireState(Prepare, PrepareEnd, RoundSettle, TotalWin, TotalLose);
                    prog.Session = s = new Session { NextUid = s.NextUid };
                    frames.Add(F(srv, 89125, StateWire(s)));
                    break;
                case 89138:
                    {
                        var gid = (long)HandlerHelpers.ReadUInt(req.Body, "group_id", 101);
                        var rnd = (int)HandlerHelpers.ReadUInt(req.Body, "round", 1);
                        var view = s.State == None ? new Session { StageId = gid, Round = rnd } : s;
                        var st = Stage(view.StageId) ?? throw new ModeError("Unknown stage");
                        var recs = EnemyRecords(EnemyTeam(view, rnd));
                        return [HandlerHelpers.Reply(codec, req, 89139, new Dictionary<string, object?>
                        {
                            ["result"] = 0u,
                            ["enemy_auto_chess_board"] = new Dictionary<string, object?>
                            {
                                ["base_info_list"] = new[] { Kv(1, I(st, "defeat_num", 3)), Kv(4, rnd), Kv(5, view.StageId) }, ["chess_list"] = recs.Select(r => (object)RecordWire(r)).ToArray()
                            }
                        })];
                    }
                case 89142:
                    {
                        Require(s.ReplayInfo is not null && s.ReplayActions is not null, "No saved replay");
                        var info = ToObject(JsonDocument.Parse(s.ReplayInfo!).RootElement);
                        var actions = ToObject(JsonDocument.Parse(s.ReplayActions!).RootElement);
                        var infoDict = (Dictionary<string, object?>)info!;
                        return
                        [
                            HandlerHelpers.Reply(codec, req, 89143, new Dictionary<string, object?> { ["result"] = 0u, ["replay_info"] = infoDict }),
                            F(srv, 89113, new() { ["game_type"] = 0u, ["battle_uid"] = infoDict["battle_uid"], ["battle_info"] = new Dictionary<string, object?> { ["group_id"] = 1u, ["action_list"] = actions } })
                        ];
                    }
                case 89146:
                    RequireState(PrepareEnd, RoundSettle, TotalWin, TotalLose);
                    return [HandlerHelpers.Reply(codec, req, 89147, new Dictionary<string, object?>
                    {
                        ["result"] = 0u,
                        ["export_info"] = new Dictionary<string, object?>
                        {
                            ["chess_list"] = s.Board.OrderBy(p => p.Index).Select(p => (object)new Dictionary<string, object?>
                            {
                                ["chess_id"] = (uint)p.ChessId, ["index"] = (uint)p.Index, ["chess_kv"] = new[] { Kv(1, p.Atk), Kv(2, p.Hp), Kv(3, p.Exp) },
                                ["buffs"] = p.Buffs.Select(b => (object)new Dictionary<string, object?> { ["buff_id"] = (uint)b.BuffId }).ToArray()
                            }).ToArray(),
                            ["user_buff"] = Array.Empty<object>()
                        }
                    })];
                case 89148: break;
                case 89206:
                    return [HandlerHelpers.Reply(codec, req, 89207, BattleRecords())];
                default: throw new ModeError("Not available in PVE");
            }
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(codec, req, cmd + 1, 2)]; }
        await SaveAsync(uid, prog, ct);
        var ack = HandlerHelpers.ResultOnly(codec, req, cmd + 1);
        if (cmd is 89116 or 89118 or 89128 or 89126) frames.Add(ack);
        else frames.Insert(cmd == 89122 ? Math.Min(1, frames.Count) : 0, ack);
        return frames;
    }

    private static object? ToObject(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => ToObject(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(ToObject).ToList(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private async Task LaunchAsync(Progress prog, long uid, GameRequest req, ushort srv, List<GameResponse> frames, CancellationToken ct)
    {
        var s = prog.Session;
        Require(s.State == PrepareEnd, "Invalid autochess state");
        var st = Stage(s.StageId) ?? throw new ModeError("Unknown stage");
        var defeat = I(st, "defeat_num", 3);
        var players = s.Board.OrderBy(p => p.Index).Select(p =>
            new Record(p.UniqueId, p.ChessId, p.Index, p.Atk + s.TeamBonus[0], p.Hp + s.TeamBonus[1], p.Exp, PieceBuffs(p))).ToList();
        var enemies = EnemyRecords(EnemyTeam(s));
        var playerWire = players.Select(r => (object)RecordWire(r)).ToArray();
        var enemyWire = enemies.Select(r => (object)RecordWire(r)).ToArray();
        var (result, actions) = new AutoChessCombat(this, s, players, enemies).Run();
        var battleUid = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 12) | (uid & 0xFFF);
        var winNum = Math.Max(1, I(st, "win_num", 1));
        s.State = result == 1 ? (s.Win + 1 >= winNum ? TotalWin : RoundSettle) : result == 2 ? (s.Hp - 1 <= 0 ? TotalLose : RoundSettle) : RoundSettle;
        s.Pending = new Pending { StageId = s.StageId, Round = s.Round, BattleUid = battleUid, Result = result, State = s.State };
        var info = new Dictionary<string, object?>
        {
            ["game_type"] = 0u, ["result"] = (uint)result, ["battle_uid"] = battleUid, ["user_id1"] = uid,
            ["auto_chessboard_info1"] = new Dictionary<string, object?> { ["base_info_list"] = new[] { Kv(1, s.Hp), Kv(4, s.Round), Kv(5, s.StageId), Kv(2, s.Win) }, ["chess_list"] = playerWire },
            ["user_id2"] = 999999L,
            ["auto_chessboard_info2"] = new Dictionary<string, object?> { ["base_info_list"] = new[] { Kv(1, defeat), Kv(4, s.Round), Kv(5, s.StageId), Kv(2, 0) }, ["chess_list"] = enemyWire },
            ["max_group_id"] = 1u, ["point"] = 100u, ["point_detail"] = Array.Empty<object>(),
            ["enemy_name"] = st.TryGetProperty("enemy_name", out var en) ? en.GetString() : "",
            ["enemy_icon"] = uint.TryParse(st.TryGetProperty("enemy_icon", out var ei) ? ei.ToString() : "", out var icon) ? icon : 1028u
        };
        s.ReplayInfo = JsonSerializer.Serialize(info);
        s.ReplayActions = JsonSerializer.Serialize(actions);
        frames.Add(HandlerHelpers.Reply(codec, req, 89109, new Dictionary<string, object?> { ["result"] = 0u, ["replay_info"] = info }));
        frames.Add(F(srv, 89125, StateWire(s)));
        frames.Add(F(srv, 89113, new() { ["game_type"] = 0u, ["battle_uid"] = battleUid, ["battle_info"] = new Dictionary<string, object?> { ["group_id"] = 1u, ["action_list"] = actions.ToArray() } }));
        await Task.CompletedTask;
    }

    internal object ChessRecordWire(long uid, long cid, int index, long atk, long hp, int exp, IEnumerable<BuffRec> buffs) => new Dictionary<string, object?>
    {
        ["unique_id"] = (uint)uid, ["chess_id"] = (uint)cid, ["index"] = (uint)index, ["chess_attr_list"] = new[] { Kv(1, atk), Kv(2, hp), Kv(3, exp) },
        ["buff_list"] = buffs.Select(b => (object)BuffWire(b)).ToArray()
    };
}

/// <summary>Bounded PVE battle interpreter (autochess_combat.py): buff moments/selectors/actions produce the action stream.</summary>
internal sealed class AutoChessCombat
{
    private const int MaxActions = 4096, MaxRounds = 256, MaxEvents = 4096, Cells = 7;

    private sealed class FBuff { public AutoChess.BuffRec Rec = new(); public int Used; public int Triggers; }
    private sealed class Fighter { public long Uid; public long Cid; public int Index; public int Side; public long Atk; public long Hp; public long MaxHp; public List<FBuff> Buffs = []; public AutoChess.Record Initial = null!; }

    private readonly AutoChess _svc;
    private readonly AutoChess.Session _s;
    private readonly List<Dictionary<string, object?>> _actions = [];
    private readonly List<(int Moment, Fighter? Actor, Fighter? Victim)> _events = [];
    private int _eventCount;
    private readonly int[] _dead = [0, 0], _summons = [0, 0];
    private readonly long[] _damageBonus = [0, 0];
    private readonly Dictionary<long, long>[] _globalGrowth = [new(), new()];
    private readonly Dictionary<long, long[]> _globalCidGrowth0 = [];
    private readonly Dictionary<long, long[]> _growth = [];
    private readonly HashSet<long> _removed = [];
    private readonly HashSet<long> _used;
    private long _nextBuff;
    private readonly List<Fighter>[] _teams;
    private readonly Dictionary<long, AutoChess.Piece> _original;
    private readonly Dictionary<long, long[]>[] _cidGrowth = [new(), new()];

    public AutoChessCombat(AutoChess svc, AutoChess.Session s, List<AutoChess.Record> players, List<AutoChess.Record> enemies)
    {
        _svc = svc; _s = s;
        _used = players.Concat(enemies).Select(p => p.UniqueId).ToHashSet();
        _nextBuff = Math.Max(100_000_000, players.Concat(enemies).SelectMany(p => p.Buffs).Select(b => b.UniqueId).DefaultIfEmpty(0).Max());
        _teams = [players.OrderBy(p => p.Index).Select(p => Make(p, 0)).ToList(), enemies.OrderBy(p => p.Index).Select(p => Make(p, 1)).ToList()];
        _original = s.Board.ToDictionary(p => p.UniqueId);
        for (var side = 0; side < 2; side++) _damageBonus[side] = _teams[side].Sum(DamageBonusOf);
    }

    private long DamageBonusOf(Fighter f) => f.Buffs.Select(b => Cfg(b)).Where(c => c is { } cc && AutoChess.I(cc, "moment") == 1 && AutoChess.I(cc, "action_type") == 6
        && cc.GetProperty("param") is { ValueKind: JsonValueKind.Array } p && p.GetArrayLength() > 2 && p[0].GetInt32() == 1 && p[1].GetInt32() == 4).Sum(c => c!.Value.GetProperty("param")[2].GetInt64());

    private static void Fail(string m) => throw new ModeError(m);

    private Fighter Make(AutoChess.Record r, int side)
    {
        var buffs = r.Buffs.Count > 0 ? r.Buffs : _svc.LevelBuffIds(r.ChessId, _svc.Level(r.ChessId, r.Exp)).Select((b, i) => new AutoChess.BuffRec { BuffId = b, UniqueId = r.UniqueId * 100 + i + 1 }).ToList();
        return new Fighter
        {
            Uid = r.UniqueId, Cid = r.ChessId, Index = r.Index, Side = side, Atk = Math.Max(0, r.Atk), Hp = r.Hp, MaxHp = r.Hp, Initial = r,
            Buffs = buffs.Select(b => new FBuff { Rec = new AutoChess.BuffRec { UniqueId = b.UniqueId != 0 ? b.UniqueId : ++_nextBuff, BuffId = b.BuffId, OwnerType = b.OwnerType, SourceType = b.SourceType, SourceUid = b.SourceUid, SourceCfgId = b.SourceCfgId } }).ToList()
        };
    }

    private JsonElement? Cfg(FBuff b) => _svc.Buff(b.Rec.BuffId);
    private static int C(JsonElement? c, string k) => c is { } v ? AutoChess.I(v, k) : 0;
    private List<Fighter> Live(int side) => _teams[side].Where(f => f.Hp > 0).ToList();
    private bool IsShield(FBuff b) => C(Cfg(b), "moment") == 1 && C(Cfg(b), "action_type") == 7;

    private void Action(int type, string field, Dictionary<string, object?> data)
    {
        if (_actions.Count >= MaxActions) Fail("Autochess action limit reached");
        _actions.Add(new() { ["action_id"] = (uint)(_actions.Count + 1), ["action_type"] = (uint)type, [field] = data });
    }

    private static List<T> Sample<T>(List<T> l, int n) => l.OrderBy(_ => Random.Shared.Next()).Take(Math.Min(n, l.Count)).ToList();

    private List<Fighter> Targets(int kind, Fighter owner, Fighter? actor)
    {
        var allies = Live(owner.Side);
        var enemies = Live(1 - owner.Side);
        var others = allies.Where(f => f != owner).ToList();
        var rear = allies.Where(f => f.Index > owner.Index).ToList();
        var front = allies.Where(f => f.Index < owner.Index).ToList();
        return kind switch
        {
            111 => owner.Hp > 0 ? [owner] : [],
            112 => allies,
            113 => others,
            114 or 115 or 116 => Sample(allies, kind - 113),
            117 or 118 or 119 => Sample(others, kind - 116),
            120 => allies.Where(f => Math.Abs(f.Index - owner.Index) == 1).ToList(),
            121 or 122 or 123 => front.TakeLast(kind - 120).ToList(),
            124 => allies.Take(1).ToList(),
            127 or 128 or 129 => rear.Take(kind - 126).ToList(),
            214 or 215 or 216 => Sample(enemies, kind - 213),
            212 => enemies,
            5 => others.Concat(enemies).ToList(),
            13 => actor is { Hp: > 0 } ? [actor] : [],
            151 => allies.Where(f => f.Buffs.Any(IsShield)).ToList(),
            153 or 154 or 155 => Sample(allies.Where(f => f.Buffs.Any(IsShield)).ToList(), kind - 152),
            156 or 157 or 158 => Sample(allies.Where(f => !f.Buffs.Any(IsShield)).ToList(), kind - 155),
            _ => []
        };
    }

    private static bool Matches(int e, Fighter owner, Fighter? actor) => e is 0 or 1 || actor is not null && e switch
    {
        111 => owner == actor,
        112 => owner.Side == actor.Side,
        113 => owner.Side == actor.Side && owner != actor,
        212 => owner.Side != actor.Side,
        121 or 122 or 123 => owner.Side == actor.Side && owner.Index - actor.Index > 0 && owner.Index - actor.Index <= e - 120,
        _ => false
    };

    private void Emit(int moment, Fighter? actor, Fighter? victim = null) => _events.Add((moment, actor, victim));

    private void Flush()
    {
        while (_events.Count > 0)
        {
            if (++_eventCount > MaxEvents) Fail("Autochess event limit reached");
            var (moment, actor, victim) = _events[0];
            _events.RemoveAt(0);
            var owners = _teams.SelectMany(t => t).Where(f => f.Hp > 0).ToList();
            if (moment == 17 && actor is not null && !owners.Contains(actor)) owners.Add(actor);
            foreach (var owner in owners)
                foreach (var buff in owner.Buffs.ToList())
                {
                    var cfg = Cfg(buff);
                    if (cfg is null || C(cfg, "moment") != moment || !Matches(C(cfg, "enum"), owner, actor)) continue;
                    int limit = C(cfg, "effective_time"), gap = C(cfg, "gap");
                    if (limit > 0 && buff.Used >= limit) continue;
                    buff.Triggers++;
                    if (gap > 0 && buff.Triggers % gap != 0) continue;
                    buff.Used++;
                    var repeats = 1;
                    if (moment == 17 && owner == actor && C(cfg, "enum") == 111)
                        repeats += owners.Sum(f => f.Buffs.Where(b => C(Cfg(b), "moment") == 17 && C(Cfg(b), "action_type") == 14 && Matches(C(Cfg(b), "enum"), f, actor))
                            .Sum(b => Cfg(b)!.Value.GetProperty("param")[0].GetInt32()));
                    for (var i = 0; i < repeats; i++)
                    {
                        Effect(owner, buff, cfg.Value, actor, victim);
                        if (moment == 17 && owner == actor && C(cfg, "enum") == 111) Emit(21, owner);
                    }
                }
        }
    }

    private static object Kv(long k, long v) => new Dictionary<string, object?> { ["key"] = (uint)k, ["value"] = (int)v };

    private void Attr(Fighter t, long da, long dh, FBuff buff, bool permanent = false)
    {
        if ((da == 0 && dh == 0) || t.Hp <= 0) return;
        t.Atk += da; t.MaxHp += dh; t.Hp += dh;
        Action(3, "action_effect_info", new() { ["effect_enum"] = 1u, ["buff_uid"] = (uint)buff.Rec.UniqueId, ["target_list"] = new object[] { new Dictionary<string, object?> { ["id"] = (uint)t.Uid, ["temp_id"] = (uint)t.Cid, ["update_list"] = new[] { Kv(1, da), Kv(2, dh) } } } });
        if (permanent && t.Side == 0 && _original.ContainsKey(t.Uid))
        {
            var g = _growth.TryGetValue(t.Uid, out var x) ? x : _growth[t.Uid] = [0, 0];
            g[0] += da; g[1] += dh;
        }
    }

    private void Grant(Fighter t, long bid, Fighter src, FBuff buff)
    {
        var cfg = _svc.Buff(bid);
        var at = C(cfg, "action_type");
        if (at is 7 or 33 && t.Buffs.Any(x => C(Cfg(x), "action_type") == at)) return;
        var rec = new AutoChess.BuffRec { BuffId = bid, UniqueId = ++_nextBuff, OwnerType = t.Side, SourceUid = src.Uid, SourceCfgId = src.Cid };
        t.Buffs.Add(new FBuff { Rec = rec });
        Action(3, "action_effect_info", new() { ["effect_enum"] = 4u, ["buff_uid"] = (uint)buff.Rec.UniqueId, ["add_buff_info"] = new object[] { new Dictionary<string, object?> { ["target_type"] = 0u, ["target_uid"] = (uint)t.Uid, ["buff_info"] = AutoChess.BuffWire(rec) } } });
    }

    private long Hit(Fighter t, long amount, Fighter src)
    {
        amount = Math.Max(0, amount);
        if (t.Hp <= 0 || amount == 0) return 0;
        if (t.Buffs.FirstOrDefault(IsShield) is { } shield)
        {
            t.Buffs.Remove(shield);
            Action(7, "remove_effect_info", new() { ["buff_uid"] = (uint)shield.Rec.UniqueId, ["target_list"] = Array.Empty<object>(), ["attr_list"] = Array.Empty<object>() });
            Emit(20, t);
            return 0;
        }
        amount += _damageBonus[src.Side];
        if (src.Buffs.FirstOrDefault(b => C(Cfg(b), "action_type") == 33) is { } deadly)
        {
            src.Buffs.Remove(deadly);
            Action(7, "remove_effect_info", new() { ["buff_uid"] = (uint)deadly.Rec.UniqueId, ["target_list"] = Array.Empty<object>(), ["attr_list"] = Array.Empty<object>() });
            amount = Math.Max(amount, t.Hp);
        }
        t.Hp -= amount;
        Emit(16, t);
        if (t.Side != src.Side)
        {
            Emit(23, src, t);
            if (t.Hp <= 0) Emit(22, src, t);
        }
        return amount;
    }

    private void Damage(Fighter t, long amount, Fighter owner, FBuff buff)
    {
        var actual = Hit(t, amount, owner);
        Action(3, "action_effect_info", new() { ["effect_enum"] = 2u, ["buff_uid"] = (uint)buff.Rec.UniqueId, ["target_list"] = new object[] { new Dictionary<string, object?> { ["id"] = (uint)t.Uid, ["temp_id"] = (uint)t.Cid, ["update_list"] = new[] { Kv(2, -actual) } } } });
    }

    private void Renumber(int side)
    {
        var changes = new List<object>();
        for (var i = 0; i < _teams[side].Count; i++)
            if (_teams[side][i].Index != i + 1) { _teams[side][i].Index = i + 1; changes.Add(Kv(_teams[side][i].Uid, i + 1)); }
        if (changes.Count > 0) Action(1, "action_move_info", new() { ["action_list"] = changes.ToArray() });
    }

    private int Spawn(List<long> cids, Fighter owner, FBuff buff, bool front = false, long[]? bonus = null, int exp = 1, List<AutoChess.Record>? templates = null)
    {
        bonus ??= [0, 0];
        var team = _teams[owner.Side];
        cids = cids.Take(Math.Max(0, Cells - team.Count)).ToList();
        if (cids.Count == 0) return 0;
        var at = front ? 0 : team.FindIndex(f => f.Index > owner.Index) is var ix and >= 0 ? ix : team.Count;
        var shifts = new List<object>();
        foreach (var f in team.Skip(at)) { f.Index += cids.Count; shifts.Add(Kv(f.Uid, f.Index)); }
        if (shifts.Count > 0) Action(1, "action_move_info", new() { ["action_list"] = shifts.ToArray() });
        var fresh = new List<Fighter>();
        var records = new List<object>();
        for (var i = 0; i < cids.Count; i++)
        {
            var cfg = _svc.Chess(cids[i]) ?? throw new ModeError("Missing summon chess");
            long uid;
            do uid = AutoChess.NewUid(_s); while (!_used.Add(uid));
            var body = AutoChess.Ints(cfg.GetProperty("body"));
            if (body.Count < 2) body = [2, 2];
            var gg = _cidGrowth[owner.Side].GetValueOrDefault(cids[i], [0, 0]);
            var saved = owner.Side == 0 ? _s.ChessBonus.GetValueOrDefault(cids[i].ToString(), [0, 0]) : [0, 0];
            AutoChess.Record rec = templates is not null
                ? templates[i] with { UniqueId = uid, Index = at + i + 1, Buffs = templates[i].Buffs.Select(b => new AutoChess.BuffRec { BuffId = b.BuffId, UniqueId = ++_nextBuff, OwnerType = b.OwnerType, SourceUid = b.SourceUid, SourceCfgId = b.SourceCfgId }).ToList() }
                : new AutoChess.Record(uid, cids[i], at + i + 1, body[0] + bonus[0] + gg[0] + saved[0], body[1] + bonus[1] + gg[1] + saved[1], exp, []);
            var f = Make(rec, owner.Side);
            _damageBonus[owner.Side] += DamageBonusOf(f);
            fresh.Add(f);
            records.Add(_svc.ChessRecordWire(f.Uid, f.Cid, f.Index, f.Atk, f.Hp, rec.Exp, f.Buffs.Select(b => b.Rec)));
        }
        team.InsertRange(at, fresh);
        Action(3, "action_effect_info", new() { ["effect_enum"] = 3u, ["buff_uid"] = (uint)buff.Rec.UniqueId, ["call_info"] = new Dictionary<string, object?> { ["user_id"] = (ulong)owner.Side, ["chess_list"] = records.ToArray() } });
        foreach (var f in fresh) { Emit(18, f); Emit(19, f); }
        Renumber(owner.Side);
        return fresh.Count;
    }

    private void Effect(Fighter owner, FBuff buff, JsonElement cfg, Fighter? actor, Fighter? victim)
    {
        var action = AutoChess.I(cfg, "action_type");
        var p = cfg.TryGetProperty("param", out var pp) ? pp : default;
        int N(int i) => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > i && p[i].ValueKind == JsonValueKind.Number ? p[i].GetInt32() : 0;
        long[] Pair(int i) => AutoChess.Ints(p[i]) is [var a, var b, ..] ? [a, b] : [0, 0];
        switch (action)
        {
            case 5: foreach (var t in Targets(N(0), owner, actor)) { var d = Pair(2); Attr(t, d[0], d[1], buff, N(1) == 2); } break;
            case 2: foreach (var t in Targets(N(0), owner, actor)) Damage(t, N(1), owner, buff); break;
            case 4:
                if (N(0) == 2)
                {
                    foreach (var bid in AutoChess.Ints(p[1]))
                        if (_svc.Buff(bid) is { } child && AutoChess.I(child, "moment") == 1) Effect(owner, buff, child, actor, victim);
                        else Fail("Unsupported player effect");
                }
                else foreach (var t in Targets(N(0), owner, actor)) foreach (var bid in AutoChess.Ints(p[1])) Grant(t, bid, owner, buff);
                break;
            case 3: Spawn(AutoChess.Ints(p[1]), owner, buff, front: N(0) == 2); break;
            case 22: foreach (var t in Targets(N(0), owner, actor)) Spawn([t.Cid], owner, buff, templates: [t.Initial]); break;
            case 23: { var x = Pair(2); Spawn([N(0)], owner, buff, bonus: [_dead[owner.Side] * x[0], _dead[owner.Side] * x[1]]); break; }
            case 25:
                {
                    var g = _cidGrowth[owner.Side].TryGetValue(N(0), out var v) ? v : _cidGrowth[owner.Side][N(0)] = [0, 0];
                    var d = Pair(2);
                    g[0] += d[0]; g[1] += d[1];
                    foreach (var t in Live(owner.Side).Where(t => t.Cid == N(0))) Attr(t, d[0], d[1], buff);
                    break;
                }
            case 28: if (actor is { Side: 0 } && _original.ContainsKey(actor.Uid)) _removed.Add(actor.Uid); break;
            case 36:
                {
                    var count = owner.Side == 0 ? _s.Effects.GetValueOrDefault("used_count") : 0;
                    var b0 = Pair(2); var b1 = Pair(3);
                    foreach (var t in Targets(N(0), owner, actor)) Attr(t, b0[0] + count * b1[0], b0[1] + count * b1[1], buff, N(1) == 2);
                    break;
                }
            case 37:
                {
                    var shared = AutoChess.I(cfg, "id") is 4131 or 4132 or 4133 or 4137 or 4138 or 4139;
                    if (shared && _summons[owner.Side] >= 8) return;
                    foreach (var e in p[1].EnumerateArray())
                    {
                        var n = Spawn([e[0].GetInt64()], owner, buff, front: N(0) == 2, exp: e[1].GetInt32());
                        if (shared) _summons[owner.Side] += n;
                    }
                    break;
                }
            case 34:
                if (victim is not null && victim.Cid != 315)
                    foreach (var t in Targets(N(1), owner, actor)) Attr(t, 0, victim.Initial.Hp, buff);
                break;
            case 8 when p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > 2 && N(1) == 17:
                foreach (var t in Targets(N(0), owner, actor))
                    foreach (var b in t.Buffs.ToList())
                        if (Cfg(b) is { } c && AutoChess.I(c, "moment") == 17 && AutoChess.I(c, "enum") == 111) { Effect(t, b, c, t, null); Emit(21, t); }
                break;
            case 8 or 14: break;
            default: Fail($"Unsupported autochess effect {AutoChess.I(cfg, "id")}/{action}"); break;
        }
    }

    private void Deaths()
    {
        while (true)
        {
            var dead = _teams.SelectMany(t => t).Where(f => f.Hp <= 0).ToList();
            if (dead.Count == 0) return;
            foreach (var f in dead) { _teams[f.Side].Remove(f); _dead[f.Side]++; }
            Action(5, "action_dead_info", new() { ["uid_list"] = dead.Select(f => (uint)f.Uid).ToArray() });
            foreach (var f in dead) Emit(17, f);
            Flush();
            Renumber(0); Renumber(1);
        }
    }

    public (int Result, List<Dictionary<string, object?>> Actions) Run()
    {
        if (_teams[0].Count == 0 && _teams[1].Count == 0) return (0, []);
        if (_teams[0].Count == 0) return (2, []);
        if (_teams[1].Count == 0) return (1, []);
        foreach (var team in _teams)
            foreach (var owner in team.ToList())
                foreach (var buff in owner.Buffs.ToList())
                    if (Cfg(buff) is { } c && AutoChess.I(c, "moment") == 1 && AutoChess.I(c, "action_type") is not (0 or 1 or 6 or 7 or 33)) Effect(owner, buff, c, owner, null);
        Emit(11, null); Flush(); Deaths();
        var rounds = 0;
        while (Live(0).Count > 0 && Live(1).Count > 0)
        {
            if (++rounds > MaxRounds) Fail("Autochess round limit reached");
            Fighter a = _teams[0][0], b = _teams[1][0];
            Emit(12, a); Emit(12, b); Flush(); Deaths();
            if (!Live(0).Contains(a) || !Live(1).Contains(b)) continue;
            long da = Math.Max(0, a.Atk), db = Math.Max(0, b.Atk);
            var gotB = Hit(b, da, a);
            var gotA = Hit(a, db, b);
            Action(2, "action_battle_info", new() { ["action_list"] = new[] { Kv(a.Uid, -gotA), Kv(b.Uid, -gotB) } });
            Flush(); Deaths();
            if (a.Hp > 0) Emit(13, a);
            if (b.Hp > 0) Emit(13, b);
            Flush(); Deaths();
        }
        foreach (var (uid, g) in _growth) { var pc = _original[uid]; pc.Atk += g[0]; pc.Hp += g[1]; }
        foreach (var (cid, g) in _cidGrowth[0])
        {
            var total = _s.ChessBonus.TryGetValue(cid.ToString(), out var t) ? t : _s.ChessBonus[cid.ToString()] = [0, 0];
            total[0] += g[0]; total[1] += g[1];
            foreach (var pc in _s.Board.Concat(_s.Bench).Where(x => x.ChessId == cid)) { pc.Atk += g[0]; pc.Hp += g[1]; }
            foreach (var it in _s.Shop.Where(x => x.ShopType != 1 && x.Id == cid)) { it.Atk += g[0]; it.Hp += g[1]; }
        }
        _s.Board.RemoveAll(x => _removed.Contains(x.UniqueId));
        return (Live(0).Count > 0 ? 1 : Live(1).Count > 0 ? 2 : 0, _actions);
    }
}
