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

/// <summary>Persistent lobby rotation preferences (heroes type=1 and backgrounds type=2) for sc_32009.random_info.</summary>
public static class LobbyRandomPrefs
{
    private const string Kind = "lobby_random_v1";

    public sealed class Entry
    {
        public uint Model { get; set; }
        public uint ShowHeroDressingScene { get; set; } = 1;
        public uint RoutineHeroDressingScene { get; set; } = 1;
        public List<uint> List { get; set; } = [];
    }

    public sealed class State
    {
        public Entry Hero { get; set; } = new();
        public Entry Scene { get; set; } = new();
        public Entry ForType(uint type) => type == 2 ? Scene : Hero;
    }

    public static async Task<State> LoadAsync(GameAccountRepository accounts, long uid, CancellationToken ct) =>
        await accounts.GetJsonAsync<State>(uid, Kind, ct) ?? new State();

    public static Task SaveAsync(GameAccountRepository accounts, long uid, State state, CancellationToken ct) =>
        accounts.SetJsonAsync(uid, Kind, state, ct);

    public static async Task<object[]> BuildRandomInfoAsync(GameAccountRepository accounts, long uid, int boardHero, int curScene, CancellationToken ct)
    {
        var st = await LoadAsync(accounts, uid, ct);
        var heroList = st.Hero.List.Count > 0 ? st.Hero.List.ToArray() : [(uint)(boardHero > 0 ? boardHero : 1084)];
        var sceneList = st.Scene.List.Count > 0 ? st.Scene.List.ToArray() : [(uint)(curScene > 0 ? curScene : 6000)];
        return
        [
            new Dictionary<string, object?>
            {
                ["random_type"] = 1u,
                ["random_model"] = st.Hero.Model,
                ["show_hero_dressing_scene"] = st.Hero.ShowHeroDressingScene,
                ["routine_hero_dressing_scene"] = st.Hero.RoutineHeroDressingScene,
                ["random_list"] = heroList
            },
            new Dictionary<string, object?>
            {
                ["random_type"] = 2u,
                ["random_model"] = st.Scene.Model,
                ["show_hero_dressing_scene"] = st.Scene.ShowHeroDressingScene,
                ["routine_hero_dressing_scene"] = st.Scene.RoutineHeroDressingScene,
                ["random_list"] = sceneList
            }
        ];
    }
}

/// <summary>Persists lobby random/rotation preferences for heroes and backgrounds (32122, 32124, 32126, 32128, 32130).</summary>
public sealed class ProfileLobbyRandomHandler(uint command, GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public static readonly uint[] Commands = [32122, 32124, 32126, 32128, 32130];
    public uint Command => command;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var st = await LobbyRandomPrefs.LoadAsync(accounts, request.AccountId, cancellationToken);
        switch (command)
        {
            case 32122:
            {
                if (request.Body.TryGetValue("random_info_list", out var raw) && raw is System.Collections.IEnumerable seq && raw is not string)
                {
                    foreach (var item in seq.OfType<IReadOnlyDictionary<string, object?>>())
                    {
                        var rtype = HandlerHelpers.ReadUInt(item, "random_type", 1);
                        var entry = st.ForType(rtype);
                        entry.Model = HandlerHelpers.ReadUInt(item, "random_model", entry.Model);
                        entry.ShowHeroDressingScene = HandlerHelpers.ReadUInt(item, "show_hero_dressing_scene", entry.ShowHeroDressingScene);
                        entry.RoutineHeroDressingScene = HandlerHelpers.ReadUInt(item, "routine_hero_dressing_scene", entry.RoutineHeroDressingScene);
                        var ids = HandlerHelpers.ReadIdList(item, "random_list").Where(x => x > 0).Select(x => (uint)x).Distinct().ToList();
                        if (ids.Count > 0) entry.List = ids;
                    }
                }
                break;
            }
            case 32124:
            {
                var type = HandlerHelpers.ReadUInt(request.Body, "type", 1);
                st.ForType(type).Model = HandlerHelpers.ReadUInt(request.Body, "model");
                break;
            }
            case 32126:
            {
                var type = HandlerHelpers.ReadUInt(request.Body, "type", 1);
                st.ForType(type).ShowHeroDressingScene = HandlerHelpers.ReadUInt(request.Body, "show_hero_dressing_scene");
                break;
            }
            case 32128:
            {
                var type = HandlerHelpers.ReadUInt(request.Body, "type", 1);
                st.ForType(type).RoutineHeroDressingScene = HandlerHelpers.ReadUInt(request.Body, "routine_hero_dressing_scene");
                break;
            }
            case 32130:
            {
                var type = HandlerHelpers.ReadUInt(request.Body, "type", 1);
                var ids = HandlerHelpers.ReadIdList(request.Body, "random_list").Where(x => x > 0).Select(x => (uint)x).Distinct().ToList();
                st.ForType(type).List = ids;
                if (ids.Count > 0)
                {
                    if (type == 1) await accounts.UpdateBoardHeroAsync(request.AccountId, (int)ids[0], cancellationToken);
                    else if (type == 2) await accounts.UpdateHomeSceneAsync(request.AccountId, (int)ids[0], cancellationToken);
                }
                break;
            }
        }
        await LobbyRandomPrefs.SaveAsync(accounts, request.AccountId, st, cancellationToken);
        return [HandlerHelpers.ResultOnly(codec, request, command + 1, 0u)];
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
