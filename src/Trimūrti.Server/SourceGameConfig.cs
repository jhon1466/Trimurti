using System.Text.Json;

namespace Trimūrti.Server;

/// <summary>Read-only access to source game configuration JSON, cached after first access.</summary>
public sealed class SourceGameConfig
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "Data", "Configs");
    // Shared by every instance: the configs are read-only, so each file is parsed once per process.
    private static readonly Dictionary<string, JsonDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _sync = new();

    public int AvailableFiles => Directory.Exists(_directory) ? Directory.EnumerateFiles(_directory, "*.json", SearchOption.AllDirectories).Count() : 0;

    public JsonElement? Get(string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        lock (_sync)
        {
            if (!_documents.TryGetValue(safeName, out var document))
            {
                var path = Path.Combine(_directory, safeName);
                if (!File.Exists(path)) return null;
                document = JsonDocument.Parse(File.ReadAllBytes(path));
                _documents.Add(safeName, document);
            }
            // Documents stay cached for the whole process, so the root element can be shared without a copy.
            return document.RootElement;
        }
    }

    public IReadOnlyList<(long ItemId, long Count)> GetSevenDaySignRewards(long activityId, int signNumber)
    {
        if (signNumber < 1) return [];
        var activityConfig = Get("activity_cumulative_sign_cfg.json");
        var signConfig = Get("sign_cfg.json");
        if (activityConfig is not { ValueKind: JsonValueKind.Object } activities ||
            signConfig is not { ValueKind: JsonValueKind.Object } rewards ||
            !activities.TryGetProperty(activityId.ToString(), out var activity) ||
            !activity.TryGetProperty("config_list", out var configList) ||
            configList.ValueKind != JsonValueKind.Array || configList.GetArrayLength() < signNumber)
            return [];

        var rewardKey = configList[signNumber - 1].ToString();
        if (!rewards.TryGetProperty(rewardKey, out var reward) || !reward.TryGetProperty("reward", out var rewardList) || rewardList.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<(long ItemId, long Count)>();
        foreach (var item in rewardList.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 2) continue;
            if (item[0].TryGetInt64(out var itemId) && item[1].TryGetInt64(out var count) && itemId > 0 && count > 0)
                result.Add((itemId, count));
        }
        return result;
    }

    public IReadOnlyList<(long ItemId, long Count)> GetStageFirstClearRewards(long stageId)
    {
        var cfg = Get("stage_first_clear_rewards.json");
        if (cfg is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(stageId.ToString(), out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        return ParsePairList(arr);
    }

    public (long UpS, IReadOnlyList<long> UpA) GetDrawPoolUp(int poolId)
    {
        var cfg = Get("pool_up_config.json");
        if (cfg is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(poolId.ToString(), out var pool) || pool.ValueKind != JsonValueKind.Object)
            return (0, []);
        var upS = pool.TryGetProperty("up_s", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
        var upA = new List<long>();
        if (pool.TryGetProperty("up_a", out var a) && a.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in a.EnumerateArray())
                if (elem.TryGetInt64(out var av) && av > 0) upA.Add(av);
        }
        return (upS, upA);
    }

    public IReadOnlyList<long> GetAllHeroIds()
    {
        var cfg = Get("heroes_catalog.json");
        if (cfg is not { ValueKind: JsonValueKind.Array } arr) return [1084, 1011, 1127, 1199];
        var ids = new List<long>();
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id) && id > 0)
                ids.Add(id);
        return ids.Count > 0 ? ids : [1084, 1011, 1127, 1199];
    }

    public IReadOnlyList<int> GetHeroSkills(long heroId)
    {
        var cfg = Get("hero_skills_cfg.json");
        if (cfg is { ValueKind: JsonValueKind.Object } root &&
            root.TryGetProperty(heroId.ToString(), out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            var skills = new List<int>();
            foreach (var el in arr.EnumerateArray())
                if (el.TryGetInt32(out var sid) && sid > 0) skills.Add(sid);
            if (skills.Count > 0) return skills;
        }
        var b = (int)heroId * 1000;
        return [b + 101, b + 201, b + 202, b + 203, b + 209, b + 305];
    }

    public IReadOnlyDictionary<long, long> GetSkinHeroMap()
    {
        var result = new Dictionary<long, long>();
        if (Get("skin_acquisition_cfg.json") is { ValueKind: JsonValueKind.Object } acqRoot)
        {
            foreach (var prop in acqRoot.EnumerateObject())
            {
                if (long.TryParse(prop.Name, out var skinId) &&
                    prop.Value.ValueKind == JsonValueKind.Object &&
                    prop.Value.TryGetProperty("hero_id", out var hidEl) &&
                    hidEl.TryGetInt64(out var heroId) &&
                    skinId > 0 && heroId is > 1001 and < 2000)
                {
                    result[skinId] = heroId;
                }
            }
        }
        if (Get("all_skins_data.json") is { ValueKind: JsonValueKind.Array } arr)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("sid", out var sidEl) && sidEl.TryGetInt64(out var skinId) &&
                    item.TryGetProperty("hero_id", out var hidEl) && hidEl.TryGetInt64(out var heroId) &&
                    skinId is >= 100000 and < 200000 && heroId is > 1001 and < 2000)
                {
                    result[skinId] = heroId;
                }
            }
        }
        return result;
    }

    /// <summary>Cumulative experience needed to reach the max player level (exp of levels 1..max-1).
    /// Sending more than this makes the profile view look up a level beyond the table and crash.</summary>
    public long GetMaxUserTotalExp()
    {
        if (Get("user_level_setting.json") is not { ValueKind: JsonValueKind.Object } cfg) return 295_372;
        var levels = cfg.EnumerateObject()
            .Select(p => (Level: int.TryParse(p.Name, out var l) ? l : 0, Exp: p.Value.TryGetInt64(out var e) ? e : 0))
            .Where(x => x.Level > 0).ToList();
        if (levels.Count == 0) return 295_372;
        var max = levels.Max(x => x.Level);
        return levels.Where(x => x.Level < max).Sum(x => x.Exp);
    }

    public IReadOnlyList<uint> GetAllSceneIds()
    {
        var ids = new SortedSet<uint> { 6000u, 6100u };
        if (Get("decorations_catalog.json") is { ValueKind: JsonValueKind.Object } dec &&
            dec.TryGetProperty("SCENE", out var scenes) && scenes.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in scenes.EnumerateObject())
            {
                var label = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : "";
                if (uint.TryParse(prop.Name, out var id) && id is >= 6000 and < 10000 && !label.Contains("天）"))
                    ids.Add(id);
            }
        }
        var sceneToSkin = new Dictionary<uint, string>();
        if (Get("skin_scene_map.json") is { ValueKind: JsonValueKind.Object } ssm)
        {
            foreach (var prop in ssm.EnumerateObject())
            {
                if (prop.Value.TryGetUInt32(out var sceneId) && sceneId is >= 6000 and < 10000)
                {
                    ids.Add(sceneId);
                    sceneToSkin[sceneId] = prop.Name;
                }
            }
        }

        return ids.ToArray();
    }

    public IReadOnlyList<uint> GetDecorationIds(string category)
    {
        var ids = new SortedSet<uint>();
        if (Get("decorations_catalog.json") is { ValueKind: JsonValueKind.Object } dec &&
            dec.TryGetProperty(category, out var group) && group.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in group.EnumerateObject())
                if (uint.TryParse(prop.Name, out var id) && id > 0)
                    ids.Add(id);
        }
        return ids.ToArray();
    }

    public IReadOnlyList<int> GetAllFiveStarEquipPrefabs()
    {
        var bySuitPos = new Dictionary<(int Suit, int Pos), int>();
        if (Get("equip_suit_pos_map.json") is { ValueKind: JsonValueKind.Object } eq)
        {
            foreach (var prop in eq.EnumerateObject())
            {
                if (!prop.Name.StartsWith('5') || !int.TryParse(prop.Name, out var prefabId)) continue;
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var suit = prop.Value.TryGetProperty("suit", out var s) && s.TryGetInt32(out var sv) ? sv : 0;
                var pos = prop.Value.TryGetProperty("pos", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
                if (suit <= 0 || pos <= 0) continue;
                if (!bySuitPos.TryGetValue((suit, pos), out var cur) || prefabId < cur)
                    bySuitPos[(suit, pos)] = prefabId;
            }
        }
        return bySuitPos.Values.Order().ToArray();
    }

    public IReadOnlyList<int> GetAllWeaponServantIds()
    {
        var ids = new SortedSet<int>();
        if (Get("weapon_servants_catalog.json") is { ValueKind: JsonValueKind.Object } ws)
        {
            foreach (var prop in ws.EnumerateObject())
                if (int.TryParse(prop.Name, out var id) && id > 0)
                    ids.Add(id);
        }
        return ids.ToArray();
    }

    public static readonly IReadOnlySet<long> ValidCurrencyIds = new HashSet<long>
    {
        1, 2, 4, 5, 6, 12, 14, 16, 17, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 47, 48,
        53, 54, 55, 56, 58, 60, 61, 101, 53029, 53030, 53111
    };

    /// <summary>Client ItemCfg type of every stackable item (item_types.json): 1 currency, 4 props, 5 gift/pack, 6 material,
    /// 10 archive gift, 16 canteen ingredient, 17 club.</summary>
    public IReadOnlyDictionary<long, int> ItemTypes => _itemTypes ??= LoadItemTypes();
    private Dictionary<long, int>? _itemTypes;

    private Dictionary<long, int> LoadItemTypes()
    {
        var map = new Dictionary<long, int>();
        if (Get("item_types.json") is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("types", out var types))
            foreach (var p in types.EnumerateObject())
                if (long.TryParse(p.Name, out var id) && p.Value.TryGetInt32(out var t)) map[id] = t;
        return map;
    }

    /// <summary>Currency in the client's sense (ItemCfg type 1), stored in the currency table.</summary>
    public bool IsCurrencyItem(long id) => ValidCurrencyIds.Contains(id) || ItemTypes.TryGetValue(id, out var t) && t == 1;

    /// <summary>
    /// Every stackable item the client knows (ItemCfg types 4/5/6/10/16/17), plus the server-managed currencies and the
    /// trust gifts. Used by the login inventory (17009) and the GM "grant all items". Other type-1 currencies are left out of
    /// "grant all" on purpose: several of them (vitality, event tickets…) are driven by their own systems.
    /// </summary>
    public IReadOnlyList<long> GetAllItemIds()
    {
        var ids = new HashSet<long>(ValidCurrencyIds);
        foreach (var (id, type) in ItemTypes)
            if (type != 1) ids.Add(id);
        ids.UnionWith(new long[]
        {
            // Functor / Servant awakening & selector materials (verified in 15:50 working session)
            10001, 10002, 10003, 10004, 10005, 10006, 10007, 10008, 10009, 10010,
            20001, 20002, 20003, 20007, 20008, 20009, 20010, 20011, 20012,
            20022, 20023, 20024, 20025, 20026, 20027, 20028,
            22001, 22002, 22003, 22004,
            70000, 70001, 70002, 70003, 70004, 70005, 70006, 70008,
            // Stamina / Coolant tonics
            30011, 30012, 30013, 30014, 30015, 35003, 35004,
            // Core Hero EXP, Weapon EXP, Sigil EXP, Skill, Break & Reconstruct materials
            40101, 40102, 40103, 40104,
            40201, 40202, 40203,
            40301,
            40411, 40412, 40413, 40414, 40415,
            40501, 40502, 40503, 40504,
            40602, 40603, 40604,
            40701,
            40801, 40802, 40803,
            // Warp (跃迁) & Synchro (同调) & Omega growth materials
            41002, 41201, 41301, 41302, 41303, 41401, 41501, 41601, 41701,
            // Gacha / Skin / Event vouchers
            53014, 53015, 53193
        });
        if (Get("trust_item_cfg.json") is { ValueKind: JsonValueKind.Object } trust &&
            trust.TryGetProperty("items", out var trustItems) && trustItems.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in trustItems.EnumerateObject())
                if (long.TryParse(prop.Name, out var id) && id > 0) ids.Add(id);
        }
        return ids.Order().ToArray();
    }

    public (long GiveId, long GiveNum, long CostId, long Cost)? GetShopGoods(int shopId, long goodsId)
    {
        var cfg = Get("daily_shop_pool.json");
        if (cfg is not { ValueKind: JsonValueKind.Object } root) return null;
        if (root.TryGetProperty(shopId.ToString(), out var pool) && pool.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in pool.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("goods_id", out var gid) && gid.TryGetInt64(out var gv) && gv == goodsId)
                {
                    var giveId = item.TryGetProperty("give_id", out var gi) && gi.TryGetInt64(out var giv) ? giv : 0;
                    var giveNum = item.TryGetProperty("give_num", out var gn) && gn.TryGetInt64(out var gnv) ? gnv : 1;
                    var costId = item.TryGetProperty("cost_id", out var ci) && ci.TryGetInt64(out var civ) ? civ : 2;
                    var cost = item.TryGetProperty("cost", out var c) && c.TryGetInt64(out var cv) ? cv : 0;
                    var cheap = item.TryGetProperty("cheap_cost", out var cc) && cc.TryGetInt64(out var ccv) ? ccv : 0;
                    return (giveId, giveNum, costId, cost > 0 ? cost : cheap);
                }
            }
        }
        return null;
    }

    private static IReadOnlyList<(long ItemId, long Count)> ParsePairList(JsonElement arr)
    {
        var result = new List<(long ItemId, long Count)>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 2) continue;
            if (item[0].TryGetInt64(out var itemId) && item[1].TryGetInt64(out var count) && itemId > 0 && count > 0)
                result.Add((itemId, count));
        }
        return result;
    }
}
