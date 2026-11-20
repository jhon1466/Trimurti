using System.Text.Json;
using Trimūrti.Server.Protocol;

namespace Trimūrti.Server;

/// <summary>
/// Login frames the capture used to provide and that Trimūrti now builds itself:
/// operator settings (Data/Configs/server_settings.json) and the initial state of systems/activities whose
/// services hold no per-player state yet (complete default messages: the player's real, untouched state).
/// </summary>
public static class LoginDynamicFrames
{
    /// <summary>Systems without stored state yet: their real state for every player is the initial one.</summary>
    private static readonly uint[] InitialState = [];

    /// <summary>
    /// Per-activity frames (activity_id/template_id required) of activities that are not open: the client indexes
    /// ActivityCfg[activity_id] on receipt, so a default (0) crashes it and the capture's ids belong to another
    /// account's old seasons. They are not sent at login; each mode sends its own when its activity runs.
    /// </summary>
    public static readonly uint[] ActivityBound = [60201, 64031, 68151, 68161, 68185, 76151, 83401, 86037, 11035, 11097];

    public static IEnumerable<(uint Command, byte[] Payload)> Build(ProtoCodec codec, SchemaDefaults defaults, ISet<uint> already)
    {
        var s = Settings();
        var settings = new (uint Cmd, Dictionary<string, object?> Body)[]
        {
            (21001, new() { ["setting"] = Obj(s, "client_setting") }),
            (40021, new() { ["id_list"] = Ids(s, "open_system_ids") }),
            (40023, new() { ["state"] = 0 }),
            (40031, new() { ["id_list"] = Ids(s, "closed_system_ids") }),
            (40033, new() { ["media_list"] = Ids(s, "media_list") }),
            (40035, new() { ["sys_supend_list"] = s.TryGetProperty("suspended_systems", out var ss)
                ? ss.EnumerateArray().Select(x => (object?)new Dictionary<string, object?> { ["type"] = x.GetProperty("type").GetUInt32(), ["id_list"] = Ids(x, "id_list") }).ToArray()
                : Array.Empty<object?>() }),
            // 12019 gift limit, 12099 hidden content (HideInfoData), 20007 server goods overrides, 31207 community welfare type.
            (12019, new() { ["gift_limit"] = s.TryGetProperty("gift_limit", out var gl) && gl.ValueKind == JsonValueKind.True }),
            (12099, new() { ["hide_list"] = s.TryGetProperty("hide_list", out var hl) && hl.ValueKind == JsonValueKind.Array
                ? hl.EnumerateArray().Select(x => (object?)new Dictionary<string, object?> { ["type"] = x.GetProperty("type").GetUInt32(), ["id_list"] = Ids(x, "id_list") }).ToArray()
                : Array.Empty<object?>() }),
            (20007, new() { ["shop_item_cfg_list"] = s.TryGetProperty("shop_item_cfg_list", out var sc) && sc.ValueKind == JsonValueKind.Array
                ? sc.EnumerateArray().Select(x => (object?)JsonSerializer.Deserialize<Dictionary<string, object?>>(x.GetRawText())).ToArray()
                : Array.Empty<object?>() }),
            (31207, new() { ["state"] = s.TryGetProperty("community_welfare_state", out var cw) ? cw.GetUInt32() : 0u }),
            (30099, new() { ["announcement_list"] = s.TryGetProperty("announcements", out var an)
                ? an.EnumerateArray().Select(x => (object?)JsonSerializer.Deserialize<Dictionary<string, object?>>(x.GetRawText())).ToArray()
                : Array.Empty<object?>() }),
        };
        foreach (var (cmd, body) in settings)
            if (!already.Contains(cmd)) yield return (cmd, codec.Encode($"sc_{cmd}", defaults.Complete($"sc_{cmd}", body)));
        foreach (var cmd in InitialState)
            if (!already.Contains(cmd) && defaults.Has($"sc_{cmd}")) yield return (cmd, codec.Encode($"sc_{cmd}", defaults.Build($"sc_{cmd}")));
    }

    private static JsonElement Settings() =>
        new SourceGameConfig().Get("server_settings.json") is { ValueKind: JsonValueKind.Object } e ? e : JsonDocument.Parse("{}").RootElement;

    private static object?[] Ids(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => (object?)x.GetUInt32()).ToArray() : [];

    private static Dictionary<string, object?> Obj(JsonElement e, string name) =>
        e.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Object
            ? o.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.GetUInt32()) : new();
}
