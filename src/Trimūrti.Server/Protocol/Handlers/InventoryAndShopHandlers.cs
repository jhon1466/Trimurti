namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>Exchanges currency/items (15022 -> 15023).</summary>
public sealed class CurrencyExchangeHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 15022;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var exchangeId = HandlerHelpers.ReadUInt(request.Body, "exchange_id");
        var num = Math.Max(1u, HandlerHelpers.ReadUInt(request.Body, "num", 1));
        if (exchangeId == 0) return [HandlerHelpers.ResultOnly(codec, request, 15023, 2)];

        var grants = new (long, long)[] { (2, 1000L * num) };
        var ok = await accounts.ConsumeAndGrantAsync(request.AccountId, [(1, 10L * num)], grants, cancellationToken);
        return [
            HandlerHelpers.Reply(codec, request, 15023, new Dictionary<string, object?>
            {
                ["result"] = ok ? 0u : 2u,
                ["reward_list"] = ok ? HandlerHelpers.ToItemNetRecList(grants) : Array.Empty<object>()
            })
        ];
    }
}

/// <summary>Uses consumable items from inventory (17012 -> 17013).</summary>
public sealed class UseItemHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 17012;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var useItems = HandlerHelpers.ReadItemList(request.Body, "use_item_list");
        if (useItems.Count == 0) return [HandlerHelpers.ResultOnly(codec, request, 17013, 2)];

        var grants = useItems.Select(x => x.ItemId == 30001 ? (4L, 60L * x.Count) : (2L, 500L * x.Count)).ToArray();
        var ok = await accounts.ConsumeAndGrantAsync(request.AccountId, useItems, grants, cancellationToken);
        return [
            HandlerHelpers.Reply(codec, request, 17013, new Dictionary<string, object?>
            {
                ["result"] = ok ? 0u : 2u,
                ["drop_list"] = ok ? HandlerHelpers.ToItemNetRecList(grants) : Array.Empty<object>()
            })
        ];
    }
}

/// <summary>Monthly card daily claim (34024 -> 34025). Ported from Alpha-Persei MonthCardSignOp.</summary>
public sealed class MonthCardClaimHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 34024;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var granted = await accounts.ClaimMonthCardDailyAsync(request.AccountId, cancellationToken);
        var rewards = granted
            ? new object[] { new Dictionary<string, object?> { ["item_id"] = 1u, ["item_num"] = 90u } }
            : Array.Empty<object>();
        await accounts.TryClaimTodayAsync(request.AccountId, "monthcard_day", cancellationToken);
        var frames = await InventorySync.FramesAsync(accounts, codec, request, [1], cancellationToken);
        // Alpha-Persei MonthCardSignOp always sends 59009 before 34025 (the client's newbie monthly-card state).
        frames.Add(CardState.Push(request, 59009, CardState.NewbieRecharge(codec)));
        frames.Add(HandlerHelpers.Reply(codec, request, 34025, new Dictionary<string, object?>
        {
            ["result"] = 0u, ["is_sign"] = 1u, ["reward_list"] = rewards
        }));
        return frames;
    }
}
