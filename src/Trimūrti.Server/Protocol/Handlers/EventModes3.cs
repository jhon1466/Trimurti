using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

// ======================================================================================================
// 现时无返之途·薇儿丹蒂 (SP hero challenge, stage type 66): story stages (first-clear drop), three training groups
// (proficiency up to 3000 with the native gain table), erosion bosses (daily fights, base + time score, reward pool);
// four rewarded runs per day (05:00 UTC+8).
// ======================================================================================================
public sealed class SpheroMode(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
    : EventModeBase(accounts, config, codec, "sphero_contract.json"), IStageMode
{
    private const uint Activity = 242841, BossActivity = 242851;

    public sealed class State
    {
        public string Day { get; set; } = "";
        public int Rewarded { get; set; }
        public int BossFights { get; set; }
        public List<long> BossToday { get; set; } = [];
        public Dictionary<long, long> Best { get; set; } = [];
        public List<long> PoolDraws { get; set; } = [];
        public List<long> Stories { get; set; } = [];
        public Dictionary<long, long> Train { get; set; } = [];     // stage → value of its group when last passed
        public Dictionary<int, long> GroupValue { get; set; } = []; // group 2/3/4 proficiency
    }

    private JsonElement Stage(long id) => Cfg.GetProperty("stages").GetProperty(id.ToString());
    private int SubType(long id) => Stage(id).GetProperty("sub_type").GetInt32();
    private JsonElement Setting(string k) => Cfg.GetProperty("settings").GetProperty(k);
    private IEnumerable<long> StagesOfSub(Func<int, bool> f) => Cfg.GetProperty("stages").EnumerateObject().Where(p => f(p.Value.GetProperty("sub_type").GetInt32())).Select(p => long.Parse(p.Name));

    public bool Reserved(uint type, long stage) => type == 66 && Cfg.GetProperty("stages").TryGetProperty(stage.ToString(), out _);

    private async Task<State> LoadAsync(long uid, CancellationToken ct)
    {
        var s = await Accounts.GetJsonAsync<State>(uid, "sphero_v1", ct) ?? new State();
        var day = GameClock.Today.ToString("yyyy-MM-dd");
        if (s.Day != day) { s.Day = day; s.Rewarded = 0; s.BossFights = 0; s.BossToday = []; }
        return s;
    }

    private long Gain(long stage, long value)
    {
        var group = SubType(stage);
        var order = StagesOfSub(t => t == group).OrderBy(x => x).ToList().IndexOf(stage) + 1;
        var levels = Setting("activity_hero_challenge_train_num_" + order).EnumerateArray().Select(x => (x[0].GetInt64(), x[1].GetInt64())).ToList();
        return levels.FirstOrDefault(l => value <= l.Item1, levels[^1]).Item2;
    }

    private Dictionary<string, object?> TrainPayload(State s) => new()
    {
        ["activity_id"] = Activity,
        ["train_list"] = new[] { 2, 3, 4 }.Select(g => (object)new Dictionary<string, object?>
        {
            ["type"] = (uint)g, ["value"] = (uint)s.GroupValue.GetValueOrDefault(g), ["passed_level"] = s.Train.Keys.Where(k => SubType(k) == g).OrderBy(x => x).Select(x => (uint)x).ToArray()
        }).ToArray()
    };

    private Dictionary<string, object?> BossPayload(State s) => new()
    {
        ["activity_id"] = BossActivity, ["stage_id_list"] = s.BossToday.Distinct().OrderBy(x => x).Select(x => (uint)x).ToArray(), ["fight_cnt"] = (uint)s.BossFights,
        ["score_info_list"] = s.Best.OrderBy(x => x.Key).Select(b => (object)new Dictionary<string, object?> { ["stage_id"] = (uint)b.Key, ["score"] = (uint)b.Value }).ToArray(),
        ["is_start"] = true, ["got_award_cfg_list"] = s.PoolDraws.Select(x => (uint)x).ToArray()
    };

    public async Task<(List<GameResponse>, IReadOnlyList<(long, long)>)> SettleAsync(ModeBattle b, CancellationToken ct)
    {
        var s = await LoadAsync(b.Uid, ct);
        var stage = b.StageId;
        var sub = SubType(stage);
        foreach (var prev in Cfg.GetProperty("stages").EnumerateObject())
            if (prev.Value.GetProperty("next_unlock_id_list") is { ValueKind: JsonValueKind.Array } next && next.EnumerateArray().Any(x => x.GetInt64() == stage))
                Require(s.Stories.Contains(long.Parse(prev.Name)) || s.Train.ContainsKey(long.Parse(prev.Name)), "SP hero previous stage not cleared");
        if (sub != 1) Require(StagesOfSub(t => t == 1).All(s.Stories.Contains), "SP hero needs every story stage");
        if (!b.Win) return ([], []);
        var first = sub == 1 && !s.Stories.Contains(stage);
        var eligible = sub != 1 && s.Rewarded < Setting("activity_hero_challenge_max_num")[0].GetInt32();
        var frames = new List<GameResponse>();
        if (sub == 1) { if (first) s.Stories.Add(stage); }
        else if (sub is >= 2 and <= 4)
        {
            var value = s.GroupValue.GetValueOrDefault(sub);
            if (eligible) value = Math.Min(3000, value + Gain(stage, value));
            s.GroupValue[sub] = value;
            s.Train[stage] = value;
        }
        else
        {
            var full = Setting("activity_hero_challenge_boss_basescore")[0].GetInt64() + Setting("activity_hero_challenge_boss_timescore")[1].GetInt64();
            s.Best[stage] = Math.Max(s.Best.GetValueOrDefault(stage), full);
            s.BossFights++;
            if (!s.BossToday.Contains(stage)) s.BossToday.Add(stage);
        }
        var drop = Cfg.GetProperty("drops").GetProperty(Stage(stage).GetProperty("drop_lib_id").GetInt64().ToString());
        var raw = first && drop.GetProperty("base_drop").ValueKind == JsonValueKind.Array ? drop.GetProperty("base_drop")
            : eligible && drop.GetProperty("weight_drop").ValueKind == JsonValueKind.Array ? drop.GetProperty("weight_drop") : default;
        var items = raw.ValueKind == JsonValueKind.Array ? raw.EnumerateArray().Where(x => x[2].GetInt32() == 100).Select(x => (x[0].GetInt64(), x[1].GetInt64())).ToList() : [];
        if (sub == 5 && eligible)
        {
            var remaining = Cfg.GetProperty("boss_pool").EnumerateObject()
                .SelectMany(p => Enumerable.Repeat(long.Parse(p.Name), Math.Max(0, p.Value.GetProperty("total").GetInt32() - s.PoolDraws.Count(d => d == long.Parse(p.Name))))).ToList();
            if (remaining.Count > 0)
            {
                var pick = remaining[Random.Shared.Next(remaining.Count)];
                s.PoolDraws.Add(pick);
                var rw = Cfg.GetProperty("boss_pool").GetProperty(pick.ToString()).GetProperty("reward");
                items.Add((rw[0].GetInt64(), rw[1].GetInt64()));
            }
        }
        if (eligible) s.Rewarded++;
        await Accounts.SetJsonAsync(b.Uid, "sphero_v1", s, ct);
        var granted = items.Count > 0 ? await Accounts.GrantItemsAsync(b.Uid, items.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))), ct) : [];
        frames.AddRange(await SyncAsync(b.Request, granted.Select(x => x.Item1), ct));
        if (sub is >= 2 and <= 4) frames.Add(Push(b.Srv, 83015, TrainPayload(s)));
        if (sub == 5) frames.Add(Push(b.Srv, 83020, BossPayload(s)));
        return (frames, granted);
    }

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(ProtoCodec codec, long uid, CancellationToken ct) => [Frame(83020, BossPayload(await LoadAsync(uid, ct)))];
}

