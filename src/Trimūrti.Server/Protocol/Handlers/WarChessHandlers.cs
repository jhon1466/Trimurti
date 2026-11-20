using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

public sealed class WarChessError(string message) : Exception(message);

/// <summary>
/// 因果观测 (WarChess, p49) exploration, port of Alpha-Persei warchess_service / warchess_battle on the native
/// contract Data/Configs/warchess_contract.json (levels, hex types, event pools, globals, artifacts, drops, maps) and
/// warchess_spawns.json. The server runs each tile's event queue (logs, tile swaps, state cycles, rotations, keys,
/// HP, teleports, chests, artifact picks, purify/revive, battles) and persists the run; the client replays the
/// acknowledged queue locally. Stage-15 battles continue the queue on a win and keep the tile on a loss.
/// </summary>
public sealed class WarChess(GameAccountRepository accounts, SourceGameConfig config)
{
    private static readonly (int X, int Z)[] Directions = [(1, 1), (0, 1), (-1, 0), (-1, -1), (0, -1), (1, 0)];
    private JsonElement Cfg => config.Get("warchess_contract.json") ?? throw new WarChessError("Missing warchess contract");
    private JsonElement Spawns => config.Get("warchess_spawns.json") ?? default;

    public JsonElement Level(long cid) => Cfg.GetProperty("levels").TryGetProperty(cid.ToString(), out var l) ? l : throw new WarChessError("Unknown chess chapter");
    public IEnumerable<long> Levels => Cfg.GetProperty("levels").EnumerateObject().Select(p => long.Parse(p.Name)).OrderBy(x => x);
    public bool IsStage(uint type, long stage) => type == 15 || Cfg.GetProperty("stages").TryGetProperty(stage.ToString(), out _);

    // ---------------- persisted state ----------------

    public sealed class Grid { public int Tag { get; set; } public int X { get; set; } public int Z { get; set; } public int State { get; set; } public List<uint> Attribute { get; set; } = []; public int Rotation { get; set; } }
    public sealed class Key { public long Type { get; set; } public long Num { get; set; } }
    public sealed class Hp { public long Id { get; set; } public int Value { get; set; } }
    public sealed class Log { public int Index { get; set; } public int Tag { get; set; } public long Id { get; set; } }
    public sealed class Global { public long Id { get; set; } public long Progress { get; set; } }

    public sealed class MapState
    {
        public int X { get; set; }
        public int Z { get; set; }
        public int Direction { get; set; }
        public bool IsFog { get; set; }
        public List<uint> Fog { get; set; } = [];
        public List<Grid> Grids { get; set; } = [];
        public List<Key> Items { get; set; } = [];
        public List<long> Artifacts { get; set; } = [];
        public List<Hp> HpList { get; set; } = [];
        public List<Log> Logs { get; set; } = [];
        public List<Global> Events { get; set; } = [];
        public List<int> GuidePos { get; set; } = [];
    }

    public sealed class Pending
    {
        public string Kind { get; set; } = "";
        public int X { get; set; }
        public int Z { get; set; }
        public int Tag { get; set; }
        public long StageId { get; set; }
        public long Event { get; set; }
        public List<JsonElement> Rest { get; set; } = [];
        public List<long> Choices { get; set; } = [];
    }

    public sealed class Run
    {
        public long Chapter { get; set; }
        public MapState Map { get; set; } = new();
        public Pending? Pending { get; set; }
        public List<long[]> Marks { get; set; } = [];          // [event, x, z, tag]
        public Dictionary<string, int> EventCounts { get; set; } = [];
        public int ChoiceCount { get; set; }
        public string? LastMove { get; set; }
    }

    public sealed class Save
    {
        public Run? Active { get; set; }
        public Dictionary<long, Dictionary<long, int>> Boxes { get; set; } = [];   // chapter → box id → count
        public List<long[]> Opened { get; set; } = [];                            // [chapter, box, tag, x, z]
        public Dictionary<long, long> Current { get; set; } = [];                 // level type → chapter
    }

    public async Task<Save> LoadAsync(long uid, CancellationToken ct) => await accounts.GetJsonAsync<Save>(uid, "warchess_v1", ct) ?? new Save();
    public Task StoreAsync(long uid, Save s, CancellationToken ct) => accounts.SetJsonAsync(uid, "warchess_v1", s, ct);

    public int ChapterProgress(Save s, long cid)
    {
        var lv = Level(cid);
        var c = s.Boxes.TryGetValue(cid, out var b) ? b : [];
        return Math.Min(100, c.GetValueOrDefault(10203) * lv.GetProperty("sbox_progress").GetInt32() + c.GetValueOrDefault(10204) * lv.GetProperty("lbox_progress").GetInt32());
    }

