using System.Text.Json;
using System.Text.Json.Nodes;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 夏日竞技 Summer ChessBoard, port of Alpha-Persei summer_board_service / movement / npc / chance / games / props /
/// nested / progress on summer_board_contract.json + summer_board_maps.json. The run state is kept as a JSON tree with
/// the protocol's own field names (chess_map_net_rec), so the native event projection maps one-to-one: hex movement
/// with tile enter/exit/start/end event pools, NPC turns and encounters, buffs, chances (shops, random rewards, QTE,
/// arena, nested battles), props and in-run tasks; clearing a chapter records history (51001) and unlocks the next.
/// Ops 85000–85038, frames 85031 (events), 85033 (chance), 85035 (system).
/// </summary>
public sealed class SummerBoard(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec) : IStageMode
{
    private static readonly (int X, int Z)[] Dirs = [(1, 1), (0, 1), (-1, 0), (-1, -1), (0, -1), (1, 0)];
    private static readonly string[] FloorKeys = ["typeID", "status", "rotationY", "banDir", "banDirIsHide", "enterParam", "exitParam", "startMoveParam", "endMoveParam"];
    private static readonly string[] ThingKeys = ["thingTypeID", "thingRotationY", "thingEnterParam", "thingExitParam", "thingStartMoveParam", "thingEndMoveParam"];
    private static readonly int[] NestedKinds = [3, 7, 8, 10, 11, 12, 13];

