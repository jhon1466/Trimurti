namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>Claims a single task reward (28010 -> 28011).</summary>
public sealed class TaskClaimHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 28010;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var taskId = HandlerHelpers.ReadUInt(request.Body, "id");
        if (taskId == 0) return [HandlerHelpers.ResultOnly(codec, request, 28011, 2)];
        var rewards = await accounts.ClaimTasksAsync(request.AccountId, [taskId], _ => [(1, 20), (2, 1000)], cancellationToken);
        return [
            HandlerHelpers.Reply(codec, request, 28011, new Dictionary<string, object?>
            {
                ["result"] = rewards.Count > 0 ? 0u : 2u,
                ["reward_list"] = HandlerHelpers.ToItemNetRecList(rewards)
            })
        ];
    }
}

/// <summary>Claims multiple task rewards in batch (28014 -> 28015).</summary>
public sealed class TaskBatchClaimHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 28014;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var ids = HandlerHelpers.ReadIdList(request.Body, "id_list");
        if (ids.Count == 0) return [HandlerHelpers.ResultOnly(codec, request, 28015, 2)];
        var rewards = await accounts.ClaimTasksAsync(request.AccountId, ids, _ => [(1, 20), (2, 1000)], cancellationToken);
        return [
            HandlerHelpers.Reply(codec, request, 28015, new Dictionary<string, object?>
            {
                ["result"] = rewards.Count > 0 ? 0u : 2u,
                ["reward_list"] = HandlerHelpers.ToItemNetRecList(rewards)
            })
        ];
    }
}