    // ---------------- payloads ----------------

    public Dictionary<string, object?> Overview(Save s)
    {
        var groups = Levels.GroupBy(c => Level(c).GetProperty("type").GetInt64()).OrderBy(g => g.Key);
        return new()
        {
            ["chess_map_list"] = groups.Select(g => (object)new Dictionary<string, object?>
            {
                ["activity_id"] = (uint)g.Key,
                ["chapter"] = (uint)(s.Current.TryGetValue(g.Key, out var cur) && g.Contains(cur) ? cur : 0),
                ["chapter_info"] = g.Select(c => (object)new Dictionary<string, object?>
                {
                    ["chapter_id"] = (uint)c,
                    ["box"] = (s.Boxes.TryGetValue(c, out var b) ? b : []).Select(x => (object)new Dictionary<string, object?> { ["id"] = (uint)x.Key, ["num"] = (uint)x.Value }).ToArray()
                }).ToArray()
            }).ToArray()
        };
    }

    public Dictionary<string, object?> OpenInfo() => new()
    {
        ["chess_open_info_list"] = Levels.Select(c => (object)new Dictionary<string, object?> { ["chapter_id"] = (uint)c, ["timestamp"] = 0u }).ToArray()
    };

    private static Dictionary<string, object?> Pos(int x, int z) => new() { ["x"] = x, ["z"] = z };

    public Dictionary<string, object?> MapInfo(Save s, Run r)
    {
        var m = r.Map;
        var marks = r.Marks.Concat(s.Opened.Where(o => o[0] == r.Chapter).Select(o => new[] { o[1], o[3], o[4], o[2] }));
        var info = marks.GroupBy(e => e[0]).OrderBy(g => g.Key).Select(g => (object)new Dictionary<string, object?>
        {
            ["event_id"] = (uint)g.Key,
            ["pos_list"] = g.Select(e => (e[1], e[2], e[3])).Distinct().Select(e => (object)new Dictionary<string, object?> { ["pos"] = Pos((int)e.Item1, (int)e.Item2), ["tag"] = (uint)e.Item3 }).ToArray()
        }).ToArray();
        return new()
        {
            ["pos"] = Pos(m.X, m.Z),
            ["bag"] = new Dictionary<string, object?>
            {
                ["artifact"] = m.Artifacts.Select(x => (uint)x).ToArray(),
                ["item"] = m.Items.Select(i => (object)new Dictionary<string, object?> { ["type"] = (uint)i.Type, ["num"] = (uint)i.Num }).ToArray()
            },
            ["log"] = m.Logs.Select(l => (object)new Dictionary<string, object?> { ["event_name"] = l.Id.ToString(), ["attribute_json"] = JsonSerializer.Serialize(new { index = l.Index, tag = l.Tag, log = l.Id }) }).ToArray(),
            ["hp_list"] = m.HpList.Select(h => (object)new Dictionary<string, object?> { ["id"] = (uint)h.Id, ["hp"] = (uint)h.Value }).ToArray(),
            ["map"] = m.Grids.Select(g => (object)new Dictionary<string, object?> { ["tag"] = (uint)g.Tag, ["pos"] = Pos(g.X, g.Z), ["state"] = (uint)g.State, ["attribute"] = g.Attribute.ToArray(), ["rotation"] = (uint)g.Rotation }).ToArray(),
            ["is_viewed_log"] = false, ["is_fog"] = m.IsFog, ["fog"] = m.Fog.ToArray(),
            ["event_list"] = m.Events.Select(e => (object)new Dictionary<string, object?> { ["id"] = (uint)e.Id, ["progress"] = (uint)e.Progress }).ToArray(),
            ["guide_pos"] = m.GuidePos.Select(x => (uint)x).ToArray(), ["direction"] = (uint)m.Direction, ["event_info"] = info
        };
    }

    // ---------------- engine ----------------

    public sealed class Engine(WarChess w, Save save, Run run)
    {
        public readonly List<(uint Cmd, Dictionary<string, object?> Body)> Frames = [];
        public readonly List<(long, long)> Grants = [];
        private int _steps;
        public bool Stop;
        private JsonElement Native => w.Cfg.GetProperty("maps").GetProperty(run.Chapter.ToString());
        private MapState M => run.Map;

        public sealed record Tile(int TypeId, int Status, int Tag, int Rotation, JsonElement Params, List<uint> Attribute);