// ======================================================================================================
// 刺桐港埠漫行纪 (Quanzhou): sea voyages (stage type 84, dest = voyage id) grant first-clear drops; sandbox battles
// (type 85) record their clear. 79602 saves the sandbox position.
// ======================================================================================================
public sealed class QuanzhouMode(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
    : EventModeBase(accounts, config, codec, "quanzhou_contract.json"), IStageMode
{
    private const uint Activity = 323641;
    public sealed class State { public List<long> Completed { get; set; } = []; public List<long> SandCleared { get; set; } = []; public Dictionary<string, double>? Position { get; set; } }

    private JsonElement Sea => Cfg.GetProperty("sea_stages");
    private bool IsSea(long id) => Sea.TryGetProperty(id.ToString(), out _);
    private long? SeaOfReal(long real) => Sea.EnumerateObject().Where(p => p.Value.GetProperty("stage_id").GetInt64() == real).Select(p => (long?)long.Parse(p.Name)).FirstOrDefault();

    public bool Reserved(uint type, long stage) => type is 84 or 85 || IsSea(stage) || Cfg.GetProperty("sand_battles").TryGetProperty(stage.ToString(), out _) || SeaOfReal(stage) is not null;

    private async Task<State> LoadAsync(long uid, CancellationToken ct) => await Accounts.GetJsonAsync<State>(uid, "quanzhou_v1", ct) ?? new State();

    public async Task<(List<GameResponse>, IReadOnlyList<(long, long)>)> SettleAsync(ModeBattle b, CancellationToken ct)
    {
        if (!b.Win) return ([], []);
        var s = await LoadAsync(b.Uid, ct);
        IReadOnlyList<(long, long)> granted = [];
        var sea = IsSea(b.StageId) ? b.StageId : SeaOfReal(b.StageId);
        if (sea is { } id)
        {
            var stage = Sea.GetProperty(id.ToString());
            var pre = stage.GetProperty("pre_stage").GetInt64();
            Require(pre == 0 || s.Completed.Contains(pre), "Voyage prerequisite not completed");
            if (!s.Completed.Contains(id))
            {
                s.Completed.Add(id);
                var reward = stage.GetProperty("reward").GetInt64();
                if (reward != 0 && Cfg.GetProperty("drops").TryGetProperty(reward.ToString(), out var drop))
                    granted = await GrantOnceAsync(b.Uid, "quanzhou_sea_first", id.ToString(),
                        drop.GetProperty("base_drop").EnumerateArray().Where(x => x[2].GetInt32() == 100).Select(x => (x[0].GetInt64(), x[1].GetInt64())).ToArray(), ct);
            }
        }
        else if (!s.SandCleared.Contains(b.StageId)) s.SandCleared.Add(b.StageId);
        await Accounts.SetJsonAsync(b.Uid, "quanzhou_v1", s, ct);
        var frames = await SyncAsync(b.Request, granted.Select(x => x.Item1), ct);
        var real = sea is { } sid ? Sea.GetProperty(sid.ToString()).GetProperty("stage_id").GetInt64() : b.StageId;
        if (await new QuanzhouQuests(Accounts, SharedConfig, Codec).BattleClearAsync(b.Uid, b.Srv, real, ct) is { } questDiff) frames.Add(questDiff);
        frames.Add(Push(b.Srv, 86035, SeaPayload(s)));
        return (frames, granted);
    }

    private static Dictionary<string, object?> SeaPayload(State s) => new()
    {
        ["data"] = new Dictionary<string, object?> { ["affix"] = Array.Empty<uint>(), ["stage_id_list"] = s.Completed.OrderBy(x => x).Select(x => (uint)x).ToArray() }
    };

    private static Dictionary<string, object?> PositionPayload(State s) => new()
    {
        ["activity_id"] = Activity, ["stage_list"] = s.Position is null ? Array.Empty<object>() : new object[] { s.Position.ToDictionary(k => k.Key, k => k.Key is "stage_id" or "map_status" ? (object?)(uint)k.Value : (float)k.Value) }
    };

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(ProtoCodec codec, long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        return [Frame(86035, SeaPayload(s)), Frame(79601, PositionPayload(s))];
    }

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        try
        {
            Require(HandlerHelpers.ReadUInt(r.Body, "activity_id") == Activity, "Sandbox activity mismatch");
            Require(r.Body.GetValueOrDefault("stage_list") is System.Collections.IList { Count: 1 } list && list[0] is IReadOnlyDictionary<string, object?>, "Save one sandbox map");
            var p = (IReadOnlyDictionary<string, object?>)((System.Collections.IList)r.Body["stage_list"]!)[0]!;
            Require(HandlerHelpers.ReadUInt(p, "stage_id") == 1110101 && HandlerHelpers.ReadUInt(p, "map_status") == 0, "Sandbox map mismatch");
            var keep = new Dictionary<string, double> { ["stage_id"] = 1110101, ["map_status"] = 0 };
            foreach (var k in new[] { "position_x", "position_y", "position_z", "rotation_x", "rotation_y", "rotation_z", "rotation_w" })
            {
                var v = Convert.ToDouble(p.GetValueOrDefault(k) ?? 0.0);
                Require(double.IsFinite(v) && Math.Abs(v) <= 10000000, "Invalid sandbox coordinate");
                keep[k] = v;
            }
            var s = await LoadAsync(r.AccountId, ct);
            s.Position = keep;
            await Accounts.SetJsonAsync(r.AccountId, "quanzhou_v1", s, ct);
            return [HandlerHelpers.ResultOnly(Codec, r, 79603)];
        }
        catch (Exception e) when (e is ModeError or InvalidCastException or FormatException) { return [HandlerHelpers.ResultOnly(Codec, r, 79603, 2)]; }
    }
}
