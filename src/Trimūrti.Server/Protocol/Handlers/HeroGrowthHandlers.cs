using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// Hero growth (port of Alpha-Persei hero_service.py) on the client tables HeroCfg, HeroStarCfg, HeroBreakCfg,
/// GameLevelSetting, SkillCfg, HeroSkillElementCfg, HeroAstrolabeCfg, WeaponModuleCfg and GameSetting
/// (→ Data/Configs/hero_cfg.json). Command numbers follow the client HeroAction / AstrolabeAction / ShopAction:
/// 14012 star up (pieces + gold), 14014 exp items, 14016 unlock by pieces, 14030 skill up (SkillCfg cost / break limit),
/// 14032 surplus pieces → badges, 14034 / 14046 display / battle skin, 14036 breakthrough, 14038 / 14040 / 14028 / 71116
/// astrolabe equip, 14026 astrolabe unlock (cost, previous node, ConditionCfg hero condition), 14042 refresh,
/// 14044 skill attribute, 14110 skin unlock with its ticket, 14112 / 14114 跃迁 talents / skills, 14116 weapon module,
/// 14118 module material resolve, 14120 one-click level + breakthrough.
/// Exp is cumulative from level 1 (GameLevelSetting hero_level_exp1); the cap is HeroBreakCfg[race][break].max_level;
/// overflow returns as exp items.
/// </summary>
public sealed class HeroGrowth(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    private JsonElement Cfg => config.Get("hero_cfg.json") ?? throw new ModeError("Missing hero_cfg.json");
    private static void Require(bool ok, string m) { if (!ok) throw new ModeError(m); }
    private JsonElement Setting(string k) => Cfg.GetProperty("setting").GetProperty(k);
    private static List<long> L(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList() : [];
    private static List<(long, long)> Pairs(JsonElement e) => e.ValueKind == JsonValueKind.Array
        ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Array && x.GetArrayLength() >= 2).Select(x => (x[0].GetInt64(), x[1].GetInt64())).ToList() : [];
    private static int I(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetInt64() : 0;

    private JsonElement Hero(long id) { Require(Cfg.GetProperty("heroes").TryGetProperty(id.ToString(), out var h), "Unknown hero"); return h; }
    private JsonElement? Break(int race, int brk) => Cfg.GetProperty("breaks").TryGetProperty(race.ToString(), out var r) && r.TryGetProperty(brk.ToString(), out var b) ? b : null;
    private int MaxLevel(int race, int brk) => Break(race, brk) is { } b ? I(b, "max_level") : 20;
    private long Curve(int level, string col) => Cfg.GetProperty("curve").TryGetProperty(level.ToString(), out var c) ? c.GetProperty(col).GetInt64() : 0;
    private long ExpTo(int level) { long s = 0; for (var l = 1; l < level; l++) s += Curve(l, "hero"); return s; }
    private int LevelOf(long exp, int cap) { var lv = 1; while (lv < cap && exp >= ExpTo(lv + 1)) lv++; return lv; }
    private int WeaponLevel(long exp) { var lv = 1; while (Cfg.GetProperty("curve").TryGetProperty((lv + 1).ToString(), out var c) && exp >= c.GetProperty("weapon_sum").GetInt64()) lv++; return lv; }
    private Dictionary<long, long> ExpItems => Cfg.GetProperty("exp_items").EnumerateObject().ToDictionary(p => long.Parse(p.Name), p => p.Value.GetInt64());

    private List<(long, long)> GiveBack(long exp)
    {
        var list = new List<(long, long)>();
        foreach (var (id, val) in ExpItems.OrderByDescending(x => x.Value))
            if (exp >= val) { list.Add((id, exp / val)); exp %= val; }
        return list;
    }

    private (long Absorbed, List<(long, long)> Mats, long Raw) ExpFromItems(IReadOnlyList<(long ItemId, long Count)> items)
    {
        long raw = 0;
        var mats = new List<(long, long)>();
        foreach (var (id, n) in items)
        {
            Require(ExpItems.TryGetValue(id, out var v) && n > 0, "Not a hero exp item");
            raw += v * n;
            mats.Add((id, n));
        }
        return (0, mats, raw);
    }

    private static List<(long, long)> Costs(JsonElement e) => e.ValueKind == JsonValueKind.Array ? Pairs(e) : [];

    // ---------- hero conditions (client P08Support IsConditionAchieved, hero branch) ----------
    private async Task<bool> ConditionAsync(long uid, int id, HeroRow h, CancellationToken ct)
    {
        if (id == 0) return true;
        var cond = config.Get("condition_cfg.json") is { } cc && cc.TryGetProperty(id.ToString(), out var c) ? c : (JsonElement?)null;
        if (cond is not { } cd) return false;
        var p = L(cd.GetProperty("params"));
        var p0 = p.FirstOrDefault();
        async Task<bool> Cleared(long stage) => (await accounts.GetLobbySnapshotAsync(uid, ct)).Chapters.Any(x => x.Id == stage && x.ClearTimes > 0);
        return I(cd, "type") switch
        {
            1001 => h.Level >= p0,
            1002 => h.Skills.Sum(s => s[1]) >= p0,
            1003 => h.Star >= p0,
            1004 => Math.Min(h.ClearTimes, 100) >= p0,
            1005 => h.UnlockAstrolabe.Count >= p0,
            1007 => WeaponLevel(h.WeaponExp) >= p0,
            1008 => L(Hero(h.Id).GetProperty("study")) is var st && (st.Count == 0 || await Cleared(st[0])),
            1009 => (h.Skills.FirstOrDefault(s => s[0] % 1000 == 209)?[1] ?? 1) >= p0,
            1010 => h.BreakLevel >= p0,
            1011 or 5002 => p0 > 0 && await Cleared(p0),
            _ => false
        };
    }

    // ---------- operations ----------
    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var b = r.Body;
        var srv = (ushort)(r.ServerIndex + 1);
        uint U(string k) => HandlerHelpers.ReadUInt(b, k);
        var reply = new Dictionary<string, object?> { ["result"] = 0u };
        var costs = new List<(long, long)>();
        var grants = new List<(long, long)>();
        HeroRow? hero = null;
        (long, int, int, string)? warp = null;
        var pieces = false;
        long touchedHero = 0;
        try
        {
            async Task<HeroRow> Load(long id)
            {
                var h = await accounts.GetHeroRowAsync(uid, id, ct);
                Require(h is not null, "Hero not owned");
                touchedHero = id;
                // The client counts exp from the floor of the current level (LvTools.CheckHeroExp); a save with less exp than
                // its level needs would level up lower here than on screen.
                h!.Exp = Math.Max(h.Exp, ExpTo(h.Level));
                return h;
            }
            var playerLevel = (await accounts.GetProfileAsync(uid, ct))?.Level ?? 1;

            switch (cmd)
            {
                case 14012:
                    {
                        hero = await Load(U("id"));
                        Require(Cfg.GetProperty("stars").TryGetProperty(hero.Star.ToString(), out var sc), "Unknown star");
                        var order = Cfg.GetProperty("stars").EnumerateObject().Select(p => int.Parse(p.Name)).Order().ToList();
                        var next = order.SkipWhile(x => x <= hero.Star).FirstOrDefault();
                        Require(next != 0 && I(sc, "pieces") > 0, "Max star reached");
                        costs.Add((10000 + hero.Id, I(sc, "pieces")));
                        costs.Add((2, I(sc, "gold")));
                        hero.Star = next;
                        pieces = true;
                        break;
                    }
                case 14016:
                    {
                        var id = (long)U("id");
                        var hc = Hero(id);
                        Require(await accounts.GetHeroRowAsync(uid, id, ct) is null, "Hero already owned");
                        var need = L(Setting("unlock_hero_need"));
                        costs.Add((10000 + id, need[Math.Clamp(I(hc, "unlock_star") / 100 - 1, 0, need.Count - 1)]));
                        grants.Add((id, 1));
                        touchedHero = id;
                        pieces = true;
                        break;
                    }
                case 14014:
                    {
                        hero = await Load(U("id"));
                        var race = I(Hero(hero.Id), "race");
                        var cap = MaxLevel(race, hero.BreakLevel);
                        var capExp = ExpTo(cap);
                        Require(hero.Exp < capExp, "Level cap reached, break through first");
                        var (_, mats, raw) = ExpFromItems(HandlerHelpers.ReadItemList(b, "item_list"));
                        Require(raw > 0, "No exp");
                        costs.AddRange(mats);
                        var absorbed = Math.Min(raw, capExp - hero.Exp);
                        grants.AddRange(GiveBack(raw - absorbed));
                        hero.Exp += absorbed;
                        hero.Level = LevelOf(hero.Exp, cap);
                        break;
                    }
                case 14036:
                    {
                        hero = await Load(U("hero_id"));
                        var race = I(Hero(hero.Id), "race");
                        Require(Break(race, hero.BreakLevel) is { } cur && Break(race, hero.BreakLevel + 1) is not null, "Max breakthrough");
                        var bc = Break(race, hero.BreakLevel)!.Value;
                        Require(hero.Level >= I(bc, "max_level") && playerLevel >= I(bc, "user_level"), "Breakthrough requirements not met");
                        costs.Add((2, I(bc, "gold")));
                        costs.AddRange(Costs(bc.GetProperty("cost")));
                        hero.BreakLevel++;
                        break;
                    }
                case 14120:
                    {
                        hero = await Load(U("hero_id"));
                        var race = I(Hero(hero.Id), "race");
                        var breaks = HandlerHelpers.ReadIdList(b, "break_list").Select(x => (int)x).Order().ToList();
                        if (b.TryGetValue("break_list", out var bl) && bl is System.Collections.IEnumerable e && e.Cast<object?>().Any(x => Convert.ToInt64(x) == 0)) breaks.Insert(0, 0);
                        var stage = hero.BreakLevel;
                        foreach (var s in breaks)
                        {
                            Require(s == stage && Break(race, s + 1) is not null, "Breakthrough list must start at the current stage");
                            var bc = Break(race, s)!.Value;
                            Require(playerLevel >= I(bc, "user_level"), "Player level too low");
                            costs.Add((2, I(bc, "gold")));
                            costs.AddRange(Costs(bc.GetProperty("cost")));
                            stage++;
                        }
                        var cap = MaxLevel(race, stage);
                        var target = (int)U("level") is var t and > 0 ? t : cap;
                        Require(target <= cap && target >= hero.Level, "Bad target level");
                        var (_, mats, raw) = ExpFromItems(HandlerHelpers.ReadItemList(b, "item_list"));
                        costs.AddRange(mats);
                        var targetExp = ExpTo(target);
                        Require(hero.Exp + raw >= targetExp || U("level") == 0, "Not enough exp for the target level");
                        foreach (var s in breaks) Require(hero.Exp + raw >= ExpTo(MaxLevel(race, s)), "Exp does not reach the selected breakthrough");
                        var absorbed = Math.Max(0, Math.Min(raw, targetExp - hero.Exp));
                        var refund = GiveBack(raw - absorbed);
                        grants.AddRange(refund);
                        hero.Exp += absorbed;
                        hero.BreakLevel = stage;
                        hero.Level = LevelOf(hero.Exp, target);
                        reply["item_list"] = HandlerHelpers.ToItemNetRecList(refund);
                        break;
                    }
                case 14030:
                    {
                        hero = await Load(U("hero_id"));
                        var skill = (int)U("skill_id");
                        var num = Math.Max(1, (int)U("num"));
                        var configured = L(Hero(hero.Id).GetProperty("skills")).Take(5).Select(x => (int)x).ToList();
                        var slot = configured.IndexOf(skill) + 1;
                        Require(slot >= 1, "Skill not upgradable");
                        var rec = hero.Skills.FirstOrDefault(s => s[0] == skill);
                        if (rec is null) hero.Skills.Add(rec = [skill, 1]);
                        var max = (int)L(Setting("hero_skill_level_max")).FirstOrDefault(35);
                        Require(rec[1] + num <= max, "Skill level cap");
                        for (var lv = rec[1]; lv < rec[1] + num; lv++)
                        {
                            Require(Cfg.GetProperty("skill").TryGetProperty(lv.ToString(), out var sc), "Missing skill cost");
                            Require(hero.BreakLevel >= I(sc, $"limit{slot}"), "Breakthrough too low for this skill level");
                            costs.AddRange(Costs(sc.GetProperty($"cost{slot}")));
                        }
                        rec[1] += num;
                        break;
                    }
                case 14044:
                    {
                        hero = await Load(U("hero_id"));
                        var idx = (int)U("index");
                        var num = Math.Max(1, (int)U("num"));
                        Require(idx is >= 1 and <= 5, "Bad attribute");
                        var rec = hero.Intensify.FirstOrDefault(s => s[0] == idx);
                        if (rec is null) hero.Intensify.Add(rec = [idx, 0]);
                        Require(rec[1] + num <= (int)L(Setting("hero_skill_attr_level_max")).FirstOrDefault(15), "Attribute level cap");
                        Require(Cfg.GetProperty("element").TryGetProperty(hero.Id.ToString(), out var el), "No skill attributes");
                        for (var lv = rec[1] + 1; lv <= rec[1] + num; lv++)
                        {
                            Require(el.TryGetProperty(lv.ToString(), out var ec), "Missing attribute cost");
                            costs.AddRange(Costs(ec.GetProperty($"cost{idx}")));
                        }
                        rec[1] += num;
                        break;
                    }
                case 14026:
                    {
                        hero = await Load(U("id"));
                        var node = (int)U("astrolabe_id");
                        Require(Cfg.GetProperty("astrolabe").TryGetProperty(node.ToString(), out var ac), "Unknown node");
                        Require(L(Hero(hero.Id).GetProperty("astrolabe")).Contains(I(ac, "suit")) && !hero.UnlockAstrolabe.Contains(node), "Node not available");
                        Require(I(ac, "pre") == 0 || hero.UnlockAstrolabe.Contains(I(ac, "pre")), "Previous node locked");
                        Require(await ConditionAsync(uid, I(ac, "cond"), hero, ct), "Unlock condition not met");
                        costs.AddRange(Costs(ac.GetProperty("cost")));
                        hero.UnlockAstrolabe.Add(node);
                        reply["hero_id"] = (uint)hero.Id;
                        reply["unlock_id"] = (uint)node;
                        break;
                    }
                case 14028:
                    {
                        hero = await Load(U("id"));
                        var node = (int)U("astrolabe_id");
                        if (U("operation") == 2) { Require(hero.UsingAstrolabe.Remove(node), "Node not equipped"); break; }
                        Require(hero.UnlockAstrolabe.Contains(node) && Cfg.GetProperty("astrolabe").TryGetProperty(node.ToString(), out var ac), "Node locked");
                        var pos = I(Cfg.GetProperty("astrolabe").GetProperty(node.ToString()), "pos");
                        hero.UsingAstrolabe.RemoveAll(x => Cfg.GetProperty("astrolabe").TryGetProperty(x.ToString(), out var o) && I(o, "pos") == pos);
                        hero.UsingAstrolabe.Add(node);
                        break;
                    }
                case 14038:
                case 71116:
                    {
                        hero = await Load(U("hero_id"));
                        var nodes = cmd == 14038
                            ? Cfg.GetProperty("astrolabe").EnumerateObject().Where(p => I(p.Value, "suit") == (int)U("astrolabe_suit_id")).Select(p => int.Parse(p.Name)).ToList()
                            : HandlerHelpers.ReadIdList(b, "astrolabe_id_list").Select(x => (int)x).ToList();
                        Require(nodes.Count is >= 1 and <= 3 && nodes.All(hero.UnlockAstrolabe.Contains), "Nodes locked");
                        var positions = nodes.Select(n => I(Cfg.GetProperty("astrolabe").GetProperty(n.ToString()), "pos")).ToList();
                        Require(positions.Distinct().Count() == positions.Count, "Two nodes in one position");
                        hero.UsingAstrolabe = nodes;
                        break;
                    }
                case 14040:
                    hero = await Load(U("hero_id"));
                    hero.UsingAstrolabe.Clear();
                    break;
                case 14034:
                case 14046:
                    {
                        hero = await Load(U("hero_id"));
                        var skin = (int)U("skin_id");
                        if (skin != 0 && skin != hero.Id)
                        {
                            Require(config.Get("skin_hero_map.json") is { } sm && sm.TryGetProperty(skin.ToString(), out var owner) && owner.GetInt64() == hero.Id, "Skin of another hero");
                            Require((await accounts.GetUnlockedSkinsAsync(uid, ct)).Contains(skin), "Skin locked");
                        }
                        if (cmd == 14034) hero.Skin = skin == 0 ? (int)hero.Id : skin; else hero.BattleSkin = skin == 0 ? (int)hero.Id : skin;
                        break;
                    }
                case 14110:
                    {
                        var skin = (long)U("skin_id");
                        var owner = config.Get("skin_hero_map.json") is { } sm && sm.TryGetProperty(skin.ToString(), out var ow) ? ow.GetInt64() : 0;
                        Require(owner != 0, "Unknown skin");
                        Require(!(await accounts.GetUnlockedSkinsAsync(uid, ct)).Contains(skin), "Skin already owned");
                        var ticket = config.Get("ticket_skin_map.json") is { } tm ? tm.EnumerateObject().Where(p => p.Value.GetInt64() == skin).Select(p => long.Parse(p.Name)).FirstOrDefault() : 0;
                        Require(ticket != 0 && await accounts.GetItemCountAsync(uid, ticket, ct) > 0, "Skin ticket missing");
                        costs.Add((ticket, 1));
                        touchedHero = owner;
                        if (!await accounts.ConsumeAndGrantAsync(uid, costs, [], ct)) throw new ModeError("Skin ticket missing");
                        costs.Clear();
                        await accounts.UnlockAllSkinsAsync(uid, new Dictionary<long, long> { [skin] = touchedHero }, ct);
                        break;
                    }
                case 14112:
                    {
                        hero = await Load(U("hero_id"));
                        var slotId = (int)U("slot_id");
                        var num = Math.Max(1, (int)U("lv_up_num"));
                        Require(hero.Level >= L(Setting("exclusive_open_need")).FirstOrDefault(40), "跃迁 locked");
                        var slots = (await accounts.GetHeroWarpAsync(uid, ct)).GetValueOrDefault(hero.Id) ?? [];
                        var slot = slots.FirstOrDefault(s => s.SlotId == slotId);
                        var tp = slot?.TalentPoints ?? 0;
                        var cost = Setting("exclusive_skill_talent_strengthen_cost");
                        Require(slotId is >= 1 and <= 6 && tp + num <= (int)L(Setting("exclusive_skill_talent_num_max")).FirstOrDefault(6), "Talent cap");
                        for (var lv = tp + 1; lv <= tp + num; lv++) costs.AddRange(Pairs(cost[lv - 1]));
                        warp = (hero.Id, slotId, tp + num, JsonSerializer.Serialize((slot?.Skills ?? []).Select(s => new[] { s.Id, s.Level })));
                        break;
                    }
                case 14114:
                    {
                        hero = await Load(U("hero_id"));
                        var slotId = (int)U("slot_id");
                        var skills = Msgs(b, "skill_list").Select(x => new[] { HandlerHelpers.ReadUInt(x, "id") is var i and > 0 ? i : HandlerHelpers.ReadUInt(x, "skill_id"), HandlerHelpers.ReadUInt(x, "skill_level") }).ToList();
                        Require(slotId is >= 1 and <= 6 && skills.Count <= (int)L(Setting("exclusive_skill_use_num_max")).FirstOrDefault(2), "Too many 跃迁 skills");
                        var tp = (await accounts.GetHeroWarpAsync(uid, ct)).GetValueOrDefault(hero.Id)?.FirstOrDefault(s => s.SlotId == slotId)?.TalentPoints ?? 0;
                        Require(skills.Sum(s => (long)s[1]) <= tp, "Not enough talent points");
                        warp = (hero.Id, slotId, tp, JsonSerializer.Serialize(skills));
                        break;
                    }
                case 14116:
                    {
                        hero = await Load(U("hero_id"));
                        Require(Cfg.GetProperty("module").TryGetProperty(hero.Id.ToString(), out var mc), "No weapon module");
                        var levels = mc.GetProperty("cost");
                        Require(hero.ModuleLevel < levels.GetArrayLength(), "Module max level");
                        costs.AddRange(Pairs(levels[hero.ModuleLevel]));
                        hero.ModuleLevel++;
                        break;
                    }
                case 14118:
                    {
                        var items = HandlerHelpers.ReadItemList(b, "item_list");
                        var moduleItems = Cfg.GetProperty("module").EnumerateObject().SelectMany(p => p.Value.GetProperty("cost").EnumerateArray().SelectMany(Pairs)).Select(x => x.Item1).Where(x => x >= 80000 && x < 90000).ToHashSet();
                        Require(items.Count > 0 && items.All(x => moduleItems.Contains(x.ItemId)), "Not module material");
                        var ret = L(Setting("weapon_module_break_return"));
                        costs.AddRange(items.Select(x => (x.ItemId, x.Count)));
                        grants.Add((ret[0], ret[1] * items.Sum(x => x.Count)));
                        break;
                    }
                case 14032:
                    {
                        var order = Cfg.GetProperty("stars").EnumerateObject().Select(p => (int.Parse(p.Name), I(p.Value, "pieces"))).OrderBy(x => x.Item1).ToList();
                        var conv = Setting("fragment_break_num");
                        foreach (var (h, have) in await accounts.GetHeroPiecesAsync(uid, ct))
                        {
                            var row = await accounts.GetHeroRowAsync(uid, h, ct);
                            if (row is null) continue;
                            var need = order.Where(x => x.Item1 >= row.Star).Sum(x => (long)x.Item2);
                            if (have <= need) continue;
                            var surplus = have - need;
                            var rare = Math.Clamp(I(Hero(h), "rare"), 1, conv.GetArrayLength());
                            costs.Add((10000 + h, surplus));
                            grants.Add((conv[rare - 1][0].GetInt64(), conv[rare - 1][1].GetInt64() * surplus));
                        }
                        Require(costs.Count > 0, "No surplus pieces");
                        pieces = true;
                        break;
                    }
                case 14042:
                    touchedHero = U("hero_id");
                    break;
            }
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)]; }

        var c2 = costs.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).Where(x => x.Item2 > 0).ToList();
        var g2 = grants.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).Where(x => x.Item2 > 0).ToList();
        if ((hero is not null || warp is not null || c2.Count > 0 || g2.Count > 0) && !await accounts.CommitHeroAsync(uid, hero, c2, g2, warp, ct))
            return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)];

        var frames = await InventorySync.FramesAsync(accounts, codec, r, c2.Concat(g2).Select(x => x.Item1), ct);
        if (pieces)
            frames.Add(new GameResponse(14019, 0, srv, codec.Encode("sc_14019", new Dictionary<string, object?>
            { ["piece_list"] = (await accounts.GetHeroPiecesAsync(uid, ct)).Select(p => (object)new Dictionary<string, object?> { ["id"] = (uint)p.Hero, ["num"] = (uint)p.Num }).ToArray() })));
        // Alpha-Persei hero_service / test_hero_response_boundary: level-up and breakthrough answer ACK first, then the
        // inventory diff and the authoritative 14007. The client applies its own prediction in the ACK callback
        // (e.g. +exp on 14015) and 14007 then overwrites it; a 14007 before the ACK gets the exp added twice.
        // Level-up, breakthrough, one-click and skin put the ACK before everything (HeroAddExpOp/HeroBreakOp/OneClickHeroUpOp/
        // HeroSkinSelectOp); the other hero operations keep inventory → ACK → 14007 (SkillUpOp sends its 14007 after the ACK).
        var ack = HandlerHelpers.Reply(codec, r, cmd + 1, reply);
        if (cmd is 14014 or 14036 or 14120 or 14034 or 14046) frames.Insert(0, ack); else frames.Add(ack);
        if (touchedHero != 0 && await LoginBootstrap.BuildSingleHeroNetRecAsync(accounts, uid, touchedHero, ct) is { } heroRec)
            frames.Add(new GameResponse(14007, 0, srv, codec.Encode("sc_14007", new Dictionary<string, object?> { ["hero"] = heroRec })));
        return frames;
    }

    private static IEnumerable<IReadOnlyDictionary<string, object?>> Msgs(IReadOnlyDictionary<string, object?> b, string k) =>
        b.TryGetValue(k, out var v) && v is System.Collections.IEnumerable e && v is not string ? e.OfType<IReadOnlyDictionary<string, object?>>() : [];

    /// <summary>Raises every hero's exp to at least the cumulative exp of its level (GameLevelSetting hero_lv_exp_sum).</summary>
    public async Task RepairExpAsync(long uid, CancellationToken ct)
    {
        foreach (var (id, level, exp) in await accounts.GetHeroLevelsAsync(uid, ct))
            if (exp < ExpTo(level)) await accounts.SetHeroExpAsync(uid, id, ExpTo(level), ct);
    }

    public static readonly uint[] Commands =
        [14012, 14014, 14016, 14026, 14028, 14030, 14032, 14034, 14036, 14038, 14040, 14042, 14044, 14046, 14110, 14112, 14114, 14116, 14118, 14120, 71116];
}