        public Tile Get(int x, int z)
        {
            if (!Native.GetProperty("tiles").TryGetProperty($"{x},{z}", out var baseTile)) throw new WarChessError("Chess position outside native map");
            var row = M.Grids.FirstOrDefault(g => g.X == x && g.Z == z);
            var tag = row?.Tag ?? 0;
            var raw = tag > 0 ? Native.GetProperty("extend")[tag - 1] : baseTile;
            return new Tile(raw.GetProperty("typeID").GetInt32(), row?.State ?? raw.GetProperty("status").GetInt32(), tag,
                row?.Rotation ?? raw.GetProperty("rotationY").GetInt32(), raw.GetProperty("paramList"), row?.Attribute.ToList() ?? []);
        }

        public JsonElement Hex(Tile t)
        {
            var hexes = w.Cfg.GetProperty("hexes");
            return t.Status != 0 && hexes.TryGetProperty((t.TypeId * 10 + t.Status).ToString(), out var h) ? h : hexes.GetProperty(t.TypeId.ToString());
        }

        public void Change(int x, int z, int? tag = null, int? state = null, int? rotation = null)
        {
            var t = Get(x, z);
            int newTag = t.Tag, status = t.Status, rot = t.Rotation;
            var attr = t.Attribute;
            if (tag is { } tg)
            {
                if (tg < 0 || tg > Native.GetProperty("extend").GetArrayLength()) throw new WarChessError("Unknown native extension");
                var raw = tg > 0 ? Native.GetProperty("extend")[tg - 1] : Native.GetProperty("tiles").GetProperty($"{x},{z}");
                newTag = tg; status = raw.GetProperty("status").GetInt32(); rot = raw.GetProperty("rotationY").GetInt32(); attr = [];
            }
            if (state is { } s) status = s;
            if (rotation is { } r) rot = ((r % 360) + 360) % 360;
            M.Grids.RemoveAll(g => g.X == x && g.Z == z);
            M.Grids.Add(new Grid { Tag = newTag, X = x, Z = z, State = status, Attribute = attr, Rotation = rot });
        }

        public List<JsonElement> Actions(int x, int z)
        {
            var t = Get(x, z);
            if (t.Params.ValueKind != JsonValueKind.Array || t.Params.GetArrayLength() == 0) return [];
            if (!w.Cfg.GetProperty("pools").TryGetProperty(t.Params[0].GetInt64().ToString(), out var pool)) throw new WarChessError("Missing native chess event pool");
            var g = pool.GetProperty("event_group");
            return g.ValueKind == JsonValueKind.Array ? g.EnumerateArray().ToList() : [];
        }

        public void Mark(long ev, int x, int z, int tag)
        {
            if (!run.Marks.Any(m => m[0] == ev && m[1] == x && m[2] == z && m[3] == tag)) run.Marks.Add([ev, x, z, tag]);
        }

        private void Item(long id, long num)
        {
            if (!w.Cfg.GetProperty("objects").TryGetProperty(id.ToString(), out var cfg)) throw new WarChessError("Unknown local chess item");
            var old = M.Items.FirstOrDefault(i => i.Type == id)?.Num ?? 0;
            var value = old + num;
            if (value < 0) throw new WarChessError("Insufficient local chess item");
            var max = cfg.GetProperty("max").GetInt64();
            if (max > 0) value = Math.Min(value, max);
            M.Items.RemoveAll(i => i.Type == id);
            if (value > 0) M.Items.Add(new Key { Type = id, Num = value });
        }

        private void HpAll(int amount)
        {
            foreach (var h in M.HpList.Where(h => h.Value > 0)) h.Value = Math.Clamp(h.Value + amount, 0, 10000);
        }

        private void Reward(long ev, long drop, int x, int z, int tag)
        {
            if (save.Opened.Any(o => o[0] == run.Chapter && o[1] == ev && o[2] == tag && o[3] == x && o[4] == z)) return;
            if (!w.Cfg.GetProperty("drops").TryGetProperty(drop.ToString(), out var cfg)) throw new WarChessError("Missing native chess drop");
            save.Opened.Add([run.Chapter, ev, tag, x, z]);
            if (!save.Boxes.TryGetValue(run.Chapter, out var b)) save.Boxes[run.Chapter] = b = [];
            b[ev] = b.GetValueOrDefault(ev) + 1;
            foreach (var d in cfg.GetProperty("base_drop").EnumerateArray())
                if (d.GetArrayLength() < 3 || d[2].GetInt32() >= 100 || Random.Shared.Next(1, 101) <= d[2].GetInt32())
                    Grants.Add((d[0].GetInt64(), d[1].GetInt64()));
        }

