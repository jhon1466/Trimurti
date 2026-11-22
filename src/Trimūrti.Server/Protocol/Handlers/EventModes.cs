using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

public sealed class ModeError(string message) : Exception(message);

/// <summary>One finished battle handed to an event mode (stage type / activity from the 54030 common_info).</summary>
public sealed record ModeBattle(GameRequest Request, ulong BattleId, long StageId, uint StageType, uint ActivityId, uint Outcome,
    IReadOnlyList<long> Heroes, BattleStats Stats)
{
    public bool Win => Outcome == 1;
    public ushort Srv => (ushort)(Request.ServerIndex + 1);
    public long Uid => Request.AccountId;
}

/// <summary>
/// Event modes own their stages: settlement gives only the mode's own rewards (no ordinary stage drops/EXP/fatigue),
/// like the reserved branches of Alpha-Persei middleware._settle_frames.
/// </summary>
public interface IStageMode
{
    bool Reserved(uint stageType, long stageId);
    Task<(List<GameResponse> Frames, IReadOnlyList<(long, long)> Drops)> SettleAsync(ModeBattle b, CancellationToken ct);
    Task<IReadOnlyList<(uint Cmd, byte[] Payload)>> LoginFramesAsync(ProtoCodec codec, long uid, CancellationToken ct);
}

public sealed class StageModes(IEnumerable<IStageMode> modes)
{
    public IReadOnlyList<IStageMode> All { get; } = modes.ToList();
    public IStageMode? Find(uint type, long stage) => All.FirstOrDefault(m => m.Reserved(type, stage));
}

/// <summary>Shared helpers for contract-driven event modes.</summary>
public abstract class EventModeBase(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec, string contract)
{
    protected readonly GameAccountRepository Accounts = accounts;
    protected readonly ProtoCodec Codec = codec;
    protected JsonElement Cfg => SharedConfig.Get(contract) ?? throw new ModeError($"Missing {contract}");
    protected static void Require(bool ok, string message) { if (!ok) throw new ModeError(message); }
    protected GameResponse Push(ushort srv, uint cmd, Dictionary<string, object?> body) => new(cmd, 0, srv, Codec.Encode($"sc_{cmd}", body));
    protected (uint, byte[]) Frame(uint cmd, Dictionary<string, object?> body) => (cmd, Codec.Encode($"sc_{cmd}", body));
    protected static IReadOnlyList<(long, long)> Pairs(JsonElement arr) =>
        arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2).Select(p => (p[0].GetInt64(), p[1].GetInt64())).ToArray() : [];
    protected static object[] Items(IEnumerable<(long, long)> items) => HandlerHelpers.ToItemNetRecList(items);

    /// <summary>Grants once per (kind,key) through the claim ledger.</summary>
    protected async Task<IReadOnlyList<(long, long)>> GrantOnceAsync(long uid, string kind, string key, IReadOnlyList<(long, long)> items, CancellationToken ct)
    {
        if (items.Count == 0 || !await Accounts.TryClaimAsync(uid, kind, key, ct)) return [];
        return await Accounts.GrantItemsAsync(uid, items, ct);
    }

    protected async Task<List<GameResponse>> SyncAsync(GameRequest r, IEnumerable<long> touched, CancellationToken ct) =>
        await InventorySync.FramesAsync(Accounts, Codec, r, touched.Distinct(), ct);

    /// <summary>Projects a mode fact into the task system (set-style conditions keyed by stage/node).</summary>
    protected async Task TaskAsync(List<GameResponse> frames, ModeBattle b, int condition, long param, long value = 1)
    {
        if (await new TaskSystem(Accounts, SharedConfig, Codec).EventAsync(b.Uid, b.Srv, s => new TaskSystem(Accounts, SharedConfig, Codec).Trigger(s, condition, value, param, set: true), CancellationToken.None) is { } d)
            frames.Add(d);
    }
    protected SourceGameConfig SharedConfig { get; } = config;
}

