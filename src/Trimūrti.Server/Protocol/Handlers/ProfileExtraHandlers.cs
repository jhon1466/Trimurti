namespace Trimūrti.Server.Protocol.Handlers;

// Profile / personalization / social / MomoTalk domain, ported from Alpha-Persei-Cluster peripheral_service.py,
// achievement_service.py and operations.py.

public static class ProfileAcks
{
    /// <summary>Result-only operations (UI state the client keeps itself):
    /// 32040/32060/32064/32206/32208/32112 overdue decoration cleanup, 32042/32044 recommend-equip / voice checks,
    /// 32054 touch poster girl, 32056 sticker page, 32068/32070 pure-mode table settings,
    /// 32118 send like, 32122..32128 lobby random settings, 32052 skin gift, 53006 achievement story read,
    /// 91002/91006/91014 MomoTalk progress.</summary>
    public static readonly uint[] Commands =
    [
        32040, 32042, 32044, 32052, 32054, 32056, 32060, 32064, 32068, 32070, 32112, 32118,
        32122, 32124, 32126, 32128, 32206, 32208, 91002, 91006, 91014
    ];
}

/// <summary>Changes a profile decoration stored in game_user (32114 card bg, 32120 chat bubble).</summary>
public sealed class ProfileDecorationHandler(uint command, string field, string column, GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => command;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var id = HandlerHelpers.ReadUInt(r.Body, field);
        if (id == 0) return [HandlerHelpers.ResultOnly(codec, r, command + 1, 2)];
        await accounts.SetGameUserIntAsync(r.AccountId, column, (int)id, ct);
        return [HandlerHelpers.ResultOnly(codec, r, command + 1)];
    }
}

/// <summary>Profile tags (32116 -> 32117): stored for the next login's used_tag_list.</summary>
public sealed class ProfileTagsHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 32116;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var tags = HandlerHelpers.ReadIdList(r.Body, "tags");
        await accounts.SetValueAsync(r.AccountId, "profile", "used_tags", 0, ct);
        for (var i = 0; i < 8; i++)
            await accounts.SetValueAsync(r.AccountId, "profile_tag", i.ToString(), i < tags.Count ? tags[i] : 0, ct);
        return [HandlerHelpers.ResultOnly(codec, r, 32117)];
    }
}

/// <summary>Brief info for a list of players (32036 -> 32037).</summary>
public sealed class SimpleForeignInfoHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 32036;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        var list = new List<object>();
        foreach (var uid in HandlerHelpers.ReadIdList(r.Body, "user_id_list"))
        {
            var card = await accounts.GetPlayerCardAsync(uid, ct);
            list.Add(new Dictionary<string, object?>
            {
                ["user_id"] = (ulong)uid,
                ["level"] = (uint)(card?.Level ?? 80),
                ["base_info"] = new Dictionary<string, object?>
                {
                    ["nick"] = card?.Nickname ?? $"Observer_{uid % 10000}",
                    ["icon"] = (uint)(card?.Portrait > 0 ? card.Portrait : 2110841),
                    ["icon_frame"] = (uint)(card?.IconFrame > 0 ? card.IconFrame : 2001)
                }
            });
        }
        return [HandlerHelpers.Reply(codec, r, 32037, new Dictionary<string, object?> { ["result"] = 0u, ["user_brief_list"] = list.ToArray() })];
    }
}

/// <summary>Foreign player's showcased heroes (32020 -> 32021): empty lists (no other real players locally).</summary>
public sealed class ForeignHeroInfoHandler(ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 32020;

    public ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<GameResponse>>([HandlerHelpers.Reply(codec, r, 32021, new Dictionary<string, object?>
        {
            ["result"] = 0u, ["user_id"] = HandlerHelpers.ReadULong(r.Body, "user_id"),
            ["hero_list"] = Array.Empty<object>(), ["hero_oath_display"] = Array.Empty<object>()
        })]);
}

/// <summary>Overdue decoration queries (32062 portraits, 32066 bubbles): permanent decorations never expire.</summary>
public sealed class OverdueQueryHandler(uint command, string field, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => command;

    public ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<GameResponse>>([HandlerHelpers.Reply(codec, r, command + 1,
            new Dictionary<string, object?> { [field] = Array.Empty<object>() })]);
}

/// <summary>Sticker suit rewards (32058 -> 32059): one-time claim per suit id, no item payout configured.</summary>
public sealed class StickerSuitRewardHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 32058;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, CancellationToken ct)
    {
        foreach (var id in HandlerHelpers.ReadIdList(r.Body, "reward_id_list"))
            await accounts.TryClaimAsync(r.AccountId, "sticker_suit", id.ToString(), ct);
        return [HandlerHelpers.Reply(codec, r, 32059, new Dictionary<string, object?> { ["result"] = 0u, ["reward_list"] = Array.Empty<object>() })];
    }
}