        /// <summary>Three artifact candidates: rarity by the native counter weights, uniform within a rarity.</summary>
        private List<long> Choices()
        {
            run.ChoiceCount++;
            var rar = w.Cfg.GetProperty("rarities");
            var maxKey = rar.EnumerateObject().Max(p => int.Parse(p.Name));
            var weights = rar.GetProperty(Math.Min(run.ChoiceCount, maxKey).ToString()).GetProperty("rare_weight").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var items = w.Cfg.GetProperty("items");
            var available = items.EnumerateObject().Where(p => p.Value.GetProperty("identify_buff").GetInt32() == 0 && !M.Artifacts.Contains(long.Parse(p.Name)))
                .Select(p => (Id: long.Parse(p.Name), Rare: p.Value.GetProperty("item_rare").GetInt32())).ToList();
            var result = new List<long>();
            for (var n = 0; n < 3; n++)
            {
                var eligible = weights.Select((wt, i) => (Rare: i + 1, Wt: wt)).Where(e => e.Wt > 0 && available.Any(a => a.Rare == e.Rare)).ToArray();
                if (eligible.Length == 0) break;
                var roll = Random.Shared.Next(eligible.Sum(e => e.Wt));
                var rare = eligible.First(e => (roll -= e.Wt) < 0).Rare;
                var pool = available.Where(a => a.Rare == rare).ToArray();
                var pick = pool[Random.Shared.Next(pool.Length)];
                available.Remove(pick);
                result.Add(pick.Id);
            }
            if (result.Count == 0) throw new WarChessError("No native chess artifact candidates");
            return result;
        }

