using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// Hero archive (一阶好感 / 心链) and trust (二阶交心), port of Alpha-Persei archive_service.py + trust_service.py on the
/// client tables HeroRecordCfg / HeroAnedoteCfg (archives, stories, anecdotes), HeroTrustCfg (per-level conditions and
/// rewards), HeroTrustLevelCfg (exp per level), HeroTrustMoodCfg (exp rate) and the trust gift rules
/// (→ Data/Configs/trust_cfg.json). Commands (client HeroAction / ArchiveAction):
/// 14100 archive gifts (preferred gifts ×2 hero_love_exp_gain, cap 1000), 14102 heart texts and 14104 stories with their
/// archive-exp gates, 71114 super heart stories (trust 2 / 5), 71108 anecdote (trust 5, first-view reward),
/// 73020 open trust (archive exp ≥ unlock, random mood), 73012 trust gifts (level / favoured gifts, mood rate, capped at the
/// level's exp, only the gifts needed are consumed), 73014 trust level up (exp full + HeroTrustCfg conditions → rewards),
/// 73010 gift exchange (ratio and daily limit, sc_73005).
/// </summary>
public sealed class TrustSystem(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    private static readonly int[] PlotGates = [100, 300, 600, 1000];
    private static readonly int[] TextGates = [0, 100, 300, 600, 1000];
    private static readonly int[] SuperGates = [2, 5];
    private static readonly Dictionary<int, int> MoodRate = new() { [1] = 1000, [2] = 1100, [3] = 1300 };   // HeroTrustMoodCfg

    private JsonElement Cfg => config.Get("trust_cfg.json") ?? throw new ModeError("Missing trust_cfg.json");
    private static void Require(bool ok, string m) { if (!ok) throw new ModeError(m); }
    private static List<long> L(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList() : [];

    public sealed class Extra
    {
        public Dictionary<long, List<int>> Super { get; set; } = [];
        public List<long> Anecdotes { get; set; } = [];
        public Dictionary<long, Dictionary<long, long>> Gifts { get; set; } = [];
        public long ExchangeDay { get; set; }
        public Dictionary<long, long> Exchanged { get; set; } = [];
    }

    private async Task<Extra> ExtraAsync(long uid, CancellationToken ct)
    {
        var x = await accounts.GetJsonAsync<Extra>(uid, "trust_extra_v1", ct) ?? new Extra();
        if (x.ExchangeDay != GameClock.TodayStartUnix) { x.ExchangeDay = GameClock.TodayStartUnix; x.Exchanged.Clear(); }
        return x;
    }

    public async Task<(uint, byte[])> ExchangeFrameAsync(long uid, CancellationToken ct)
    {
        var x = await ExtraAsync(uid, ct);
        return (73005, codec.Encode("sc_73005", new Dictionary<string, object?>
        {
            ["limit_list"] = Cfg.GetProperty("exchange_limits").EnumerateObject().Select(p => long.Parse(p.Name)).Order()
                .Select(id => (object)new Dictionary<string, object?> { ["item_id"] = (uint)id, ["use"] = (uint)x.Exchanged.GetValueOrDefault(id) }).ToArray()
        }));
    }

    private long ArchiveOf(long id) => Cfg.GetProperty("hero_to_archive").TryGetProperty(id.ToString(), out var a) ? a.GetInt64() : Cfg.GetProperty("archives").TryGetProperty(id.ToString(), out _) ? id : 0;
    private JsonElement Archive(long archive) { Require(Cfg.GetProperty("archives").TryGetProperty(archive.ToString(), out var a), "Unknown archive"); return a; }

    private async Task<bool> ConditionAsync(long uid, int id, TrustRow t, CancellationToken ct)
    {
        if (id == 0) return true;
        if (!Cfg.GetProperty("conditions").TryGetProperty(id.ToString(), out var c)) return false;
        var p = L(c.GetProperty("params"));
        switch (c.GetProperty("type").GetInt32())
        {
            case 1008:
                var hero = config.Get("hero_cfg.json")?.GetProperty("heroes");
                var study = hero is { } h && h.TryGetProperty(t.Hero.ToString(), out var hc) ? L(hc.GetProperty("study")) : [];
                return study.Count > 0 && (await accounts.GetLobbySnapshotAsync(uid, ct)).Chapters.Any(x => x.Id == study[0] && x.ClearTimes > 0);
            case 1004:
                return p.Count == 1 && t.ClearTimes >= p[0];
            case 8023:
                var home = await accounts.GetJsonAsync<BackHome.State>(uid, "backhome_v1", ct);
                var arch = ArchiveOf(t.Hero);
                return p.Count == 1 && home is not null && home.Heroes.TryGetValue(arch, out var bh) && bh.TotalFeed >= p[0];
            default:
                return false;
        }
    }

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var b = r.Body;
        var srv = (ushort)(r.ServerIndex + 1);
        uint U(string k) => HandlerHelpers.ReadUInt(b, k);
        var reply = new Dictionary<string, object?> { ["result"] = 0u };
        var costs = new List<(long, long)>();
        var grants = new List<(long, long)>();
        TrustRow? trust = null;
        ArchiveRow? archive = null;
        Extra? extra = null;
        long touchedHero = 0;
        try
        {
            async Task<List<long>> OwnedForms(long arch)
            {
                var forms = L(Archive(arch).GetProperty("hero_ids"));
                var owned = (await accounts.GetOwnedHeroIdsAsync(uid, ct)).ToHashSet();
                var mine = forms.Where(owned.Contains).ToList();
                Require(mine.Count > 0, "Archive locked");
                return mine;
            }

            switch (cmd)
            {
                case 14100:
                    {
                        var arch = ArchiveOf(U("archive_id"));
                        await OwnedForms(arch);
                        var cfg = Archive(arch);
                        var preferred = L(cfg.GetProperty("gift_like_ids")).ToHashSet();
                        var allowed = L(Cfg.GetProperty("first_tier_gifts")).Concat(preferred).ToHashSet();
                        var gifts = HandlerHelpers.ReadItemList(b, "gift_list");
                        Require(gifts.Count > 0 && gifts.All(g => allowed.Contains(g.ItemId)), "Not an archive gift");
                        archive = await accounts.GetArchiveAsync(uid, arch, ct);
                        Require(archive.Exp < 1000, "Affection maxed");
                        var gain = config.Get("hero_cfg.json") is { } hc ? L(hc.GetProperty("setting").GetProperty("hero_love_exp_gain")).FirstOrDefault(5) : 5;
                        var exp = gifts.Sum(g => g.Count * (preferred.Contains(g.ItemId) ? gain * 2 : gain));
                        costs.AddRange(gifts.Select(g => (g.ItemId, g.Count)));
                        archive = archive with { Exp = (int)Math.Min(1000, archive.Exp + exp) };
                        extra = await ExtraAsync(uid, ct);
                        if (!extra.Gifts.TryGetValue(arch, out var totals)) extra.Gifts[arch] = totals = [];
                        foreach (var g in gifts) totals[g.ItemId] = totals.GetValueOrDefault(g.ItemId) + g.Count;
                        break;
                    }
                case 14102:
                    {
                        var arch = ArchiveOf(U("archive_id"));
                        await OwnedForms(arch);
                        archive = await accounts.GetArchiveAsync(uid, arch, ct);
                        var req = HandlerHelpers.ReadIdList(b, "text_list").Select(x => (int)x).Distinct().ToList();
                        Require(req.Count > 0 && req.All(i => i >= 1 && i <= TextGates.Length && archive.Exp >= TextGates[i - 1]), "Heart text locked");
                        archive = archive with { Texts = archive.Texts.Union(req).Order().ToList() };
                        break;
                    }
                case 14104:
                    {
                        var arch = ArchiveOf(U("archive_id"));
                        await OwnedForms(arch);
                        archive = await accounts.GetArchiveAsync(uid, arch, ct);
                        var plots = L(Archive(arch).GetProperty("plot_ids")).Select(x => (int)x).ToList();
                        var seen = archive.Videos.ToHashSet();
                        foreach (var id in HandlerHelpers.ReadIdList(b, "video_list").Select(x => (int)x).OrderBy(x => plots.IndexOf(x)))
                        {
                            var i = plots.IndexOf(id);
                            Require(i >= 0 && archive.Exp >= PlotGates[Math.Min(i, PlotGates.Length - 1)] && plots.Take(i).All(seen.Contains), "Story locked");
                            seen.Add(id);
                        }
                        archive = archive with { Videos = plots.Where(seen.Contains).ToList() };
                        break;
                    }
                case 71114:
                    {
                        var arch = ArchiveOf(U("archive_id"));
                        var forms = await OwnedForms(arch);
                        var cfg = Archive(arch);
                        var supers = L(cfg.GetProperty("super_plot_ids"));
                        var index = (int)U("index");
                        Require(index >= 1 && index <= supers.Count, "Bad super heart");
                        var a = await accounts.GetArchiveAsync(uid, arch, ct);
                        Require(L(cfg.GetProperty("plot_ids")).All(p => a.Videos.Contains((int)p)), "Heart stories not complete");
                        extra = await ExtraAsync(uid, ct);
                        if (!extra.Super.TryGetValue(arch, out var seen)) extra.Super[arch] = seen = [];
                        Require(Enumerable.Range(1, index - 1).All(seen.Contains), "Previous super heart not viewed");
                        var levels = new List<int>();
                        foreach (var f in forms) levels.Add((await accounts.GetTrustAsync(uid, f, ct))?.Level ?? 0);
                        Require(levels.Max() >= SuperGates[Math.Min(index - 1, SuperGates.Length - 1)], "Trust level too low");
                        if (!seen.Contains(index)) seen.Add(index);
                        break;
                    }
                case 71108:
                    {
                        var hero = (long)U("hero_id");
                        Require(Cfg.GetProperty("anecdotes").TryGetProperty(hero.ToString(), out var an), "No anecdote");
                        var t = await accounts.GetTrustAsync(uid, hero, ct);
                        Require(t is not null && t.Level >= 5, "Trust level 5 needed");
                        extra = await ExtraAsync(uid, ct);
                        var rewards = new List<(long, long)>();
                        if (!extra.Anecdotes.Contains(hero))
                        {
                            extra.Anecdotes.Add(hero);
                            rewards = an.GetProperty("rewards").EnumerateArray().Select(x => (x.GetProperty("id").GetInt64(), x.GetProperty("num").GetInt64())).ToList();
                            grants.AddRange(rewards);
                        }
                        reply["reward_list"] = HandlerHelpers.ToItemNetRecList(rewards);
                        break;
                    }
                case 73020:
                    {
                        var hero = (long)U("hero_id");
                        Require(Cfg.GetProperty("trust").TryGetProperty(hero.ToString(), out _), "Trust not supported");
                        var t = await accounts.GetTrustAsync(uid, hero, ct);
                        Require(t is not null && t.Level == 0, "Trust already open");
                        var a = await accounts.GetArchiveAsync(uid, ArchiveOf(hero), ct);
                        Require(a.Exp >= Cfg.GetProperty("archive_unlock_exp").GetInt32(), "Affection not maxed");
                        trust = t! with { Level = 1, Exp = 0, Mood = Random.Shared.Next(1, 4) };
                        reply["mood"] = (uint)trust.Mood;
                        touchedHero = hero;
                        break;
                    }
                case 73012:
                    {
                        var hero = (long)U("hero_id");
                        var t = await accounts.GetTrustAsync(uid, hero, ct);
                        Require(t is not null && t.Level >= 1 && Cfg.GetProperty("trust").TryGetProperty(hero.ToString(), out _), "Trust not open");
                        var allowed = (Cfg.GetProperty("favored").TryGetProperty(hero.ToString(), out var fav) ? L(fav) : [])
                            .Concat(Cfg.GetProperty("level_gifts").TryGetProperty(t!.Level.ToString(), out var lg) ? L(lg) : []).ToHashSet();
                        var cap = Cfg.GetProperty("level_exp").GetProperty(t.Level.ToString()).GetInt32();
                        Require(cap > 0 && t.Exp < cap, "Trust exp full, level up first");
                        var remaining = (long)(cap - t.Exp);
                        long total = 0;
                        foreach (var (id, n) in HandlerHelpers.ReadItemList(b, "item_list"))
                        {
                            Require(allowed.Contains(id) && Cfg.GetProperty("items").TryGetProperty(id.ToString(), out var ic), "Gift not usable at this trust level");
                            var single = Cfg.GetProperty("items").GetProperty(id.ToString()).GetProperty("exp").GetInt64() * MoodRate.GetValueOrDefault(t.Mood, 1000) / 1000;
                            var use = remaining <= 0 ? 0 : Math.Min(n, (remaining + single - 1) / single);
                            if (use <= 0) continue;
                            costs.Add((id, use));
                            total += single * use;
                            remaining -= single * use;
                        }
                        Require(total > 0, "No gift used");
                        trust = t with { Exp = (int)Math.Min(cap, t.Exp + total) };
                        touchedHero = hero;
                        break;
                    }
                case 73014:
                    {
                        var hero = (long)U("hero_id");
                        var t = await accounts.GetTrustAsync(uid, hero, ct);
                        Require(t is not null && t.Level is >= 1 and < 5, "Cannot level up");
                        Require(t!.Exp >= Cfg.GetProperty("level_exp").GetProperty(t.Level.ToString()).GetInt32(), "Trust exp not full");
                        Require(Cfg.GetProperty("trust").GetProperty(hero.ToString()).TryGetProperty(t.Level.ToString(), out var lc), "Missing trust level");
                        foreach (var c in L(lc.GetProperty("conditions"))) Require(await ConditionAsync(uid, (int)c, t, ct), "Trust condition not met");
                        var rewards = lc.GetProperty("rewards").EnumerateArray().Select(x => (x[0].GetInt64(), x[1].GetInt64())).ToList();
                        grants.AddRange(rewards);
                        trust = t with { Level = t.Level + 1, Exp = 0 };
                        reply["reward_list"] = HandlerHelpers.ToItemNetRecList(rewards);
                        touchedHero = hero;
                        break;
                    }
                case 73010:
                    {
                        var use = HandlerHelpers.ReadItemList(b, "use_item_list").GroupBy(x => x.ItemId).Select(g => (g.Key, g.Sum(x => x.Count))).ToList();
                        var want = HandlerHelpers.ReadItemList(b, "reward_item_list").GroupBy(x => x.ItemId).Select(g => (g.Key, g.Sum(x => x.Count))).ToList();
                        Require(want.Count == 1 && use.Count > 0, "Bad exchange");
                        var (target, amount) = want[0];
                        Require(Cfg.GetProperty("exchange_ratio").TryGetProperty(target.ToString(), out _) && Cfg.GetProperty("exchange_limits").TryGetProperty(target.ToString(), out _), "Not exchangeable");
                        var ratio = Cfg.GetProperty("exchange_ratio").GetProperty(target.ToString());
                        var limit = Cfg.GetProperty("exchange_limits").GetProperty(target.ToString());
                        Require(use.All(u => Cfg.GetProperty("items").TryGetProperty(u.Key.ToString(), out var ic) && ic.GetProperty("sub_type").GetInt32() == 1), "Not an exchange material");
                        Require(use.Sum(u => u.Item2) == amount * ratio.GetInt64(), "Wrong exchange ratio");
                        extra = await ExtraAsync(uid, ct);
                        Require(extra.Exchanged.GetValueOrDefault(target) + amount <= limit.GetInt64(), "Daily exchange limit");
                        extra.Exchanged[target] = extra.Exchanged.GetValueOrDefault(target) + amount;
                        costs.AddRange(use);
                        grants.Add((target, amount));
                        break;
                    }
            }
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)]; }

        if ((trust is not null || archive is not null || costs.Count > 0 || grants.Count > 0) && !await accounts.CommitTrustAsync(uid, trust, archive, costs, grants, ct))
            return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)];
        if (extra is not null) await accounts.SetJsonAsync(uid, "trust_extra_v1", extra, ct);
        var frames = await InventorySync.FramesAsync(accounts, codec, r, costs.Concat(grants).Select(x => x.Item1), ct);
        if (cmd == 73010) { var (c5, p5) = await ExchangeFrameAsync(uid, ct); frames.Add(new GameResponse(c5, 0, srv, p5)); }
        frames.Add(HandlerHelpers.Reply(codec, r, cmd + 1, reply));   // ACK before the authoritative 14007 (as the hero ops)
        if (touchedHero != 0 && await LoginBootstrap.BuildSingleHeroNetRecAsync(accounts, uid, touchedHero, ct) is { } rec)
            frames.Add(new GameResponse(14007, 0, srv, codec.Encode("sc_14007", new Dictionary<string, object?> { ["hero"] = rec })));
        return frames;
    }

    public static readonly uint[] Commands = [14100, 14102, 14104, 71108, 71114, 73010, 73012, 73014, 73020];
}
