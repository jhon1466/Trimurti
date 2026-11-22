using Trimūrti.Server.Protocol.Handlers;

namespace Trimūrti.Server.Protocol;

/// <summary>Ports the source's daily activity 3 claim (cs_11010 → sc_11011) with live inventory sync.</summary>
public sealed class DailySignHandler(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 11010;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var activityId = ReadUInt(request.Body, "activity_id");
        if (activityId is 0 or 3 or 10001)
        {
            var daily = await accounts.ClaimDailySignAsync(request.AccountId, cancellationToken);
            return await ReplyWithSyncAsync(request, daily, cancellationToken);
        }
        if (activityId < 0 || activityId > int.MaxValue)
            return [Reply(request, 11011, new Dictionary<string, object?> { ["result"] = 2, ["item_list"] = Array.Empty<object>() })];

        var result = await accounts.ClaimCumulativeSignAsync(request.AccountId, (int)activityId, config, cancellationToken);
        return await ReplyWithSyncAsync(request, result, cancellationToken);
    }

    private async Task<IReadOnlyList<GameResponse>> ReplyWithSyncAsync(GameRequest request, DailySignResult result, CancellationToken ct)
    {
        var touched = result.Rewards is { Count: > 0 } rewards
            ? rewards.Select(x => x.ItemId)
            : result.ItemId > 0 ? [result.ItemId] : Array.Empty<long>();
        var frames = result.Success && !result.AlreadySigned
            ? await InventorySync.FramesAsync(accounts, codec, request, touched, ct)
            : [];
        frames.Add(Reply(request, 11011, ToPayload(result)));
        return frames;
    }

    private static IReadOnlyDictionary<string, object?> ToPayload(DailySignResult result)
    {
        var items = result.Rewards is { Count: > 0 } rewards
            ? HandlerHelpers.ToItemNetRecList(rewards)
            : result.ItemId > 0
                ? HandlerHelpers.ToItemNetRecList([(result.ItemId, result.ItemCount)])
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