        private static long N(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;

        private static List<int> Values(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => (int)N(x)).ToList() : [];

        public void Queue(IEnumerable<JsonElement> actions, int x, int z, int? tagOverride = null, int depth = 0)
        {
            if (depth >= 32) throw new WarChessError("Chess event recursion limit");
            var tag = tagOverride ?? Get(x, z).Tag;
            var queue = new List<JsonElement>(actions);
            while (queue.Count > 0)
            {
                if (++_steps > 1024) throw new WarChessError("Chess event action limit");
                var row = queue[0]; queue.RemoveAt(0);
                var eid = row[0].GetInt64();
                JsonElement P(int i) => row.GetArrayLength() > i + 1 ? row[i + 1] : default;
                long Pn(int i) => N(P(i));
                switch (eid)
                {
                    case 10000 or 10102 or 10103 or 10104 or 10105 or 10107 or 10108 or 10307 or 10701 or 10702 or 10703: break;
                    case 10101:
                        {
                            var index = z * Native.GetProperty("width").GetInt32() + x + 1;
                            if (!M.Logs.Any(l => l.Index == index && l.Tag == tag && l.Id == Pn(0))) M.Logs.Add(new Log { Index = index, Tag = tag, Id = Pn(0) });
                            break;
                        }
                    case 10106: Mark(eid, x, z, tag); break;
                    case 10201:
                        if (!w.Cfg.GetProperty("stages").TryGetProperty(Pn(0).ToString(), out _)) throw new WarChessError("Unknown native chess battle");
                        if (run.Marks.Any(m => m[0] == eid && m[1] == x && m[2] == z && m[3] == tag)) continue;
                        run.Pending = new Pending { Kind = "battle", X = x, Z = z, Tag = tag, StageId = Pn(0), Rest = queue, Event = eid };
                        return;
                    case 10202:
                        {
                            var choices = Choices();
                            run.Pending = new Pending { Kind = "artifact", X = x, Z = z, Tag = tag, Rest = queue, Event = eid, Choices = choices };
                            Frames.Add((49011, ChoiceFrame(run.Pending)));
                            return;
                        }
                    case 10203 or 10204: Reward(eid, Pn(0), x, z, tag); break;
                    case 10205:
                        if (!w.Cfg.GetProperty("items").TryGetProperty(Pn(0).ToString(), out var deb) || deb.GetProperty("identify_buff").GetInt32() == 0) throw new WarChessError("Invalid chess debuff");
                        if (!M.Artifacts.Contains(Pn(0))) M.Artifacts.Add(Pn(0));
                        Stop = true;
                        break;
                    case 10206 or 10404:
                        run.Pending = new Pending { Kind = eid == 10206 ? "purify" : "revive", X = x, Z = z, Tag = tag, Rest = queue, Event = eid };
                        return;
                    case 10301: Change(x, z, tag: (int)Pn(0)); break;
                    case 10302: Change((int)Pn(0), (int)Pn(1), tag: (int)Pn(2)); break;
                    case 10303 or 10304 or 10308 or 10309:
                        {
                            var self = eid is 10303 or 10308;
                            var (tx, tz) = self ? (x, z) : ((int)Pn(0), (int)Pn(1));
                            var values = Values(self ? P(0) : P(2));
                            if (values.Count == 0) throw new WarChessError("Invalid chess state cycle");
                            var old = Get(tx, tz).Status;
                            var i = values.IndexOf(old);
                            Change(tx, tz, state: values[i >= 0 ? (i + 1) % values.Count : 0]);
                            break;
                        }
                    case 10305: Change(x, z, rotation: Get(x, z).Rotation + (int)Pn(0) * 60); break;
                    case 10306: Change((int)Pn(0), (int)Pn(1), rotation: Get((int)Pn(0), (int)Pn(1)).Rotation + (int)Pn(2) * 60); break;
                    case 10401: HpAll((int)Pn(0) * 100); break;
                    case 10402: HpAll(-(int)Pn(0) * 100); Stop = true; break;
                    case 10403:
                        if (w.Hex(Get((int)Pn(0), (int)Pn(1)), this).GetProperty("move").GetInt32() <= 0) throw new WarChessError("Invalid chess teleport");
                        M.X = (int)Pn(0); M.Z = (int)Pn(1); Stop = true;
                        break;
                    case 10501: Item(Pn(0), Pn(1)); break;
                    case 10502:
                        if ((M.Items.FirstOrDefault(i => i.Type == Pn(0))?.Num ?? 0) < Pn(1)) return;
                        Item(Pn(0), -Pn(1)); Change(x, z, state: (int)Pn(2));
                        break;
                    case 10601 or 10603:
                        {
                            var self = eid == 10601;
                            var (tx, tz) = self ? (x, z) : ((int)Pn(0), (int)Pn(1));
                            var cases = self ? P(0) : P(2);
                            var status = Get(tx, tz).Status;
                            var nested = cases.ValueKind == JsonValueKind.Array
                                ? cases.EnumerateArray().FirstOrDefault(c => c.ValueKind == JsonValueKind.Array && N(c[0]) == status) : default;
                            if (nested.ValueKind == JsonValueKind.Array && nested.GetArrayLength() > 1 && nested[1].ValueKind == JsonValueKind.Array)
                                queue.InsertRange(0, nested[1].EnumerateArray());
                            break;
                        }
                    case 10602: if (P(0).ValueKind == JsonValueKind.Array) queue.InsertRange(0, P(0).EnumerateArray()); break;
                    case 10901:
                        {
                            var attr = Get(x, z).Attribute;
                            if ((attr.Count > 0 ? attr[0] : 0) <= Pn(0)) Change(x, z, tag: (int)Pn(1)); else return;
                            break;
                        }
                    case 10902:
                        foreach (var e in M.Events.Where(e => w.Cfg.GetProperty("globals").GetProperty(e.Id.ToString()).GetProperty("type").GetInt32() == 8)) e.Progress += Pn(0);
                        break;
                    case 11001: M.GuidePos = [(int)Pn(0), (int)Pn(1)]; break;
                    case 11002:
                        {
                            var path = Values(P(0));
                            if (path.Count < 2 || path.Count % 2 != 0) throw new WarChessError("Invalid chess guide path");
                            M.GuidePos = path.TakeLast(2).ToList();
                            break;
                        }
                    default: throw new WarChessError($"Native chess event {eid} needs a verified implementation");
                }
                run.EventCounts[eid.ToString()] = run.EventCounts.GetValueOrDefault(eid.ToString()) + 1;
            }
        }

        public void Globals(bool start = false)
        {
            foreach (var ev in M.Events.ToList())
            {
                var cfg = w.Cfg.GetProperty("globals").GetProperty(ev.Id.ToString());
                var kind = cfg.GetProperty("type").GetInt32();
                var prm = cfg.GetProperty("params");
                long Pr(int i) => prm.ValueKind == JsonValueKind.Array && prm.GetArrayLength() > i ? N(prm[i]) : 0;
                bool ready;
                switch (kind)
                {
                    case 1: ready = start; break;
                    case 2: ev.Progress = w.ChapterProgress(save, run.Chapter); ready = ev.Progress >= Pr(0); break;
                    case 3: ev.Progress = M.Items.FirstOrDefault(i => i.Type == Pr(0))?.Num ?? 0; ready = ev.Progress >= Pr(1); break;
                    case 4: ev.Progress = run.EventCounts.GetValueOrDefault(Pr(0).ToString()); ready = ev.Progress >= Pr(1); break;
                    case 8: ready = ev.Progress >= Pr(0); break;
                    default: ready = false; break;   // area/order/seal globals stay open
                }
                if (!ready) continue;
                M.Events.Remove(ev);
                var list = cfg.GetProperty("event_list");
                if (list.ValueKind == JsonValueKind.Array) Queue(list.EnumerateArray(), M.X, M.Z);
            }
        }
    }

