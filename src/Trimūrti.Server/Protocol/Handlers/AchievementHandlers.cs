using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 成就 (port of Alpha-Persei achievement_service.py) on the client AchievementCfg / AchievementStoryCfg
/// (→ Data/Configs/achievement_cfg2.json). Progress is recomputed from the real save (reconcile) for the state conditions:
/// 2 player level, 12 login days, 101 heroes of a race, 102 / 105 / 106 / 109 / 110 / 218 heroes reaching a level, star, skill
/// sum, affection, proficiency, attribute sum, 120 heroes with every astrolabe node, 121 complete heart stories, 160 heroes
/// (param 3 = rank S), 200 sigils of a level, 206 weapon level, 210 servant refine, 212 跃迁 points, 260 / 261 chips,
/// 301 5★ sigils, 441 stage cleared, 2001 canteen furniture levels, 2004 dorm rooms, 2006 furniture given, 112 gifts;
/// counters fed by the systems: 204 enchant, 205 race / 211 exclusive reconstruction, 2002 entrusts, 2003 meals, 2005 layout,
/// 700 pulls, 780 5★ servants from pulls, 80001–80005 ten-pull patterns. Battle-report conditions (100xxx / 120xxx) are not
/// tracked. 53004 claims (configured reward, item 33 = achievement points), 53006 reads an achievement story
/// (unlock_point), login 53001, diff 53003.
/// </summary>
public sealed class AchievementSystem(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    private const long PointItem = 33;
    private JsonElement Cfg => config.Get("achievement_cfg2.json") ?? throw new ModeError("Missing achievement_cfg2.json");
    private static List<long> L(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList() : [];

    public sealed class Rec { public long Progress { get; set; } public long Time { get; set; } public bool Claimed { get; set; } }
    public sealed class State
    {
        public Dictionary<long, Rec> Items { get; set; } = [];
        public Dictionary<int, long> Counters { get; set; } = [];
        public long LoginDay { get; set; }
        public long LoginDays { get; set; }
        public List<uint> Stories { get; set; } = [];
    }

    private Task<State?> RawAsync(long uid, CancellationToken ct) => accounts.GetJsonAsync<State>(uid, "achievement_v1", ct);
    private Task SaveAsync(long uid, State s, CancellationToken ct) => accounts.SetJsonAsync(uid, "achievement_v1", s, ct);

    private static object Info(long id, Rec r) => new Dictionary<string, object?>
    { ["id"] = (uint)id, ["progress"] = (ulong)r.Progress, ["achieve_time"] = (uint)r.Time, ["complete_flag"] = r.Claimed ? 1u : 0u };

    /// <summary>Recomputes every achievement; returns the changed records.</summary>
    private async Task<List<object>> ReconcileAsync(long uid, State s, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (s.LoginDay != GameClock.TodayStartUnix) { s.LoginDay = GameClock.TodayStartUnix; s.LoginDays++; }
        var profile = await accounts.GetProfileAsync(uid, ct);
        var login = await accounts.GetInitialLoginStateAsync(uid, ct);
        var heroes = login?.Heroes.Where(h => h.Unlock == 1).ToList() ?? [];
        var heroCfg = config.Get("hero_cfg.json")!.Value;
        var intensify = await accounts.GetHeroIntensifyAsync(uid, ct);
        var warp = await accounts.GetHeroWarpAsync(uid, ct);
        var sigils = await accounts.GetSigilsAsync(uid, ct);
        var sigilSys = new SigilSystem(accounts, config, codec);
        var servants = await accounts.GetServantRowsAsync(uid, ct);
        var archives = (await accounts.GetHeroArchivesAsync(uid, ct)).ToDictionary(a => (long)a.ArchiveId, a => a);
        var trustCfg = config.Get("trust_cfg.json");
        var home = await accounts.GetJsonAsync<BackHome.State>(uid, "backhome_v1", ct);
        var chips = await accounts.GetJsonAsync<ChipSystem.State>(uid, "chips_v1", ct);
        var trustExtra = await accounts.GetJsonAsync<TrustSystem.Extra>(uid, "trust_extra_v1", ct);
        var chapters = (await accounts.GetLobbySnapshotAsync(uid, ct)).Chapters;
        int Race(long hero) => heroCfg.GetProperty("heroes").TryGetProperty(hero.ToString(), out var h) ? h.GetProperty("race").GetInt32() : 0;
        int Rare(long hero) => heroCfg.GetProperty("heroes").TryGetProperty(hero.ToString(), out var h) ? h.GetProperty("rare").GetInt32() : 0;
        int WeaponLevel(long exp) => Enumerable.Range(1, 100).LastOrDefault(l => heroCfg.GetProperty("curve").TryGetProperty(l.ToString(), out var c) && exp >= c.GetProperty("weapon_sum").GetInt64(), 1);
        int Love(long exp) => exp >= 1000 ? 5 : exp >= 600 ? 4 : exp >= 300 ? 3 : exp >= 100 ? 2 : 1;
        long ArchiveOf(long hero) => trustCfg is { } t && t.GetProperty("hero_to_archive").TryGetProperty(hero.ToString(), out var a) ? a.GetInt64() : hero;
        var ownedArchives = heroes.Select(h => ArchiveOf(h.Id)).Distinct().ToList();

        long? Value(int cond, List<long> p) => cond switch
        {
            2 => profile?.Level ?? 1,
            12 => s.LoginDays,
            101 => heroes.Count(h => p.Count > 0 && Race(h.Id) == p[0]),
            102 => heroes.Count(h => p.Count > 1 && h.Level >= p[1]),
            105 => heroes.Count(h => p.Count > 1 && h.Star >= p[1]),
            106 => heroes.Count(h => p.Count > 1 && h.Skills.Sum(x => x.Level) >= p[1]),
            109 => ownedArchives.Count(a => p.Count > 1 && Love(archives.GetValueOrDefault(a)?.Exp ?? 0) >= p[1]),
            110 => heroes.Count(h => p.Count > 1 && h.ClearTimes >= p[1]),
            112 => trustExtra?.Gifts.Values.Sum(g => g.Values.Sum()) ?? 0,
            120 => heroes.Count(h => h.UnlockedAstrolabe.Count >= 9),
            121 => ownedArchives.Count(a => trustCfg is { } t && t.GetProperty("archives").TryGetProperty(a.ToString(), out var ac)
                       && L(ac.GetProperty("plot_ids")) is { Count: > 0 } plots && plots.All(x => archives.GetValueOrDefault(a)?.VideoList.Contains((uint)x) == true)),
            160 => p.Count > 0 && p[0] == 3 ? heroes.Count(h => Rare(h.Id) == 3) : heroes.Count,
            200 => sigils.Count(x => p.Count > 0 && sigilSys.Level(x.Prefab, x.Exp, x.Break) >= p[0]),
            206 => heroes.Count(h => WeaponLevel(h.WeaponExperience) >= (p.Count > 0 ? p[0] : 20)),
            210 => servants.Count(x => x.Stage >= (p.Count > 0 ? p[0] : 5)),
            212 => warp.Values.Sum(l => l.Sum(w => w.TalentPoints)),
            218 => heroes.Count(h => p.Count > 1 && (intensify.GetValueOrDefault(h.Id) ?? []).Sum(x => x[1]) >= p[1]),
            260 => chips?.Kernel.Count ?? 0,
            261 => (chips?.Secondary.Count ?? 0) + (chips?.Hero.Count ?? 0),
            301 => sigils.Count(x => sigilSys.Star(x.Prefab) >= 5),
            441 => p.Count > 0 && chapters.Any(c => c.Id == p[0] && c.ClearTimes > 0) ? long.MaxValue : null,
            2001 => home?.CanteenFur.Values.Sum() ?? 0,
            2004 => home?.Dorms.Count(d => d.Key >= 6) ?? 0,
            2006 => home?.Given.Values.Sum() ?? 0,
            80001 or 80002 or 80003 or 80004 or 80005 => p.Count > 0 && s.Counters.GetValueOrDefault(cond) >= p[0] ? 1 : 0,
            204 or 205 or 211 or 2002 or 2003 or 2005 or 700 or 780 => s.Counters.GetValueOrDefault(cond),
            _ => null
        };

        var changed = new List<object>();
        foreach (var p in Cfg.GetProperty("items").EnumerateObject())
        {
            var id = long.Parse(p.Name);
            var rec = s.Items.TryGetValue(id, out var x) ? x : s.Items[id] = new Rec();
            if (rec.Claimed) continue;
            var need = p.Value.GetProperty("need").GetInt64();
            var v = Value(p.Value.GetProperty("cond").GetInt32(), L(p.Value.GetProperty("param")));
            if (v is null) continue;
            var next = Math.Min(need, Math.Max(rec.Progress, v.Value));
            if (next == rec.Progress) continue;
            rec.Progress = next;
            if (next >= need && rec.Time == 0) rec.Time = now;
            changed.Add(Info(id, rec));
        }
        return changed;
    }

    /// <summary>Counter event from another system (204, 205, 211, 2002, 2003, 2005, 700, 780, 8000x); returns the 53003 diff.</summary>
    public async Task<GameResponse?> EventAsync(long uid, ushort srv, int condition, long delta, CancellationToken ct)
    {
        var s = await RawAsync(uid, ct) ?? new State();
        if (condition is >= 80001 and <= 80005) s.Counters[condition] = Math.Max(s.Counters.GetValueOrDefault(condition), delta);
        else s.Counters[condition] = s.Counters.GetValueOrDefault(condition) + delta;
        var changed = await ReconcileAsync(uid, s, ct);
        await SaveAsync(uid, s, ct);
        return changed.Count == 0 ? null : new GameResponse(53003, 0, srv, codec.Encode("sc_53003", new Dictionary<string, object?> { ["achievement_progress"] = changed.ToArray() }));
    }

    public async Task<(uint, byte[])> LoginFrameAsync(long uid, CancellationToken ct)
    {
        var s = await RawAsync(uid, ct) ?? new State();
        await ReconcileAsync(uid, s, ct);
        await SaveAsync(uid, s, ct);
        return (53001, codec.Encode("sc_53001", new Dictionary<string, object?>
        { ["achievement_list"] = s.Items.OrderBy(x => x.Key).Select(x => Info(x.Key, x.Value)).ToArray(), ["story_line"] = s.Stories.ToArray() }));
    }

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var s = await RawAsync(uid, ct) ?? new State();
        var changed = await ReconcileAsync(uid, s, ct);
        var grants = new List<(long, long)>();
        uint result = 0;
        if (cmd == 53004)
        {
            var ids = HandlerHelpers.ReadIdList(r.Body, "id");
            var claimed = 0;
            foreach (var id in ids.Distinct())
            {
                if (!Cfg.GetProperty("items").TryGetProperty(id.ToString(), out var c) || !s.Items.TryGetValue(id, out var rec) || rec.Claimed
                    || rec.Progress < c.GetProperty("need").GetInt64()) continue;
                rec.Claimed = true;
                claimed++;
                grants.AddRange(c.GetProperty("reward").EnumerateArray().Select(x => (x[0].GetInt64(), x[1].GetInt64())));
                changed.Add(Info(id, rec));
            }
            if (claimed == 0) result = 2;
        }
        else if (cmd == 53006)
        {
            var id = HandlerHelpers.ReadUInt(r.Body, "id");
            var points = await accounts.GetItemCountAsync(uid, PointItem, ct);
            if (!Cfg.GetProperty("stories").TryGetProperty(id.ToString(), out var st) || points < st.GetProperty("unlock_point").GetInt64()) result = 2;
            else if (!s.Stories.Contains(id)) s.Stories.Add(id);
        }
        var g2 = grants.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).ToList();
        if (g2.Count > 0) await accounts.GrantItemsAsync(uid, g2, ct);
        await SaveAsync(uid, s, ct);
        var frames = await InventorySync.FramesAsync(accounts, codec, r, g2.Select(x => x.Item1), ct);
        if (changed.Count > 0) frames.Add(new GameResponse(53003, 0, (ushort)(r.ServerIndex + 1), codec.Encode("sc_53003", new Dictionary<string, object?> { ["achievement_progress"] = changed.ToArray() })));
        frames.Add(HandlerHelpers.Reply(codec, r, cmd + 1, cmd == 53004
            ? new Dictionary<string, object?> { ["result"] = result, ["reward_list"] = HandlerHelpers.ToItemNetRecList(g2) }
            : new Dictionary<string, object?> { ["result"] = result }));
        return frames;
    }

    public static readonly uint[] Commands = [53004, 53006];
}
