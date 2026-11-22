using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 刺桐 sandbox quests (port of Alpha-Persei quanzhou_quest on quanzhou_contract.json): main quests unlock by
/// conditions (100 main done, 101 sub done, 1014 player level), sub-objectives advance by client parameters
/// (condition 60) or validated battles (330001/330002); 28820 claim main, 28822 complete sub, 28830 take main,
/// 28832 parameter, 28834 unlock function. Frames 28801 (login), 28803 (diff), 28837 (functions).
/// </summary>
public sealed class QuanzhouQuests(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    private JsonElement Cfg => config.Get("quanzhou_contract.json") ?? throw new ModeError("Missing quanzhou_contract.json");
    private static void Require(bool ok, string m) { if (!ok) throw new ModeError(m); }
    private static int I(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    private static List<long> L(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList() : [];

    public sealed class Sub { public int Status { get; set; } = -100; public long Progress { get; set; } }
    public sealed class State { public Dictionary<long, int> Mains { get; set; } = []; public Dictionary<long, Sub> Tasks { get; set; } = []; public List<long> Functions { get; set; } = []; }

    private JsonElement Mains => Cfg.GetProperty("main_tasks");
    private JsonElement Tasks => Cfg.GetProperty("tasks");

    private async Task<State> LoadAsync(long uid, CancellationToken ct)
    {
        var s = await accounts.GetJsonAsync<State>(uid, "quanzhou_quest_v1", ct) ?? new State();
        foreach (var m in Mains.EnumerateObject()) s.Mains.TryAdd(long.Parse(m.Name), 0);
        foreach (var t in Tasks.EnumerateObject()) s.Tasks.TryAdd(long.Parse(t.Name), new Sub());
        return s;
    }

    private bool Condition(State s, long cid, int level)
    {
        if (!Cfg.GetProperty("conditions").TryGetProperty(cid.ToString(), out var c)) return false;
        var p = L(c, "params");
        return I(c, "type") switch
        {
            100 => p.All(i => s.Mains.GetValueOrDefault(i) == 9),
            101 => p.All(i => s.Tasks.TryGetValue(i, out var t) && t.Status == 1),
            1014 => level >= p.FirstOrDefault(),
            _ => false
        };
    }

    private static bool Conj(IEnumerable<bool> values, int op) { var v = values.ToList(); return v.Count == 0 || (op == 2 ? v.All(x => x) : v.Any(x => x)); }

    private void Advance(State s, int level)
    {
        foreach (var m in Mains.EnumerateObject())
        {
            var id = long.Parse(m.Name);
            if (s.Mains[id] is 0 or 1 && Conj(L(m.Value, "unlock_param").Select(c => Condition(s, c, level)), I(m.Value, "unlock_operation")))
                s.Mains[id] = m.Value.TryGetProperty("receive_npc", out var npc) && npc.ValueKind == JsonValueKind.Number && npc.GetInt32() != 0 ? 2 : 3;
        }
        foreach (var t in Tasks.EnumerateObject())
        {
            var id = long.Parse(t.Name);
            if (s.Tasks[id].Status == -100 && s.Mains.GetValueOrDefault(I(t.Value, "main_task_id")) == 3
                && Conj(L(t.Value, "pre_task_id").Select(p => s.Tasks.TryGetValue(p, out var x) && x.Status == 1), I(t.Value, "pre_operation")))
                s.Tasks[id].Status = 0;
        }
    }

    private List<object> Quests(State s, Func<long, bool>? filter = null) => s.Mains.Where(m => m.Value != 0 && (filter?.Invoke(m.Key) ?? true)).OrderBy(m => m.Key).Select(m => (object)new Dictionary<string, object?>
    {
        ["id"] = (uint)m.Key, ["status"] = (uint)m.Value, ["expired_timestamp"] = 0u,
        ["task_list"] = s.Tasks.Where(t => I(Tasks.GetProperty(t.Key.ToString()), "main_task_id") == m.Key && t.Value.Status >= 0).OrderBy(t => t.Key)
            .Select(t => (object)new Dictionary<string, object?> { ["id"] = (uint)t.Key, ["status"] = (uint)t.Value.Status, ["progress"] = (uint)t.Value.Progress }).ToArray()
    }).ToList();

    private async Task<int> LevelAsync(long uid, CancellationToken ct) => (await accounts.GetProfileAsync(uid, ct))?.Level ?? 1;

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        Advance(s, await LevelAsync(uid, ct));
        await accounts.SetJsonAsync(uid, "quanzhou_quest_v1", s, ct);
        return
        [
            (28801, codec.Encode("sc_28801", new Dictionary<string, object?> { ["quest_list"] = Quests(s).ToArray() })),
            (28837, codec.Encode("sc_28837", new Dictionary<string, object?> { ["sys_id_list"] = s.Functions.OrderBy(x => x).Select(x => (uint)x).ToArray() }))
        ];
    }

    private GameResponse? Diff(ushort srv, State before, State after)
    {
        var old = Quests(before).Cast<Dictionary<string, object?>>().ToDictionary(q => (uint)q["id"]!, q => JsonSerializer.Serialize(q));
        var changed = Quests(after).Cast<Dictionary<string, object?>>().Where(q => !old.TryGetValue((uint)q["id"]!, out var o) || o != JsonSerializer.Serialize(q)).Cast<object>().ToArray();
        return changed.Length == 0 ? null : new GameResponse(28803, 0, srv, codec.Encode("sc_28803", new Dictionary<string, object?> { ["quest_list"] = changed }));
    }

    /// <summary>A validated winning battle advances battle objectives (330001 sea / 330002 sandbox).</summary>
    public async Task<GameResponse?> BattleClearAsync(long uid, ushort srv, long stageId, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        var before = JsonSerializer.Deserialize<State>(JsonSerializer.Serialize(s))!;
        Advance(s, await LevelAsync(uid, ct));
        foreach (var t in Tasks.EnumerateObject())
            if (I(t.Value, "condition") is 330001 or 330002 && L(t.Value, "additional_parameter").Contains(stageId))
            {
                var sub = s.Tasks[long.Parse(t.Name)];
                if (sub.Status == 0 && s.Mains.GetValueOrDefault(I(t.Value, "main_task_id")) == 3) sub.Progress = Math.Min(I(t.Value, "need"), sub.Progress + 1);
            }
        await accounts.SetJsonAsync(uid, "quanzhou_quest_v1", s, ct);
        return Diff(srv, before, s);
    }

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var srv = (ushort)(r.ServerIndex + 1);
        var s = await LoadAsync(uid, ct);
        var before = JsonSerializer.Deserialize<State>(JsonSerializer.Serialize(s))!;
        var level = await LevelAsync(uid, ct);
        Advance(s, level);
        var rewards = new List<(long, long)>();
        try
        {
            switch (cmd)
            {
                case 28832:
                    {
                        var param = (long)HandlerHelpers.ReadUInt(r.Body, "param");
                        var value = (long)HandlerHelpers.ReadUInt(r.Body, "value");
                        if (param == 0) { Require(value == 1, "Bad refresh parameter"); break; }
                        Require(Tasks.TryGetProperty(param.ToString(), out var tc) && I(tc, "condition") == 60, "Objective does not take client progress");
                        var t = s.Tasks[param];
                        Require(t.Status == 0 || (t.Status == 1 && value <= t.Progress), "Objective not active");
                        Require(s.Mains.GetValueOrDefault(I(tc, "main_task_id")) == 3 || t.Status == 1, "Main quest not active");
                        t.Progress = Math.Min(I(tc, "need"), Math.Max(t.Progress, value));
                        break;
                    }
                case 28822:
                    {
                        var ids = HandlerHelpers.ReadIdList(r.Body, "quest_task_id");
                        Require(ids.Count > 0 && ids.All(i => s.Tasks.ContainsKey(i)), "Bad objective list");
                        foreach (var id in ids)
                        {
                            var t = s.Tasks[id];
                            var tc = Tasks.GetProperty(id.ToString());
                            Require(t.Status == 1 || (t.Status == 0 && s.Mains.GetValueOrDefault(I(tc, "main_task_id")) == 3 && t.Progress >= I(tc, "need")), "Objective not completed");
                        }
                        foreach (var id in ids) s.Tasks[id].Status = 1;
                        break;
                    }
                case 28820:
                    {
                        var ids = HandlerHelpers.ReadIdList(r.Body, "quest_id_list");
                        Require(ids.Count > 0 && ids.All(i => s.Mains.ContainsKey(i)), "Bad quest list");
                        foreach (var id in ids)
                            Require(s.Mains[id] == 9 || (s.Mains[id] == 3 && s.Tasks.TryGetValue(I(Mains.GetProperty(id.ToString()), "end_task_id"), out var end) && end.Status == 1), "Main quest not completed");
                        foreach (var id in ids.Where(i => s.Mains[i] != 9))
                        {
                            s.Mains[id] = 9;
                            var mc = Mains.GetProperty(id.ToString());
                            if (mc.TryGetProperty("reward", out var rw) && rw.ValueKind == JsonValueKind.Array)
                                rewards.AddRange(rw.EnumerateArray().Select(x => (x[0].GetInt64(), x[1].GetInt64())));
                            foreach (var (tid, t) in s.Tasks)
                                if (I(Tasks.GetProperty(tid.ToString()), "main_task_id") == id && t.Status != 1) t.Status = -101;
                        }
                        break;
                    }
                case 28830:
                    {
                        var id = (long)HandlerHelpers.ReadUInt(r.Body, "quest_id");
                        Require(s.Mains.TryGetValue(id, out var st) && st is 2 or 3 or 9, "Quest cannot be taken");
                        if (st != 9) s.Mains[id] = 3;
                        break;
                    }
                case 28834:
                    {
                        var sid = (long)HandlerHelpers.ReadUInt(r.Body, "sys_id");
                        Require(Cfg.GetProperty("function_open").TryGetProperty(sid.ToString(), out var fc) && Condition(s, I(fc, "condition_id"), level), "Function locked");
                        if (!s.Functions.Contains(sid)) s.Functions.Add(sid);
                        break;
                    }
            }
        }
        catch (ModeError) { return cmd == 28834 ? [] : [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)]; }
        Advance(s, level);
        await accounts.SetJsonAsync(uid, "quanzhou_quest_v1", s, ct);
        var granted = rewards.Count > 0 ? await accounts.GrantItemsAsync(uid, rewards.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))), ct) : [];
        var frames = await InventorySync.FramesAsync(accounts, codec, r, granted.Select(x => x.Item1), ct);
        if (Diff(srv, before, s) is { } d) frames.Add(d);
        if (cmd == 28834) return frames;
        var reply = new Dictionary<string, object?> { ["result"] = 0u };
        if (cmd is 28820 or 28822) reply["reward_list"] = HandlerHelpers.ToItemNetRecList(granted);
        frames.Add(HandlerHelpers.Reply(codec, r, cmd + 1, reply));
        return frames;
    }
}