    private JsonElement Hex(Engine.Tile t, Engine e) => e.Hex(t);

    public static Dictionary<string, object?> ChoiceFrame(Pending p) => new()
    {
        ["tag"] = (uint)p.Tag, ["pos"] = Pos(p.X, p.Z), ["event_id"] = (uint)p.Event, ["attribute"] = p.Choices.Select(x => (uint)x).ToArray()
    };

    // ---------------- operations ----------------

    public async Task<(Dictionary<string, object?> MapInfo, Engine Engine, Save Save)> EnterAsync(long uid, long cid, CancellationToken ct)
    {
        var lv = Level(cid);
        var save = await LoadAsync(uid, ct);
        var unlock = lv.GetProperty("unlock_level").GetInt64();
        if (unlock != 0 && ChapterProgress(save, unlock) < lv.GetProperty("success_progress").GetInt32()) throw new WarChessError("Chess predecessor is not complete");
        if (lv.GetProperty("unlock_condition") is { ValueKind: JsonValueKind.Array } cond && cond.GetArrayLength() >= 2 && cond[1].GetArrayLength() >= 2)
        {
            var sid = cond[1][1].GetInt64();
            var lobby = await accounts.GetLobbySnapshotAsync(uid, ct);
            if (lobby?.Chapters.Any(c => c.Id == sid && c.ClearTimes > 0) != true) throw new WarChessError("Chess narrative prerequisite is not complete");
        }
        var group = lv.GetProperty("type").GetInt64();
        if (save.Active is { } act && act.Chapter == cid)
        {
            var eng = new Engine(this, save, act);
            if (act.Pending is { Kind: "artifact" } p) eng.Frames.Add((49011, ChoiceFrame(p)));
            return (MapInfo(save, act), eng, save);
        }
        if (save.Active is { } old) save.Current.Remove(Level(old.Chapter).GetProperty("type").GetInt64());
        var native = Cfg.GetProperty("maps").GetProperty(cid.ToString());
        if (Spawns.ValueKind != JsonValueKind.Object || !Spawns.TryGetProperty(cid.ToString(), out var spawn)) throw new WarChessError("Missing reviewed chess spawn");
        int width = native.GetProperty("width").GetInt32(), height = native.GetProperty("height").GetInt32();
        var fog = lv.GetProperty("is_fog").GetInt32() != 0;
        var run = new Run
        {
            Chapter = cid,
            Map = new MapState
            {
                X = spawn[0].GetInt32(), Z = spawn[1].GetInt32(), IsFog = fog,
                Fog = fog ? Enumerable.Repeat(0xffffffffu, (width * height + 31) / 32).ToList() : [(uint)width, (uint)height],
                Events = Cfg.GetProperty("globals").EnumerateObject().Where(g => g.Value.GetProperty("map_id").GetInt64() == cid)
                    .Select(g => new Global { Id = long.Parse(g.Name) }).ToList()
            }
        };
        var engine = new Engine(this, save, run);
        // Previously opened chests keep their native extension.
        foreach (var o in save.Opened.Where(o => o[0] == cid))
        {
            int x = (int)o[3], z = (int)o[4], tag = (int)o[2];
            if (!native.GetProperty("tiles").TryGetProperty($"{x},{z}", out _)) continue;
            if (tag != 0) engine.Change(x, z, tag: tag);
            var actions = engine.Actions(x, z);
            if (actions.Any(a => a[0].GetInt64() == o[1]))
                foreach (var a in actions.Where(a => a[0].GetInt64() == 10301)) engine.Change(x, z, tag: a[1].GetInt32());
        }
        save.Active = run;
        save.Current[group] = cid;
        var wire = MapInfo(save, run);
        engine.Globals(start: true);
        return (wire, engine, save);
    }

