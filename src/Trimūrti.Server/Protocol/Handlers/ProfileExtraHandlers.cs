namespace Trimūrti.Server.Protocol.Handlers;

// Profile / personalization / social / MomoTalk domain, ported from Alpha-Persei-Cluster peripheral_service.py,
// achievement_service.py and operations.py.

public static class ProfileAcks
{
    /// <summary>Result-only operations (UI state the client keeps itself):
    /// 32040/32060/32064/32206/32208/32112 overdue decoration cleanup, 32042/32044 recommend-equip / voice checks,
    /// 32054 touch poster girl, 32056 sticker page, 32068/32070 pure-mode table settings,
    /// 32118 send like, 32052 skin gift.</summary>
    public static readonly uint[] Commands =
    [
        32040, 32042, 32044, 32052, 32054, 32056, 32060, 32064, 32068, 32070, 32112, 32118,
        32206, 32208
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

/// <summary>
/// MomoTalk (sc_91001, cs_91002/91006/91014/91016) backed by the client's ChatMessageCfg (Data/Configs/momotalk_cfg.json):
/// exposes all 266 character sessions grouped by sender and persists dialogue breakpoints, choices, read state and icon.
/// </summary>
public sealed class MomoTalkSystem(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    public static readonly uint[] Commands = [91002, 91006, 91014, 91016];
    private const string Kind = "momotalk_v1";

    public sealed class SavePoint { public uint ContentId { get; set; } public uint State { get; set; } }
    public sealed class SessionProgress
    {
        public uint SendTime { get; set; }
        public uint IsView { get; set; }
        public uint CurrentContentId { get; set; }
        public List<SavePoint> SaveList { get; set; } = [];
    }
    public sealed class State
    {
        public uint Icon { get; set; } = 1;
        public Dictionary<uint, SessionProgress> Sessions { get; set; } = [];
    }

    private async Task<State> LoadAsync(long uid, CancellationToken ct) =>
        await accounts.GetJsonAsync<State>(uid, Kind, ct) ?? new State();

    private Task SaveAsync(long uid, State s, CancellationToken ct) =>
        accounts.SetJsonAsync(uid, Kind, s, ct);

    public async Task<(uint, byte[])> LoginFrameAsync(long uid, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var byType = new Dictionary<uint, Dictionary<uint, List<object>>>();

        if (config.Get("momotalk_cfg.json") is { ValueKind: System.Text.Json.JsonValueKind.Object } cfg
            && cfg.TryGetProperty("sessions", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var row in arr.EnumerateArray())
            {
                var sid = row.GetProperty("id").GetUInt32();
                var mtype = row.GetProperty("type").GetUInt32();
                if (mtype == 0) mtype = 1;
                var sender = row.GetProperty("sender").GetUInt32();
                if (!byType.TryGetValue(mtype, out var heroes)) byType[mtype] = heroes = [];
                if (!heroes.TryGetValue(sender, out var list)) heroes[sender] = list = [];

                var prog = s.Sessions.GetValueOrDefault(sid);
                list.Add(new Dictionary<string, object?>
                {
                    ["id"] = sid,
                    ["send_time"] = prog is { SendTime: > 0 } ? prog.SendTime : now,
                    ["save_list"] = (prog?.SaveList ?? []).Select(sp => (object)new Dictionary<string, object?>
                    {
                        ["content_id"] = sp.ContentId, ["state"] = sp.State
                    }).ToArray(),
                    ["is_view"] = prog?.IsView ?? 0u,
                    ["current_content_id"] = prog?.CurrentContentId ?? 0u
                });
            }
        }

        var typeHero = byType.OrderBy(kv => kv.Key).Select(kv => (object)new Dictionary<string, object?>
        {
            ["type"] = kv.Key,
            ["hero_session"] = kv.Value.OrderBy(h => h.Key).Select(h => (object)new Dictionary<string, object?>
            {
                ["sender_id"] = h.Key,
                ["session_list"] = h.Value.ToArray()
            }).ToArray()
        }).ToArray();

        return (91001, codec.Encode("sc_91001", new Dictionary<string, object?>
        {
            ["icon"] = s.Icon > 0 ? s.Icon : 1u,
            ["icon_list"] = new uint[] { 1, 2, 3, 4, 5 },
            ["type_hero"] = typeHero
        }));
    }

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var s = await LoadAsync(uid, ct);
        var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SessionProgress GetOrCreate(uint sid)
        {
            if (!s.Sessions.TryGetValue(sid, out var p)) s.Sessions[sid] = p = new SessionProgress { SendTime = now };
            if (p.SendTime == 0) p.SendTime = now;
            return p;
        }

        switch (cmd)
        {
            case 91014: // Add breakpoint
            {
                var sid = HandlerHelpers.ReadUInt(r.Body, "session_id");
                var cid = HandlerHelpers.ReadUInt(r.Body, "content_id");
                if (sid > 0 && cid > 0)
                {
                    var p = GetOrCreate(sid);
                    p.CurrentContentId = cid;
                    if (!p.SaveList.Any(x => x.ContentId == cid)) p.SaveList.Add(new SavePoint { ContentId = cid, State = 0 });
                    await SaveAsync(uid, s, ct);
                }
                return [HandlerHelpers.ResultOnly(codec, r, 91015)];
            }
            case 91002: // Finish breakpoint / choice
            {
                var sid = HandlerHelpers.ReadUInt(r.Body, "session_id");
                var cid = HandlerHelpers.ReadUInt(r.Body, "content_id");
                var st = HandlerHelpers.ReadUInt(r.Body, "state");
                if (sid > 0 && cid > 0)
                {
                    var p = GetOrCreate(sid);
                    p.CurrentContentId = cid;
                    var existing = p.SaveList.FirstOrDefault(x => x.ContentId == cid);
                    if (existing is not null) existing.State = st;
                    else p.SaveList.Add(new SavePoint { ContentId = cid, State = st });
                    await SaveAsync(uid, s, ct);
                }
                return [HandlerHelpers.ResultOnly(codec, r, 91003)];
            }
            case 91006: // Mark session read
            {
                var sid = HandlerHelpers.ReadUInt(r.Body, "session_id");
                if (sid > 0)
                {
                    GetOrCreate(sid).IsView = 1;
                    await SaveAsync(uid, s, ct);
                }
                return [HandlerHelpers.ResultOnly(codec, r, 91007)];
            }
            case 91016: // Set avatar/frame icon
            {
                var icon = HandlerHelpers.ReadUInt(r.Body, "icon");
                if (icon == 0) return [HandlerHelpers.ResultOnly(codec, r, 91017, 2)];
                s.Icon = icon;
                await SaveAsync(uid, s, ct);
                return [HandlerHelpers.ResultOnly(codec, r, 91017)];
            }
        }
        return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)];
    }
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

