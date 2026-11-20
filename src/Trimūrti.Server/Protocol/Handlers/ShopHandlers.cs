using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// Shops and currency purchases (port of Alpha-Persei shop_service.py) on the client tables ShopCfg/ShopCfg2-4,
/// ShopListCfg and GameCurrencyBuySetting (→ Data/Configs/shop_cfg.json).
///  - 20009 / 20005: every shop's goods with this player's buy times; the daily shop (initiative_refresh) rolls one good per
///    position by weight among the goods the player level allows (available_limit type 1), re-rolled at 05:00 UTC+8.
///  - 20012 buy: good must be on the shelf, open, within limit_num (4294967295 = unlimited), previous tier sold out and the
///    level allowed; pays cheap_cost when discounted; grants give_id × give (heroes / sigils / skins … become entities).
///    Limits reset by refresh_cycle (ShopConst SHOP_LIMIT_TEXT: 4 day, 3 week, 2 month, others never).
///  - 20014 refresh the daily shop (GameCurrencyBuySetting shop_money_refresh per refresh, ShopListCfg refresh_num_limit).
///  - 15012 buy gold / 15016 buy fatigue with 移转之辉 (item 1), price by today's purchase count.
/// Paid (real-money) goods are refused.
/// </summary>
public sealed class ShopSystem(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    private const long Unlimited = 4294967295;
    private JsonElement Cfg => config.Get("shop_cfg.json") ?? throw new ModeError("Missing shop_cfg.json");
    private static void Require(bool ok, string m) { if (!ok) throw new ModeError(m); }
    private static int I(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetInt64() : 0;
    private static long N(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    public sealed class Bought { public long Times { get; set; } public long Reset { get; set; } }
    public sealed class State
    {
        public Dictionary<long, Bought> Bought { get; set; } = [];
        public Dictionary<int, List<long>> Rolled { get; set; } = [];
        public Dictionary<int, int> Refreshes { get; set; } = [];
        public long Day { get; set; }
        public int GoldBuys { get; set; }
        public int FatigueBuys { get; set; }
    }

    private Dictionary<int, List<(long Id, JsonElement G)>>? _byShop;
    private Dictionary<int, List<(long Id, JsonElement G)>> ByShop => _byShop ??= Cfg.GetProperty("goods").EnumerateObject()
        .Where(p => I(p.Value, "taken_down") == 0).Select(p => (long.Parse(p.Name), p.Value))
        .GroupBy(x => I(x.Value, "shop_id")).ToDictionary(g => g.Key, g => g.ToList());

    private bool Rolling(int shop) => Cfg.GetProperty("shops").TryGetProperty(shop.ToString(), out var s) && I(s, "initiative_refresh") == 1;

    private static long NextReset(int cycle)
    {
        var day = GameClock.TodayStartUnix;
        var today = GameClock.Today;
        return cycle switch
        {
            4 => day + 86400,
            3 => new DateTimeOffset(today.AddDays(7 - ((int)today.DayOfWeek + 6) % 7).AddHours(5), GameClock.Offset).ToUnixTimeSeconds(),
            2 => new DateTimeOffset(new DateTime(today.Year, today.Month, 1).AddMonths(1).AddHours(5), GameClock.Offset).ToUnixTimeSeconds(),
            _ => 0
        };
    }

    private static bool LevelOk(JsonElement g, int level) =>
        !g.TryGetProperty("avail", out var a) || a.ValueKind != JsonValueKind.Array || a.EnumerateArray().All(x => x[0].GetInt32() != 1 || level >= x[1].GetInt32());

    private List<long> Roll(int shop, int level)
    {
        var list = new List<long>();
        foreach (var pos in ByShop.GetValueOrDefault(shop, []).GroupBy(x => I(x.G, "position")).OrderBy(g => g.Key))
        {
            var cands = pos.Where(x => LevelOk(x.G, level)).ToList();
            if (cands.Count == 0) continue;
            var total = cands.Sum(x => Math.Max(1, N(x.G, "weight")));
            var pick = Random.Shared.NextInt64(total);
            foreach (var c in cands) { pick -= Math.Max(1, N(c.G, "weight")); if (pick < 0) { list.Add(c.Id); break; } }
        }
        return list;
    }

    private async Task<(State S, int Level)> LoadAsync(long uid, CancellationToken ct)
    {
        var s = await accounts.GetJsonAsync<State>(uid, "shop_v1", ct) ?? new State();
        var level = (await accounts.GetProfileAsync(uid, ct))?.Level ?? 1;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var b in s.Bought.Values.Where(b => b.Reset > 0 && b.Reset <= now)) { b.Times = 0; b.Reset = 0; }
        if (s.Day != GameClock.TodayStartUnix)
        {
            s.Day = GameClock.TodayStartUnix;
            s.Rolled.Clear();
            s.Refreshes.Clear();
            s.GoldBuys = 0;
            s.FatigueBuys = 0;
        }
        foreach (var shop in ByShop.Keys.Where(Rolling))
            if (!s.Rolled.ContainsKey(shop)) s.Rolled[shop] = Roll(shop, level);
        return (s, level);
    }

    private object ShopInfo(State s, int shop) => new Dictionary<string, object?>
    {
        ["shop_id"] = (uint)shop, ["refresh_times"] = (uint)s.Refreshes.GetValueOrDefault(shop), ["is_need_tag"] = false,
        ["goods_list"] = (Rolling(shop) ? s.Rolled.GetValueOrDefault(shop, []) : ByShop.GetValueOrDefault(shop, []).Select(x => x.Id).ToList())
            .Select(id => (object)new Dictionary<string, object?> { ["goods_id"] = (uint)id, ["buy_times"] = (uint)(s.Bought.GetValueOrDefault(id)?.Times ?? 0), ["next_refresh_timestamp"] = (uint)(s.Bought.GetValueOrDefault(id)?.Reset ?? 0) }).ToArray()
    };

    public async Task<(uint, byte[])> LoginFrameAsync(long uid, CancellationToken ct)
    {
        var (s, _) = await LoadAsync(uid, ct);
        await accounts.SetJsonAsync(uid, "shop_v1", s, ct);
        // Every ShopListCfg shop, also those whose goods the client tables do not carry (e.g. 15 换装商店): the client's
        // ShopData.GetShop(id) must find an entry or views such as NewHeroSkinView fail on nil.
        var shops = Cfg.GetProperty("shops").EnumerateObject().Select(p => int.Parse(p.Name)).Concat(ByShop.Keys).Distinct().Order();
        return (20009, codec.Encode("sc_20009", new Dictionary<string, object?> { ["shop_item_list"] = shops.Select(id => ShopInfo(s, id)).ToArray() }));
    }

    private JsonElement Buy(int count) { var rows = Cfg.GetProperty("buy"); var key = Math.Min(count + 1, rows.EnumerateObject().Count()).ToString(); return rows.GetProperty(key); }
    private int BuyLimit => Cfg.GetProperty("buy").EnumerateObject().Count();

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var b = r.Body;
        var srv = (ushort)(r.ServerIndex + 1);
        var (s, level) = await LoadAsync(uid, ct);
        var reply = new Dictionary<string, object?> { ["result"] = 0u };
        var costs = new List<(long, long)>();
        var grants = new List<(long, long)>();
        var changedShops = new HashSet<int>();
        try
        {
            switch (cmd)
            {
                case 20010:
                case 20016:
                    reply = new Dictionary<string, object?> { ["result"] = 0u, ["timestamp"] = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                    break;
                case 20012:
                    {
                        var shop = (int)HandlerHelpers.ReadUInt(b, "shop_id");
                        var shelf = Rolling(shop) ? s.Rolled.GetValueOrDefault(shop, []) : ByShop.GetValueOrDefault(shop, []).Select(x => x.Id).ToList();
                        var list = b.TryGetValue("buy_goods_list", out var raw) && raw is System.Collections.IEnumerable e ? e.OfType<IReadOnlyDictionary<string, object?>>().ToList() : [];
                        Require(list.Count > 0, "Nothing to buy");
                        foreach (var item in list)
                        {
                            var id = (long)HandlerHelpers.ReadUInt(item, "buy_id");
                            var num = Math.Max(1L, HandlerHelpers.ReadUInt(item, "buy_num", 1));
                            Require(shelf.Contains(id) && Cfg.GetProperty("goods").TryGetProperty(id.ToString(), out var gg), "Good not on the shelf");
                            var g = Cfg.GetProperty("goods").GetProperty(id.ToString());
                            Require(LevelOk(g, level), "Level too low");
                            var limit = N(g, "limit_num");
                            var bought = s.Bought.GetValueOrDefault(id)?.Times ?? 0;
                            Require(limit == Unlimited || limit <= 0 || bought + num <= limit, "Purchase limit reached");
                            foreach (var pre in g.GetProperty("pre").EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()))
                                if (Cfg.GetProperty("goods").TryGetProperty(pre.ToString(), out var pg) && N(pg, "limit_num") is var pl and > 0 and < Unlimited)
                                    Require((s.Bought.GetValueOrDefault(pre)?.Times ?? 0) >= pl, "Previous tier not sold out");
                            var cheap = N(g, "cheap_cost") > 0 && (N(g, "discount") != 0 || N(g, "cost") == 0 || HandlerHelpers.ReadUInt(item, "buy_type") == 1);
                            var costId = cheap && N(g, "cheap_cost_id") > 0 ? N(g, "cheap_cost_id") : N(g, "cost_id");
                            var unit = cheap ? N(g, "cheap_cost") : N(g, "cost");
                            Require(costId > 0 || unit == 0, "Paid goods are not sold");
                            if (unit > 0) costs.Add((costId, unit * num));
                            grants.Add((N(g, "give_id"), Math.Max(1, N(g, "give")) * num));
                            var rec = s.Bought.TryGetValue(id, out var x) ? x : s.Bought[id] = new Bought();
                            rec.Times += num;
                            if (rec.Reset == 0) rec.Reset = NextReset(I(g, "refresh_cycle"));
                        }
                        changedShops.Add(shop);
                        reply["give_items"] = HandlerHelpers.ToItemNetRecList(grants);
                        reply["cost_items"] = HandlerHelpers.ToItemNetRecList(costs);
                        break;
                    }
                case 20014:
                    {
                        var shop = (int)HandlerHelpers.ReadUInt(b, "shop_id");
                        Require(Rolling(shop), "Shop cannot be refreshed");
                        var n = s.Refreshes.GetValueOrDefault(shop);
                        Require(n < I(Cfg.GetProperty("shops").GetProperty(shop.ToString()), "refresh_num_limit"), "Refresh limit reached");
                        costs.Add((1, N(Buy(n), "refresh")));
                        s.Refreshes[shop] = n + 1;
                        foreach (var id in s.Rolled.GetValueOrDefault(shop, [])) s.Bought.Remove(id);
                        s.Rolled[shop] = Roll(shop, level);
                        changedShops.Add(shop);
                        break;
                    }
                case 20018:
                case 20020:
                    break;
                case 15012:
                    {
                        Require(s.GoldBuys < BuyLimit, "Daily limit reached");
                        var row = Buy(s.GoldBuys);
                        costs.Add((1, N(row, "money_cost")));
                        grants.Add((2, N(row, "money")));
                        s.GoldBuys++;
                        reply["reward_list"] = HandlerHelpers.ToItemNetRecList(grants);
                        break;
                    }
                case 15016:
                    {
                        var times = Math.Max(1, (int)HandlerHelpers.ReadUInt(b, "num", 1));
                        Require(s.FatigueBuys + times <= BuyLimit, "Daily limit reached");
                        for (var i = 0; i < times; i++)
                        {
                            var row = Buy(s.FatigueBuys);
                            costs.Add((1, N(row, "fatigue_cost")));
                            grants.Add((4, N(row, "fatigue")));
                            s.FatigueBuys++;
                        }
                        reply["reward_list"] = HandlerHelpers.ToItemNetRecList(grants.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))));
                        break;
                    }
            }
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)]; }

        var c2 = costs.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).Where(x => x.Item2 > 0).ToList();
        var g2 = grants.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).Where(x => x.Item2 > 0).ToList();
        if ((c2.Count > 0 || g2.Count > 0) && !await accounts.ConsumeAndGrantAsync(uid, c2, g2, ct))
            return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, cmd == 20012 ? 12u : 2u)];
        await accounts.SetJsonAsync(uid, "shop_v1", s, ct);
        var frames = await InventorySync.FramesAsync(accounts, codec, r, c2.Concat(g2).Select(x => x.Item1), ct);
        foreach (var shop in changedShops)
            frames.Add(new GameResponse(20005, 0, srv, codec.Encode("sc_20005", new Dictionary<string, object?> { ["changed_shop_info"] = ShopInfo(s, shop) })));
        if (cmd == 20016)
            frames.Add(new GameResponse(20009, 0, srv, (await LoginFrameAsync(uid, ct)).Item2));
        if (cmd == 20012 && g2.Any(x => ItemKinds.IsHero(x.Item1) || ItemKinds.IsPiece(x.Item1)))
            frames.Add(new GameResponse(14019, 0, srv, codec.Encode("sc_14019", new Dictionary<string, object?>
            { ["piece_list"] = (await accounts.GetHeroPiecesAsync(uid, ct)).Select(p => (object)new Dictionary<string, object?> { ["id"] = (uint)p.Hero, ["num"] = (uint)p.Num }).ToArray() })));
        frames.Add(HandlerHelpers.Reply(codec, r, cmd + 1, reply));
        return frames;
    }

    public static readonly uint[] Commands = [15012, 15016, 20010, 20012, 20014, 20016, 20018, 20020];
}
