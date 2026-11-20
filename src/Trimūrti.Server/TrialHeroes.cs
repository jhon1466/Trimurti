using System.Text.Json;

namespace Trimūrti.Server;

/// <summary>
/// Trial / system heroes used by story and tutorial stages (client config HeroStandardSystemCfg +
/// TemplateHeroDataTemplate, exported to Data/Configs/trial_system_data.json). Port of the trial branch of
/// Alpha-Persei battle_payload.build_battle_hero_bytes: builds battle_hero_net_rec with hero_type 2 so the client
/// resolves HeroStandardSystemCfg[base.id].hero_id itself. These are game templates, not account progression.
/// </summary>
public sealed class TrialHeroes(SourceGameConfig config)
{
    private JsonElement Root => config.Get("trial_system_data.json") ?? default;

    public bool IsTrial(long standardId) =>
        Root.ValueKind == JsonValueKind.Object && Root.GetProperty("std_cfgs").TryGetProperty(standardId.ToString(), out _);

    public Dictionary<string, object?>? BuildBattleHero(long standardId)
    {
        if (Root.ValueKind != JsonValueKind.Object || !Root.GetProperty("std_cfgs").TryGetProperty(standardId.ToString(), out var std))
            return null;
        int I(string name, int fallback) => std.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
        var realHero = I("hero_id", (int)standardId);
        var skin = I("skin_id", realHero);
        var heroLevel = I("hero_lv", 80);
        var skillLevel = I("skill_lv", 35);
        var weaponLevel = I("weapon_level", 60);
        var levels = Root.GetProperty("level_cfgs");
        long Exp(int level, string key) => levels.TryGetProperty(level.ToString(), out var l) && l.TryGetProperty(key, out var e) ? e.GetInt64() : 0;

        // Skills (GetSkillTable): attack/skills at skill_lv, dodge ("avoid") skills at level 1.
        var skills = new List<object>();
        if (Root.GetProperty("hero_cfgs").TryGetProperty(realHero.ToString(), out var heroCfg))
        {
            var avoid = heroCfg.TryGetProperty("avoid", out var a) ? a.EnumerateArray().Select(x => x.GetInt64()).ToHashSet() : [];
            foreach (var sid in heroCfg.GetProperty("skills").EnumerateArray().Select(x => x.GetInt64()).Where(x => x > 0))
                skills.Add(new Dictionary<string, object?> { ["skill_id"] = (uint)sid, ["skill_level"] = (uint)(avoid.Contains(sid) ? 1 : skillLevel) });
        }
        if (skills.Count == 0) skills.Add(new Dictionary<string, object?> { ["skill_id"] = (uint)(realHero * 100 + 1), ["skill_level"] = (uint)skillLevel });

        var astro = std.TryGetProperty("astrolabe_id", out var astroArr) ? astroArr.EnumerateArray().Select(x => (uint)x.GetInt64()).Where(x => x > 0).ToArray() : [];

        // Transition exclusive skills (InitTransitionByCfg).
        var exclusive = new List<object>();
        if (std.TryGetProperty("equip_exclusive_id_list", out var exIds) && std.TryGetProperty("equip_exclusive_lv_list", out var exLvs))
        {
            var ids = exIds.EnumerateArray().ToArray();
            var lvs = exLvs.EnumerateArray().ToArray();
            for (var slot = 0; slot < Math.Min(ids.Length, lvs.Length); slot++)
            {
                var pairs = ids[slot].EnumerateArray().Zip(lvs[slot].EnumerateArray(), (i, l) => (Id: i.GetInt64(), Lv: l.GetInt64())).ToArray();
                if (pairs.Length == 0) continue;
                exclusive.Add(new Dictionary<string, object?>
                {
                    ["slot_id"] = (uint)(slot + 1), ["talent_points"] = (uint)pairs.Sum(p => p.Lv),
                    ["skill_list"] = pairs.Select(p => (object)new Dictionary<string, object?> { ["id"] = (uint)p.Id, ["skill_level"] = (uint)p.Lv }).ToArray()
                });
            }
        }
        var elements = std.TryGetProperty("skill_element", out var el)
            ? el.EnumerateArray().Select((v, i) => (Index: i + 1, Level: v.GetInt32())).Where(x => x.Level > 0)
                .Select(x => (object)new Dictionary<string, object?> { ["index"] = (uint)x.Index, ["level"] = (uint)x.Level }).ToArray()
            : [];

        var baseInfo = new Dictionary<string, object?>
        {
            ["id"] = (uint)standardId, ["level"] = (uint)heroLevel, ["star"] = (uint)I("star_lv", 500), ["exp"] = (uint)Exp(heroLevel, "hero_exp"),
            ["skill"] = skills.ToArray(),
            ["weapon"] = new Dictionary<string, object?> { ["exp"] = (uint)Exp(weaponLevel, "weapon_exp"), ["breakthrough"] = (uint)I("weapon_break", 4), ["servant_uid"] = 0u },
            ["servant"] = new Dictionary<string, object?> { ["id"] = (uint)I("weapon_key", 0), ["stage"] = (uint)I("weapon_stage", 1) },
            ["using_astrolabe"] = astro, ["unlock_astrolabe"] = astro,
            ["using_skin"] = (uint)skin, ["break_level"] = (uint)I("hero_break", 6),
            ["exclusive_skill_list"] = exclusive.ToArray(), ["weapon_module_level"] = (uint)I("weapon_module_level", 0),
            ["skill_intensify_attribute_list"] = elements, ["battle_using_skin"] = (uint)skin
        };

        // Virtual sigils (GetConstructVirtualEquips): prefab, level exp by star, enchant pools.
        var equips = new List<object>();
        if (std.TryGetProperty("equip_list", out var eqList))
        {
            var pools = std.TryGetProperty("equip_pool_list", out var pl) ? pl.EnumerateArray().ToArray() : [];
            var equipLevel = I("equip_lv", 60);
            var breakLevel = Math.Max(0, I("break_lv", 1) - 1);
            var idx = 0;
            foreach (var prefabEl in eqList.EnumerateArray())
            {
                var prefab = prefabEl.GetInt64();
                if (prefab > 0)
                {
                    var star = Root.GetProperty("eq_stars").TryGetProperty(prefab.ToString(), out var s) ? s.GetInt32() : 5;
                    var exp = Root.GetProperty("exp_cfgs").TryGetProperty(equipLevel.ToString(), out var ec) && ec.TryGetProperty($"exp_sum_{star}", out var es) ? es.GetInt64() : 0;
                    var slots = new List<object>();
                    if (idx < pools.Length)
                    {
                        var slotNo = 0;
                        foreach (var pool in pools[idx].EnumerateArray())
                        {
                            slotNo++;
                            var effects = Root.GetProperty("pool_cfgs").TryGetProperty(pool.GetInt64().ToString(), out var pc)
                                ? pc.EnumerateArray().Select(p => (object)new Dictionary<string, object?> { ["id"] = (uint)p[0].GetInt64(), ["level"] = (uint)p[1].GetInt64() }).ToArray()
                                : [];
                            if (effects.Length > 0) slots.Add(new Dictionary<string, object?> { ["id"] = (uint)slotNo, ["effect_list"] = effects });
                        }
                    }
                    equips.Add(new Dictionary<string, object?>
                    {
                        ["equip_id"] = (uint)(standardId * 100 + idx + 1), ["prefab_id"] = (uint)prefab, ["exp"] = (uint)exp,
                        ["hero_id"] = (uint)realHero, ["is_lock"] = true, ["now_break_level"] = (uint)breakLevel,
                        ["enchant_slot_list"] = slots.ToArray(), ["race"] = (uint)realHero
                    });
                }
                idx++;
            }
        }

        return new Dictionary<string, object?>
        {
            ["hero_base_info"] = baseInfo,
            ["hero_type"] = 2u,
            ["equip_list"] = equips.ToArray(),
            ["dorm_level"] = 0u,
            ["trust"] = new Dictionary<string, object?>
            {
                ["level"] = 1u, ["exp"] = 0u, ["mood"] = 1u,
                ["relation"] = new Dictionary<string, object?> { ["tier_list"] = new object[] { new Dictionary<string, object?> { ["tier"] = 1u, ["upgrade_complete_list"] = Array.Empty<uint>() } } }
            },
            ["main_damage_type"] = 0u
        };
    }
}
