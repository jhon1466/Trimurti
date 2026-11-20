using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 失序深阱 (abyss, 55xxx). Config: abyss_cfg.json (client AbyssCfg/AbyssBossHpCfg/GameSetting abyss_*).
/// Layers of the running season (activity 230102): a layer opens when the previous one is fully cleared
/// (AbyssData.IsLayerUnlock reads the layer entries the server sends). Normal stages (types 1/2) store completion;
/// boss stages (type 3) store score, phase and remaining HP rate. Layer/stage resets: 3 each, +1 per day (05:00).
/// 55002 reset layer (and the layers above), 55004 reset stage, 55006 layer rewards, 55012 save boss progress,
/// 55020 read the season-back flag. Settlement pushes 55017 (layer) and 55011 (boss).
/// </summary>
public sealed class AbyssMode(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
    : EventModeBase(accounts, config, codec, "abyss_cfg.json"), IStageMode
{
    public sealed class Boss { public bool Done { get; set; } public uint Score { get; set; } public uint Phase { get; set; } public uint HpRate { get; set; } = 10000; }
    public sealed class Layer { public Dictionary<uint, bool> Stages { get; set; } = []; public Dictionary<uint, Boss> Bosses { get; set; } = []; }
    public sealed class State
    {
        public uint Activity { get; set; }
        public uint HistoryMax { get; set; }
        public List<uint> Rewards { get; set; } = [];
        public Dictionary<uint, Layer> Layers { get; set; } = [];
        public uint LayerResets { get; set; }
        public uint StageResets { get; set; }
        public long Day { get; set; }
        public bool Back { get; set; }
    }

    public static readonly uint[] Commands = [55002, 55004, 55006, 55012, 55020];
    private const string Kind = "abyss_v1";

    public uint ActivityId => Cfg.GetProperty("activity_id").GetUInt32();
    private JsonElement Layers => Cfg.GetProperty("layers").GetProperty(ActivityId.ToString());
    private uint Setting(string name) => Cfg.GetProperty("settings").TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v[0].GetUInt32() : 0;
    private JsonElement? LayerCfg(uint layer) => Layers.TryGetProperty(layer.ToString(), out var l) ? l : null;
    private IEnumerable<(uint Type, uint Stage, uint Score)> StagesOf(uint layer) =>
        LayerCfg(layer) is { } l ? l.GetProperty("stages").EnumerateArray().Select(s => (s[0].GetUInt32(), s[1].GetUInt32(), s[2].GetUInt32())) : [];
    private uint? LayerOf(long stage) =>
        Layers.EnumerateObject().Where(l => l.Value.GetProperty("stages").EnumerateArray().Any(s => s[1].GetInt64() == stage)).Select(l => (uint?)uint.Parse(l.Name)).FirstOrDefault();

    private static long Today => (long)Math.Floor((GameClock.Now - TimeSpan.FromHours(5)).ToUnixTimeSeconds() / 86400.0);

    private Layer NewLayer(uint layer)
    {
        var l = new Layer();
        foreach (var (type, stage, _) in StagesOf(layer))
            if (type == 3) l.Bosses[stage] = new Boss(); else l.Stages[stage] = false;
        return l;
    }

    private async Task<State> LoadAsync(long uid, CancellationToken ct)
    {
        var s = await Accounts.GetJsonAsync<State>(uid, Kind, ct);
        if (s is null || s.Activity != ActivityId)
            s = new State { Activity = ActivityId, LayerResets = Setting("abyss_layer_reset_time"), StageResets = Setting("abyss_stage_reset_time"), Day = Today, Back = s is not null };
        if (s.Layers.Count == 0) s.Layers[1] = NewLayer(1);
        var days = Today - s.Day;
        if (days > 0)
        {
            var add = (uint)Math.Min(100, days * Setting("abyss_daily_reset_recover"));
            s.LayerResets = Math.Min(Setting("abyss_layer_reset_time"), s.LayerResets + add);
            s.StageResets = Math.Min(Setting("abyss_stage_reset_time"), s.StageResets + add);
            s.Day = Today;
        }
        return s;
    }

    private bool Complete(uint layer, Layer l) => StagesOf(layer).Any() && l.Stages.Values.All(x => x) && l.Bosses.Values.All(b => b.Done);

    private static Dictionary<string, object?> BossRec(uint stage, Boss b) => new()
    {
        ["stage_id"] = stage, ["is_completed"] = b.Done, ["score"] = b.Score, ["phase"] = b.Phase, ["boss_hp_rate"] = b.HpRate
    };

    private static Dictionary<string, object?> LayerRec(uint id, Layer l) => new()
    {
        ["layer_id"] = id,
        ["stage_info_list"] = l.Stages.OrderBy(x => x.Key).Select(x => (object?)new Dictionary<string, object?> { ["stage_id"] = x.Key, ["is_completed"] = x.Value }).ToArray(),
        ["boss_stage_info_list"] = l.Bosses.OrderBy(x => x.Key).Select(x => (object?)BossRec(x.Key, x.Value)).ToArray()
    };

    private static Dictionary<string, object?> Full(State s) => new()
    {
        ["is_back"] = s.Back, ["history_max_layer"] = s.HistoryMax,
        ["got_layer_reward_list"] = s.Rewards.Cast<object?>().ToArray(),
        ["layer_reset_time"] = s.LayerResets, ["stage_reset_time"] = s.StageResets,
        ["layer_info_list"] = s.Layers.OrderBy(x => x.Key).Select(x => (object?)LayerRec(x.Key, x.Value)).ToArray(),
        ["last_version_max_unlock_layer"] = 0u,
        ["refresh_timestamp"] = (uint)((Today + 1) * 86400 + 5 * 3600),
        ["activity_id"] = s.Activity
    };

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(ProtoCodec codec, long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        await Accounts.SetJsonAsync(uid, Kind, s, ct);
        return [Frame(55001, Full(s))];
    }

    public bool Reserved(uint type, long stage) => LayerOf(stage) is not null;

    public async Task<(List<GameResponse>, IReadOnlyList<(long, long)>)> SettleAsync(ModeBattle b, CancellationToken ct)
    {
        var s = await LoadAsync(b.Uid, ct);
        var layer = LayerOf(b.StageId)!.Value;
        Require(s.Layers.TryGetValue(layer, out var l), "Abyss layer locked");
        var stage = (uint)b.StageId;
        var frames = new List<GameResponse>();
        var cfgScore = StagesOf(layer).First(x => x.Stage == stage).Score;
        if (l!.Bosses.TryGetValue(stage, out var boss))
        {
            if (b.Win)
            {
                boss.Done = true;
                boss.HpRate = 0;
                boss.Phase = (uint)(Cfg.GetProperty("boss_hp").TryGetProperty(stage.ToString(), out var hp) ? hp.GetArrayLength() : 1);
                boss.Score = Math.Max(boss.Score, cfgScore);
            }
            frames.Add(Push(b.Srv, 55011, new() { ["layer"] = layer, ["boss_stage_info"] = BossRec(stage, boss) }));
        }
        else if (b.Win) l.Stages[stage] = true;
        if (b.Win && Complete(layer, l))
        {
            s.HistoryMax = Math.Max(s.HistoryMax, layer);
            if (LayerCfg(layer + 1) is not null && !s.Layers.ContainsKey(layer + 1)) s.Layers[layer + 1] = NewLayer(layer + 1);
        }
        await Accounts.SetJsonAsync(b.Uid, Kind, s, ct);
        frames.Add(Push(b.Srv, 55017, new() { ["history_max_layer"] = s.HistoryMax, ["layer_info"] = LayerRec(layer, l) }));
        if (s.Layers.TryGetValue(layer + 1, out var next))
            frames.Add(Push(b.Srv, 55017, new() { ["history_max_layer"] = s.HistoryMax, ["layer_info"] = LayerRec(layer + 1, next) }));
        return (frames, []);
    }

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var s = await LoadAsync(r.AccountId, ct);
        uint result = 0;
        var grants = new List<(long, long)>();
        switch (cmd)
        {
            case 55002:
            {
                var layer = Convert.ToUInt32(r.Body["layer"]);
                if (s.LayerResets == 0 || !s.Layers.ContainsKey(layer)) { result = 1; break; }
                s.LayerResets--;
                foreach (var k in s.Layers.Keys.Where(k => k > layer).ToList()) s.Layers.Remove(k);
                s.Layers[layer] = NewLayer(layer);
                break;
            }
            case 55004:
            {
                var stage = Convert.ToUInt32(r.Body["stage_id"]);
                var layer = LayerOf(stage);
                if (s.StageResets == 0 || layer is null || !s.Layers.TryGetValue(layer.Value, out var l)) { result = 1; break; }
                s.StageResets--;
                if (l.Stages.ContainsKey(stage)) l.Stages[stage] = false;
                else if (l.Bosses.ContainsKey(stage)) l.Bosses[stage] = new Boss();
                break;
            }
            case 55006:
            {
                var list = r.Body.TryGetValue("layer_list", out var v) && v is IEnumerable<object?> e ? e.Select(Convert.ToUInt32).ToList() : [];
                foreach (var layer in list)
                {
                    if (s.Rewards.Contains(layer) || !s.Layers.TryGetValue(layer, out var l) || !Complete(layer, l)) { result = 1; break; }
                    grants.AddRange(Pairs(LayerCfg(layer)!.Value.GetProperty("reward")));
                }
                if (result == 0) s.Rewards.AddRange(list);
                break;
            }
            case 55012:
                // is_save = false abandons the partial progress on that boss.
                if (!(r.Body.TryGetValue("is_save", out var save) && save is true))
                {
                    var stage = Convert.ToUInt32(r.Body["stage_id"]);
                    if (LayerOf(stage) is { } ly && s.Layers.TryGetValue(ly, out var l) && l.Bosses.TryGetValue(stage, out var bo) && !bo.Done)
                        l.Bosses[stage] = new Boss();
                }
                break;
            case 55020:
                s.Back = false;
                break;
        }
        if (result == 0 && grants.Count > 0 && !await Accounts.ConsumeAndGrantAsync(r.AccountId, [], grants, ct)) result = 1;
        if (result == 0) await Accounts.SetJsonAsync(r.AccountId, Kind, s, ct);
        var frames = new List<GameResponse>();
        if (result == 0 && cmd is 55002 or 55004)
            frames.Add(Push((ushort)(r.ServerIndex + 1), 55009, new() { ["layer_reset_time"] = s.LayerResets, ["stage_reset_time"] = s.StageResets }));
        frames.Add(HandlerHelpers.Reply(Codec, r, cmd + 1, new Dictionary<string, object?> { ["result"] = result }));
        return frames;
    }
}
