namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>Persists story plot progress (12002 -> 12003).</summary>
public sealed class StoryProgressHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 12002;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var storyId = (int)HandlerHelpers.ReadUInt(request.Body, "story_id");
        var ok = await accounts.UpdatePlotProgressAsync(request.AccountId, storyId, cancellationToken);
        return [HandlerHelpers.ResultOnly(codec, request, 12003, ok ? 0u : 2u)];
    }
}

/// <summary>Persists main tutorial guide completion (12012 -> 12013).</summary>
public sealed class MainGuideHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 12012;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var guideId = (int)HandlerHelpers.ReadUInt(request.Body, "mod_guide_id");
        var ok = await accounts.CompleteGuideAsync(request.AccountId, guideId, "main", cancellationToken);
        return [HandlerHelpers.ResultOnly(codec, request, 12013, ok ? 0u : 2u)];
    }
}

/// <summary>Persists weak tutorial guide completion (12112 -> 12113).</summary>
public sealed class WeakGuideHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 12112;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var guideId = (int)HandlerHelpers.ReadUInt(request.Body, "mod_guide_id");
        var ok = await accounts.CompleteGuideAsync(request.AccountId, guideId, "weak", cancellationToken);
        return [HandlerHelpers.ResultOnly(codec, request, 12113, ok ? 0u : 2u)];
    }
}

/// <summary>Persists player nickname changes (23012 -> 23013).</summary>
public sealed class NicknameChangeHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 23012;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var nick = HandlerHelpers.ReadString(request.Body, "nick");
        var update = await accounts.UpdateNicknameAsync(request.AccountId, nick, cancellationToken);
        return [
            HandlerHelpers.Reply(codec, request, 23013, new Dictionary<string, object?>
            {
                ["result"] = update.Success ? 0u : 2u,
                ["cost_item_list"] = Array.Empty<object>(),
                ["is_changed_nick"] = (uint)update.IsChangedNick,
                ["system_change_nick_times"] = (uint)update.ChangeNickTimes
            })
        ];
    }
}