    public async Task<(Engine Engine, Save Save)> MoveAsync(long uid, IReadOnlyList<(int X, int Z)> path, string rawPath, CancellationToken ct)
    {
        if (path.Count > 1024) throw new WarChessError("Invalid chess movement path");
        var save = await LoadAsync(uid, ct);
        var run = save.Active ?? throw new WarChessError("No active chess exploration");
        var engine = new Engine(this, save, run);
        var after = $"{rawPath}@{run.Map.X},{run.Map.Z}";
        if (run.LastMove == after) return (engine, save);
        if (run.Pending is { } p && p.Kind is not ("battle" or "purify" or "revive")) throw new WarChessError("Chess decision is pending");
        run.Pending = null;
        int cx = run.Map.X, cz = run.Map.Z;
        for (var n = 0; n < path.Count; n++)
        {
            var (x, z) = path[n];
            if (n == 0 && x == cx && z == cz) continue;
            var dir = Array.IndexOf(Directions, (x - cx, z - cz));
            if (dir < 0) throw new WarChessError("Nonadjacent chess movement");
            var t = engine.Get(x, z);
            var hex = engine.Hex(t);
            if (hex.GetProperty("move").GetInt32() <= 0) throw new WarChessError("Chess path crosses a blocked tile");
            run.Map.X = x; run.Map.Z = z; run.Map.Direction = dir; cx = x; cz = z;
            if (hex.GetProperty("interact").GetInt32() == 2) { engine.Queue(engine.Actions(x, z), x, z); engine.Globals(); }
            if (engine.Stop || run.Pending is not null) break;
        }
        run.LastMove = $"{rawPath}@{run.Map.X},{run.Map.Z}";
        return (engine, save);
    }

    public async Task<(Engine Engine, Save Save)> InteractAsync(long uid, int x, int z, int type, long param, CancellationToken ct)
    {
        if (type is not (1 or 2)) throw new WarChessError("Invalid chess interaction");
        var save = await LoadAsync(uid, ct);
        var run = save.Active ?? throw new WarChessError("No active chess exploration");
        var engine = new Engine(this, save, run);
        int cx = run.Map.X, cz = run.Map.Z;
        if (!(x == cx && z == cz) && !Directions.Contains((x - cx, z - cz))) throw new WarChessError("Remote chess interaction");
        var pending = run.Pending;
        run.LastMove = null;
        if (pending is not null && pending.X == x && pending.Z == z)
        {
            if (type != 1) throw new WarChessError("Wrong chess continuation kind");
            if (param == 0 && pending.Kind == "artifact") { engine.Frames.Add((49011, ChoiceFrame(pending))); return (engine, save); }
            if (pending.Kind == "battle") return (engine, save);
            if (param <= 0) throw new WarChessError("Chess selection is required");
            switch (pending.Kind)
            {
                case "artifact":
                    if (!pending.Choices.Contains(param)) throw new WarChessError("Unissued chess artifact");
                    run.Map.Artifacts.Add(param);
                    break;
                case "purify":
                    if (!run.Map.Artifacts.Contains(param) || Cfg.GetProperty("items").GetProperty(param.ToString()).GetProperty("identify_buff").GetInt32() == 0) throw new WarChessError("Invalid chess purification");
                    run.Map.Artifacts.Remove(param);
                    break;
                case "revive":
                    var hero = run.Map.HpList.FirstOrDefault(h => h.Id == param && h.Value == 0) ?? throw new WarChessError("Invalid chess revive");
                    hero.Value = 10000;
                    break;
            }
            run.Pending = null;
            engine.Mark(pending.Event, x, z, pending.Tag);
            engine.Queue(pending.Rest, x, z, pending.Tag);
        }
        else
        {
            if (param != 0) throw new WarChessError("Unsolicited chess selection");
            if (pending is { Kind: "artifact" }) throw new WarChessError("Another chess decision is pending");
            run.Pending = null;
            if (engine.Hex(engine.Get(x, z)).GetProperty("interact").GetInt32() == 0) throw new WarChessError("Chess tile is no longer interactive");
            if (type != 1) throw new WarChessError("Ocean secondary interactions need the ship state machine");
            engine.Queue(engine.Actions(x, z), x, z);
        }
        if (run.Pending is null) engine.Globals();
        return (engine, save);
    }

    public async Task QuitAsync(long uid, CancellationToken ct)
    {
        var save = await LoadAsync(uid, ct);
        if (save.Active is { } r) save.Current.Remove(Level(r.Chapter).GetProperty("type").GetInt64());
        save.Active = null;
        await StoreAsync(uid, save, ct);
    }

