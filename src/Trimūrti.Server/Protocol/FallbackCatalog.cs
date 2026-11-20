using System.Text.Json;

namespace Trimūrti.Server.Protocol;

public sealed class FallbackCatalog(ProtoCodec codec, SchemaDefaults defaults)
{
    private readonly Dictionary<uint, JsonElement> _templates = LoadTemplates(Path.Combine(AppContext.BaseDirectory, "Data", "total_response_templates.json"));
    private readonly Dictionary<uint, JsonElement> _skeleton = LoadSkeleton(Path.Combine(AppContext.BaseDirectory, "Data", "skeleton.json"));

    public int TemplateCount => _templates.Count;
    public int SkeletonCount => _skeleton.Count;

    public IReadOnlyList<GameResponse>? Resolve(GameFrame request)
    {
        _templates.TryGetValue(request.Command, out var templateEntry);
        _skeleton.TryGetValue(request.Command, out var skeletonEntry);

        if (HasReplyNo(templateEntry) || HasReplyNo(skeletonEntry)) return Array.Empty<GameResponse>();

        var frames = new List<GameResponse>();
        if (skeletonEntry.ValueKind == JsonValueKind.Object && skeletonEntry.TryGetProperty("frames", out var frameList) && frameList.ValueKind == JsonValueKind.Array)
        {
            var nextServer = request.ServerIndex;
            foreach (var item in frameList.EnumerateArray())
            {
                if (!item.TryGetProperty("sc", out var scElement) || !scElement.TryGetUInt32(out var sc)) continue;
                var payload = TryHex(item, out var raw) ? raw : EncodeTemplate(templateEntry, sc);
                var srv = item.TryGetProperty("srv", out var srvElement) && srvElement.TryGetUInt16(out var specified)
                    ? specified : ++nextServer;
                frames.Add(new GameResponse(sc, request.Index, srv, payload));
            }
            return frames;
        }

        if (templateEntry.ValueKind == JsonValueKind.Object && templateEntry.TryGetProperty("sc", out var responseElement) && responseElement.TryGetUInt32(out var responseCommand))
            return [new GameResponse(responseCommand, request.Index, (ushort)(request.ServerIndex + 1), EncodeTemplate(templateEntry, responseCommand))];

        // No captured template: answer sc_{cmd+1} with a schema-complete default (result = 0) so the client never hangs.
        var reply = request.Command + 1;
        if (defaults.Has($"sc_{reply}"))
            return [new GameResponse(reply, request.Index, (ushort)(request.ServerIndex + 1), codec.Encode($"sc_{reply}", defaults.Build($"sc_{reply}")))];
        return null;
    }

    private byte[] EncodeTemplate(JsonElement entry, uint command)
    {
        var payload = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("template", out var value) && value.ValueKind == JsonValueKind.Object
            ? ToClr(value) as Dictionary<string, object?> ?? []
            : [];
        return codec.Encode($"sc_{command}", defaults.Complete($"sc_{command}", payload));
    }

    private static bool HasReplyNo(JsonElement element) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty("reply", out var reply) && reply.ValueKind == JsonValueKind.String && reply.GetString() == "no";

    private static bool TryHex(JsonElement item, out byte[] bytes)
    {
        bytes = [];
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("hex", out var hex) || hex.ValueKind != JsonValueKind.String) return false;
        try { bytes = Convert.FromHexString(hex.GetString() ?? ""); return true; }
        catch (FormatException) { return false; }
    }

    private static object? ToClr(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => ToClr(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Select(ToClr).ToArray(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var number) => number,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static Dictionary<uint, JsonElement> LoadTemplates(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement.GetProperty("cs_sc");
        return root.EnumerateObject().Where(p => uint.TryParse(p.Name, out _)).ToDictionary(p => uint.Parse(p.Name), p => p.Value.Clone());
    }

    private static Dictionary<uint, JsonElement> LoadSkeleton(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement.GetProperty("cs_sc");
        return root.EnumerateObject().Where(p => uint.TryParse(p.Name, out _)).ToDictionary(p => uint.Parse(p.Name), p => p.Value.Clone());
    }
}
