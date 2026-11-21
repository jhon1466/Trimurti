namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>Persistent profile sticker wall (32038 -> 32039) loaded into sc_32009.sticker_show_info.</summary>
public static class StickerWallPrefs
{
    private const string Kind = "sticker_wall_v1";

    public sealed class StickerItem
    {
        public uint StickerId { get; set; }
        public uint LocationX { get; set; }
        public uint LocationY { get; set; }
        public uint Scale { get; set; } = 100;
        public uint Layer { get; set; }
        public uint Rotate { get; set; }
    }

    public sealed class StickerPage
    {
        public uint PageId { get; set; } = 1;
        public uint Foreground { get; set; }
        public List<StickerItem> DisplayInfo { get; set; } = [];
    }

    public sealed class State
    {
        public List<StickerPage> Pages { get; set; } = [];
    }

    public static async Task<State> LoadAsync(GameAccountRepository accounts, long uid, CancellationToken ct) =>
        await accounts.GetJsonAsync<State>(uid, Kind, ct) ?? new State();

    public static Task SaveAsync(GameAccountRepository accounts, long uid, State state, CancellationToken ct) =>
        accounts.SetJsonAsync(uid, Kind, state, ct);

    public static async Task<object[]> BuildStickerShowInfoAsync(GameAccountRepository accounts, long uid, CancellationToken ct)
    {
        var st = await LoadAsync(accounts, uid, ct);
        if (st.Pages.Count == 0)
        {
            return
            [
                new Dictionary<string, object?>
                {
                    ["page_id"] = 1u,
                    ["foreground"] = 0u,
                    ["sticker_display_info"] = Array.Empty<object>()
                }
            ];
        }
        return st.Pages.Select(p => (object)new Dictionary<string, object?>
        {
            ["page_id"] = p.PageId,
            ["foreground"] = p.Foreground,
            ["sticker_display_info"] = p.DisplayInfo.Select(d => (object)new Dictionary<string, object?>
            {
                ["sticker_id"] = d.StickerId,
                ["location_x"] = d.LocationX,
                ["location_y"] = d.LocationY,
                ["scale"] = d.Scale,
                ["layer"] = d.Layer,
                ["rotate"] = d.Rotate
            }).ToArray()
        }).ToArray();
    }
}

/// <summary>Saves profile sticker wall layout (32038 -> 32039).</summary>
public sealed class ProfileStickerWallHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 32038;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var st = new StickerWallPrefs.State();
        if (request.Body.TryGetValue("sticker_show_info", out var raw) && raw is System.Collections.IEnumerable seq && raw is not string)
        {
            foreach (var p in seq.OfType<IReadOnlyDictionary<string, object?>>())
            {
                var page = new StickerWallPrefs.StickerPage
                {
                    PageId = HandlerHelpers.ReadUInt(p, "page_id", 1),
                    Foreground = HandlerHelpers.ReadUInt(p, "foreground")
                };
                if (p.TryGetValue("sticker_display_info", out var rawItems) && rawItems is System.Collections.IEnumerable items && rawItems is not string)
                {
                    foreach (var d in items.OfType<IReadOnlyDictionary<string, object?>>())
                    {
                        page.DisplayInfo.Add(new StickerWallPrefs.StickerItem
                        {
                            StickerId = HandlerHelpers.ReadUInt(d, "sticker_id"),
                            LocationX = HandlerHelpers.ReadUInt(d, "location_x"),
                            LocationY = HandlerHelpers.ReadUInt(d, "location_y"),
                            Scale = HandlerHelpers.ReadUInt(d, "scale", 100),
                            Layer = HandlerHelpers.ReadUInt(d, "layer"),
                            Rotate = HandlerHelpers.ReadUInt(d, "rotate")
                        });
                    }
                }
                st.Pages.Add(page);
            }
        }
        await StickerWallPrefs.SaveAsync(accounts, request.AccountId, st, cancellationToken);
        return [HandlerHelpers.ResultOnly(codec, request, 32039, 0u)];
    }
}

