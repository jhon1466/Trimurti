namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>Marks a hero as favorite (14106 -> 14107).</summary>
public sealed class HeroAddFavoriteHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 14106;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var heroId = HandlerHelpers.ReadUInt(request.Body, "hero_id");
        var ok = await accounts.SetHeroFavoriteAsync(request.AccountId, heroId, isFavorite: true, cancellationToken);
        return [HandlerHelpers.ResultOnly(codec, request, 14107, ok ? 0u : 2u)];
    }
}

/// <summary>Unmarks a hero as favorite (14108 -> 14109).</summary>
public sealed class HeroRemoveFavoriteHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 14108;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var heroId = HandlerHelpers.ReadUInt(request.Body, "hero_id");
        var ok = await accounts.SetHeroFavoriteAsync(request.AccountId, heroId, isFavorite: false, cancellationToken);
        return [HandlerHelpers.ResultOnly(codec, request, 14109, ok ? 0u : 2u)];
    }
}

