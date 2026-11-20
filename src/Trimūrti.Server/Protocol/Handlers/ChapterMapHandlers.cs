using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 章节地图 V2 (ChapterV2Map*): per-player location, completed events and backtracks.
/// 24061 state at login, 24062 finish event, 24064 move, 24066 backtrack to an event, 24068 track map.
/// Config: chapter_map_cfg.json (client ChapterV2MapCfg/LocationCfg/EventCfg).
/// </summary>
public sealed class ChapterMap(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    public sealed class Backtrack { public uint Map { get; set; } public uint Start { get; set; } public List<uint> Events { get; set; } = []; }
    public sealed class State { public ulong Location { get; set; } public List<uint> Events { get; set; } = []; public List<Backtrack> Backtracks { get; set; } = []; public uint Tracking { get; set; } }

    public static readonly uint[] Commands = [24062, 24064, 24066, 24068];
    private const string Kind = "chapter_map_v1";

    private JsonElement Cfg => config.Get("chapter_map_cfg.json") ?? default;

    private ulong DefaultLocation() =>
        Cfg.ValueKind == JsonValueKind.Object ? Cfg.GetProperty("maps").EnumerateObject().OrderBy(m => uint.Parse(m.Name)).Select(m => m.Value.GetProperty("default_location").GetUInt64()).FirstOrDefault() : 0;

    private uint MapOf(ulong location) =>
        Cfg.ValueKind == JsonValueKind.Object && Cfg.GetProperty("locations").TryGetProperty(location.ToString(), out var m) ? m.GetUInt32() : 0;

    private JsonElement? Event(uint id) =>
        Cfg.ValueKind == JsonValueKind.Object && Cfg.GetProperty("events").TryGetProperty(id.ToString(), out var e) ? e : null;

    private async Task<State> LoadAsync(long uid, CancellationToken ct)
    {
        var s = await accounts.GetJsonAsync<State>(uid, Kind, ct) ?? new State();
        if (s.Location == 0) s.Location = DefaultLocation();
        return s;
    }

    public async Task<byte[]> LoginFrameAsync(long uid, CancellationToken ct) => Frame(await LoadAsync(uid, ct));

    private byte[] Frame(State s) => codec.Encode("sc_24061", new Dictionary<string, object?>
    {
        ["now_location"] = s.Location,
        ["event_list"] = s.Events.Cast<object?>().ToArray(),
        ["backtrack_list"] = s.Backtracks.Select(b => (object?)new Dictionary<string, object?>
        {
            ["map_id"] = b.Map, ["start_event"] = b.Start, ["backtrack_event_list"] = b.Events.Cast<object?>().ToArray()
        }).ToArray()
    });

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var s = await LoadAsync(r.AccountId, ct);
        uint result = 0;
        switch (cmd)
        {
            case 24062:
            {
                var id = Convert.ToUInt32(r.Body["event_id"]);
                if (Event(id) is null) result = 1;
                else if (!s.Events.Contains(id)) s.Events.Add(id);
                break;
            }
            case 24064:
            {
                var loc = Convert.ToUInt64(r.Body["new_location"]);
                if (MapOf(loc) == 0) result = 1; else s.Location = loc;
                break;
            }
            case 24066:
            {
                // Replay from an event: it and the later finished events of its map become a backtrack.
                var id = Convert.ToUInt32(r.Body["event_id"]);
                if (Event(id) is not { } e || !s.Events.Contains(id)) { result = 1; break; }
                var map = MapOf(e.GetProperty("location").GetUInt64());
                var from = e.GetProperty("time_stamp").GetInt64();
                var later = s.Events.Where(x => Event(x) is { } o && MapOf(o.GetProperty("location").GetUInt64()) == map && o.GetProperty("time_stamp").GetInt64() >= from).ToList();
                s.Backtracks.RemoveAll(b => b.Map == map);
                s.Backtracks.Add(new Backtrack { Map = map, Start = id, Events = later });
                s.Events.RemoveAll(later.Contains);
                break;
            }
            case 24068:
                s.Tracking = Convert.ToUInt32(r.Body["map_id"]);
                break;
        }
        if (result == 0) await accounts.SetJsonAsync(r.AccountId, Kind, s, ct);
        var frames = new List<GameResponse>();
        if (result == 0 && cmd is 24062 or 24066) frames.Add(new GameResponse(24061, 0, (ushort)(r.ServerIndex + 1), Frame(s)));
        frames.Add(HandlerHelpers.Reply(codec, r, cmd + 1, new Dictionary<string, object?> { ["result"] = result }));
        return frames;
    }
}
