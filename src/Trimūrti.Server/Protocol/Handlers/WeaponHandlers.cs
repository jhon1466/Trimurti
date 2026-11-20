using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 权钥 (hero weapon) and 钥从 (weapon servants), port of Alpha-Persei hero_service weapon growth + servant_service on the
/// client tables WeaponServantCfg, GameLevelSetting (weapon_lv_exp_sum), EquipMaterialCfg and the GameSetting weapon_* keys.
/// Command numbers follow the client WeaponAction / ServantAction:
/// 46010 list, 46012 refine (same-name servant or 41201 crystals), 46014 lock, 46016 weapon exp, 46018 weapon breakthrough,
/// 46020 wear, 46030 awaken a sleeping servant, 46032 decompose, 46034 one-click level + breakthrough.
/// Weapon exp is cumulative; the cap is weapon_exp_limit[break]; breaking needs the player level weapon_user_limit[break].
/// </summary>
public sealed class WeaponSystem(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    private JsonElement Hero => config.Get("hero_cfg.json") ?? throw new ModeError("Missing hero_cfg.json");
    private JsonElement Sigil => config.Get("sigil_cfg.json") ?? throw new ModeError("Missing sigil_cfg.json");
    private static void Require(bool ok, string m) { if (!ok) throw new ModeError(m); }
    private JsonElement Setting(string k) => Hero.GetProperty("setting").GetProperty(k);
    private static List<long> L(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList() : [];
    private static List<(long, long)> Pairs(JsonElement e) => e.ValueKind == JsonValueKind.Array
        ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Array && x.GetArrayLength() >= 2).Select(x => (x[0].GetInt64(), x[1].GetInt64())).ToList() : [];
    private List<(long, long)> Material(long id) => Sigil.GetProperty("materials").TryGetProperty(id.ToString(), out var v) ? Pairs(v) : [];

    private (int Star, int Type) Info(int prefab) => Hero.GetProperty("servants").TryGetProperty(prefab.ToString(), out var s) ? (s.GetProperty("star").GetInt32(), s.GetProperty("type").GetInt32()) : (3, 1);
    private long ExpAt(int level) => Hero.GetProperty("curve").TryGetProperty(level.ToString(), out var c) ? c.GetProperty("weapon_sum").GetInt64() : long.MaxValue;
    private int Cap(int brk) { var l = L(Setting("weapon_exp_limit")); return (int)l[Math.Clamp(brk, 0, l.Count - 1)]; }
    private Dictionary<long, long> Items => Hero.GetProperty("weapon_items").EnumerateObject().ToDictionary(p => long.Parse(p.Name), p => p.Value.GetInt64());
    private long FoodExp(int prefab) { var e = L(Setting("base_exp_weapon_servant")); var s = Info(prefab).Star; return s >= 1 && s <= e.Count ? e[s - 1] : 0; }

    private List<(long, long)> GiveBack(long exp)
    {
        var list = new List<(long, long)>();
        foreach (var (id, val) in Items.OrderByDescending(x => x.Value))
            if (exp >= val) { list.Add((id, exp / val)); exp %= val; }
        return list;
    }

    public static object Rec(ServantRow s) => new Dictionary<string, object?> { ["uid"] = (ulong)s.Uid, ["id"] = (uint)s.Prefab, ["stage"] = (uint)s.Stage, ["is_locked"] = s.Lock ? 1u : 0u };

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var b = r.Body;
        var srv = (ushort)(r.ServerIndex + 1);
        uint U(string k) => HandlerHelpers.ReadUInt(b, k);
        long UL(string k) => (long)HandlerHelpers.ReadULong(b, k);
        var reply = new Dictionary<string, object?> { ["result"] = 0u };
        var updates = new List<ServantRow>();
        var deletes = new List<(long Uid, int Prefab)>();
        var heroes = new List<(long, long, int, long)>();
        var costs = new List<(long, long)>();
        var grants = new List<(long, long)>();
        long touchedHero = 0;
        var resend = false;
        try
        {
            var all = (await accounts.GetServantRowsAsync(uid, ct)).ToDictionary(s => s.Uid);
            var worn = await accounts.GetWornServantsAsync(uid, ct);
            ServantRow Get(long id) { Require(all.TryGetValue(id, out var s), "Servant not found"); return s!; }
            ServantRow Food(long id) { var s = Get(id); Require(!s.Lock && !worn.ContainsValue(id), "Servant locked or worn"); return s; }
            var playerLevel = (await accounts.GetProfileAsync(uid, ct))?.Level ?? 1;

            async Task<HeroRow> HeroAsync(long id) { var h = await accounts.GetHeroRowAsync(uid, id, ct); Require(h is not null, "Hero not owned"); touchedHero = id; return h!; }

            long FeedExp()
            {
                long exp = 0;
                foreach (var (id, n) in HandlerHelpers.ReadItemList(b, "material_list"))
                {
                    Require(Items.TryGetValue(id, out var v), "Not a weapon exp item");
                    costs.Add((id, n));
                    exp += v * n;
                }
                foreach (var id in HandlerHelpers.ReadIdList(b, "servant_list").Distinct())
                {
                    var s = Food(id);
                    exp += FoodExp(s.Prefab);
                    deletes.Add((s.Uid, s.Prefab));
                }
                return exp;
            }

            switch (cmd)
            {
                case 46010:
                    resend = true;
                    break;
                case 46014:
                    {
                        var s = Get(UL("uid"));
                        updates.Add(s with { Lock = U("is_lock") != 0 });
                        break;
                    }
                case 46020:
                    {
                        var h = await HeroAsync(U("hero_id"));
                        var sid = UL("servant_id");
                        if (sid != 0) Get(sid);
                        heroes.Add((h.Id, h.WeaponExp, h.WeaponBreak, sid));
                        break;
                    }
                case 46012:
                    {
                        var s = Get(UL("uid"));
                        var (star, type) = Info(s.Prefab);
                        var max = L(Setting("weapon_promote_max"));
                        Require(star >= 1 && star <= max.Count && s.Stage <= max[star - 1], "Refine cap");
                        var gold = Setting("weapon_servant_gold_cost")[star - 1];
                        if (gold.ValueKind == JsonValueKind.Array && gold.GetArrayLength() >= s.Stage) costs.Add((2, gold[s.Stage - 1].GetInt64()));
                        var costUid = UL("cost_uid");
                        if (costUid != 0)
                        {
                            var food = Food(costUid);
                            Require(food.Uid != s.Uid && food.Prefab == s.Prefab, "Needs a servant with the same name");
                            deletes.Add((food.Uid, food.Prefab));
                        }
                        else
                        {
                            Require(star >= 4, "3★ servants refine only with duplicates");
                            var mat = type == 2 ? L(Setting("weapon_promote_cost_exclusive")) : L(Setting("weapon_promote_cost")[star - 4]);
                            costs.Add((mat[0], mat[1]));
                        }
                        updates.Add(s with { Stage = s.Stage + 1 });
                        break;
                    }
                case 46030:
                    {
                        var target = (int)UL("servant_id");
                        var aw = config.Get("servant_awake_cfg.json") ?? throw new ModeError("No awaken config");
                        Require(aw.GetProperty("targets").TryGetProperty(target.ToString(), out var tc), "Servant cannot be awakened");
                        Require(aw.GetProperty("recipes").TryGetProperty(tc.GetProperty("race").GetInt32().ToString(), out var rc), "No recipe");
                        var ids = HandlerHelpers.ReadIdList(b, "cost_uid_list");
                        Require(ids.Count == 1, "Needs one sleeping servant");
                        var src = Get(ids[0]);
                        Require(!src.Lock && src.Prefab == rc.GetProperty("base_id").GetInt32(), "Wrong sleeping servant");
                        costs.Add((rc.GetProperty("currency_id").GetInt64(), rc.GetProperty("currency_num").GetInt64()));
                        deletes.Add((src.Uid, src.Prefab));     // old prefab index removed on the client (17023)
                        updates.Add(src with { Prefab = target });
                        reply["servant_uid"] = (ulong)src.Uid;
                        break;
                    }
                case 46032:
                    {
                        var ids = HandlerHelpers.ReadIdList(b, "servant_list").Distinct().ToList();
                        Require(ids.Count > 0, "Nothing to decompose");
                        var ret = Setting("weapon_servant_break_cost_return");
                        foreach (var id in ids)
                        {
                            var s = Food(id);
                            var star = Info(s.Prefab).Star;
                            if (star >= 1 && star <= ret.GetArrayLength()) grants.AddRange(Pairs(ret[star - 1]));
                            deletes.Add((s.Uid, s.Prefab));
                        }
                        reply["return_list"] = HandlerHelpers.ToItemNetRecList(grants.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))));
                        resend = true;
                        break;
                    }
                case 46016:
                    {
                        var h = await HeroAsync(U("hero_id"));
                        var capExp = ExpAt(Cap(h.WeaponBreak));
                        Require(h.WeaponExp < capExp, "Weapon at level cap");
                        var add = FeedExp();
                        Require(add > 0, "No exp");
                        var absorbed = Math.Min(add, capExp - h.WeaponExp);
                        costs.Add((2, absorbed * L(Setting("weapon_strengthen_gold_cost")).FirstOrDefault(1)));
                        grants.AddRange(GiveBack(add - absorbed));
                        heroes.Add((h.Id, h.WeaponExp + absorbed, h.WeaponBreak, h.ServantUid));
                        break;
                    }
                case 46018:
                    {
                        var h = await HeroAsync(U("hero_id"));
                        var costIds = L(Setting("weapon_break_cost"));
                        Require(h.WeaponBreak < costIds.Count, "Weapon break max");
                        Require(h.WeaponExp >= ExpAt(Cap(h.WeaponBreak)), "Weapon not at level cap");
                        Require(playerLevel >= L(Setting("weapon_user_limit"))[h.WeaponBreak], "Player level too low");
                        costs.AddRange(Material(costIds[h.WeaponBreak]));
                        heroes.Add((h.Id, h.WeaponExp, h.WeaponBreak + 1, h.ServantUid));
                        break;
                    }
                case 46034:
                    {
                        var h = await HeroAsync(U("hero_id"));
                        var costIds = L(Setting("weapon_break_cost"));
                        var final = h.WeaponBreak + (int)U("breakthrough_times");
                        Require(final <= costIds.Count, "Weapon break max");
                        var target = (int)U("target_level");
                        Require(target >= 1 && target <= Cap(final), "Bad target level");
                        var add = FeedExp();
                        var targetExp = ExpAt(target);
                        Require(targetExp >= h.WeaponExp && h.WeaponExp + add >= targetExp, "Not enough exp");
                        for (var s = h.WeaponBreak; s < final; s++)
                        {
                            Require(targetExp >= ExpAt(Cap(s)) && playerLevel >= L(Setting("weapon_user_limit"))[s], "Breakthrough requirements not met");
                            costs.AddRange(Material(costIds[s]));
                        }
                        var absorbed = targetExp - h.WeaponExp;
                        costs.Add((2, absorbed * L(Setting("weapon_strengthen_gold_cost")).FirstOrDefault(1)));
                        var refund = GiveBack(add - absorbed);
                        grants.AddRange(refund);
                        heroes.Add((h.Id, targetExp, final, h.ServantUid));
                        reply["mat_list"] = HandlerHelpers.ToItemNetRecList(refund);
                        break;
                    }
            }
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)]; }

        var c2 = costs.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).Where(x => x.Item2 > 0).ToList();
        var g2 = grants.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).Where(x => x.Item2 > 0).ToList();
        // Awakening keeps the instance: its uid is updated, not deleted.
        var realDeletes = deletes.Where(d => !updates.Any(u => u.Uid == d.Uid)).Select(d => d.Uid).ToList();
        if ((updates.Count > 0 || realDeletes.Count > 0 || heroes.Count > 0 || c2.Count > 0 || g2.Count > 0)
            && !await accounts.CommitServantsAsync(uid, updates, realDeletes, heroes, c2, g2, ct))
            return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)];

        var frames = await InventorySync.FramesAsync(accounts, codec, r, c2.Concat(g2).Select(x => x.Item1), ct);
        if (deletes.Count > 0)
            frames.Add(new GameResponse(17023, 0, srv, codec.Encode("sc_17023", new Dictionary<string, object?>
            {
                ["weapon_list"] = deletes.Select(d => (object)new Dictionary<string, object?> { ["uid"] = (ulong)d.Uid, ["item"] = new Dictionary<string, object?> { ["item_id"] = (uint)d.Prefab, ["item_num"] = 0u } }).ToArray()
            })));
        if (resend || cmd == 46030)
        {
            var list = cmd == 46030 ? updates.Select(Rec).ToArray() : (await accounts.GetServantRowsAsync(uid, ct)).Select(Rec).ToArray();
            frames.Add(new GameResponse(46011, cmd == 46010 ? r.Index : (ushort)0, srv, codec.Encode("sc_46011", new Dictionary<string, object?> { ["servant_list"] = list })));
            if (cmd == 46010) return frames;
        }
        // HeroWeaponStrOp/BreakOp/QuickOp: inventory → ACK → 14007 (the ACK callback predicts, 14007 settles it).
        frames.Add(HandlerHelpers.Reply(codec, r, cmd + 1, reply));
        if (touchedHero != 0 && await LoginBootstrap.BuildSingleHeroNetRecAsync(accounts, uid, touchedHero, ct) is { } rec)
            frames.Add(new GameResponse(14007, 0, srv, codec.Encode("sc_14007", new Dictionary<string, object?> { ["hero"] = rec })));
        return frames;
    }

    public static readonly uint[] Commands = [46010, 46012, 46014, 46016, 46018, 46020, 46030, 46032, 46034];
}
