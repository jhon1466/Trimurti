using System.Text.Json;
using System.Text.Json.Nodes;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 刻印 (sigils / equips), port of Alpha-Persei equip_service.py + hero_service equip slots on the client tables
/// (EquipCfg, EquipExpCfg, EquipMaterialCfg, EquipSkillCfg, EquipSkillPoolCfg, EquipSuitCfg, ItemCfg and the
/// GameSetting equip_* keys → Data/Configs/sigil_cfg.json, exported with scripts/luadump/luacfg.py). Command numbers
/// follow the client EquipAction / HeroAction / ProposalAction:
/// 13012 wear, 13018 take all off, 13026 quick dress, 13014 enhance, 13022 breakthrough, 13058 one-click enhance+break,
/// 13016 lock, 13024 resolve, 13028/13030/13044/13060 enchant roll / confirm / discard / directional, 13032/13034 race
/// reconstruction, 13046 exclusive-hero reconstruction, 13052 inherit, 13054 auto-decompose flag (13057),
/// 13036–13042 equip proposals (sc_14009 proposal_list).
/// Exp is cumulative from level 1 (EquipExpCfg expN by the sigil's variant); the level cap is max_level[break];
/// overflow exp returns as 启示录 items; gold cost = absorbed exp × equip_strengthen_gold_cost.
/// </summary>
public sealed class SigilSystem(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    private JsonElement Cfg => config.Get("sigil_cfg.json") ?? throw new ModeError("Missing sigil_cfg.json");
    private static void Require(bool ok, string m) { if (!ok) throw new ModeError(m); }
    private JsonElement Setting(string k) => Cfg.GetProperty("setting").GetProperty(k);
    private static int I(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetInt64() : 0;
    private static List<long> L(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList() : [];
    private static List<(long, long)> Pairs(JsonElement e) => e.ValueKind == JsonValueKind.Array
        ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Array && x.GetArrayLength() >= 2).Select(x => (x[0].GetInt64(), x[1].GetInt64())).ToList() : [];

    public JsonElement? Equip(int prefab) => Cfg.GetProperty("equips").TryGetProperty(prefab.ToString(), out var v) ? v : null;
    private List<(long, long)> Material(long id) => Cfg.GetProperty("materials").TryGetProperty(id.ToString(), out var v) ? Pairs(v) : [];
    public int Pos(int prefab) => Equip(prefab) is { } e ? I(e, "pos") : prefab / 10000 % 10;
    public int Star(int prefab) => Equip(prefab) is { } e ? I(e, "starlevel") : 0;
    private int Variant(JsonElement e) => Math.Clamp((int)(L(e.GetProperty("exp")).FirstOrDefault(1)), 1, 7);

    /// <summary>Cumulative exp needed to reach <paramref name="level"/> from level 1.</summary>
    private long ExpTo(int variant, int level)
    {
        long sum = 0;
        for (var l = 1; l < level; l++)
            if (Cfg.GetProperty("curve").TryGetProperty(l.ToString(), out var c)) sum += c.GetProperty($"exp{variant}").GetInt64();
        return sum;
    }

    public int Level(int prefab, long exp, int brk)
    {
        if (Equip(prefab) is not { } e) return 1;
        var cap = MaxLevel(e, brk);
        var v = Variant(e);
        var lv = 1;
        while (lv < cap && exp >= ExpTo(v, lv + 1)) lv++;
        return lv;
    }

    private static int MaxLevel(JsonElement e, int brk) { var ml = L(e.GetProperty("max_level")); return ml.Count == 0 ? 1 : (int)ml[Math.Clamp(brk, 0, ml.Count - 1)]; }

    private Dictionary<long, long> ExpItems => Cfg.GetProperty("exp_items").EnumerateObject().ToDictionary(p => long.Parse(p.Name), p => p.Value.GetInt64());

    private List<(long, long)> GiveBack(long exp)
    {
        var list = new List<(long, long)>();
        foreach (var (id, val) in ExpItems.OrderByDescending(x => x.Value))
            if (exp >= val) { list.Add((id, exp / val)); exp %= val; }
        return list;
    }

    private long FodderExp(SigilRow s)
    {
        var stars = L(Setting("base_exp_equip_star"));
        var star = Star(s.Prefab);
        return (star >= 1 && star <= stars.Count ? stars[star - 1] : 0) + s.Exp;
    }

    // ---------- enchant slots ----------
    public sealed class Slot { public int Id { get; set; } public List<int[]> Effects { get; set; } = []; public List<List<int[]>> Previews { get; set; } = []; }
    public static List<Slot> Slots(string json)
    {
        try { return JsonSerializer.Deserialize<List<Slot>>(json) ?? []; } catch { return []; }
    }
    private static string Save(List<Slot> slots) => JsonSerializer.Serialize(slots.OrderBy(s => s.Id));

    public static object[] SlotRecs(string json) => Slots(json).Where(s => s.Effects.Count > 0).OrderBy(s => s.Id).Select(s => (object)new Dictionary<string, object?>
    {
        ["id"] = (uint)s.Id, ["effect_list"] = s.Effects.Select(e => (object)new Dictionary<string, object?> { ["id"] = (uint)e[0], ["level"] = (uint)e[1] }).ToArray()
    }).ToArray();

    private static object Effects(List<int[]> effects) => new Dictionary<string, object?>
    { ["effect_list"] = effects.Select(e => (object)new Dictionary<string, object?> { ["id"] = (uint)e[0], ["level"] = (uint)e[1] }).ToArray() };

    private List<int[]> Roll(int pool)
    {
        Require(Cfg.GetProperty("pools").TryGetProperty(pool.ToString(), out var entries), "Unknown enchant pool");
        var list = entries.EnumerateArray().ToList();
        var total = list.Sum(x => x.GetProperty("w").GetInt64());
        var pick = Random.Shared.NextInt64(total);
        foreach (var x in list)
        {
            pick -= x.GetProperty("w").GetInt64();
            if (pick < 0) return x.GetProperty("s").EnumerateArray().Select(p => new[] { p[0].GetInt32(), p[1].GetInt32() }).ToList();
        }
        return [];
    }

    private int RelativePool(int pool) => Setting("equip_skill_relative_pools").EnumerateArray().Where(p => p[0].GetInt32() == pool).Select(p => p[1].GetInt32()).FirstOrDefault(100 + pool);

    /// <summary>Slot ids open at the sigil's level: normal slots by slot_open_level, exclusive slots (race = owned hero) by hero_slot_open_level.</summary>
    private bool SlotOpen(SigilRow s, int slot)
    {
        if (Equip(s.Prefab) is not { } e) return false;
        var lv = Level(s.Prefab, s.Exp, s.Break);
        var normal = I(e, "slot_num");
        var open = L(e.GetProperty("slot_open_level"));
        if (slot >= 1 && slot <= normal) return slot - 1 >= open.Count || lv >= open[slot - 1];
        var hero = I(e, "hero_slot_num");
        var hopen = L(e.GetProperty("hero_slot_open_level"));
        var k = slot - normal;
        return s.Race > 1000 && k >= 1 && k <= hero && (k - 1 >= hopen.Count || lv >= hopen[k - 1]);
    }

    // ---------- proposals ----------
    public sealed class Proposal { public uint Id { get; set; } public string Name { get; set; } = ""; public Dictionary<uint, long> Equips { get; set; } = []; }
    public sealed class Extra { public Dictionary<uint, Proposal> Proposals { get; set; } = []; public List<uint> AutoDecompose { get; set; } = []; }
    public async Task<Extra> ExtraAsync(long uid, CancellationToken ct) => await accounts.GetJsonAsync<Extra>(uid, "sigil_extra_v1", ct) ?? new Extra();

    public static object[] ProposalRecs(Extra x) => x.Proposals.Values.OrderBy(p => p.Id).Select(p => (object)new Dictionary<string, object?>
    {
        ["id"] = p.Id, ["name"] = p.Name, ["equip"] = p.Equips.OrderBy(e => e.Key).Select(e => (object)new Dictionary<string, object?> { ["pos"] = e.Key, ["equip_id"] = (uint)e.Value }).ToArray()
    }).ToArray();

    public static object EquipRec(SigilRow s) => new Dictionary<string, object?>
    {
        ["equip_id"] = (uint)s.Id, ["prefab_id"] = (uint)s.Prefab, ["exp"] = (uint)s.Exp, ["hero_id"] = (uint)s.HeroId, ["is_lock"] = s.Lock,
        ["now_break_level"] = (uint)s.Break, ["enchant_slot_list"] = SlotRecs(s.Enchant), ["race"] = (uint)s.Race, ["race_preview"] = (uint)s.RacePreview
    };

    public async Task<(uint, byte[])> AutoDecomposeFrameAsync(long uid, CancellationToken ct) =>
        (13057, codec.Encode("sc_13057", new Dictionary<string, object?> { ["type_list"] = (await ExtraAsync(uid, ct)).AutoDecompose.OrderBy(x => x).ToArray() }));

    // ---------- operations ----------
    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var b = r.Body;
        var srv = (ushort)(r.ServerIndex + 1);
        uint U(string k) => HandlerHelpers.ReadUInt(b, k);
        var reply = new Dictionary<string, object?> { ["result"] = 0u };
        var updates = new List<SigilRow>();
        var deletes = new List<long>();
        var costs = new List<(long, long)>();
        var grants = new List<(long, long)>();
        var heroes = new HashSet<long>();
        Extra? extra = null;
        try
        {
            var all = (await accounts.GetSigilsAsync(uid, ct)).ToDictionary(s => s.Id);
            var owned = (await accounts.GetOwnedHeroIdsAsync(uid, ct)).ToHashSet();
            SigilRow Get(long id) { Require(all.TryGetValue(id, out var s), "Sigil not found"); return updates.LastOrDefault(u => u.Id == id) ?? s!; }
            void Put(SigilRow s) { updates.RemoveAll(u => u.Id == s.Id); updates.Add(s); if (s.HeroId != 0) heroes.Add(s.HeroId); }

            void Wear(long hero, SigilRow s)
            {
                var pos = Pos(s.Prefab);
                foreach (var o in all.Values.Select(x => updates.FirstOrDefault(u => u.Id == x.Id) ?? x).Where(x => x.HeroId == hero && x.Id != s.Id && Pos(x.Prefab) == pos).ToList())
                    Put(o with { HeroId = 0 });
                if (s.HeroId != 0 && s.HeroId != hero) heroes.Add(s.HeroId);
                Put(s with { HeroId = hero });
            }

            (long Absorbed, List<(long, long)> Refund) AddExp(SigilRow s, long add, int targetBreak, int targetLevel)
            {
                var e = Equip(s.Prefab)!.Value;
                var cap = ExpTo(Variant(e), Math.Min(targetLevel, MaxLevel(e, targetBreak)));
                var absorbed = Math.Max(0, Math.Min(add, cap - s.Exp));
                return (absorbed, GiveBack(add - absorbed));
            }

            long Feed(long target, IReadOnlyList<long> fodders, IReadOnlyList<(long ItemId, long Count)> mats)
            {
                long add = 0;
                foreach (var (item, n) in mats)
                {
                    Require(ExpItems.TryGetValue(item, out var val) && n > 0, "Not a sigil exp item");
                    costs.Add((item, n));
                    add += val * n;
                }
                foreach (var f in fodders.Distinct())
                {
                    Require(f != target, "Cannot feed a sigil to itself");
                    var s = Get(f);
                    Require(!s.Lock && s.HeroId == 0, "Fodder sigil locked or worn");
                    add += FodderExp(s);
                    deletes.Add(f);
                }
                return add;
            }

            switch (cmd)
            {
                case 13012:
                    {
                        var hero = (long)U("hero_id");
                        var s = Get(U("equip_id"));
                        Require(owned.Contains(hero) && (U("pos") == 0 || U("pos") == Pos(s.Prefab)), "Bad slot");
                        Wear(hero, s);
                        break;
                    }
                case 13018:
                    {
                        var hero = (long)U("hero_id");
                        foreach (var s in all.Values.Where(x => x.HeroId == hero)) Put(s with { HeroId = 0 });
                        heroes.Add(hero);
                        break;
                    }
                case 13026:
                    {
                        var hero = (long)U("hero_id");
                        Require(owned.Contains(hero), "Hero not owned");
                        var results = new List<object>();
                        foreach (var u in Msgs(b, "use_equip_list"))
                        {
                            var id = (long)HandlerHelpers.ReadUInt(u, "equip_id");
                            var ok = all.TryGetValue(id, out var s) && (HandlerHelpers.ReadUInt(u, "pos") is var p && (p == 0 || p == Pos(s!.Prefab)));
                            if (ok) Wear(hero, Get(id));
                            results.Add(new Dictionary<string, object?> { ["equip_id"] = (uint)id, ["result"] = ok ? 0u : 2u });
                        }
                        heroes.Add(hero);
                        reply = new Dictionary<string, object?> { ["result"] = results.ToArray() };
                        break;
                    }
                case 13016:
                    {
                        var s = Get(U("equip_id"));
                        Put(s with { Lock = U("is_lock") != 0 });
                        break;
                    }
                case 13014:
                    {
                        var s = Get(U("equip_id"));
                        Require(Equip(s.Prefab) is { } e, "Unknown sigil");
                        var add = Feed(s.Id, HandlerHelpers.ReadIdList(b, "equip_list"), HandlerHelpers.ReadItemList(b, "mat_list"));
                        Require(add > 0, "No exp");
                        var (absorbed, refund) = AddExp(s, add, s.Break, int.MaxValue);
                        Require(absorbed > 0, "Level cap reached, break through first");
                        costs.Add((2, absorbed * L(Setting("equip_strengthen_gold_cost")).FirstOrDefault(1)));
                        grants.AddRange(refund);
                        Put(s with { Exp = s.Exp + absorbed });
                        reply["mat_list"] = HandlerHelpers.ToItemNetRecList(refund);
                        break;
                    }
                case 13022:
                    {
                        var s = Get(U("equip_id"));
                        Require(Equip(s.Prefab) is { } e0, "Unknown sigil");
                        var e = Equip(s.Prefab)!.Value;
                        Require(s.Break < I(e, "break_times_max"), "Break limit reached");
                        Require(s.Exp >= ExpTo(Variant(e), MaxLevel(e, s.Break)), "Sigil not at level cap");
                        costs.AddRange(Material(L(e.GetProperty("break_cost"))[s.Break]));
                        Put(s with { Break = s.Break + 1 });
                        break;
                    }
                case 13058:
                    {
                        var s = Get(U("equip_id"));
                        Require(Equip(s.Prefab) is { } e0, "Unknown sigil");
                        var e = Equip(s.Prefab)!.Value;
                        var times = (int)U("break_times");
                        var final = s.Break + times;
                        Require(final <= I(e, "break_times_max"), "Break limit");
                        var target = U("target_level") is var t and > 0 ? (int)t : MaxLevel(e, final);
                        Require(target >= 1 && target <= MaxLevel(e, final), "Bad target level");
                        var add = Feed(s.Id, HandlerHelpers.ReadIdList(b, "equip_list"), HandlerHelpers.ReadItemList(b, "mat_list"));
                        var (absorbed, refund) = AddExp(s, add, final, target);
                        var exp = s.Exp + absorbed;
                        var breaks = L(e.GetProperty("break_cost"));
                        for (var step = s.Break; step < final; step++)
                        {
                            Require(exp >= ExpTo(Variant(e), MaxLevel(e, step)), "Not enough exp for the breakthroughs");
                            costs.AddRange(Material(breaks[step]));
                        }
                        costs.Add((2, absorbed * L(Setting("equip_strengthen_gold_cost")).FirstOrDefault(1)));
                        grants.AddRange(refund);
                        Put(s with { Exp = exp, Break = final });
                        reply["mat_list"] = HandlerHelpers.ToItemNetRecList(refund);
                        break;
                    }
                case 13024:
                    {
                        var ids = HandlerHelpers.ReadIdList(b, "equip_id_list").Distinct().ToList();
                        Require(ids.Count > 0, "Nothing to resolve");
                        long exp = 0;
                        var stars = L(Setting("base_exp_equip_star"));
                        var breakReturn = Setting("equip_break_cost_return");
                        var resolve = Setting("equip_resolve_num");
                        foreach (var id in ids)
                        {
                            var s = Get(id);
                            Require(!s.Lock && s.HeroId == 0, "Sigil locked or worn");
                            var star = Star(s.Prefab);
                            exp += s.Exp * 8 / 10 + (star >= 1 && star <= stars.Count ? stars[star - 1] : 0);
                            if (star >= 1 && star <= breakReturn.GetArrayLength())
                                foreach (var m in L(breakReturn[star - 1]).Take(s.Break)) grants.AddRange(Material(m).Where(x => x.Item1 != 2));
                            if (star >= 5 && star - 5 < resolve.GetArrayLength()) grants.Add((resolve[star - 5][0].GetInt64(), resolve[star - 5][1].GetInt64()));
                            deletes.Add(id);
                        }
                        grants.AddRange(GiveBack(exp));
                        reply["return_mat_list"] = HandlerHelpers.ToItemNetRecList(grants.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))));
                        break;
                    }
                case 13028:
                    {
                        var s = Get(U("equip_id"));
                        var slotId = (int)U("enchant_slot_id");
                        var pool = (int)U("pool_id");
                        var lockType = (int)U("lock_type");
                        Require(SlotOpen(s, slotId), "Enchant slot locked");
                        var slots = Slots(s.Enchant);
                        var slot = slots.FirstOrDefault(x => x.Id == slotId) ?? new Slot { Id = slotId };
                        if (!slots.Contains(slot)) slots.Add(slot);
                        Require(slot.Previews.Count < L(Setting("equip_enchant_save_num")).FirstOrDefault(5), "Preview list full");
                        List<int[]> rolled;
                        if (lockType is 1 or 2)
                        {
                            Require(slot.Effects.Count >= 2, "Nothing to lock");
                            costs.AddRange(Material(L(Setting("equip_enchant_lock_cost"))[0]));
                            var keep = slot.Effects[lockType - 1];
                            var rel = RelativePool(pool);
                            List<int[]> other;
                            var tries = 0;
                            do other = Roll(rel); while (other.Count > 0 && other[0][0] == keep[0] && ++tries < 20);
                            rolled = lockType == 1 ? [keep, other[0]] : [other[0], keep];
                        }
                        else
                        {
                            var cost = L(Setting("equip_enchant_cost"));
                            Require(pool >= 1 && pool <= cost.Count, "Bad pool");
                            costs.AddRange(Material(cost[pool - 1]));
                            rolled = Roll(pool);
                        }
                        slot.Previews.Add(rolled);
                        Put(s with { Enchant = Save(slots) });
                        reply["enchant_preview"] = Effects(rolled);
                        break;
                    }
                case 13030:
                    {
                        var s = Get(U("equip_id"));
                        var slots = Slots(s.Enchant);
                        var slot = slots.FirstOrDefault(x => x.Id == (int)U("enchant_slot_id"));
                        var idx = (int)U("preview_index") - 1;
                        Require(slot is not null && idx >= 0 && idx < slot.Previews.Count, "Preview expired");
                        var chosen = slot!.Previews[idx];
                        slot.Previews.RemoveAt(idx);
                        if (b.TryGetValue("confirm", out var c) && c is true) { slot.Effects = chosen; slot.Previews.Clear(); }
                        Put(s with { Enchant = Save(slots) });
                        break;
                    }
                case 13044:
                    {
                        var s = Get(U("equip_id"));
                        var slots = Slots(s.Enchant);
                        foreach (var slot in slots.Where(x => x.Id == (int)U("enchant_slot_id"))) slot.Previews.Clear();
                        Put(s with { Enchant = Save(slots) });
                        break;
                    }
                case 13060:
                    {
                        var s = Get(U("equip_id"));
                        var slotId = (int)U("enchant_slot_id");
                        var seq = (int)U("seq");
                        var skill = (int)U("id");
                        Require(SlotOpen(s, slotId) && seq is 1 or 2, "Bad slot");
                        Require(Cfg.GetProperty("skills").TryGetProperty(skill.ToString(), out _), "Unknown enchant skill");
                        var slots = Slots(s.Enchant);
                        var slot = slots.FirstOrDefault(x => x.Id == slotId) ?? new Slot { Id = slotId };
                        if (!slots.Contains(slot)) slots.Add(slot);
                        Require(slot.Effects.All(e => e[0] != skill), "Skill already in the slot");
                        costs.AddRange(Material(L(Setting("equip_enchant_directional_cost"))[0]));
                        if (seq - 1 < slot.Effects.Count) slot.Effects[seq - 1] = [skill, 1]; else slot.Effects.Add([skill, 1]);
                        Put(s with { Enchant = Save(slots) });
                        break;
                    }
                case 13032:
                    {
                        var s = Get(U("equip_id"));
                        Require(Star(s.Prefab) >= 5, "Only 5-star sigils can be reconstructed");
                        costs.AddRange(Material(L(Setting("equip_reset_cost"))[0]));
                        var races = new[] { 1, 2, 3, 4, 5, 9 }.Where(x => x != s.Race).ToArray();
                        var preview = races[Random.Shared.Next(races.Length)];
                        Put(s with { RacePreview = preview });
                        reply["race_preview"] = (uint)preview;
                        break;
                    }
                case 13034:
                    {
                        var s = Get(U("equip_id"));
                        var confirm = b.TryGetValue("confirm", out var c) && c is true;
                        Require(!confirm || s.RacePreview != 0, "No pending race");
                        Put(s with { Race = confirm ? s.RacePreview : s.Race, RacePreview = 0 });
                        break;
                    }
                case 13046:
                    {
                        var s = Get(U("equip_id"));
                        var hero = (long)U("hero_id");
                        Require(owned.Contains(hero) && Star(s.Prefab) >= 5, "Bad exclusive reconstruction");
                        costs.AddRange(Material(L(Setting("equip_hero_reset_cost"))[0]));
                        Put(s with { Race = (int)hero, RacePreview = 0 });
                        break;
                    }
                case 13052:
                    {
                        var s = Get(U("new_equip_id"));
                        var target = (int)U("inherit_equip_prefab_id");
                        Require(Equip(target) is { } te && Equip(s.Prefab) is { } se, "Unknown sigil");
                        var tcfg = Equip(target)!.Value;
                        Require(Star(s.Prefab) >= 5 && I(tcfg, "starlevel") >= 5 && Pos(target) == Pos(s.Prefab) && target != s.Prefab, "Cannot inherit");
                        var costIds = L(Setting("equip_inherit_cost"));
                        var type = Cfg.GetProperty("suits").TryGetProperty(I(tcfg, "suit").ToString(), out var ty) ? ty.GetInt32() : 1;
                        costs.AddRange(Material(costIds[type == 1 ? 0 : 1]));
                        Put(s with { Prefab = target });
                        break;
                    }
                case 13054:
                    {
                        var type = U("type");
                        Require(type is 1 or 2, "Bad decompose type");
                        extra = await ExtraAsync(uid, ct);
                        extra.AutoDecompose.Remove(type);
                        if (U("sign") == 1) extra.AutoDecompose.Add(type);
                        break;
                    }
                case 13036:
                case 13038:
                case 13042:
                    {
                        extra = await ExtraAsync(uid, ct);
                        var name = HandlerHelpers.ReadString(b, "proposal_name");
                        Require(name.Length is > 0 and <= 32, "Bad name");
                        Dictionary<uint, long> set;
                        if (cmd == 13042)
                            set = Msgs(b, "equip").ToDictionary(x => HandlerHelpers.ReadUInt(x, "pos"), x => (long)HandlerHelpers.ReadUInt(x, "equip_id"));
                        else
                        {
                            var hero = (long)U("hero_id");
                            Require(owned.Contains(hero), "Hero not owned");
                            set = all.Values.Where(x => x.HeroId == hero).ToDictionary(x => (uint)Pos(x.Prefab), x => x.Id);
                        }
                        Require(set.All(x => all.ContainsKey(x.Value) && Pos(all[x.Value].Prefab) == x.Key), "Bad sigil set");
                        uint id;
                        if (cmd == 13038) { id = U("proposal_id"); Require(extra.Proposals.ContainsKey(id), "Proposal not found"); }
                        else
                        {
                            Require(extra.Proposals.Count < L(Setting("equip_proposal_num_max")).FirstOrDefault(20), "Too many proposals");
                            id = Enumerable.Range(1, 1000).Select(x => (uint)x).First(x => !extra.Proposals.ContainsKey(x));
                        }
                        extra.Proposals[id] = new Proposal { Id = id, Name = name, Equips = set };
                        if (cmd != 13038) reply["proposal_id"] = id;
                        break;
                    }
                case 13040:
                    extra = await ExtraAsync(uid, ct);
                    Require(extra.Proposals.Remove(U("proposal_id")), "Proposal not found");
                    break;
            }
        }
        catch (ModeError)
        {
            return [cmd == 13026
                ? HandlerHelpers.Reply(codec, r, 13027, new Dictionary<string, object?> { ["result"] = Array.Empty<object>() })
                : HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)];
        }

        var c2 = costs.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).Where(x => x.Item2 > 0).ToList();
        var g2 = grants.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).Where(x => x.Item2 > 0).ToList();
        if ((updates.Count > 0 || deletes.Count > 0 || c2.Count > 0 || g2.Count > 0) && !await accounts.CommitSigilsAsync(uid, updates, deletes, c2, g2, ct))
            return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)];
        if (extra is not null) await accounts.SetJsonAsync(uid, "sigil_extra_v1", extra, ct);

        var frames = await InventorySync.FramesAsync(accounts, codec, r, c2.Concat(g2).Select(x => x.Item1), ct);
        // Item-like sigil changes: deletions (num 0) and enchant / race / prefab changes (exp is tracked by the client itself).
        var changed = updates.Where(u => cmd is 13028 or 13030 or 13044 or 13060 or 13032 or 13034 or 13046)
            .Select(u => (object)new Dictionary<string, object?> { ["equip_id"] = (uint)u.Id, ["prefab_id"] = (uint)u.Prefab, ["num"] = 1u, ["enchant_slot_list"] = SlotRecs(u.Enchant), ["race"] = (uint)u.Race })
            .Concat(deletes.Select(d => (object)new Dictionary<string, object?> { ["equip_id"] = (uint)d, ["prefab_id"] = 0u, ["num"] = 0u, ["enchant_slot_list"] = Array.Empty<object>(), ["race"] = 0u })).ToArray();
        if (changed.Length > 0)
            frames.Add(new GameResponse(17023, 0, srv, codec.Encode("sc_17023", new Dictionary<string, object?> { ["equip_list"] = changed })));
        if (cmd == 13052 && updates.Count > 0)
        {
            // EquipData:AddEquip keeps old prefab index unless removed first via sc_17023(num=0), then restored via sc_13009.
            var inherited = updates[0];
            frames.Add(new GameResponse(17023, 0, srv, codec.Encode("sc_17023", new Dictionary<string, object?>
            {
                ["equip_list"] = new object[] { new Dictionary<string, object?> { ["equip_id"] = (uint)inherited.Id, ["prefab_id"] = (uint)inherited.Prefab, ["num"] = 0u, ["enchant_slot_list"] = Array.Empty<object>(), ["race"] = 0u } }
            })));
            frames.Add(new GameResponse(13009, 0, srv, codec.Encode("sc_13009", new Dictionary<string, object?> { ["equip_list"] = new[] { EquipRec(inherited) }, ["is_init"] = 0u })));
        }
        var ackFirst = cmd is 13012 or 13018 or 13026;
        if (ackFirst) frames.Add(HandlerHelpers.Reply(codec, r, cmd + 1, reply));
        foreach (var hero in heroes)
            if (await LoginBootstrap.BuildSingleHeroNetRecAsync(accounts, uid, hero, ct) is { } rec)
                frames.Add(new GameResponse(14007, 0, srv, codec.Encode("sc_14007", new Dictionary<string, object?> { ["hero"] = rec })));
        if (cmd == 13054) frames.Add(new GameResponse(13057, 0, srv, (await AutoDecomposeFrameAsync(uid, ct)).Item2));
        var achCond = cmd switch { 13028 or 13060 => 204, 13032 => 205, 13046 => 211, _ => 0 };
        if (achCond != 0 && await new AchievementSystem(accounts, config, codec).EventAsync(uid, srv, achCond, 1, ct) is { } ach) frames.Add(ach);
        if (!ackFirst) frames.Add(HandlerHelpers.Reply(codec, r, cmd + 1, reply));
        return frames;
    }

    private static IEnumerable<IReadOnlyDictionary<string, object?>> Msgs(IReadOnlyDictionary<string, object?> b, string k) =>
        b.TryGetValue(k, out var v) && v is System.Collections.IEnumerable e && v is not string ? e.OfType<IReadOnlyDictionary<string, object?>>() : [];

    public static readonly uint[] Commands =
        [13012, 13014, 13016, 13018, 13022, 13024, 13026, 13028, 13030, 13032, 13034, 13036, 13038, 13040, 13042, 13044, 13046, 13052, 13054, 13058, 13060];
}
