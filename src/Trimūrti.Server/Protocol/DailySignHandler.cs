namespace Trimūrti.Server.Protocol;

/// <summary>Ports the source's daily activity 3 claim (cs_11010 → sc_11011).</summary>
public sealed class DailySignHandler(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 11010;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var activityId = ReadUInt(request.Body, "activity_id");
        if (activityId is 0 or 3 or 10001)
        {
            var daily = await accounts.ClaimDailySignAsync(request.AccountId, cancellationToken);
            return [Reply(request, 11011, ToPayload(daily))];
        }
        if (activityId < 0 || activityId > int.MaxValue)
            return [Reply(request, 11011, new Dictionary<string, object?> { ["result"] = 2, ["item_list"] = Array.Empty<object>() })];

        var result = await accounts.ClaimCumulativeSignAsync(request.AccountId, (int)activityId, config, cancellationToken);
        return [Reply(request, 11011, ToPayload(result))];
    }

    private static IReadOnlyDictionary<string, object?> ToPayload(DailySignResult result)
    {
        var items = result.Rewards is { Count: > 0 } rewards
            ? rewards.Select(x => (object)new Dictionary<string, object?> { ["id"] = x.ItemId, ["num"] = x.Count }).ToArray()
            : result.ItemId > 0
                ? new object[] { new Dictionary<string, object?> { ["id"] = result.ItemId, ["num"] = result.ItemCount } }
                : Array.Empty<object>();
        return new Dictionary<string, object?>
        {
            ["result"] = result.Success ? 0 : 2,
            ["item_list"] = items
        };
    }

    private GameResponse Reply(GameRequest request, uint command, IReadOnlyDictionary<string, object?> payload) =>
        new(command, request.Index, request.ServerIndex, codec.Encode($"sc_{command}", payload));

    private static uint ReadUInt(IReadOnlyDictionary<string, object?> body, string name)
    {
        if (!body.TryGetValue(name, out var value) || value is null) return 0;
        try { return Convert.ToUInt32(value); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return 0; }
    }
}