/// <summary>Illustrated / Collection rewards, view markers and hero race collection (52004, 52014, 52016, 52018, 52022).</summary>
public sealed class IllustratedHandler(uint command, GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public static readonly uint[] Commands = [52004, 52014, 52016, 52018, 52022];
    private const string Kind = "illustrated_v1";
    public uint Command => command;

    public sealed class State
    {
        public List<uint> ClaimedRewards { get; set; } = [];
        public List<uint> ViewedIds { get; set; } = [];
        public List<uint> Favorites { get; set; } = [];
        public List<string> ClaimedRaceTiers { get; set; } = [];
    }

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var uid = request.AccountId;
        var st = await accounts.GetJsonAsync<State>(uid, Kind, cancellationToken) ?? new State();
        switch (command)
        {
            case 52004:
            {
                var ids = HandlerHelpers.ReadIdList(request.Body, "id_list").Where(x => x > 0).Select(x => (uint)x).Distinct().ToList();
                var newly = ids.Where(id => !st.ClaimedRewards.Contains(id)).ToList();
                if (newly.Count > 0)
                {
                    st.ClaimedRewards.AddRange(newly);
                    var rewards = new (long, long)[] { (1, 20L * newly.Count) };
                    var granted = await accounts.GrantItemsAsync(uid, rewards, cancellationToken);
                    await accounts.SetJsonAsync(uid, Kind, st, cancellationToken);
                    var frames = await InventorySync.FramesAsync(accounts, codec, request, granted.Select(x => x.Item1), cancellationToken);
                    frames.Add(HandlerHelpers.Reply(codec, request, 52005, new Dictionary<string, object?>
                    {
                        ["result"] = 0u, ["item_list"] = HandlerHelpers.ToItemNetRecList(granted)
                    }));
                    return frames;
                }
                return [HandlerHelpers.Reply(codec, request, 52005, new Dictionary<string, object?> { ["result"] = 0u, ["item_list"] = Array.Empty<object>() })];
            }
            case 52014:
            {
                var id = HandlerHelpers.ReadUInt(request.Body, "id");
                if (id > 0 && !st.ViewedIds.Contains(id)) { st.ViewedIds.Add(id); await accounts.SetJsonAsync(uid, Kind, st, cancellationToken); }
                return [HandlerHelpers.ResultOnly(codec, request, 52015, 0u)];
            }
            case 52016:
            {
                var ids = HandlerHelpers.ReadIdList(request.Body, "all_id_list").Where(x => x > 0).Select(x => (uint)x);
                var added = false;
                foreach (var id in ids) if (!st.ViewedIds.Contains(id)) { st.ViewedIds.Add(id); added = true; }
                if (added) await accounts.SetJsonAsync(uid, Kind, st, cancellationToken);
                return [HandlerHelpers.ResultOnly(codec, request, 52017, 0u)];
            }
            case 52018:
            {
                var id = HandlerHelpers.ReadUInt(request.Body, "id");
                var on = request.Body.TryGetValue("type", out var raw) && raw is true;
                if (id > 0)
                {
                    if (on && !st.Favorites.Contains(id)) st.Favorites.Add(id);
                    else if (!on) st.Favorites.Remove(id);
                    await accounts.SetJsonAsync(uid, Kind, st, cancellationToken);
                }
                return [HandlerHelpers.ResultOnly(codec, request, 52019, 0u)];
            }
            case 52022:
            {
                var newly = 0;
                if (request.Body.TryGetValue("collect_info_list", out var raw) && raw is System.Collections.IEnumerable seq && raw is not string)
                {
                    foreach (var entry in seq.OfType<IReadOnlyDictionary<string, object?>>())
                    {
                        var race = HandlerHelpers.ReadUInt(entry, "race");
                        var rewardIds = HandlerHelpers.ReadIdList(entry, "reward_list");
                        foreach (var rid in rewardIds)
                        {
                            var key = $"{race}:{rid}";
                            if (!st.ClaimedRaceTiers.Contains(key)) { st.ClaimedRaceTiers.Add(key); newly++; }
                        }
                    }
                }
                if (newly > 0)
                {
                    var granted = await accounts.GrantItemsAsync(uid, [(1, 50L * newly)], cancellationToken);
                    await accounts.SetJsonAsync(uid, Kind, st, cancellationToken);
                    var frames = await InventorySync.FramesAsync(accounts, codec, request, granted.Select(x => x.Item1), cancellationToken);
                    frames.Add(HandlerHelpers.Reply(codec, request, 52023, new Dictionary<string, object?>
                    {
                        ["result"] = 0u, ["item_list"] = HandlerHelpers.ToItemNetRecList(granted)
                    }));
                    return frames;
                }
                return [HandlerHelpers.Reply(codec, request, 52023, new Dictionary<string, object?> { ["result"] = 0u, ["item_list"] = Array.Empty<object>() })];
            }
        }
        return [HandlerHelpers.ResultOnly(codec, request, command + 1, 0u)];
    }
}

