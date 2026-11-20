namespace Trimūrti.Server.Protocol.Handlers;

internal static class HandlerHelpers
{
    public static GameResponse Reply(ProtoCodec codec, GameRequest request, uint responseCommand, IReadOnlyDictionary<string, object?> payload) =>
        new(responseCommand, request.Index, (ushort)(request.ServerIndex + 1), codec.Encode($"sc_{responseCommand}", payload));

    public static GameResponse ResultOnly(ProtoCodec codec, GameRequest request, uint responseCommand, uint result = 0) =>
        Reply(codec, request, responseCommand, new Dictionary<string, object?> { ["result"] = result });

    public static uint ReadUInt(IReadOnlyDictionary<string, object?> body, string name, uint fallback = 0)
    {
        if (!body.TryGetValue(name, out var value) || value is null) return fallback;
        try { return Convert.ToUInt32(value); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return fallback; }
    }

    public static ulong ReadULong(IReadOnlyDictionary<string, object?> body, string name, ulong fallback = 0)
    {
        if (!body.TryGetValue(name, out var value) || value is null) return fallback;
        try { return Convert.ToUInt64(value); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return fallback; }
    }

    public static string ReadString(IReadOnlyDictionary<string, object?> body, string name, string fallback = "") =>
        body.TryGetValue(name, out var value) && value is not null ? Convert.ToString(value) ?? fallback : fallback;

    public static IReadOnlyList<(long ItemId, long Count)> ReadItemList(IReadOnlyDictionary<string, object?> body, string fieldName)
    {
        if (!body.TryGetValue(fieldName, out var raw) || raw is not System.Collections.IEnumerable seq) return [];
        var list = new List<(long, long)>();
        foreach (var entry in seq)
        {
            if (entry is not IReadOnlyDictionary<string, object?> dict) continue;
            var id = dict.TryGetValue("item_id", out var iv) || dict.TryGetValue("id", out iv) ? ToLong(iv) : 0;
            var num = dict.TryGetValue("item_num", out var nv) || dict.TryGetValue("num", out nv) ? ToLong(nv) : 0;
            if (id > 0 && num > 0) list.Add((id, num));
        }
        return list;
    }

    public static IReadOnlyList<long> ReadIdList(IReadOnlyDictionary<string, object?> body, string fieldName)
    {
        if (!body.TryGetValue(fieldName, out var raw) || raw is not System.Collections.IEnumerable seq || raw is string) return [];
        var list = new List<long>();
        foreach (var entry in seq)
        {
            var val = ToLong(entry);
            if (val > 0) list.Add(val);
        }
        return list;
    }

    public static object[] ToItemNetRecList(IEnumerable<(long ItemId, long Count)> items) =>
        items.Select(x => (object)new Dictionary<string, object?>
        {
            ["item_id"] = (uint)x.ItemId,
            ["item_num"] = (uint)x.Count
        }).ToArray();

    private static long ToLong(object? value)
    {
        if (value is null) return 0;
        try { return Convert.ToInt64(value); }
        catch { return 0; }
    }
}
