namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>Inventory refresh pushes sent after a grant or cost, plus item_net_rec reward lists.</summary>
internal static class InventorySync
{
    private static readonly SourceGameConfig Config = new();

    /// <summary>
    /// One 17023 (ItemAction.ModifyItemList) with the new totals of the touched items: the client sets each item by its
    /// ItemCfg type (currency, material, gift, props…) and leaves the rest alone. 17009 is the full material list
    /// (MaterialData.InitMaterialList wipes the bag first), so it is only valid at login: using it for a diff left the bag
    /// with just the touched items.
    /// </summary>
    public static async Task<List<GameResponse>> FramesAsync(GameAccountRepository accounts, ProtoCodec codec, GameRequest request,
        IEnumerable<long> itemIds, CancellationToken ct)
    {
        var ids = itemIds.Where(x => x > 0).ToHashSet();
        var frames = new List<GameResponse>();
        if (ids.Count == 0) return frames;
        var inv = (await accounts.GetInventoryAsync(request.AccountId, ct))
            .Where(x => ids.Contains(x.Id)).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
        // Items spent down to 0 may have no row any more: they are sent as 0 so the client clears them. Entities (heroes,
        // sigils, servants…) are not stackable items and have their own pushes.
        var items = ids.Where(id => inv.ContainsKey(id) || SourceGameConfig.ValidCurrencyIds.Contains(id) || Config.ItemTypes.ContainsKey(id))
            .Order().Select(id => (object)new Dictionary<string, object?>
        {
            ["id"] = (uint)id, ["num"] = (uint)Math.Clamp(inv.GetValueOrDefault(id), 0, uint.MaxValue), ["time_valid"] = 0u
        }).ToArray();
        if (items.Length == 0) return frames;
        ushort srv = (ushort)(request.ServerIndex + 1);
        frames.Add(new GameResponse(17023, 0, srv, codec.Encode("sc_17023", new Dictionary<string, object?> { ["normal_items"] = items })));
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
