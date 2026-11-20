using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 关系网 and 连携奥义 (port of Hoshimi-Public hero_affinity.py / hero_training_progress.py) on the client tables
/// HeroRelationNetCfg, HeroRelationUpgradeCfg, HeroRelationUpgradeConditionCfg, HeroRelationCfg, ComboSkillCfg and
/// ComboSkillLevelCfg (→ Data/Configs/relation_cfg.json).
///  - 73016 unlock a relation node: tier ≤ trust level, one of the intimate heroes meets the upgrade condition
///    (1 level, 2 total skill levels, 3 weapon level, 4 star, 5 proficiency, 6 跃迁 talent points); tiers go to hero trust.
///  - 73022 relation story reward once: story tier opened and its ConditionCfg condition met (73007).
///  - 73018 combo level up when every condition of the current level is met: type 1 = both heroes' ultimate level;
///    other types count wins of battles that both combo heroes fought together (73003, 73001 at login).
/// </summary>
public sealed class RelationSystem(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    private JsonElement Cfg => config.Get("relation_cfg.json") ?? throw new ModeError("Missing relation_cfg.json");
    private static void Require(bool ok, string m) { if (!ok) throw new ModeError(m); }
    private static List<long> L(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList() : [];

    public sealed class Combo { public int Level { get; set; } public Dictionary<int, long> Progress { get; set; } = []; }
    public sealed class State
    {
        public Dictionary<long, Dictionary<int, List<int>>> Tiers { get; set; } = [];
        public List<long> Stories { get; set; } = [];
        public Dictionary<int, Combo> Combos { get; set; } = [];
    }

    public async Task<State> LoadAsync(long uid, CancellationToken ct) => await accounts.GetJsonAsync<State>(uid, "relation_v1", ct) ?? new State();

    /// <summary>hero_relation_net_net_rec for one hero.</summary>
    public static object RelationRec(State s, long hero) => new Dictionary<string, object?>
    {
        ["tier_list"] = (s.Tiers.GetValueOrDefault(hero) ?? []).OrderBy(t => t.Key).Select(t => (object)new Dictionary<string, object?>
        { ["tier"] = (uint)t.Key, ["upgrade_complete_list"] = t.Value.Select(x => (uint)x).ToArray() }).ToArray()
    };

    private static object ComboRec(int id, Combo c) => new Dictionary<string, object?>
    {
        ["id"] = (uint)id, ["level"] = (uint)c.Level,
        ["progress_list"] = c.Progress.OrderBy(p => p.Key).Select(p => (object)new Dictionary<string, object?> { ["id"] = (uint)p.Key, ["times"] = (uint)p.Value }).ToArray()
    };

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        return
        [
            (73001, codec.Encode("sc_73001", new Dictionary<string, object?> { ["info"] = new Dictionary<string, object?> { ["skill_list"] = s.Combos.OrderBy(c => c.Key).Select(c => ComboRec(c.Key, c.Value)).ToArray() } })),
            (73007, codec.Encode("sc_73007", new Dictionary<string, object?> { ["got_reward_id_list"] = s.Stories.Select(x => (uint)x).ToArray() }))
        ];
    }

    private long Ultimate(HeroRow h) => h.Skills.FirstOrDefault(x => x[0] % 1000 == 209)?[1] ?? 1;

    private async Task<long> ValueAsync(long uid, HeroRow h, int kind, CancellationToken ct)
    {
        var heroCfg = config.Get("hero_cfg.json")!.Value;
        return kind switch
        {
            1 => h.Level,
            2 => h.Skills.Sum(x => x[0] % 1000 == 305 ? 1 : x[1]),
            3 => Enumerable.Range(1, 100).LastOrDefault(l => heroCfg.GetProperty("curve").TryGetProperty(l.ToString(), out var c) && h.WeaponExp >= c.GetProperty("weapon_sum").GetInt64(), 1),
            4 => h.Star,
            5 => h.ClearTimes,
            6 => ((await accounts.GetHeroWarpAsync(uid, ct)).GetValueOrDefault(h.Id) ?? []).Sum(w => w.TalentPoints),
            _ => 0
        };
    }

    private async Task<bool> StoryConditionAsync(long uid, long id, HeroRow h, CancellationToken ct)
    {
        if (id == 0) return true;
        if (config.Get("condition_cfg.json") is not { } all || !all.TryGetProperty(id.ToString(), out var c)) return false;
        var p = L(c.GetProperty("params"));
        var trust = await accounts.GetTrustAsync(uid, h.Id, ct);
        return c.GetProperty("type").GetInt32() switch
        {
            1001 => h.Level >= p[0],
            1003 => h.Star / 100 >= p[0],
            1004 => h.ClearTimes >= p[0],
            1010 => h.BreakLevel >= p[0],
            1011 or 5002 => (await accounts.GetLobbySnapshotAsync(uid, ct)).Chapters.Any(x => x.Id == p[0] && x.ClearTimes > 0),
            1019 => (trust?.Level ?? 0) >= p[0],
            _ => false
        };
    }

    /// <summary>Battle win: combo conditions of type ≥ 2 count when both combo heroes fought.</summary>
    public async Task<GameResponse?> BattleWinAsync(long uid, ushort srv, IReadOnlyCollection<long> heroes, int times, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        var changed = new List<object>();
        foreach (var combo in Cfg.GetProperty("combos").EnumerateObject())
        {
            var roles = L(combo.Value.GetProperty("roles"));
            if (roles.Count == 0 || !roles.All(heroes.Contains)) continue;
            var id = int.Parse(combo.Name);
            var c = s.Combos.TryGetValue(id, out var x) ? x : s.Combos[id] = new Combo();
            foreach (var lc in Cfg.GetProperty("combo_levels").EnumerateObject().Where(p => p.Value.GetProperty("level").GetInt32() == c.Level && p.Value.GetProperty("type").GetInt32() is 2 or 5))
            {
                var key = int.Parse(lc.Name);
                c.Progress[key] = Math.Min(lc.Value.GetProperty("target").GetInt64(), c.Progress.GetValueOrDefault(key) + times);
            }
            changed.Add(ComboRec(id, c));
        }
        if (changed.Count == 0) return null;
        await accounts.SetJsonAsync(uid, "relation_v1", s, ct);
        return new GameResponse(73003, 0, srv, codec.Encode("sc_73003", new Dictionary<string, object?> { ["skill"] = changed[0] }));
    }

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var b = r.Body;
        var srv = (ushort)(r.ServerIndex + 1);
        var s = await LoadAsync(uid, ct);
        var reply = new Dictionary<string, object?> { ["result"] = 0u };
        var grants = new List<(long, long)>();
        var pushes = new List<GameResponse>();
        long touchedHero = 0;
        try
        {
            switch (cmd)
            {
                case 73016:
                    {
                        var id = HandlerHelpers.ReadUInt(b, "id");
                        var index = (int)HandlerHelpers.ReadUInt(b, "group_index");
                        Require(Cfg.GetProperty("net").TryGetProperty(id.ToString(), out var n), "Unknown relation node");
                        var hero = n.GetProperty("hero").GetInt64();
                        var tier = n.GetProperty("index").GetInt32();
                        var groups = L(n.GetProperty("groups"));
                        var trust = await accounts.GetTrustAsync(uid, hero, ct);
                        Require(trust is not null && index >= 1 && index <= groups.Count && Math.Max(1, trust.Level) >= tier, "Relation tier locked");
                        var cond = Cfg.GetProperty("upgrade_cond").GetProperty(Cfg.GetProperty("upgrade").GetProperty(groups[index - 1].ToString()).GetInt64().ToString());
                        var ok = false;
                        foreach (var other in L(n.GetProperty("intimate")))
                            if (await accounts.GetHeroRowAsync(uid, other, ct) is { } h && await ValueAsync(uid, h, cond.GetProperty("type").GetInt32(), ct) >= L(cond.GetProperty("arg")).FirstOrDefault())
                            { ok = true; break; }
                        Require(ok, "Relation condition not met");
                        if (!s.Tiers.TryGetValue(hero, out var tiers)) s.Tiers[hero] = tiers = [];
                        if (!tiers.TryGetValue(tier, out var done)) tiers[tier] = done = [];
                        Require(!done.Contains(index), "Already unlocked");
                        done.Add(index);
                        touchedHero = hero;
                        break;
                    }
                case 73022:
                    {
                        var id = (long)HandlerHelpers.ReadUInt(b, "id");
                        Require(Cfg.GetProperty("stories").TryGetProperty(id.ToString(), out var st) && st.GetProperty("story").GetInt64() != 0 && !s.Stories.Contains(id), "Story unavailable");
                        var main = st.GetProperty("main").GetInt64();
                        var h = await accounts.GetHeroRowAsync(uid, main, ct);
                        Require(h is not null, "Hero not owned");
                        var tiers = Cfg.GetProperty("net").EnumerateObject().Where(p => p.Value.GetProperty("hero").GetInt64() == main && L(p.Value.GetProperty("intimate")).Contains(st.GetProperty("relate").GetInt64()))
                            .Select(p => p.Value.GetProperty("index").GetInt32()).ToList();
                        var trust = await accounts.GetTrustAsync(uid, main, ct);
                        Require(tiers.Count > 0 && Math.Max(1, trust?.Level ?? 0) >= tiers.Min() && await StoryConditionAsync(uid, st.GetProperty("cond").GetInt64(), h!, ct), "Story locked");
                        grants.AddRange(st.GetProperty("reward").EnumerateArray().Select(x => (x[0].GetInt64(), x[1].GetInt64())));
                        s.Stories.Add(id);
                        reply["reward_list"] = HandlerHelpers.ToItemNetRecList(grants);
                        pushes.Add(new GameResponse(73007, 0, srv, codec.Encode("sc_73007", new Dictionary<string, object?> { ["got_reward_id_list"] = s.Stories.Select(x => (uint)x).ToArray() })));
                        break;
                    }
                case 73018:
                    {
                        var id = (int)HandlerHelpers.ReadUInt(b, "cooperate_unique_skill_id");
                        Require(Cfg.GetProperty("combos").TryGetProperty(id.ToString(), out var cc), "Unknown combo");
                        var heroes = new List<HeroRow>();
                        foreach (var role in L(cc.GetProperty("roles"))) { var h = await accounts.GetHeroRowAsync(uid, role, ct); Require(h is not null, "Combo hero missing"); heroes.Add(h!); }
                        var c = s.Combos.TryGetValue(id, out var x) ? x : s.Combos[id] = new Combo();
                        Require(c.Level < cc.GetProperty("max").GetInt32(), "Combo max level");
                        foreach (var lc in Cfg.GetProperty("combo_levels").EnumerateObject().Where(p => p.Value.GetProperty("level").GetInt32() == c.Level))
                        {
                            var value = lc.Value.GetProperty("type").GetInt32() == 1 ? heroes.Min(Ultimate) : c.Progress.GetValueOrDefault(int.Parse(lc.Name));
                            Require(value >= lc.Value.GetProperty("target").GetInt64(), "Combo condition not met");
                        }
                        c.Level++;
                        c.Progress.Clear();
                        pushes.Add(new GameResponse(73003, 0, srv, codec.Encode("sc_73003", new Dictionary<string, object?> { ["skill"] = ComboRec(id, c) })));
                        break;
                    }
            }
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)]; }
        if (grants.Count > 0 && !await accounts.ConsumeAndGrantAsync(uid, [], grants, ct)) return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)];
        await accounts.SetJsonAsync(uid, "relation_v1", s, ct);
        var frames = await InventorySync.FramesAsync(accounts, codec, r, grants.Select(x => x.Item1), ct);
        frames.AddRange(pushes);
        frames.Add(HandlerHelpers.Reply(codec, r, cmd + 1, reply));   // ACK before the authoritative 14007 (as the hero ops)
        if (touchedHero != 0 && await LoginBootstrap.BuildSingleHeroNetRecAsync(accounts, uid, touchedHero, ct) is { } rec)
            frames.Add(new GameResponse(14007, 0, srv, codec.Encode("sc_14007", new Dictionary<string, object?> { ["hero"] = rec })));
        return frames;
    }

    public static readonly uint[] Commands = [73016, 73018, 73022];
}