// ======================================================================================================
// 列车大劫案 (Ash shooting, stage type 89): first-clear film (item 79); extra stages score 100/enemy.
// ======================================================================================================
public sealed class AshMode(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
    : EventModeBase(accounts, config, codec, "ash_contract.json"), IStageMode
{
    private Dictionary<long, (long Id, JsonElement Cfg)>? _real;
    private Dictionary<long, (long Id, JsonElement Cfg)> Real => _real ??= Cfg.GetProperty("stages").EnumerateObject()
        .ToDictionary(p => p.Value.GetProperty("stage_id").GetInt64(), p => (long.Parse(p.Name), p.Value));

    public bool Reserved(uint type, long stage) => Real.ContainsKey(stage);

    private Task<Dictionary<long, long>> ReadAsync(long uid, CancellationToken ct) => GetAsync(uid, ct);
    private async Task<Dictionary<long, long>> GetAsync(long uid, CancellationToken ct) => await Accounts.GetJsonAsync<Dictionary<long, long>>(uid, "ash_v1", ct) ?? [];

    public async Task<(List<GameResponse>, IReadOnlyList<(long, long)>)> SettleAsync(ModeBattle b, CancellationToken ct)
    {
        var (stage, cfg) = Real[b.StageId];
        var type = cfg.GetProperty("type").GetInt32();
        if (type == 1 && !b.Win) return ([], []);
        var records = await ReadAsync(b.Uid, ct);
        var front = cfg.GetProperty("front_unlock_id").GetInt64();
        Require(front == 0 || records.ContainsKey(front), "Ash front stage not cleared");
        var points = type == 2 ? Math.Min(uint.MaxValue, (long)b.Stats.EnemyDeadNum * Cfg.GetProperty("scoring").GetProperty("points_per_enemy").GetInt64()) : 0;
        var first = !records.ContainsKey(stage);
        records[stage] = Math.Max(records.GetValueOrDefault(stage), points);
        await Accounts.SetJsonAsync(b.Uid, "ash_v1", records, ct);
        var film = cfg.GetProperty("film").GetInt64();
        var granted = first && film > 0 ? await GrantOnceAsync(b.Uid, "ash_stage_first", stage.ToString(), [(79, film)], ct) : [];
        var frames = await SyncAsync(b.Request, granted.Select(x => x.Item1), ct);
        await TaskAsync(frames, b, 380001, stage);
        if (first || type == 2)
            frames.Add(Push(b.Srv, 68183, new() { ["pass_stage"] = new Dictionary<string, object?> { ["stage_id"] = (uint)stage, ["point"] = (uint)points } }));
        return (frames, granted);
    }

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(ProtoCodec codec, long uid, CancellationToken ct)
    {
        var r = await ReadAsync(uid, ct);
        return [Frame(68181, new() { ["pass_stages"] = r.OrderBy(x => x.Key).Select(x => (object)new Dictionary<string, object?> { ["stage_id"] = (uint)x.Key, ["point"] = (uint)x.Value }).ToArray() })];
    }
}

// ======================================================================================================
// 战术研习 (Osiris study, stage type 86): stage records; sandbox stages score 800000 + 5·(120000 − ms).
// ======================================================================================================
public sealed class OsirisMode(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
    : EventModeBase(accounts, config, codec, "osiris_contract.json"), IStageMode
{
    public sealed record Rec(long PassTime, long Point);

    public bool Reserved(uint type, long stage) => Cfg.GetProperty("stages").TryGetProperty(stage.ToString(), out _);

    private async Task<Dictionary<long, Rec>> ReadAsync(long uid, CancellationToken ct) => await Accounts.GetJsonAsync<Dictionary<long, Rec>>(uid, "osiris_v1", ct) ?? [];
    private bool Rank(long stage) => Cfg.GetProperty("stages").GetProperty(stage.ToString()).GetProperty("rank_activity").GetInt64() != 0;

    private List<long> Chapters(Dictionary<long, Rec> recs) => Cfg.GetProperty("chapters").EnumerateObject()
        .Where(c => c.Value.GetProperty("type").GetInt32() == 1 && c.Value.GetProperty("stages").EnumerateArray().All(s => recs.ContainsKey(s.GetInt64())))
        .Select(c => long.Parse(c.Name)).ToList();

    private long Score(long stage, long ms)
    {
        if (!Rank(stage)) return 0;
        var s = Cfg.GetProperty("scoring");
        var bonus = ms > 0 ? Math.Max(0, s.GetProperty("base_time").GetInt64() - ms) * s.GetProperty("base_factor").GetInt64() : 0;
        return Math.Min(uint.MaxValue, s.GetProperty("base_point").GetInt64() + bonus);
    }

    public async Task<(List<GameResponse>, IReadOnlyList<(long, long)>)> SettleAsync(ModeBattle b, CancellationToken ct)
    {
        if (!b.Win) return ([], []);
        var recs = await ReadAsync(b.Uid, ct);
        var cfg = Cfg.GetProperty("stages").GetProperty(b.StageId.ToString());
        var pre = cfg.GetProperty("pre_stage").GetInt64();
        Require(pre == 0 || recs.ContainsKey(pre), "Osiris prerequisite not cleared");
        var normal = Cfg.GetProperty("stages").EnumerateObject().Where(p => p.Value.GetProperty("rank_activity").GetInt64() == 0).Select(p => long.Parse(p.Name)).ToList();
        if (Rank(b.StageId)) Require(normal.All(recs.ContainsKey), "Osiris sandbox needs every study stage");
        var first = !recs.ContainsKey(b.StageId);
        var oldChapters = Chapters(recs).ToHashSet();
        var cur = new Rec(b.Stats.BattleTimeMs, Score(b.StageId, b.Stats.BattleTimeMs));
        if (first || cur.Point >= recs[b.StageId].Point) recs[b.StageId] = cur;
        await Accounts.SetJsonAsync(b.Uid, "osiris_v1", recs, ct);
        var frames = new List<GameResponse>();
        Dictionary<string, object?> Stage(long id, Rec r) => new() { ["stage_id"] = (uint)id, ["pass_time"] = (uint)r.PassTime, ["point"] = (uint)r.Point };
        if (first) frames.Add(Push(b.Srv, 68173, new() { ["pass_stages"] = new object[] { Stage(b.StageId, cur) } }));
        var newCh = Chapters(recs).Where(c => !oldChapters.Contains(c)).ToList();
        if (newCh.Count > 0) frames.Add(Push(b.Srv, 68175, new() { ["pass_chapters"] = newCh.Select(c => (uint)c).ToArray() }));
        frames.Add(Push(b.Srv, 68177, new() { ["pass_stage"] = Stage(b.StageId, cur) }));
        await TaskAsync(frames, b, 340001, b.StageId);
        return (frames, []);
    }

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(ProtoCodec codec, long uid, CancellationToken ct)
    {
        var recs = await ReadAsync(uid, ct);
        return [Frame(68171, new()
        {
            ["pass_stages"] = recs.OrderBy(x => x.Key).Select(x => (object)new Dictionary<string, object?> { ["stage_id"] = (uint)x.Key, ["pass_time"] = (uint)x.Value.PassTime, ["point"] = (uint)x.Value.Point }).ToArray(),
            ["pass_chapters"] = Chapters(recs).Select(c => (uint)c).ToArray()
        })];
    }
}

// ======================================================================================================
// 溯梦之隙·心象 (Skuld, stage type 602): node graph; first completion grants node rewards; archives unlock.
// ======================================================================================================
public sealed class SkuldMode(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
    : EventModeBase(accounts, config, codec, "skuld_contract.json"), IStageMode
{
    public sealed class State { public List<long> Marks { get; set; } = []; public Dictionary<long, long> Missions { get; set; } = []; public List<long> Archives { get; set; } = []; }

    private JsonElement Nodes => Cfg.GetProperty("nodes");
    private long? NodeOf(long dest)
    {
        if (Nodes.TryGetProperty(dest.ToString(), out var n) && n.GetProperty("type").GetInt32() == 1) return dest;
        foreach (var p in Nodes.EnumerateObject())
            if (p.Value.GetProperty("type").GetInt32() == 1 && p.Value.GetProperty("param").GetInt64() == dest) return long.Parse(p.Name);
        return null;
    }

    public bool Reserved(uint type, long stage) => type == 602 && NodeOf(stage) is not null;

    public async Task<State> LoadAsync(long uid, CancellationToken ct) => await Accounts.GetJsonAsync<State>(uid, "skuld_v1", ct) ?? new State();

    private bool Unlocked(State s, long node) => Nodes.GetProperty(node.ToString()).GetProperty("pre_id").EnumerateArray().All(p => s.Missions.ContainsKey(p.GetInt64()));

    public async Task<(IReadOnlyList<(long, long)> Granted, List<long> NewArchives)> CompleteAsync(long uid, State s, long node, CancellationToken ct)
    {
        Require(Unlocked(s, node), "Skuld prerequisite not completed");
        var first = !s.Missions.ContainsKey(node);
        s.Missions[node] = s.Missions.GetValueOrDefault(node) + 1;
        var granted = first ? await GrantOnceAsync(uid, "skuld_first", node.ToString(), Pairs(Nodes.GetProperty(node.ToString()).GetProperty("rewards")), ct) : [];
        var newArch = Cfg.GetProperty("archives").EnumerateObject()
            .Where(a => !s.Archives.Contains(long.Parse(a.Name)) && a.Value.GetProperty("nodes").EnumerateArray().All(n => s.Missions.ContainsKey(n.GetInt64())))
            .Select(a => long.Parse(a.Name)).ToList();
        s.Archives.AddRange(newArch);
        await Accounts.SetJsonAsync(uid, "skuld_v1", s, ct);
        return (granted, newArch);
    }

    public async Task<(List<GameResponse>, IReadOnlyList<(long, long)>)> SettleAsync(ModeBattle b, CancellationToken ct)
    {
        if (!b.Win) return ([], []);
        var node = NodeOf(b.StageId) ?? throw new ModeError("Unknown Skuld node");
        var s = await LoadAsync(b.Uid, ct);
        var (granted, arch) = await CompleteAsync(b.Uid, s, node, ct);
        var frames = await SyncAsync(b.Request, granted.Select(x => x.Item1), ct);
        if (arch.Count > 0) frames.Add(Push(b.Srv, 24045, new() { ["plot_list"] = arch.Select(x => (uint)x).ToArray() }));
        frames.Add(Push(b.Srv, 24051, await PayloadAsync(b.Uid, s, ct)));
        await TaskAsync(frames, b, 329001, node);
        return (frames, granted);
    }

    public async Task<Dictionary<string, object?>> PayloadAsync(long uid, State s, CancellationToken ct) => new()
    {
        ["activity_id"] = Cfg.GetProperty("activity_id").GetUInt32(), ["client_opt"] = s.Marks.Select(x => (uint)x).ToArray(),
        ["mission_list"] = s.Missions.OrderBy(x => x.Key).Select(m => (object)new Dictionary<string, object?> { ["id"] = (uint)m.Key, ["times"] = (uint)m.Value }).ToArray(),
        ["reward_list"] = Array.Empty<uint>(), ["point"] = (uint)await Accounts.GetItemCountAsync(uid, 76, ct)
    };

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(ProtoCodec codec, long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        return [Frame(24051, await PayloadAsync(uid, s, ct)), Frame(24043, new() { ["plot_list"] = s.Archives.Select(x => (uint)x).ToArray() })];
    }

    /// <summary>24052 save a mark / 24054 complete a puzzle node (type 2).</summary>
    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        try
        {
            Require(HandlerHelpers.ReadUInt(r.Body, "activity_id") == Cfg.GetProperty("activity_id").GetUInt32(), "Skuld activity mismatch");
            var s = await LoadAsync(r.AccountId, ct);
            if (cmd == 24052)
            {
                var key = HandlerHelpers.ReadUInt(r.Body, "key");
                Require(Cfg.GetProperty("marks").EnumerateArray().Any(m => m.GetInt64() == key), "Unknown Skuld mark");
                if (!s.Marks.Contains(key)) { s.Marks.Add(key); await Accounts.SetJsonAsync(r.AccountId, "skuld_v1", s, ct); }
                return [HandlerHelpers.ResultOnly(Codec, r, 24053)];
            }
            var node = (long)HandlerHelpers.ReadUInt(r.Body, "id");
            Require(Nodes.TryGetProperty(node.ToString(), out var n) && n.GetProperty("type").GetInt32() == 2, "Unknown Skuld puzzle node");
            var (granted, arch) = await CompleteAsync(r.AccountId, s, node, ct);
            var frames = await SyncAsync(r, granted.Select(x => x.Item1), ct);
            if (arch.Count > 0) frames.Add(Push((ushort)(r.ServerIndex + 1), 24045, new() { ["plot_list"] = arch.Select(x => (uint)x).ToArray() }));
            frames.Add(HandlerHelpers.Reply(Codec, r, 24055, new Dictionary<string, object?> { ["result"] = 0u, ["reward_list"] = Items(granted) }));
            return frames;
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(Codec, r, cmd + 1, 2)]; }
    }
}

/// <summary>Routes plain mode operations (cmd → mode.HandleAsync).</summary>
public sealed class ModeCommandHandler(uint command, Func<GameRequest, uint, CancellationToken, Task<IReadOnlyList<GameResponse>>> handle) : IGameCommandHandler
{
    public uint Command => command;
    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct) => await handle(r, command, ct);
}