    /// <summary>Stage-15 settlement: a win marks the battle tile and continues its queue; a loss keeps the tile.</summary>
    public async Task<IReadOnlyList<GameResponse>> SettleAsync(ProtoCodec codec, GameRequest r, long stageId, bool win, IReadOnlyList<long> heroes, CancellationToken ct)
    {
        var save = await LoadAsync(r.AccountId, ct);
        if (save.Active is not { Pending: { Kind: "battle" } p } run || p.StageId != stageId) return [];
        try
        {
            var engine = new Engine(this, save, run);
            foreach (var h in heroes.Where(h => run.Map.HpList.All(x => x.Id != h))) run.Map.HpList.Add(new Hp { Id = h, Value = 10000 });
            if (win)
            {
                run.Pending = null;
                engine.Mark(10201, p.X, p.Z, p.Tag);
                engine.Queue(p.Rest, p.X, p.Z, p.Tag);
                engine.Globals();
            }
            return await FinishAsync(codec, r, engine, save, ct);
        }
        catch (WarChessError) { return []; }
    }

    /// <summary>Saves the run, grants chest drops and returns inventory sync + engine pushes.</summary>
    public async Task<List<GameResponse>> FinishAsync(ProtoCodec codec, GameRequest r, Engine engine, Save save, CancellationToken ct)
    {
        await StoreAsync(r.AccountId, save, ct);
        var frames = new List<GameResponse>();
        if (engine.Grants.Count > 0)
        {
            var granted = await accounts.GrantItemsAsync(r.AccountId, engine.Grants.GroupBy(g => g.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))), ct);
            frames.AddRange(await InventorySync.FramesAsync(accounts, codec, r, granted.Select(x => x.Item1), ct));
        }
        frames.AddRange(engine.Frames.Select(f => new GameResponse(f.Cmd, 0, (ushort)(r.ServerIndex + 1), codec.Encode($"sc_{f.Cmd}", f.Body))));
        return frames;
    }
}

/// <summary>49002 enter, 49004 move, 49006 interact, 49008 quit, 49016 fire, 49024 switch control.</summary>
public sealed class WarChessHandler(WarChess chess, ProtoCodec codec, uint command) : IGameCommandHandler
{
    public uint Command => command;

    private static (int, int) ReadPos(object? raw) =>
        raw is IReadOnlyDictionary<string, object?> d ? ((int)Convert.ToInt64(d.GetValueOrDefault("x") ?? 0), (int)Convert.ToInt64(d.GetValueOrDefault("z") ?? 0)) : throw new WarChessError("Invalid chess position");

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        try
        {
            switch (command)
            {
                case 49002:
                    {
                        var (map, engine, save) = await chess.EnterAsync(r.AccountId, HandlerHelpers.ReadUInt(r.Body, "chapter_id"), ct);
                        var frames = await chess.FinishAsync(codec, r, engine, save, ct);
                        frames.Add(HandlerHelpers.Reply(codec, r, 49003, new Dictionary<string, object?> { ["result"] = 0u, ["map_info"] = map }));
                        return frames;
                    }
                case 49004:
                    {
                        var raw = r.Body.GetValueOrDefault("path") as System.Collections.IEnumerable;
                        var path = raw is null ? [] : raw.Cast<object?>().Select(ReadPos).ToList();
                        var (engine, save) = await chess.MoveAsync(r.AccountId, path, string.Join(";", path), ct);
                        var frames = await chess.FinishAsync(codec, r, engine, save, ct);
                        frames.Add(HandlerHelpers.ResultOnly(codec, r, 49005));
                        return frames;
                    }
                case 49006:
                    {
                        var (x, z) = ReadPos(r.Body.GetValueOrDefault("pos"));
                        var (engine, save) = await chess.InteractAsync(r.AccountId, x, z, (int)HandlerHelpers.ReadUInt(r.Body, "type"), HandlerHelpers.ReadUInt(r.Body, "param"), ct);
                        var frames = await chess.FinishAsync(codec, r, engine, save, ct);
                        frames.Add(HandlerHelpers.ResultOnly(codec, r, 49007));
                        return frames;
                    }
                case 49008:
                    await chess.QuitAsync(r.AccountId, ct);
                    return [HandlerHelpers.ResultOnly(codec, r, 49009)];
                default:
                    // Cannon (49016) and ship control (49024) need their own state machines; refuse without consuming anything.
                    return [HandlerHelpers.ResultOnly(codec, r, command + 1, 2)];
            }
        }
        catch (WarChessError)
        {
            return [HandlerHelpers.ResultOnly(codec, r, command + 1, 2)];
        }
    }
}
