namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>Builds the currency (15009) / material (17009) refresh pushes sent after a grant or cost,
/// mirroring Alpha-Persei's _refresh_frames, plus item_net_rec reward lists.</summary>
internal static class InventorySync
{
    public static async Task<List<GameResponse>> FramesAsync(GameAccountRepository accounts, ProtoCodec codec, GameRequest request,
        IEnumerable<long> itemIds, CancellationToken ct)
    {
        var ids = itemIds.ToHashSet();
        var frames = new List<GameResponse>();
        if (ids.Count == 0) return frames;
        var inv = await accounts.GetInventoryAsync(request.AccountId, ct);
        object Rec(ItemStack x) => new Dictionary<string, object?> { ["id"] = x.Id, ["num"] = x.Count, ["time_valid"] = 0u };
        var cur = inv.Where(x => x.Source == "currency" && ids.Contains(x.Id)).Select(Rec).ToArray();
        var mat = inv.Where(x => x.Source == "material" && ids.Contains(x.Id)).Select(Rec).ToArray();
        ushort srv = (ushort)(request.ServerIndex + 1);
        if (cur.Length > 0)
            frames.Add(new GameResponse(15009, 0, srv, codec.Encode("sc_15009", new Dictionary<string, object?> { ["currency_list"] = cur, ["last_fatigue_recover_time"] = 0 })));
        if (mat.Length > 0)
            frames.Add(new GameResponse(17009, 0, srv, codec.Encode("sc_17009", new Dictionary<string, object?> { ["material_list"] = mat })));
        return frames;
    }

    public static object[] Rewards(IEnumerable<(long ItemId, long Count)> items) =>
        items.Select(x => (object)new Dictionary<string, object?> { ["item_id"] = (uint)x.ItemId, ["item_num"] = (uint)x.Count }).ToArray();

    /// <summary>Reply + preceding inventory refresh pushes.</summary>
    public static async Task<IReadOnlyList<GameResponse>> ReplyWithSyncAsync(GameAccountRepository accounts, ProtoCodec codec, GameRequest request,
        uint sc, Dictionary<string, object?> payload, IEnumerable<long> touched, CancellationToken ct)
    {
        var frames = await FramesAsync(accounts, codec, request, touched, ct);
        frames.Add(HandlerHelpers.Reply(codec, request, sc, payload));
        return frames;
    }

    public static IReadOnlyList<(long, long)> Pairs(System.Text.Json.JsonElement arr) =>
        arr.ValueKind != System.Text.Json.JsonValueKind.Array ? [] :
        arr.EnumerateArray().Where(p => p.ValueKind == System.Text.Json.JsonValueKind.Array && p.GetArrayLength() >= 2)
            .Select(p => (p[0].GetInt64(), p[1].GetInt64())).ToArray();
}
