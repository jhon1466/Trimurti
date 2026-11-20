using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// Small resident systems whose login state used to be the schema default (battle_system_cfg.json, from the client
/// StageGroupCfg and GameSetting fm_* / equip_stage_unlock):
///   附魔战 BattleEnchantAction: 42001 state, 42002 request (push 42001), 42004 refresh (1 free, 2 paid).
///     Three StageGroupCfg type-8 groups are on offer; free refreshes fm_free_refresh_limit_count a day, paid ones
///     fm_item_refresh_limit_count a day at fm_item_refresh_cost[n]. Day boundary 05:00.
///   装备战 BattleEquipAction: 43001 state (today's StageGroupCfg type-9 base group, chosen UP suit, next refresh,
///     guarantee counters), 43002 request, 43004 choose the UP suit.
///   ActivityUnlockStateAction: 85105 per-activity key/value flags, 85106 set one (client push, no reply).
///   列车大劫案 momotalk (AshSystemAction): 12119 read momotalk ids, 12120 mark one read.
/// </summary>
public sealed class SystemStates(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    public static readonly uint[] Commands = [42002, 42004, 43002, 43004, 85106, 12120];

    public sealed class Enchant
    {
        public long Day { get; set; }
        public uint Free { get; set; }
        public uint Bought { get; set; }
        public List<uint> Groups { get; set; } = [];
    }

    public sealed class Equip
    {
        public uint Suit { get; set; }
        public Dictionary<uint, uint> Insure { get; set; } = [];
    }

    public sealed class State
    {
        public Enchant Enchant { get; set; } = new();
        public Equip Equip { get; set; } = new();
        public Dictionary<uint, Dictionary<uint, uint>> Unlock { get; set; } = [];
        public List<uint> Momotalk { get; set; } = [];
    }

    private const string Kind = "system_states_v1";
    private JsonElement Cfg => config.Get("battle_system_cfg.json") ?? throw new ModeError("Missing battle_system_cfg.json");

    private static long Today => (long)Math.Floor((GameClock.Now - TimeSpan.FromHours(5)).ToUnixTimeSeconds() / 86400.0);
    private static uint NextDay => (uint)((Today + 1) * 86400 + 5 * 3600);

    private async Task<State> LoadAsync(long uid, CancellationToken ct)
    {
        var s = await accounts.GetJsonAsync<State>(uid, Kind, ct) ?? new State();
        var e = s.Enchant;
        if (e.Day != Today || e.Groups.Count == 0)
        {
            if (e.Day != Today) { e.Free = 0; e.Bought = 0; }
            e.Day = Today;
            if (e.Groups.Count == 0) e.Groups = RollEnchant();
        }
        return s;
    }

    private List<uint> RollEnchant()
    {
        var c = Cfg.GetProperty("enchantment");
        var pool = c.GetProperty("groups").EnumerateArray().Select(x => x.GetUInt32()).ToList();
        return pool.OrderBy(_ => Random.Shared.Next()).Take(c.GetProperty("shown").GetInt32()).ToList();
    }

    /// <summary>Today's 装备战 base group: the type-9 groups rotate one per weekday (Monday first), 05:00 boundary.</summary>
    private uint EquipBase()
    {
        var groups = Cfg.GetProperty("battle_equip").GetProperty("groups").EnumerateArray().Select(x => x.GetUInt32()).ToList();
        var day = GameClock.Now - TimeSpan.FromHours(5);
        return groups[(((int)day.DayOfWeek + 6) % 7) % groups.Count];
    }

    private static Dictionary<string, object?> EnchantRec(Enchant e) => new()
    {
        ["free_refreshed_times"] = e.Free, ["all_buy_refreshed_times"] = e.Bought,
        ["enchantment_battle_list"] = e.Groups.Select(x => (object?)x).ToArray()
    };

    private Dictionary<string, object?> EquipRec(Equip e) => new()
    {
        ["stage_base_id"] = EquipBase(), ["equip_suit_id"] = e.Suit, ["next_refresh_time"] = NextDay,
        ["insure_list"] = e.Insure.OrderBy(x => x.Key).Select(x => (object?)new Dictionary<string, object?> { ["difficulty"] = x.Key, ["insure_times"] = x.Value }).ToArray()
    };

    private static Dictionary<string, object?> UnlockRec(State s) => new()
    {
        ["opt_list"] = s.Unlock.OrderBy(x => x.Key).Select(x => (object?)new Dictionary<string, object?>
        {
            ["id"] = x.Key,
            ["opt"] = x.Value.OrderBy(o => o.Key).Select(o => (object?)new Dictionary<string, object?> { ["key"] = o.Key, ["value"] = o.Value }).ToArray()
        }).ToArray()
    };

    /// <summary>
    /// 装备战 guarantee counters, as BattleEquipAction.CaculateGuaranteeNum: for each battle of a BattleEquipStageCfg stage
    /// (difficulty = stage % 10, BattleInstance.GetHardLevel), a 5-star equip among the drops resets that difficulty's
    /// counter to 0, otherwise it grows by 1.
    /// </summary>
    public static async Task RecordEquipRunsAsync(GameAccountRepository accounts, SourceGameConfig config, long uid, long stage,
        IEnumerable<IEnumerable<long>> runs, CancellationToken ct)
    {
        if (config.Get("battle_system_cfg.json") is not { } cfg
            || !cfg.GetProperty("battle_equip").GetProperty("stages").EnumerateArray().Any(x => x.GetInt64() == stage)) return;
        var equips = config.Get("sigil_cfg.json") is { } sig && sig.TryGetProperty("equips", out var e) ? e : default;
        bool FiveStar(long item) => equips.ValueKind == JsonValueKind.Object && equips.TryGetProperty(item.ToString(), out var row)
            && row.TryGetProperty("starlevel", out var star) && star.GetInt32() == 5;
        var s = await accounts.GetJsonAsync<State>(uid, Kind, ct) ?? new State();
        var diff = (uint)(stage % 10);
        foreach (var run in runs)
            s.Equip.Insure[diff] = run.Any(FiveStar) ? 0 : s.Equip.Insure.GetValueOrDefault(diff) + 1;
        await accounts.SetJsonAsync(uid, Kind, s, ct);
    }

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        await accounts.SetJsonAsync(uid, Kind, s, ct);
        return
        [
            (42001, codec.Encode("sc_42001", EnchantRec(s.Enchant))),
            (43001, codec.Encode("sc_43001", EquipRec(s.Equip))),
            (85105, codec.Encode("sc_85105", UnlockRec(s))),
            (12119, codec.Encode("sc_12119", new Dictionary<string, object?> { ["momotalk_list"] = s.Momotalk.Select(x => (object?)x).ToArray() }))
        ];
    }

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var srv = (ushort)(r.ServerIndex + 1);
        var s = await LoadAsync(uid, ct);
        var frames = new List<GameResponse>();
        var reply = new Dictionary<string, object?> { ["result"] = 0u };
        var touched = new List<long>();
        switch (cmd)
        {
            case 42002:
                await accounts.SetJsonAsync(uid, Kind, s, ct);
                return [new GameResponse(42001, 0, srv, codec.Encode("sc_42001", EnchantRec(s.Enchant)))];
            case 42004:
            {
                var c = Cfg.GetProperty("enchantment");
                var type = HandlerHelpers.ReadUInt(r.Body, "refresh_type");
                if (type == 1 && s.Enchant.Free < c.GetProperty("fm_free_refresh_limit_count").GetUInt32()) s.Enchant.Free++;
                else if (type == 2 && s.Enchant.Bought < c.GetProperty("fm_item_refresh_limit_count").GetUInt32())
                {
                    var costs = c.GetProperty("fm_item_refresh_cost");
                    var cost = costs[(int)Math.Min(s.Enchant.Bought, (uint)costs.GetArrayLength() - 1)];
                    if (!await accounts.ConsumeAndGrantAsync(uid, [(cost[0].GetInt64(), cost[1].GetInt64())], [], ct)) { reply["result"] = 1u; break; }
                    touched.Add(cost[0].GetInt64());
                    s.Enchant.Bought++;
                }
                else { reply["result"] = 1u; break; }
                s.Enchant.Groups = RollEnchant();
                reply["enchantment_battle_list"] = s.Enchant.Groups.Select(x => (object?)x).ToArray();
                break;
            }
            case 43002:
                frames.Add(new GameResponse(43001, 0, srv, codec.Encode("sc_43001", EquipRec(s.Equip))));
                break;
            case 43004:
            {
                var suit = HandlerHelpers.ReadUInt(r.Body, "equip_suit_id");
                if (suit != 0 && config.Get("equip_suit_pos_map.json") is { } map && !map.EnumerateObject().Any(e => e.Value.TryGetProperty("suit", out var v) && v.GetInt64() == suit)) { reply["result"] = 1u; break; }
                s.Equip.Suit = suit;
                break;
            }
            case 85106:
            {
                var id = HandlerHelpers.ReadUInt(r.Body, "id");
                if (id == 0 || !r.Body.TryGetValue("opt", out var o) || o is not IReadOnlyDictionary<string, object?> opt) return [];
                if (!s.Unlock.TryGetValue(id, out var kv)) s.Unlock[id] = kv = [];
                kv[HandlerHelpers.ReadUInt(opt, "key")] = HandlerHelpers.ReadUInt(opt, "value");
                await accounts.SetJsonAsync(uid, Kind, s, ct);
                return [];
            }
            case 12120:
            {
                var id = HandlerHelpers.ReadUInt(r.Body, "momotalk");
                if (id == 0) { reply["result"] = 1u; break; }
                if (!s.Momotalk.Contains(id)) s.Momotalk.Add(id);
                break;
            }
        }
        if (reply["result"] is 0u) await accounts.SetJsonAsync(uid, Kind, s, ct);
        if (touched.Count > 0) frames.InsertRange(0, await InventorySync.FramesAsync(accounts, codec, r, touched, ct));
        frames.Add(HandlerHelpers.Reply(codec, r, cmd + 1, reply));
        return frames;
    }
}