    private JsonElement Cfg => config.Get("summer_board_contract.json") ?? throw new ModeError("Missing summer_board_contract.json");
    private JsonElement Maps => (config.Get("summer_board_maps.json") ?? throw new ModeError("Missing summer_board_maps.json")).GetProperty("maps");
    private static void Require(bool ok, string m) { if (!ok) throw new ModeError(m); }
    private static long N(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<uint>(out var u)) return u;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        if (v.TryGetValue<bool>(out var b)) return b ? 1 : 0;
        if (v.TryGetValue<JsonElement>(out var e)) return e.ValueKind == JsonValueKind.Number ? e.GetInt64() : e.ValueKind == JsonValueKind.True ? 1 : 0;
        return 0;
    }
    private static long N(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;
    private static JsonNode Pos(long x, long z) => new JsonObject { ["x"] = x, ["z"] = z };
    private static bool SamePos(JsonNode? a, long x, long z) => a is not null && N(a["x"]) == x && N(a["z"]) == z;
    private JsonElement C(string table, long id) => Cfg.GetProperty(table).GetProperty(id.ToString());
    private bool Has(string table, long id) => Cfg.GetProperty(table).TryGetProperty(id.ToString(), out _);

    // ---------------- state ----------------

    private sealed class Ctx(SummerBoard b, JsonObject active)
    {
        public readonly SummerBoard B = b;
        public readonly JsonObject Active = active;
        public long Chapter => N(Active["chapter_id"]);
        public JsonObject Map => (JsonObject)Active["map"]!;
        public JsonArray Heroes => (JsonArray)Map["hero"]!;
        public readonly List<(uint Cmd, JsonNode Body)> Frames = [];
        public int Steps, Depth;
        public JsonObject Hero(long id) => Heroes.OfType<JsonObject>().FirstOrDefault(h => N(h["id"]) == id) ?? throw new ModeError("Board event references a missing unit");
        public bool Alive(long id) => Heroes.OfType<JsonObject>().Any(h => N(h["id"]) == id);
        public JsonObject Player => Hero(0);
    }

    private async Task<JsonObject> LoadAsync(long uid, CancellationToken ct)
    {
        var raw = await accounts.GetJsonAsync<JsonObject>(uid, "summer_board_v1", ct);
        return raw ?? new JsonObject { ["completed"] = new JsonArray(), ["finished_tasks"] = new JsonArray(), ["active"] = null };
    }

    private Task SaveAsync(long uid, JsonObject s, CancellationToken ct) => accounts.SetJsonAsync(uid, "summer_board_v1", s, ct);

    private long Attr(JsonObject hero, int key)
    {
        var a = C("attributes", key);
        var v = ((JsonArray)hero["attribute"]!).OfType<JsonObject>().FirstOrDefault(x => N(x["id"]) == key) is { } found ? N(found["value"]) : N(a.GetProperty("default"));
        return Math.Max(N(a.GetProperty("min")), v);
    }

    private static void SetAttr(JsonObject hero, int key, long value)
    {
        value = Math.Max(0, value);
        Require(value <= int.MaxValue, "Board attribute out of range");
        var arr = (JsonArray)hero["attribute"]!;
        foreach (var x in arr.OfType<JsonObject>().Where(x => N(x["id"]) == key).ToList()) arr.Remove(x);
        arr.Add(new JsonObject { ["id"] = key, ["value"] = value });
    }

    private static JsonObject EmptyChance() => new() { ["chance_id"] = 0, ["is_unlock"] = 0, ["item_list"] = new JsonArray(), ["choose_list"] = new JsonArray() };

    private JsonObject NewUnit(long id, long type, long x, long z, long dir, IEnumerable<(int, long)> attrs) => new()
    {
        ["id"] = id, ["type"] = type, ["pos"] = Pos(x, z), ["direction"] = dir,
        ["attribute"] = new JsonArray(attrs.Select(a => (JsonNode)new JsonObject { ["id"] = a.Item1, ["value"] = a.Item2 }).ToArray()),
        ["item"] = new JsonArray(), ["buff"] = new JsonArray(), ["use_item"] = 0, ["chance"] = EmptyChance(), ["tmp_item"] = new JsonArray()
    };

    // ---------------- grid ----------------

    private JsonObject Grid(Ctx c, long x, long z)
    {
        var native = Maps.GetProperty(c.Chapter.ToString());
        Require(native.GetProperty("tiles").TryGetProperty($"{x},{z}", out var baseTile), "Board coordinate out of map");
        JsonNode Def(string k, JsonElement src) => src.TryGetProperty(k, out var v) ? JsonNode.Parse(v.GetRawText())! : k.StartsWith("banDir") ? new JsonArray(Enumerable.Repeat<JsonNode?>(false, 6).ToArray()) : JsonValue.Create(0);
        var outp = new JsonObject();
        foreach (var k in FloorKeys.Concat(ThingKeys)) outp[k] = Def(k, baseTile);
        foreach (var ch in ((JsonArray)c.Map["floor"]!).OfType<JsonObject>().Where(ch => SamePos(ch["pos"], x, z)))
        {
            var tag = N(ch["tag"]);
            var src = tag > 0 ? native.GetProperty("extendInfo")[(int)tag - 1] : baseTile;
            foreach (var k in FloorKeys) outp[k] = Def(k, src);
            outp["rotationY"] = N(ch["rotation"]);
            outp["status"] = N(ch["state"]);
        }
        foreach (var ch in ((JsonArray)c.Map["object"]!).OfType<JsonObject>().Where(ch => SamePos(ch["pos"], x, z)))
        {
            var tag = N(ch["tag"]);
            foreach (var k in ThingKeys)
                outp[k] = tag == -1 ? JsonValue.Create(0) : Def(k, tag > 0 ? native.GetProperty("extendThingInfo")[(int)tag - 1] : baseTile);
            outp["thingRotationY"] = N(ch["rotation"]);
        }
        foreach (var ch in ((JsonArray)c.Map["ban_dir"]!).OfType<JsonObject>().Where(ch => SamePos(ch["pos"], x, z)))
        {
            var ban = new JsonArray(Enumerable.Repeat<JsonNode?>(false, 6).ToArray());
            var hide = new JsonArray(Enumerable.Repeat<JsonNode?>(false, 6).ToArray());
            foreach (var it in ((JsonArray)ch["ban_dir"]!).OfType<JsonObject>()) { ban[(int)N(it["direction"])] = true; hide[(int)N(it["direction"])] = N(it["is_hide"]) == 1; }
            outp["banDir"] = ban; outp["banDirIsHide"] = hide;
        }
        outp["x"] = x; outp["z"] = z;
        return outp;
    }

    private JsonElement Hex(JsonObject tile) => Has("hexes", N(tile["typeID"])) ? C("hexes", N(tile["typeID"])) : throw new ModeError("Unknown hex type");

    private List<(long X, long Z, int Dir)> Neighbors(Ctx c, JsonObject hero)
    {
        var px = N(hero["pos"]!["x"]); var pz = N(hero["pos"]!["z"]);
        var src = Grid(c, px, pz);
        var outp = new List<(long, long, int)>();
        for (var d = 0; d < 6; d++)
        {
            long x = px + Dirs[d].X, z = pz + Dirs[d].Z;
            if (src["banDir"]![d]!.GetValueKind() == JsonValueKind.True || !Maps.GetProperty(c.Chapter.ToString()).GetProperty("tiles").TryGetProperty($"{x},{z}", out _)) continue;
            var t = Grid(c, x, z);
            var thing = N(t["thingTypeID"]);
            var fix = thing != 0 && Has("things", thing) && N(C("things", thing).GetProperty("fix_walkable")) == 1;
            if (N(Hex(t).GetProperty("walkable")) == 1 && !fix) outp.Add((x, z, d));
        }
        return outp;
    }

    // ---------------- event engine ----------------

    private static JsonNode Value(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number) return new JsonObject { ["type"] = 1, ["integer"] = v.GetInt64() };
        if (v.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number)) return new JsonObject { ["type"] = 2, ["array"] = JsonNode.Parse(v.GetRawText()) };
        return new JsonObject { ["type"] = 3, ["array2"] = new JsonArray(v.EnumerateArray().Select(r => (JsonNode)new JsonObject { ["array"] = JsonNode.Parse(r.GetRawText()) }).ToArray()) };
    }

    private long AddNpc(Ctx c, long npc, long x, long z, long dir)
    {
        Require(Has("npcs", npc) && dir is >= 0 and < 6, "Unknown NPC or direction");
        Grid(c, x, z);
        if (c.Heroes.Count >= 65) return 0;
        c.Map["next_npc_id"] ??= c.Heroes.OfType<JsonObject>().Max(h => N(h["id"])) + 1;
        var id = N(c.Map["next_npc_id"]);
        c.Map["next_npc_id"] = id + 1;
        c.Heroes.Add(NewUnit(id, npc, x, z, dir, C("npcs", npc).GetProperty("init_attribute_list").EnumerateArray().Select(a => (a[0].GetInt32(), a[1].GetInt64()))));
        return id;
    }

    private static void DeleteNpc(Ctx c, long id)
    {
        Require(id > 0, "Cannot remove the player");
        foreach (var h in c.Heroes.OfType<JsonObject>().Where(h => N(h["id"]) == id).ToList()) c.Heroes.Remove(h);
    }

    private void Change(Ctx c, string kind, long x, long z, long tag, bool inherit = false)
    {
        var prev = Grid(c, x, z);
        var native = Maps.GetProperty(c.Chapter.ToString());
        JsonObject change;
        if (kind == "floor")
        {
            Require(tag >= 0 && tag <= native.GetProperty("extendInfo").GetArrayLength(), "Unknown floor extension");
            var src = tag > 0 ? native.GetProperty("extendInfo")[(int)tag - 1] : native.GetProperty("tiles").GetProperty($"{x},{z}");
            var rot = inherit ? N(prev["rotationY"]) : src.TryGetProperty("rotationY", out var r) ? N(r) : 0;
            change = new JsonObject { ["pos"] = Pos(x, z), ["tag"] = tag, ["state"] = src.TryGetProperty("status", out var st) ? N(st) : 0, ["rotation"] = rot };
        }
        else
        {
            Require(tag >= -1 && tag <= native.GetProperty("extendThingInfo").GetArrayLength(), "Unknown thing extension");
            var rot = tag == -1 ? 0 : (tag > 0 ? native.GetProperty("extendThingInfo")[(int)tag - 1] : native.GetProperty("tiles").GetProperty($"{x},{z}")) is var src && src.TryGetProperty("thingRotationY", out var r) ? N(r) : 0;
            change = new JsonObject { ["pos"] = Pos(x, z), ["tag"] = tag, ["rotation"] = rot };
        }
        var arr = (JsonArray)c.Map[kind]!;
        foreach (var o in arr.OfType<JsonObject>().Where(o => SamePos(o["pos"], x, z)).ToList()) arr.Remove(o);
        arr.Add(change);
        var cur = Grid(c, x, z);
        var blocked = kind == "floor" ? N(Hex(cur).GetProperty("walkable")) == 0
            : N(cur["thingTypeID"]) != 0 && Has("things", N(cur["thingTypeID"])) && N(C("things", N(cur["thingTypeID"])).GetProperty("fix_walkable")) == 1;
        if (!blocked) return;
        foreach (var h in c.Heroes.OfType<JsonObject>().Where(h => SamePos(h["pos"], x, z)).ToList())
        {
            if (N(h["id"]) == 0)
            {
                var lv = C("levels", c.Chapter);
                h["pos"] = Pos(lv.GetProperty("init_pos")[0].GetInt64(), lv.GetProperty("init_pos")[1].GetInt64());
                h["direction"] = N(lv.GetProperty("init_dir"));
            }
            else c.Heroes.Remove(h);
        }
    }

    private void RemoveBuff(Ctx c, long id, long bid)
    {
        var buffs = (JsonArray)c.Hero(id)["buff"]!;
        var old = buffs.OfType<JsonObject>().FirstOrDefault(b => N(b["id"]) == bid);
        if (old is null) return;
        buffs.Remove(old);
        var pool = N(C("buffs", bid).GetProperty("destory_event"));
        if (pool != 0) Pool(c, pool, 15, [bid], id);
    }

    private void AddBuff(Ctx c, long id, long bid)
    {
        Require(Has("buffs", bid), "Unknown board buff");
        var hero = c.Hero(id);
        var cfg = C("buffs", bid);
        if (Attr(hero, 9) >= 1 && N(cfg.GetProperty("buff_type")) == 2) return;
        var buffs = (JsonArray)hero["buff"]!;
        if (N(cfg.GetProperty("clash")) == 1)
            foreach (var b in buffs.OfType<JsonObject>().Where(b => N(C("buffs", N(b["id"])).GetProperty("clash")) == 1).ToList()) RemoveBuff(c, id, N(b["id"]));
        foreach (var b in buffs.OfType<JsonObject>().Where(b => N(b["id"]) == bid).ToList()) buffs.Remove(b);
        buffs.Add(new JsonObject { ["id"] = bid, ["duration"] = 0, ["buff_effect"] = new JsonArray() });
        var ev = N(cfg.GetProperty("add_event"));
        if (ev != 0) Pool(c, ev, 16, [bid], id);
    }

    private void Pool(Ctx c, long pool, int timing, long[]? prm = null, long actor = 0, long other = -1, JsonObject? tile = null)
    {
        c.Depth++;
        try
        {
            Require(c.Depth <= 32, "Board event nesting too deep");
            Require(Has("events", pool), "Board event config missing");
            var cfg = C("events", pool);
            var rows = cfg.GetProperty(actor == 0 ? "player_params" : "npc_params");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0) return;
            if (N(cfg.GetProperty("type")) == 2)
            {
                Require(rows.GetArrayLength() == 1, "Bad chance entry");
                OpenChance(c, N(rows[0]), actor);
                return;
            }
            var list = new JsonArray();
            c.Frames.Add((85031, new JsonObject
            {
                ["event"] = new JsonObject
                {
                    ["trigger_type"] = timing, ["trigger_type_param"] = new JsonArray((prm ?? []).Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
                    ["event_pool_id"] = pool, ["trigger_player_a"] = actor, ["trigger_player_b"] = other, ["event_list"] = list
                }
            }));
            tile ??= Grid(c, N(c.Hero(actor)["pos"]!["x"]), N(c.Hero(actor)["pos"]!["z"]));
            foreach (var row in rows.EnumerateArray())
            {
                Require(++c.Steps <= 512, "Too many board events");
                var a = row.EnumerateArray().Skip(1).ToArray();
                long A(int i) => N(a[i]);
                var values = new JsonArray();
                list.Add(new JsonObject { ["event"] = new JsonArray(row.EnumerateArray().Select(Value).ToArray()), ["params"] = values });
                var kind = N(row[0]);
                long tx = N(tile["x"]), tz = N(tile["z"]);
                switch (kind)
                {
                    case 10001 or 10009 or 10010 or 10014 or 10015 or 10018 or 10021: break;
                    case 10002: values.Add(AddNpc(c, A(0), A(1), A(2), A(3))); break;
                    case 10003: Change(c, "floor", A(0), A(1), A(2)); break;
                    case 10004: Change(c, "object", A(0), A(1), A(2)); break;
                    case 10005: Change(c, "floor", tx, tz, A(0)); break;
                    case 10006: Change(c, "object", tx, tz, A(0)); break;
                    case 10007 or 10017:
                        {
                            long px = kind == 10007 ? tx : A(0), pz = kind == 10007 ? tz : A(1);
                            if (c.Heroes.OfType<JsonObject>().FirstOrDefault(h => N(h["id"]) != 0 && SamePos(h["pos"], px, pz)) is { } m) DeleteNpc(c, N(m["id"]));
                            break;
                        }
                    case 10008:
                        {
                            Require(actor > 0 && other > 0 && actor != other, "Bad merge actors");
                            var first = c.Hero(actor); var second = c.Hero(other);
                            var npc = A(0);
                            var survivor = new[] { first, second }.FirstOrDefault(h => N(h["type"]) == npc);
                            long id;
                            if (survivor is not null) { id = N(survivor["id"]); DeleteNpc(c, id == actor ? other : actor); }
                            else { var dir = N(first["direction"]); DeleteNpc(c, actor); DeleteNpc(c, other); id = AddNpc(c, npc, tx, tz, dir); }
                            values.Add(id);
                            values.Add(N(c.Hero(id)["direction"]));
                            break;
                        }
                    case 10012:
                        Require(Has("tasks", A(0)), "Unknown board task");
                        if (!((JsonArray)c.Map["task"]!).OfType<JsonObject>().Any(t => N(t["id"]) == A(0))) ((JsonArray)c.Map["task"]!).Add(new JsonObject { ["id"] = A(0), ["progress"] = 0 });
                        break;
                    case 10013:
                        foreach (var t in ((JsonArray)c.Map["task"]!).OfType<JsonObject>().Where(t => N(C("tasks", N(t["id"])).GetProperty("condition_type")) == A(0)))
                            t["progress"] = N(t["progress"]) + A(1);
                        break;
                    case 10019: Change(c, "object", A(0), A(1), -1); break;
                    case 10020: Change(c, "object", tx, tz, -1); break;
                    case 20001:
                        {
                            var e = c.Hero(A(0) == 1 ? actor : other);
                            Require(N(Hex(Grid(c, A(1), A(2))).GetProperty("walkable")) == 1, "Teleport target not walkable");
                            e["pos"] = Pos(A(1), A(2)); e["direction"] = A(3);
                            break;
                        }
                    case 20002:
                        {
                            var e = c.Hero(A(0) == 1 ? actor : other);
                            Require(Has("attributes", A(1)), "Unknown board attribute");
                            SetAttr(e, (int)A(1), Attr(e, (int)A(1)) + A(2));
                            break;
                        }
                    case 20004:
                        {
                            var id = A(0) == 1 ? actor : other;
                            foreach (var b in ((JsonArray)c.Hero(id)["buff"]!).OfType<JsonObject>().Where(b => N(C("buffs", N(b["id"])).GetProperty("buff_type")) == 2).ToList()) RemoveBuff(c, id, N(b["id"]));
                            break;
                        }
                    case 20005: AddBuff(c, A(0) == 1 ? actor : other, A(1)); break;
                    case 20006: RemoveBuff(c, A(0) == 1 ? actor : other, A(1)); break;
                    default: throw new ModeError($"Board event {kind} not supported");
                }
            }
        }
        finally { c.Depth--; }
    }

    private void Trigger(Ctx c, JsonObject tile, string key, int timing, long actor = 0)
    {
        var pool = N(tile[key]);
        if (pool != 0) Pool(c, pool, timing, [N(tile["x"]), N(tile["z"])], actor, tile: tile);
    }

    private void BuffEffect(Ctx c, long id, int when, long other = -1)
    {
        var timings = new Dictionary<int, int> { [1] = 21, [2] = 22, [3] = 23, [4] = 24, [5] = 14 };
        foreach (var b in ((JsonArray)c.Hero(id)["buff"]!).OfType<JsonObject>().ToList())
        {
            if (!c.Alive(id)) break;
            if (!((JsonArray)c.Hero(id)["buff"]!).Contains(b)) continue;
            foreach (var eff in C("buffs", N(b["id"])).GetProperty("buff_efffect").EnumerateArray().Select(N))
            {
                var ec = C("effects", eff);
                if (N(ec.GetProperty("trigger_time")) != when) continue;
                var arr = (JsonArray)b["buff_effect"]!;
                var used = arr.OfType<JsonObject>().FirstOrDefault(r => N(r["id"]) == eff) is { } u ? N(u["num"]) : 0;
                var (actor, target) = when is 1 or 2 ? (other, id) : (id, other);
                var count = N(ec.GetProperty("trigger_count"));
                if (count == -1 || used < count) Pool(c, N(ec.GetProperty("event")), timings[when], [eff], actor, target);
                foreach (var r in arr.OfType<JsonObject>().Where(r => N(r["id"]) == eff).ToList()) arr.Remove(r);
                arr.Add(new JsonObject { ["id"] = eff, ["num"] = used + 1 });
            }
        }
    }

    private void BuffTurn(Ctx c, long id)
    {
        var buffs = ((JsonArray)c.Hero(id)["buff"]!).OfType<JsonObject>().ToList();
        foreach (var b in buffs) b["duration"] = N(b["duration"]) + 1;
        foreach (var b in buffs)
        {
            if (!c.Alive(id) || !((JsonArray)c.Hero(id)["buff"]!).Contains(b)) continue;
            var cfg = C("buffs", N(b["id"]));
            long upd = N(cfg.GetProperty("updateround")), remain = N(cfg.GetProperty("remainround")), turnEv = N(cfg.GetProperty("turn_event"));
            if (upd > 0 && N(b["duration"]) % upd == 0 && turnEv != 0) Pool(c, turnEv, 17, [N(b["id"])], id);
            if (remain > 0 && remain <= N(b["duration"])) RemoveBuff(c, id, N(b["id"]));
        }
    }

    private JsonObject? NpcAt(Ctx c, JsonNode? pos, long exclude = 0) =>
        c.Heroes.OfType<JsonObject>().FirstOrDefault(h => N(h["id"]) > 0 && N(h["id"]) != exclude && SamePos(h["pos"], N(pos!["x"]), N(pos!["z"])));

    private void HeroEncounter(Ctx c, JsonObject tile)
    {
        var hero = c.Player;
        if (NpcAt(c, hero["pos"]) is not { } npc) return;
        var id = N(npc["id"]);
        var stay = Attr(hero, 5) <= 0;
        BuffEffect(c, 0, stay ? 3 : 4, id);
        if (!c.Alive(id)) return;
        var pool = N(C("npcs", N(npc["type"])).GetProperty(stay ? "player_stay_npc_event" : "player_cross_npc_event"));
        if (pool != 0) Pool(c, pool, stay ? 10 : 6, [id], 0, id, tile);
    }

    private static void StopNpcs(SummerBoard b, Ctx c, long turns)
    {
        Require(turns is > 0 and <= 3, "Bad stop turns");
        foreach (var n in c.Heroes.OfType<JsonObject>().Where(h => N(h["id"]) > 0)) SetAttr(n, 12, Math.Max(b.Attr(n, 12), turns));
    }

    private List<JsonObject> RunTurn(Ctx c)
    {
        var paths = new List<JsonObject>();
        var hops = 0;
        foreach (var id in c.Heroes.OfType<JsonObject>().Select(h => N(h["id"])).Where(i => i > 0).OrderByDescending(i => i).ToList())
        {
            if (!c.Alive(id)) continue;
            var npc = c.Hero(id);
            var stopped = Attr(npc, 12);
            if (stopped > 0) { SetAttr(npc, 12, stopped - 1); continue; }
            if (Neighbors(c, npc).Count == 0) continue;
            long count = Attr(npc, 2), size = Attr(npc, 1), fix = Attr(npc, 3);
            var budget = Enumerable.Range(0, (int)count).Sum(_ => fix > 0 ? fix : Random.Shared.Next(1, (int)size + 1));
            SetAttr(npc, 5, budget);
            var path = new JsonArray();
            var route = new JsonObject { ["npc_id"] = id, ["path"] = path, ["move_num"] = budget };
            paths.Add(route);
            var cfg = C("npcs", N(npc["type"]));
            if (N(cfg.GetProperty("npc_start_event")) != 0) Pool(c, N(cfg.GetProperty("npc_start_event")), 11, [id], id);
            Require(c.Alive(id) && Attr(npc, 5) > 0, "NPC start event broke the roll");
            var visited = new HashSet<string>();
            while (c.Alive(id) && Attr(npc, 5) > 0)
            {
                Require(++hops <= 512, "NPC path too long");
                var options = Neighbors(c, npc);
                Require(options.Count > 0, "NPC path closed");
                var nonRev = options.Where(o => o.Dir != (N(npc["direction"]) + 3) % 6).ToList();
                var pick = (nonRev.Count > 0 ? nonRev : options)[Random.Shared.Next((nonRev.Count > 0 ? nonRev : options).Count)];
                Require(visited.Add($"{N(npc["pos"]!["x"])},{N(npc["pos"]!["z"])},{pick},{Attr(npc, 5)}"), "NPC auto-tile loop");
                path.Add(Pos(pick.X, pick.Z));
                Step(c, id, pick, path.Count - 1);
            }
            if (c.Alive(id))
            {
                BuffTurn(c, id);
                if (c.Alive(id) && N(cfg.GetProperty("npc_stop_event")) != 0) Pool(c, N(cfg.GetProperty("npc_stop_event")), 12, [id], id);
            }
        }
        return paths;
    }

    private void Step(Ctx c, long id, (long X, long Z, int Dir) target, int index)
    {
        var npc = c.Hero(id);
        var source = Grid(c, N(npc["pos"]!["x"]), N(npc["pos"]!["z"]));
        var dest = Grid(c, target.X, target.Z);
        if (N(Hex(dest).GetProperty("updatable")) == 1) { npc["pos"] = Pos(target.X, target.Z); npc["direction"] = target.Dir; return; }
        if (index == 0) { Trigger(c, source, "thingStartMoveParam", 26, id); Trigger(c, source, "startMoveParam", 4, id); }
        Trigger(c, source, "thingExitParam", 25, id); Trigger(c, source, "exitParam", 3, id);
        if (!c.Alive(id)) return;
        npc["pos"] = Pos(target.X, target.Z); npc["direction"] = target.Dir;
        SetAttr(npc, 5, Attr(npc, 5) - 1);
        // encounter
        var hero = c.Player;
        var stay = Attr(npc, 5) <= 0;
        var cfg = C("npcs", N(npc["type"]));
        if (SamePos(npc["pos"], N(hero["pos"]!["x"]), N(hero["pos"]!["z"])))
        {
            BuffEffect(c, 0, stay ? 1 : 2, id);
            if (c.Alive(id) && N(cfg.GetProperty(stay ? "npc_stay_player_event" : "npc_cross_player_event")) is var pool and not 0) Pool(c, pool, stay ? 9 : 7, [id], id, 0, dest);
        }
        else if (stay && NpcAt(c, npc["pos"], id) is { } other && N(cfg.GetProperty("npc_stay_npc_event")) is var sp and not 0)
            Pool(c, sp, 8, [id], id, N(other["id"]), dest);
        if (!c.Alive(id)) return;
        Trigger(c, dest, "thingEnterParam", 19, id);
        if (!c.Alive(id)) return;
        Trigger(c, dest, "enterParam", 2, id);
        if (!c.Alive(id)) return;
        if (Attr(npc, 5) <= 0)
        {
            var tp = N(dest["thingEndMoveParam"]);
            var hasThing = tp != 0 && Has("events", tp) && C("events", tp).GetProperty("npc_params").GetArrayLength() > 0;
            Trigger(c, dest, hasThing ? "thingEndMoveParam" : "endMoveParam", hasThing ? 27 : 5, id);
        }
        if (c.Alive(id)) BuffEffect(c, id, 5);
    }

    // ---------------- chances, props, games ----------------

    private JsonElement Prop(long id) => Has("props", id) ? C("props", id) : throw new ModeError("Unknown board prop");

    private static List<long> Draw(int count, List<long> candidates)
    {
        Require(count > 0 && count <= candidates.Count, "Bad prop draw count");
        return candidates.OrderBy(_ => Random.Shared.Next()).Take(count).ToList();
    }

    private void OpenChance(Ctx c, long cid, long actor)
    {
        Require(actor == 0 && Has("chances", cid), "Unknown board chance");
        var hero = c.Player;
        Require(N(hero["chance"]!["chance_id"]) == 0 && ((JsonArray)hero["tmp_item"]!).Count == 0, "Finish the current chance first");
        var cfg = C("chances", cid);
        var kind = (int)N(cfg.GetProperty("type"));
        Require(kind is >= 1 and <= 14, "Unsupported board chance");
        var items = new List<long>();
        var chosen = new List<long>();
        var p = cfg.GetProperty("params");
        if (kind is 1 or 2) items = Draw(p[0].GetInt32(), p[1].EnumerateArray().Select(N).ToList());
        else if (kind == 5)
        {
            var index = Random.Shared.Next(p.GetArrayLength());
            var row = p[index];
            var sub = row[0].GetInt32();
            Require(sub is 1 or 2 or 4 or 5, "Unsupported random chance");
            items = [index + 1, sub];
            if (sub == 2) items.AddRange(Draw(row[1].GetInt32(), row[2].EnumerateArray().Select(N).ToList()));
            else items.Add(row[1].GetInt64());
        }
        else if (kind == 9)
        {
            var rewards = p[1];
            if (p[0].GetInt32() == 2) items = Draw(rewards.EnumerateArray().Max(r => r[1].GetInt32()), p[2].EnumerateArray().Select(N).ToList());
        }
        else if (kind == 14) chosen = [Attr(hero, 13), 0, 0];
        else if (NestedKinds.Contains(kind))
        {
            c.Map["nested_token"] = Guid.NewGuid().ToString("N");
            if (kind == 12)
                c.Frames.Add((86008, new JsonObject { ["activity_id"] = 2800019, ["tangram_info_list"] = new JsonArray(new[] { 3001, 3002 }.Select(i => (JsonNode)new JsonObject { ["stage_id"] = i, ["tips_id_list"] = new JsonArray(), ["is_animation_finish"] = false }).ToArray()) }));
        }
        hero["chance"] = new JsonObject
        {
            ["chance_id"] = cid, ["is_unlock"] = N(cfg.GetProperty("cost")) == 0 ? 1 : 0,
            ["item_list"] = new JsonArray(items.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()), ["choose_list"] = new JsonArray(chosen.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray())
        };
        c.Frames.Add((85033, new JsonObject { ["chance"] = hero["chance"]!.DeepClone() }));
    }

    private static void Pending(JsonObject hero, IEnumerable<long> items)
    {
        var tmp = (JsonArray)hero["tmp_item"]!;
        foreach (var i in items) tmp.Add(i);
        Require(tmp.Count <= 64, "Too many pending props");
    }

    private void ChanceOp(Ctx c, uint cmd, IReadOnlyDictionary<string, object?> body)
    {
        var hero = c.Player;
        var chance = (JsonObject)hero["chance"]!;
        if (cmd is 85020 or 85022 or 85018)
        {
            var cid = (long)HandlerHelpers.ReadUInt(body, "chance_id");
            Require(cid > 0 && N(chance["chance_id"]) == cid, "No such board chance");
            var cfg = C("chances", cid);
            var kind = (int)N(cfg.GetProperty("type"));
            if (cmd == 85020)
            {
                Require(N(chance["is_unlock"]) == 0, "Chance already unlocked");
                var cost = N(cfg.GetProperty("cost"));
                Require(Attr(hero, 4) >= cost, "Not enough board coins");
                SetAttr(hero, 4, Attr(hero, 4) - cost);
                chance["is_unlock"] = 1;
            }
            else if (cmd == 85018)
            {
                var pid = (long)HandlerHelpers.ReadUInt(body, "prop_id");
                var item = Prop(pid);
                Require(kind == 1 && N(chance["is_unlock"]) == 1, "Not an open board shop");
                var items = ((JsonArray)chance["item_list"]!).Select(N).ToList();
                var chosen = (JsonArray)chance["choose_list"]!;
                Require(items.Contains(pid) && !chosen.Select(N).Contains(pid), "Prop missing or bought");
                Require(Attr(hero, 4) >= N(item.GetProperty("cost")), "Not enough board coins");
                SetAttr(hero, 4, Attr(hero, 4) - N(item.GetProperty("cost")));
                chosen.Add(pid);
                Pending(hero, [pid]);
            }
            else
            {
                var result = HandlerHelpers.ReadUInt(body, "result");
                var score = (long)HandlerHelpers.ReadUInt(body, "score");
                Require(result is 0 or 1, "Bad chance result");
                Require(result == 1 || N(chance["is_unlock"]) == 1, "Unlock the chance first");
                Require(kind != 5 || result == 0, "Random chance must be confirmed");
                if (kind is 9 or 14)
                {
                    if (!(result == 1 && N(chance["is_unlock"]) == 0)) FinishGame(c, cfg, kind, (int)result, score);
                    else Require(score == 0, "Closed chance cannot submit points");
                }
                else if (NestedKinds.Contains(kind))
                {
                    var spec = C("nested", cid);
                    if (result == 0 && N(spec.GetProperty("type")) != 0) Require(c.Map["nested_result"]?["win"]?.GetValueKind() == JsonValueKind.True, "No successful minigame result yet");
                    Require(kind is not (8 or 10 or 11 or 12 or 13) || score == 0, "This minigame submits no score");
                    foreach (var k in new[] { "nested_token", "nested_bid", "nested_result" }) c.Map.Remove(k);
                }
                else Require(score == 0, "Non-battle chance score must be zero");
                if (result == 0)
                {
                    var items = ((JsonArray)chance["item_list"]!).Select(N).ToList();
                    if (kind == 2) Pending(hero, items);
                    else if (kind == 5)
                    {
                        var sub = items[1];
                        var vals = items.Skip(2).ToList();
                        if (sub == 1) SetAttr(hero, 4, Attr(hero, 4) + vals[0]);
                        else if (sub == 2) Pending(hero, vals);
                        else if (sub == 4) AddBuff(c, 0, vals[0]);
                        else if (sub == 5) StopNpcs(this, c, vals[0]);
                    }
                }
                hero["chance"] = EmptyChance();
                var ev = N(cfg.GetProperty("chance_event"));
                if (result == 0 && ev != 0) Pool(c, ev, 13, [cid]);
            }
        }
        else if (cmd is 85024 or 85036)
        {
            var pid = (long)HandlerHelpers.ReadUInt(body, "prop_id");
            Prop(pid);
            var tmp = (JsonArray)hero["tmp_item"]!;
            var entry = tmp.FirstOrDefault(x => N(x) == pid);
            Require(entry is not null, "Prop not pending");
            if (cmd == 85024)
            {
                var items = (JsonArray)hero["item"]!;
                var rep = (long)HandlerHelpers.ReadUInt(body, "replace_prop_id");
                if (rep != 0) { var old = items.FirstOrDefault(x => N(x) == rep); Require(old is not null, "Replaced prop missing"); items.Remove(old); }
                Require(items.Count < 6, "Prop bag full");
                items.Add(pid);
            }
            tmp.Remove(entry);
        }
        else UseProp(c, body);
    }

    private void FinishGame(Ctx c, JsonElement cfg, int kind, int result, long score)
    {
        var hero = c.Player;
        var chance = (JsonObject)hero["chance"]!;
        var p = cfg.GetProperty("params");
        if (kind == 9)
        {
            Require(result == 0 && score is 1 or 2 or 3, "Bad QTE result");
            var reward = p[1].EnumerateArray().Where(r => r[0].GetInt64() == score).Select(r => r[1].GetInt64()).FirstOrDefault(-1);
            Require(reward >= 0, "Missing QTE reward");
            if (p[0].GetInt32() == 1) SetAttr(hero, 4, Attr(hero, 4) + reward);
            else Pending(hero, ((JsonArray)chance["item_list"]!).Select(N).Take((int)reward));
        }
        else
        {
            var cl = ((JsonArray)chance["choose_list"]!).Select(N).ToList();
            var goal = p[0].GetInt64();
            Require(score == cl[1], "Arena points mismatch");
            Require((result == 0 && cl[1] >= goal) || (result == 1 && cl[1] < goal && cl[0] == 0), "Arena not finished");
            if (result == 1) SetAttr(hero, 13, Attr(hero, 13) + 1);
            c.Active.Remove("arena_prop_dice");
        }
    }

    private void UseProp(Ctx c, IReadOnlyDictionary<string, object?> body)
    {
        var hero = c.Player;
        var pid = (long)HandlerHelpers.ReadUInt(body, "prop_id");
        var cfg = Prop(pid);
        var prm = HandlerHelpers.ReadIdList(body, "params").ToList();
        var chance = (JsonObject)hero["chance"]!;
        var items = (JsonArray)hero["item"]!;
        var held = items.FirstOrDefault(x => N(x) == pid);
        if (N(chance["chance_id"]) is var cid and > 0 && N(C("chances", cid).GetProperty("type")) == 14)
        {
            var cl = (JsonArray)chance["choose_list"]!;
            Require(N(chance["is_unlock"]) == 1 && N(cl[0]) > 0 && N(cl[1]) < C("chances", cid).GetProperty("params")[0].GetInt64() && N(cl[2]) == 0, "Arena prop not usable now");
            Require(Cfg.GetProperty("arena_props").EnumerateArray().Any(x => N(x) == pid) && held is not null, "Prop not usable in the arena");
            if (pid == 10002) { Require(prm.Count == 1 && prm[0] >= 1 && prm[0] <= Math.Min(6, Attr(hero, 1)), "Bad chosen die"); c.Active["arena_prop_dice"] = prm[0]; }
            else Require(pid == 10001 && prm.Count == 0, "Double dice takes no parameter");
            items.Remove(held);
            cl[2] = pid;
            return;
        }
        Require(prm.Count <= 2, "Bad prop parameters");
        Require(Attr(hero, 8) == 0 && Attr(hero, 5) == 0 && Attr(hero, 7) == 0 && N(hero["use_item"]) == 0 && N(chance["chance_id"]) == 0 && ((JsonArray)hero["tmp_item"]!).Count == 0, "Cannot use a prop now");
        Require(held is not null, "Prop not held");
        var kind = N(cfg.GetProperty("type"));
        var cp = cfg.GetProperty("params");
        if (kind is 10001 or 10005 or 10006 or 10009)
        {
            Require(prm.Count == 0, "Prop takes no target");
            if (kind == 10001) AddBuff(c, 0, cp[0].GetInt64());
            else if (kind == 10009) StopNpcs(this, c, cp[0].GetInt64());
            else
            {
                var key = cp[0].GetInt32(); var delta = cp[1].GetInt64();
                Require(Has("attributes", key) && (kind != 10006 || Attr(hero, key) + delta <= 2), "Dice count out of range");
                SetAttr(hero, key, Attr(hero, key) + delta);
            }
        }
        else if (kind == 10004)
        {
            Require(prm.Count == 1 && prm[0] >= 1 && prm[0] <= Math.Min(6, Attr(hero, 1)), "Chosen die out of range");
            c.Active["prop_dice"] = prm[0];
        }
        else
        {
            Require(prm.Count == 2, "Prop needs a target cell");
            long x = prm[0], z = prm[1];
            var tile = Grid(c, x, z);
            var target = NpcAt(c, Pos(x, z));
            var onPlayer = SamePos(hero["pos"], x, z);
            switch (kind)
            {
                case 10002: Require(target is not null, "No NPC there"); DeleteNpc(c, N(target!["id"])); break;
                case 10003:
                    {
                        Require(target is not null, "No NPC there");
                        var (op, od) = (hero["pos"]!.DeepClone(), N(hero["direction"]));
                        hero["pos"] = target!["pos"]!.DeepClone(); hero["direction"] = N(target["direction"]);
                        target["pos"] = op; target["direction"] = od;
                        break;
                    }
                case 10007:
                    Require(!onPlayer && target is null && N(tile["thingTypeID"]) == 0, "Cell occupied");
                    Require(N(Hex(tile).GetProperty("walkable")) >= 1 && N(Hex(tile).GetProperty("updatable")) == 0, "Cannot place here");
                    Change(c, "object", x, z, cp[0].GetInt64());
                    break;
                case 10008:
                    if (onPlayer)
                    {
                        var clash = ((JsonArray)hero["buff"]!).OfType<JsonObject>().Where(b => N(C("buffs", N(b["id"])).GetProperty("clash")) == 1).Select(b => N(b["id"])).ToList();
                        Require(clash.Count > 0, "No effect to clear");
                        foreach (var b in clash) RemoveBuff(c, 0, b);
                    }
                    else
                    {
                        var th = N(tile["thingTypeID"]);
                        Require(th != 0 && Has("things", th) && N(C("things", th).GetProperty("destoryable")) > 0, "Thing cannot be destroyed");
                        Change(c, "object", x, z, -1);
                    }
                    break;
                case 10010:
                    Require(target is not null || onPlayer, "No unit there");
                    AddBuff(c, target is not null ? N(target["id"]) : 0, cp[0].GetInt64());
                    break;
                default: throw new ModeError("Unknown prop effect");
            }
        }
        items.Remove(held);
        hero["use_item"] = pid;
    }

    // ---------------- run lifecycle ----------------

    private List<(uint, JsonNode)> Bootstrap(Ctx c)
    {
        var lv = C("levels", c.Chapter);
        var hero = c.Player;
        var packets = new List<(uint, JsonNode)>();
        void Apply(long pool, int timing, long[]? prm)
        {
            var rows = C("events", pool).GetProperty("player_params");
            var outRows = new List<JsonElement>();
            var children = new List<(long, long)>();
            var npcIds = new List<long>();
            foreach (var row in rows.EnumerateArray())
            {
                var kind = N(row[0]);
                long A(int i) => N(row[i + 1]);
                switch (kind)
                {
                    case 20002: SetAttr(hero, (int)A(1), Attr(hero, (int)A(1)) + A(2)); break;
                    case 10012:
                        if (!((JsonArray)c.Map["task"]!).OfType<JsonObject>().Any(t => N(t["id"]) == A(0))) ((JsonArray)c.Map["task"]!).Add(new JsonObject { ["id"] = A(0), ["progress"] = 0 });
                        break;
                    case 10002:
                        {
                            var id = c.Heroes.OfType<JsonObject>().Max(h => N(h["id"])) + 1;
                            c.Heroes.Add(NewUnit(id, A(0), A(1), A(2), A(3), C("npcs", A(0)).GetProperty("init_attribute_list").EnumerateArray().Select(a => (a[0].GetInt32(), a[1].GetInt64()))));
                            npcIds.Add(id);
                            break;
                        }
                    case 20005:
                        ((JsonArray)hero["buff"]!).Add(new JsonObject { ["id"] = A(1), ["duration"] = 0, ["buff_effect"] = new JsonArray() });
                        if (N(C("buffs", A(1)).GetProperty("add_event")) is var ev and not 0) children.Add((ev, A(1)));
                        break;
                    case 10001 or 10015: break;
                    default: throw new ModeError("Unsupported bootstrap event");
                }
                outRows.Add(row);
            }
            var npcIter = npcIds.GetEnumerator();
            packets.Add((85031, new JsonObject
            {
                ["event"] = new JsonObject
                {
                    ["trigger_type"] = timing, ["trigger_type_param"] = new JsonArray((prm ?? []).Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()), ["event_pool_id"] = pool,
                    ["trigger_player_a"] = 0, ["trigger_player_b"] = -1,
                    ["event_list"] = new JsonArray(outRows.Select(r => (JsonNode)new JsonObject
                    {
                        ["event"] = new JsonArray(r.EnumerateArray().Select(Value).ToArray()),
                        ["params"] = N(r[0]) == 10002 && npcIter.MoveNext() ? new JsonArray(npcIter.Current) : new JsonArray()
                    }).ToArray())
                }
            }));
            foreach (var (ev, buff) in children) Apply(ev, 16, [buff]);
        }
        Apply(N(lv.GetProperty("eventpool_id")), 1, null);
        SetAttr(hero, 6, 1);
        return packets;
    }

    private static bool Unlocked(SummerBoard b, JsonObject s, long chapter)
    {
        var cid = N(b.C("levels", chapter).GetProperty("unlock_condition"));
        var done = ((JsonArray)s["completed"]!).Select(N).ToHashSet();
        return cid == 0 || b.C("conditions", cid).GetProperty("params").EnumerateArray().All(p => done.Contains(N(p)));
    }

    /// <summary>Records in-run task completion and chapter clears (summer_board_progress.persist).</summary>
    private List<(uint, JsonNode)> Persist(JsonObject s)
    {
        var events = new List<(uint, JsonNode)>();
        if (s["active"] is not JsonObject active) return events;
        var c = new Ctx(this, active);
        var reached = ((JsonArray)c.Map["task"]!).OfType<JsonObject>().Where(t => N(t["progress"]) >= N(C("tasks", N(t["id"])).GetProperty("need"))).Select(t => N(t["id"]));
        var done = ((JsonArray?)active["tasks_done"])?.Select(N) ?? [];
        var finished = ((JsonArray)s["finished_tasks"]!).Select(N).Union(reached).Union(done).OrderBy(x => x).ToList();
        s["finished_tasks"] = new JsonArray(finished.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        if (Attr(c.Player, 7) != 0)
        {
            var need = Cfg.GetProperty("map_tasks").GetProperty(c.Chapter.ToString()).EnumerateArray().Select(N);
            Require(need.All(t => reached.Contains(t) || done.Contains(t)), "Finish every board task before the end");
            var completed = (JsonArray)s["completed"]!;
            if (!completed.Select(N).Contains(c.Chapter))
            {
                completed.Add(c.Chapter);
                var cleared = completed.Select(N).ToHashSet();
                events.Add((51001, new JsonObject
                {
                    ["data_list"] = new JsonArray(Cfg.GetProperty("conditions").EnumerateObject().Where(p => p.Value.GetProperty("params").EnumerateArray().Any(x => N(x) == c.Chapter))
                        .Select(p => (JsonNode)new JsonObject { ["id"] = long.Parse(p.Name), ["data"] = p.Value.GetProperty("params").EnumerateArray().All(x => cleared.Contains(N(x))) ? 1 : 0 }).ToArray())
                }));
            }
        }
        return events;
    }

    // ---------------- wire ----------------

    private static object? ToWire(JsonNode? n) => n switch
    {
        null => null,
        JsonObject o => o.ToDictionary(p => p.Key, p => ToWire(p.Value)),
        JsonArray a => a.Select(ToWire).ToList(),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => N(v),
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValueKind() == JsonValueKind.True,
        JsonValue v => v.ToString()
    };

    private GameResponse F(ushort srv, uint cmd, JsonNode body) => new(cmd, 0, srv, codec.Encode($"sc_{cmd}", (Dictionary<string, object?>)ToWire(body)!));

    private static JsonObject NativeMap(JsonObject map)
    {
        var o = new JsonObject();
        foreach (var k in new[] { "turn", "hero", "floor", "object", "ban_dir", "task" }) o[k] = map[k]!.DeepClone();
        return o;
    }

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        var done = ((JsonArray)s["completed"]!).Select(N).ToHashSet();
        return
        [
            (85035, codec.Encode("sc_85035", new Dictionary<string, object?> { ["chapter_id"] = (uint)(s["active"] is JsonObject a ? N(a["chapter_id"]) : 0) })),
            (51001, codec.Encode("sc_51001", new Dictionary<string, object?>
            {
                ["data_list"] = Cfg.GetProperty("conditions").EnumerateObject().Select(p => (object)new Dictionary<string, object?>
                {
                    ["id"] = uint.Parse(p.Name), ["data"] = p.Value.GetProperty("params").EnumerateArray().All(x => done.Contains(N(x))) ? 1L : 0L
                }).ToArray()
            }))
        ];
    }

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var srv = (ushort)(r.ServerIndex + 1);
        var s = await LoadAsync(uid, ct);
        var frames = new List<GameResponse>();
        var reply = new JsonObject { ["result"] = 0 };
        try
        {
            if (cmd == 85000)
            {
                var chapter = (long)HandlerHelpers.ReadUInt(r.Body, "chapter_id");
                Require(Has("levels", chapter), "Unknown board chapter");
                Require(Unlocked(this, s, chapter), "Previous board area not cleared");
                if (s["active"] is JsonObject act)
                {
                    Require(N(act["chapter_id"]) == chapter, "Finish the current board run first");
                    reply["map"] = NativeMap((JsonObject)act["map"]!);
                }
                else
                {
                    var lv = C("levels", chapter);
                    var map = new JsonObject
                    {
                        ["turn"] = 0, ["floor"] = new JsonArray(), ["object"] = new JsonArray(), ["ban_dir"] = new JsonArray(), ["task"] = new JsonArray(),
                        ["hero"] = new JsonArray(NewUnit(0, 0, lv.GetProperty("init_pos")[0].GetInt64(), lv.GetProperty("init_pos")[1].GetInt64(), N(lv.GetProperty("init_dir")),
                            Cfg.GetProperty("attributes").EnumerateObject().Select(p => (int.Parse(p.Name), N(p.Value.GetProperty("default"))))))
                    };
                    reply["map"] = NativeMap(map);
                    var active = new JsonObject { ["chapter_id"] = chapter, ["run_id"] = Guid.NewGuid().ToString("N"), ["map"] = map };
                    var c = new Ctx(this, active);
                    var events = Bootstrap(c);
                    s["active"] = active;
                    events.AddRange(Persist(s));
                    frames.AddRange(events.Select(e => F(srv, e.Item1, e.Item2)));
                }
            }
            else
            {
                Require(s["active"] is JsonObject, "No board run");
                var active = (JsonObject)s["active"]!;
                var c = new Ctx(this, active);
                var hero = c.Player;
                switch (cmd)
                {
                    case 85012:
                        {
                            Require(Attr(hero, 8) == 0 && Attr(hero, 5) == 0 && Attr(hero, 7) == 0, "Cannot roll now");
                            Require(N(hero["chance"]!["chance_id"]) == 0 && ((JsonArray)hero["tmp_item"]!).Count == 0, "Finish the current event first");
                            long count = Attr(hero, 2), size = Attr(hero, 1), fix = Attr(hero, 3);
                            var sel = N(active["prop_dice"]);
                            var dice = Enumerable.Range(0, (int)count).Select(_ => sel != 0 ? sel : fix != 0 ? fix : Random.Shared.Next(1, (int)size + 1)).ToList();
                            SetAttr(hero, 5, dice.Sum());
                            SetAttr(hero, 8, 1);
                            reply["dice_list"] = new JsonArray(dice.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray());
                            break;
                        }
                    case 85010:
                        {
                            Require(r.Body.GetValueOrDefault("next_pos") is IReadOnlyDictionary<string, object?> np, "Bad move target");
                            var p = (IReadOnlyDictionary<string, object?>)r.Body["next_pos"]!;
                            long x = Convert.ToInt64(p.GetValueOrDefault("x") ?? 0), z = Convert.ToInt64(p.GetValueOrDefault("z") ?? 0);
                            Require(Attr(hero, 8) is 1 or 2 or 3 && Attr(hero, 5) > 0 && Attr(hero, 7) == 0, "Cannot move now");
                            Require(N(hero["chance"]!["chance_id"]) == 0 && ((JsonArray)hero["tmp_item"]!).Count == 0, "Finish the current chance first");
                            var target = Neighbors(c, hero).FirstOrDefault(n => n.X == x && n.Z == z);
                            Require(target != default, "Target is not a walkable neighbor");
                            var source = Grid(c, N(hero["pos"]!["x"]), N(hero["pos"]!["z"]));
                            var dest = Grid(c, x, z);
                            if (N(Hex(dest).GetProperty("updatable")) == 1) { hero["pos"] = Pos(x, z); hero["direction"] = target.Dir; SetAttr(hero, 8, 2); break; }
                            if (Attr(hero, 8) == 1) { Trigger(c, source, "thingStartMoveParam", 26); Trigger(c, source, "startMoveParam", 4); }
                            Trigger(c, source, "thingExitParam", 25); Trigger(c, source, "exitParam", 3);
                            hero["pos"] = Pos(x, z); hero["direction"] = target.Dir;
                            SetAttr(hero, 8, 2);
                            SetAttr(hero, 5, Attr(hero, 5) - 1);
                            HeroEncounter(c, dest);
                            Trigger(c, dest, "thingEnterParam", 19); Trigger(c, dest, "enterParam", 2);
                            if (Attr(hero, 5) <= 0) { SetAttr(hero, 8, 3); Trigger(c, dest, "thingEndMoveParam", 27); Trigger(c, dest, "endMoveParam", 5); }
                            BuffEffect(c, 0, 5);
                            break;
                        }
                    case 85014:
                        {
                            Require(Attr(hero, 8) == 3 && Attr(hero, 5) == 0, "Move not finished");
                            Require(N(hero["chance"]!["chance_id"]) == 0 && ((JsonArray)hero["tmp_item"]!).Count == 0, "Finish the current chance first");
                            BuffTurn(c, 0);
                            SetAttr(hero, 8, 0);
                            if (N(hero["use_item"]) is var used and not 0)
                            {
                                var prop = Prop(used);
                                if (N(prop.GetProperty("type")) == 10006) { var pp = prop.GetProperty("params"); SetAttr(hero, pp[0].GetInt32(), Attr(hero, pp[0].GetInt32()) - pp[1].GetInt64()); }
                                if (N(prop.GetProperty("type")) == 10004) active.Remove("prop_dice");
                                hero["use_item"] = 0;
                            }
                            var paths = RunTurn(c);
                            c.Map["turn"] = N(c.Map["turn"]) + 1;
                            foreach (var le in C("levels", c.Chapter).GetProperty("level_event").EnumerateArray())
                                if (le[0].GetInt64() == N(c.Map["turn"])) Pool(c, le[1].GetInt64(), 20, [le[0].GetInt64()]);
                            reply["npc_path_list"] = new JsonArray(paths.Select(p => (JsonNode)p).ToArray());
                            break;
                        }
                    case 85016 or 85018 or 85020 or 85022 or 85024 or 85036:
                        ChanceOp(c, cmd, r.Body);
                        break;
                    case 85026:
                        {
                            var tid = (long)HandlerHelpers.ReadUInt(r.Body, "task_id");
                            var tasks = (JsonArray)c.Map["task"]!;
                            var task = tasks.OfType<JsonObject>().FirstOrDefault(t => N(t["id"]) == tid);
                            Require(task is not null && N(task!["progress"]) >= N(C("tasks", tid).GetProperty("need")), "Board task not finished");
                            tasks.Remove(task);
                            var doneArr = (JsonArray?)active["tasks_done"] ?? new JsonArray();
                            if (!doneArr.Select(N).Contains(tid)) doneArr.Add(tid);
                            active["tasks_done"] = doneArr.DeepClone();
                            var ev = N(C("tasks", tid).GetProperty("finishevent"));
                            if (ev != 0) Pool(c, ev, 18, [tid]);
                            break;
                        }
                    case 85038:
                        {
                            var chance = (JsonObject)hero["chance"]!;
                            var cid = N(chance["chance_id"]);
                            Require(cid != 0 && N(C("chances", cid).GetProperty("type")) == 14 && N(chance["is_unlock"]) == 1, "Not an open arena");
                            var cl = (JsonArray)chance["choose_list"]!;
                            long remaining = N(cl[0]), total = N(cl[1]), pid = N(cl[2]);
                            Require(remaining > 0 && total < C("chances", cid).GetProperty("params")[0].GetInt64(), "Arena finished");
                            var sel = N(active["arena_prop_dice"]);
                            var dice = Enumerable.Range(0, pid == 10001 ? 2 : 1).Select(_ => sel != 0 ? sel : Random.Shared.Next(1, (int)Attr(hero, 1) + 1)).ToList();
                            remaining = remaining - 1 + dice.Count(d => d == 6);
                            total += dice.Sum();
                            chance["choose_list"] = new JsonArray(remaining, total, 0);
                            active.Remove("arena_prop_dice");
                            reply["dice_list"] = new JsonArray(dice.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray());
                            reply["total_cnt"] = remaining;
                            break;
                        }
                    case 85028:
                        frames.AddRange(Persist(s).Select(e => F(srv, e.Item1, e.Item2)));
                        s["active"] = null;
                        await SaveAsync(uid, s, ct);
                        frames.Add(F(srv, 85029, reply));
                        return frames;
                }
                frames.AddRange(c.Frames.Select(f => F(srv, f.Cmd, f.Body)));
                frames.AddRange(Persist(s).Select(e => F(srv, e.Item1, e.Item2)));
            }
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)]; }
        await SaveAsync(uid, s, ct);
        frames.Add(F(srv, cmd + 1, reply));
        return frames;
    }

    // ---------------- nested battles (stage types 70/72/73/74/75) ----------------

    public bool Reserved(uint type, long stage) =>
        type == 75 || (type == 74 && stage == 0) || (type == 70 && stage is 1 or 10)
        || (type is 72 or 73 && Cfg.GetProperty("nested").EnumerateObject().Any(p => N(p.Value.GetProperty("type")) == type && N(p.Value.GetProperty("dest")) == stage));

    public async Task<(List<GameResponse>, IReadOnlyList<(long, long)>)> SettleAsync(ModeBattle b, CancellationToken ct)
    {
        var s = await LoadAsync(b.Uid, ct);
        if (s["active"] is JsonObject active && active["map"] is JsonObject map && map["nested_token"] is not null)
        {
            map["nested_bid"] = (long)b.BattleId;
            map["nested_result"] = new JsonObject { ["bid"] = (long)b.BattleId, ["win"] = b.Win };
            await SaveAsync(b.Uid, s, ct);
        }
        return ([], []);
    }

    Task<IReadOnlyList<(uint Cmd, byte[] Payload)>> IStageMode.LoginFramesAsync(ProtoCodec c, long uid, CancellationToken ct) => LoginFramesAsync(uid, ct);
}
