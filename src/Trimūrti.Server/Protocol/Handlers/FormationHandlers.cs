using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>Saves a team formation preset (63000 -> 63001).</summary>
public sealed class FormationSaveHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 63000;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var teamType = (int)HandlerHelpers.ReadUInt(request.Body, "team_type", 1);
        var contId = 1;
        if (request.Body.TryGetValue("cont_team", out var raw) && raw is IReadOnlyDictionary<string, object?> dict)
            contId = (int)HandlerHelpers.ReadUInt(dict, "cont_id", 1);

        var ok = await accounts.SaveFormationAsync(request.AccountId, teamType, contId, JsonSerializer.Serialize(request.Body), cancellationToken);
        return [HandlerHelpers.ResultOnly(codec, request, 63001, ok ? 0u : 2u)];
    }
}

/// <summary>Deletes a team formation preset (63002 -> 63003).</summary>
public sealed class FormationDeleteHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 63002;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var teamType = (int)HandlerHelpers.ReadUInt(request.Body, "team_type", 1);
        var contId = (int)HandlerHelpers.ReadUInt(request.Body, "cont_id", 1);
        var ok = await accounts.DeleteFormationAsync(request.AccountId, teamType, contId, cancellationToken);
        return [HandlerHelpers.ResultOnly(codec, request, 63003, ok ? 0u : 2u)];
    }
}
