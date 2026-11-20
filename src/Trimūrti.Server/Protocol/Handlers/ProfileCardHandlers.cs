namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>Updates player profile signature (32012 -> 32013).</summary>
public sealed class ProfileSignatureHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 32012;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var sign = HandlerHelpers.ReadString(request.Body, "sign");
        var ok = await accounts.UpdateSignatureAsync(request.AccountId, sign, cancellationToken);
        return [HandlerHelpers.ResultOnly(codec, request, 32013, ok ? 0u : 2u)];
    }
}

/// <summary>Updates showcase heroes on player card (32014 -> 32015).</summary>
public sealed class ProfileShowcaseHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 32014;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var heroes = HandlerHelpers.ReadIdList(request.Body, "heroes");
        var ok = await accounts.UpdateShowcaseHeroesAsync(request.AccountId, heroes, cancellationToken);
        return [HandlerHelpers.ResultOnly(codec, request, 32015, ok ? 0u : 2u)];
    }
}

/// <summary>Updates lobby board hero / poster girl (32016 -> 32017).</summary>
public sealed class ProfileBoardHeroHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 32016;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var heroId = (int)HandlerHelpers.ReadUInt(request.Body, "poster_girl");
        var ok = await accounts.UpdateBoardHeroAsync(request.AccountId, heroId, cancellationToken);
        if (ok) await OathSystem.RecordAssistantAsync(accounts, request.AccountId, AssistantHero.Of(heroId), cancellationToken);
        return [HandlerHelpers.ResultOnly(codec, request, 32017, ok ? 0u : 2u)];
    }
}

/// <summary>Returns a player's profile card (32018 -> 32019).</summary>
public sealed class ProfileCardQueryHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 32018;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var targetUid = (long)HandlerHelpers.ReadULong(request.Body, "user_id", (ulong)request.AccountId);
        var card = await accounts.GetPlayerCardAsync(targetUid, cancellationToken);
        if (card is null) return [HandlerHelpers.ResultOnly(codec, request, 32019, 2)];

        return [
            HandlerHelpers.Reply(codec, request, 32019, new Dictionary<string, object?>
            {
                ["result"] = 0u,
                ["user_id"] = (ulong)card.Uid,
                ["base_info"] = new Dictionary<string, object?>
                {
                    ["nick"] = card.Nickname,
                    ["icon"] = (uint)card.Portrait,
                    ["icon_frame"] = (uint)card.IconFrame
                },
                ["sign"] = card.Sign,
                ["level"] = (uint)card.Level,
                ["is_online"] = 1u,
                ["likes"] = (uint)card.Likes,
                ["hero_id_list"] = card.ShowHeroes.Select(x => (uint)x).ToArray()
            })
        ];
    }
}

/// <summary>Updates player avatar icon (32032 -> 32033).</summary>
public sealed class ProfilePortraitHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 32032;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var iconId = (int)HandlerHelpers.ReadUInt(request.Body, "icon_id");
        var ok = await accounts.UpdatePortraitAsync(request.AccountId, iconId, isFrame: false, cancellationToken);
        return [HandlerHelpers.ResultOnly(codec, request, 32033, ok ? 0u : 2u)];
    }
}

/// <summary>Updates player avatar frame (32034 -> 32035).</summary>
public sealed class ProfileIconFrameHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 32034;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var frameId = (int)HandlerHelpers.ReadUInt(request.Body, "iconframe_id");
        var ok = await accounts.UpdatePortraitAsync(request.AccountId, frameId, isFrame: true, cancellationToken);
        return [HandlerHelpers.ResultOnly(codec, request, 32035, ok ? 0u : 2u)];
    }
}

/// <summary>Updates lobby home scene / poster background (32108 -> 32109).</summary>
public sealed class ProfileHomeSceneHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 32108;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var sceneId = (int)HandlerHelpers.ReadUInt(request.Body, "poster_background_id");
        if (sceneId > 0)
            await accounts.UpdateHomeSceneAsync(request.AccountId, sceneId, cancellationToken);
        return [HandlerHelpers.ResultOnly(codec, request, 32109, 0u)];
    }
}

/// <summary>Updates lobby random hero or scene list (32130 -> 32131).</summary>
public sealed class ProfileRandomListHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 32130;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var type = HandlerHelpers.ReadUInt(request.Body, "type");
        var list = HandlerHelpers.ReadIdList(request.Body, "random_list");
        if (list.Count > 0 && list[0] > 0)
        {
            if (type == 1)
                await accounts.UpdateBoardHeroAsync(request.AccountId, (int)list[0], cancellationToken);
            else if (type == 2)
                await accounts.UpdateHomeSceneAsync(request.AccountId, (int)list[0], cancellationToken);
        }
        return [HandlerHelpers.ResultOnly(codec, request, 32131, 0u)];
    }
}

/// <summary>Persists active lobby background and hero reported by client (32132, no response).</summary>
public sealed class ProfileLobbySyncHandler(GameAccountRepository accounts) : IGameCommandHandler
{
    public uint Command => 32132;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var sceneId = (int)HandlerHelpers.ReadUInt(request.Body, "background_id");
        var heroId = (int)HandlerHelpers.ReadUInt(request.Body, "hero_id");
        if (sceneId > 0)
            await accounts.UpdateHomeSceneAsync(request.AccountId, sceneId, cancellationToken);
        if (heroId > 0)
        {
            await accounts.UpdateBoardHeroAsync(request.AccountId, heroId, cancellationToken);
            await OathSystem.RecordAssistantAsync(accounts, request.AccountId, AssistantHero.Of(heroId), cancellationToken);
        }
        return [];
    }
}

/// <summary>Selects special scene sub-view (32134 -> 32135).</summary>
public sealed class ProfileSpecialSceneViewHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 32134;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var subSceneId = (int)HandlerHelpers.ReadUInt(request.Body, "sub_poster_background");
        var sceneId = (int)HandlerHelpers.ReadUInt(request.Body, "poster_background_id");
        var target = subSceneId > 0 ? subSceneId : sceneId;
        if (target > 0)
            await accounts.UpdateHomeSceneAsync(request.AccountId, target, cancellationToken);
        return [HandlerHelpers.ResultOnly(codec, request, 32135, 0u)];
    }
}

/// <summary>Lobby assistant id → hero (skin ids map through skin_hero_map.json).</summary>
internal static class AssistantHero
{
    public static long Of(long id) => id >= 100000 && new SourceGameConfig().Get("skin_hero_map.json") is { } m && m.TryGetProperty(id.ToString(), out var h) ? h.GetInt64() : id;
}