/// <summary>Persists selected hero archive picture (71106 -> 71107).</summary>
public sealed class ArchivePictureHandler(GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 71106;
    private const string Kind = "archive_pic_v1";

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var archiveId = HandlerHelpers.ReadUInt(request.Body, "archive_id");
        var type = HandlerHelpers.ReadUInt(request.Body, "type");
        var id = HandlerHelpers.ReadUInt(request.Body, "id");
        if (archiveId > 0)
        {
            var map = await accounts.GetJsonAsync<Dictionary<uint, uint[]>>(request.AccountId, Kind, cancellationToken) ?? [];
            map[archiveId] = [type, id];
            await accounts.SetJsonAsync(request.AccountId, Kind, map, cancellationToken);
        }
        return [HandlerHelpers.ResultOnly(codec, request, 71107, 0u)];
    }
}

/// <summary>Preset / Reserve team management (63006 save, 63008 batch save, 63010 reset).</summary>
public sealed class ReserveTeamHandler(uint command, GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public static readonly uint[] Commands = [63006, 63008, 63010];
    private const string Kind = "reserve_teams_v1";
    public uint Command => command;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var teamType = HandlerHelpers.ReadUInt(request.Body, "team_type");
        var map = await accounts.GetJsonAsync<Dictionary<string, string>>(request.AccountId, Kind, cancellationToken) ?? [];
        var key = teamType.ToString();
        if (command == 63010) map.Remove(key);
        else map[key] = System.Text.Json.JsonSerializer.Serialize(request.Body);
        await accounts.SetJsonAsync(request.AccountId, Kind, map, cancellationToken);
        return [HandlerHelpers.ResultOnly(codec, request, command + 1, 0u)];
    }
}

/// <summary>Clean handlers for first-pass rewards (11058), newbie claim (59002), and overdue item checks (15024, 17018).</summary>
public sealed class MiscSinglePlayerHandler(uint command, GameAccountRepository accounts, ProtoCodec codec) : IGameCommandHandler
{
    public static readonly uint[] Commands = [11058, 15024, 17018, 59002];
    public uint Command => command;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case 11058:
            {
                var aid = HandlerHelpers.ReadUInt(request.Body, "activity_id");
                var fcid = HandlerHelpers.ReadUInt(request.Body, "first_clear_activity_id");
                if (await accounts.TryClaimAsync(request.AccountId, "battle_first_pass", $"{aid}:{fcid}", cancellationToken))
                {
                    var granted = await accounts.GrantItemsAsync(request.AccountId, [(1, 50)], cancellationToken);
                    var frames = await InventorySync.FramesAsync(accounts, codec, request, granted.Select(x => x.Item1), cancellationToken);
                    frames.Add(HandlerHelpers.Reply(codec, request, 11059, new Dictionary<string, object?>
                    {
                        ["result"] = 0u, ["item_list"] = HandlerHelpers.ToItemNetRecList(granted)
                    }));
                    return frames;
                }
                return [HandlerHelpers.Reply(codec, request, 11059, new Dictionary<string, object?> { ["result"] = 0u, ["item_list"] = Array.Empty<object>() })];
            }
            case 59002:
                return [HandlerHelpers.Reply(codec, request, 59003, new Dictionary<string, object?> { ["result"] = 0u, ["reward_list"] = Array.Empty<object>() })];
            default:
                return [HandlerHelpers.ResultOnly(codec, request, command + 1, 0u)];
        }
    }
}
