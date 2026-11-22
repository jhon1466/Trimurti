using Trimūrti.Server.Protocol;

namespace Trimūrti.Server;

/// <summary>Builds the minimal account-owned login snapshot from SQLite, ending with the login-complete marker.</summary>
public sealed class LoginBootstrap(GameAccountRepository accounts, ProtoCodec codec, SchemaDefaults defaults, ILogger<LoginBootstrap> logger, Protocol.Handlers.StageModes? modes = null)
{
    // Resident (permanent) chapter activities the lobby's ChapterResidentTools reads during LateInitData.
    // Same seed set as LunaAG TimeSyncHandler; theme/template/sub-lists come from activity_content_cfg.json.
    private const uint CurrentMainEvent = 4410001;
    // 失序深阱 season served by AbyssMode (abyss_cfg.json activity_id).
    private const uint AbyssActivity = 230102;

    private static readonly (uint Id, uint[] Children)[] ResidentActivitySeeds =
    [
        (281001, [280001]), (321001, [321201, 323641]), (331001, [323801]), (3510001, [3539101]),
        (3610001, [3639701]), (3710001, [3740601]), (3810001, [3814801, 3840801]), (3910001, [3941101]),
        (4010001, [4041301]), (4110001, [4142701]), (4210001, [4243101]),
        (280001, []), (321201, []), (323641, []), (323801, []), (3539101, []), (3639701, []), (3740601, []),
        (3814801, []), (3840801, []), (3941101, []), (4041301, []), (4142701, []), (4243101, [])
    ];

    private static readonly Lazy<Dictionary<uint, (uint Theme, uint Template, uint[] Subs)>> ActivityCfg = new(LoadActivityCfg);

    private static Dictionary<uint, (uint Theme, uint Template, uint[] Subs)> LoadActivityCfg()
    {
        var result = new Dictionary<uint, (uint, uint, uint[])>();
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "Configs", "activity_content_cfg.json");
        if (!File.Exists(path)) return result;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        var versions = doc.RootElement.GetProperty("versions");
        var latest = versions.EnumerateObject().OrderBy(v => int.TryParse(v.Name, out var n) ? n : 0).Last();
        foreach (var act in latest.Value.GetProperty("activities").EnumerateObject())
        {
            if (!uint.TryParse(act.Name, out var id)) continue;
            var v = act.Value;
            var subs = v.TryGetProperty("sub_activity_list", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Array
                ? s.EnumerateArray().Where(x => x.ValueKind == System.Text.Json.JsonValueKind.Number).Select(x => x.GetUInt32()).ToArray()
                : [];
            result[id] = (v.GetProperty("activity_theme").GetUInt32(), v.GetProperty("activity_template").GetUInt32(), subs);
        }
        return result;
    }

    /// <summary>Activities open right now (the 11001 list with state 1): id → ActivityCfg template.</summary>
    public static IReadOnlyDictionary<uint, uint> OpenActivityTemplates()
    {
        var cfg = ActivityCfg.Value;
        var open = new Dictionary<uint, uint>();
        foreach (var e in BuildResidentActivities(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).OfType<Dictionary<string, object?>>())
            if (Convert.ToUInt32(e["state"]) == 1 && Convert.ToUInt32(e["activity_id"]) is var id)
                open[id] = cfg.TryGetValue(id, out var c) ? c.Template : Convert.ToUInt32(e.GetValueOrDefault("template") ?? 0u);
        return open;
    }

    private static object?[] BuildResidentActivities(long now)
    {
        var cfg = ActivityCfg.Value;
        var start = now - 86400;
        var stop = now + 365L * 86400;
        var list = new List<object?>();
        foreach (var (id, children) in ResidentActivitySeeds)
        {
            if (!cfg.TryGetValue(id, out var entry)) continue;
            var subs = entry.Subs.Length > 0 ? entry.Subs : children.Where(cfg.ContainsKey).ToArray();
            list.Add(new Dictionary<string, object?>
            {
                ["activity_id"] = id,
                ["start_time"] = start,
                ["stop_time"] = stop,
                ["state"] = 0,
                ["theme"] = entry.Theme,
                ["template"] = entry.Template,
                ["sub_activity_id_list"] = subs.Cast<object>().ToArray()
            });
        }
        // ActivityConst.SIGN (3): permanent daily calendar sign, as in the official/captured activity list.
        // Without it the client's daily-sign tab is hidden while SignTools.IsCanSign still sees an unsigned day,
        // so the sign window (with the monthly-card tabs) reopens forever.
        // Gacha pages (ActivityDrawPoolCfg): the activities whose pools are open (draw_cfg.json draw_activities).
        if (new SourceGameConfig().Get("draw_cfg.json") is { } drawCfg)
            foreach (var aid in drawCfg.GetProperty("draw_activities").EnumerateArray().Select(x => x.GetUInt32()))
                if (cfg.TryGetValue(aid, out var da) && !list.Any(x => x is Dictionary<string, object?> d && Equals(d["activity_id"], aid)))
                    list.Add(new Dictionary<string, object?>
                    {
                        ["activity_id"] = aid, ["start_time"] = start, ["stop_time"] = stop, ["state"] = 1,
                        ["theme"] = da.Theme, ["template"] = da.Template, ["sub_activity_id_list"] = Array.Empty<object>()
                    });
        // 多维变量: weekly decision cycle activity (template 73).
        {
            var poly = new Protocol.Handlers.Polyhedron(null!, new SourceGameConfig());
            var (ps, pe) = Protocol.Handlers.Polyhedron.Cycle();
            if (cfg.TryGetValue(poly.ActivityId, out var pa))
                list.Add(new Dictionary<string, object?>
                {
                    ["activity_id"] = poly.ActivityId, ["start_time"] = ps, ["stop_time"] = pe, ["state"] = 1,
                    ["theme"] = pa.Theme, ["template"] = pa.Template, ["sub_activity_id_list"] = pa.Subs.Cast<object>().ToArray()
                });
        }
        // 心象梦魇 / 古远虚影: the heart demon boss running now (two-week rotation).
        list.AddRange(new Protocol.Handlers.HeartDemon(null!, new SourceGameConfig(), null!).ActivityEntries(id => cfg.TryGetValue(id, out var e) ? e : ((uint, uint, uint[])?)null));
        // 迭代校验·挑战关: the season running now (two-week rotation) with its stage/rank sub-activities.
        list.AddRange(new Protocol.Handlers.CoreChallenge(null!, new SourceGameConfig()).ActivityEntries(id => cfg.TryGetValue(id, out var e) ? e : ((uint, uint, uint[])?)null));
        // Current version main event (ActivityEntraceCfg 4410001, theme 44): the right-side entrance on the home screen.
        // Its sub activities (古远虚影) open with it; anything already listed keeps its own entry.
        if (cfg.TryGetValue(CurrentMainEvent, out var me))
            foreach (var id in new[] { CurrentMainEvent }.Concat(me.Subs).Append(AbyssActivity))
                if (cfg.TryGetValue(id, out var e) && !list.Any(x => x is Dictionary<string, object?> d && Equals(d["activity_id"], id)))
                    list.Add(new Dictionary<string, object?>
                    {
                        ["activity_id"] = id, ["start_time"] = start, ["stop_time"] = stop, ["state"] = 1,
                        ["theme"] = e.Theme, ["template"] = e.Template, ["sub_activity_id_list"] = e.Subs.Cast<object>().ToArray()
                    });
        list.Insert(0, new Dictionary<string, object?>
        {
            ["activity_id"] = 3u, ["start_time"] = 1636923600L, ["stop_time"] = 3404149200L, ["state"] = 1,
            ["theme"] = 0u, ["template"] = 2u, ["sub_activity_id_list"] = Array.Empty<object>()
        });
        return list.ToArray();
    }

    public async Task<IReadOnlyList<GameResponse>> BuildAsync(long uid, ushort index, ushort serverIndex, CancellationToken cancellationToken)
    {
        var state = await accounts.GetInitialLoginStateAsync(uid, cancellationToken);
        if (state is null)
        {
            logger.LogWarning("Cannot build login snapshot: uid {Uid} has incomplete initial state", uid);
            return Array.Empty<GameResponse>();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // Saves from older builds can hold less hero exp than their level needs; fix them before the heroes go out.
        await new Protocol.Handlers.HeroGrowth(accounts, new SourceGameConfig(), codec).RepairExpAsync(uid, cancellationToken);
        var lobby = await accounts.GetLobbySnapshotAsync(uid, cancellationToken);
        var (birthMonth, birthDay, bgmId) = await accounts.GetBirthdayAndBgmAsync(uid, cancellationToken);
        var monthCardState = await Protocol.Handlers.CardState.MonthCardAsync(accounts, codec, uid, cancellationToken);
        var bigCardState = await Protocol.Handlers.CardState.BigCardAsync(accounts, codec, uid, cancellationToken);
        // Sign state (SignData): 11013 daily calendar sign, 11015 seven-day sign. The captured stream carries another
        // player's dates, which makes SignTools.IsCanSign always true and reopens the sign/monthly-card window forever.
        var today = GameClock.Today;
        var dailySign = await accounts.GetSignRowAsync(uid, 3, cancellationToken);
        var dailyDays = dailySign is not null && dailySign.Year == today.Year && dailySign.Month == today.Month ? dailySign.Days : [];
        var sevenSign = await accounts.GetSignRowAsync(uid, 4300101, cancellationToken);
        var usedTags = new List<uint>();
        for (var i = 0; i < 8; i++)
        {
            var tag = await accounts.GetValueAsync(uid, "profile_tag", i.ToString(), 0, cancellationToken);
            if (tag > 0) usedTags.Add((uint)tag);
        }
        var equips = await accounts.GetEquipsAsync(uid, cancellationToken);
        var servants = await accounts.GetServantsAsync(uid, cancellationToken);
        var unlockedSkins = await accounts.GetUnlockedSkinsAsync(uid, cancellationToken);
        var favoriteHeroes = await accounts.GetHeroFavoritesAsync(uid, cancellationToken);
        var warpByHero = await accounts.GetHeroWarpAsync(uid, cancellationToken);
        var archives = await accounts.GetHeroArchivesAsync(uid, cancellationToken);
        var claimedTrustRewards = await accounts.GetClaimedTrustRewardsAsync(uid, cancellationToken);
        var equipsByHero = equips.Where(e => e.HeroId > 0).GroupBy(e => e.HeroId).ToDictionary(g => g.Key, g => g.ToArray());
        var sourceConfig = new SourceGameConfig();
        var skinHeroMap = sourceConfig.GetSkinHeroMap();
        var validItemIds = sourceConfig.GetAllItemIds().ToHashSet();
        var skinsByHero = unlockedSkins
            .Where(s => skinHeroMap.ContainsKey(s))
            .GroupBy(s => skinHeroMap[s])
            .ToDictionary(g => g.Key, g => g.ToArray());
        var localNow = GameClock.Now;
        var monday = new DateTimeOffset(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, localNow.Offset)
            .AddDays(-(((int)localNow.DayOfWeek + 6) % 7)).ToUnixTimeSeconds();

        var intensifyByHero = await accounts.GetHeroIntensifyAsync(uid, cancellationToken);
        var relationState = await new Protocol.Handlers.RelationSystem(accounts, new SourceGameConfig(), codec).LoadAsync(uid, cancellationToken);
        var heroList = state.Heroes
            .Select(hero => (object?)BuildHeroNetRec(
                hero,
                sourceConfig,
                equipsByHero.TryGetValue(hero.Id, out var heroEquips) ? heroEquips : Array.Empty<EquipSnapshot>(),
                warpByHero.TryGetValue(hero.Id, out var warpSlots) ? warpSlots : Array.Empty<GameAccountRepository.HeroWarpSlot>(),
                skinsByHero.TryGetValue(hero.Id, out var heroSkins) ? heroSkins : Array.Empty<long>(),
                intensifyByHero.GetValueOrDefault(hero.Id),
                Protocol.Handlers.RelationSystem.RelationRec(relationState, hero.Id)))
            .ToArray();
        var validCurrencyIds = SourceGameConfig.ValidCurrencyIds;
        var dbCurrencyMap = state.Inventory
            .Where(x => x.Source == "currency" && validCurrencyIds.Contains(x.Id))
            .ToDictionary(x => x.Id, x => x.Count);
        var currencies = validCurrencyIds
            .Select(id => (object?)new Dictionary<string, object?>
            {
                ["id"] = id,
                ["num"] = dbCurrencyMap.TryGetValue(id, out var count) && count > 0 ? count : 99_999_999L,
                ["time_valid"] = 0u
            })
            // Other client currencies (ItemCfg type 1) only with what the player really has.
            .Concat(state.Inventory
                .Where(x => x.Source == "currency" && x.Count > 0 && !validCurrencyIds.Contains(x.Id) && sourceConfig.IsCurrencyItem(x.Id))
                .Select(x => (object?)new Dictionary<string, object?> { ["id"] = x.Id, ["num"] = x.Count, ["time_valid"] = 0u }))
            .ToArray();

        var materials = state.Inventory
            .Where(x => x.Source == "material" && x.Count > 0 && validItemIds.Contains(x.Id) && !validCurrencyIds.Contains(x.Id))
            .Select(x => (object?)new Dictionary<string, object?> { ["id"] = x.Id, ["num"] = x.Count, ["time_valid"] = 0u })
            .ToArray();

        var newbie = state.Newbie;
        var newbiePayload = new Dictionary<string, object?>
        {
            ["completed_time"] = newbie.CompletedAt,
            ["newbie_sign"] = new Dictionary<string, object?> { ["now_sign_times"] = newbie.SignCount, ["last_sign_timestamp"] = newbie.LastSignAt },
            ["newbie_level_reward"] = new Dictionary<string, object?> { ["received_level_list"] = Array.Empty<int>() },
            ["newbie_recharge_reward"] = new Dictionary<string, object?>
            {
                ["first_recharge_reward"] = new Dictionary<string, object?> { ["first_gear_recharge_reward"] = 0, ["second_gear_recharge_flag"] = false, ["now_sign_times"] = 0, ["last_sign_timestamp"] = 0, ["is_new_recharge_6"] = false, ["is_new_recharge_18"] = false },
                ["first_monthly_card_reward"] = new Dictionary<string, object?> { ["flag"] = false, ["role_reward_flag"] = false, ["sign_times"] = 0, ["sign_reward_flag"] = false, ["is_new_tag_role_reward"] = false, ["is_new_tag_sign_reward"] = false },
                ["first_battlepass_reward"] = new Dictionary<string, object?> { ["first_battlepass_reward"] = 0, ["is_new_battlepass_reward"] = false }
            },
            ["trigger_time"] = newbie.TriggerAt,
            ["max_phase"] = Math.Max(1, newbie.MaxPhase),
            ["got_pt_id_list"] = Array.Empty<int>(),
            ["version_id"] = newbie.Version
        };

        // Only the player's own sigils (with enchant slots) and servants; no catalogue fallback.
        var sigils = await accounts.GetSigilsAsync(uid, cancellationToken);
        var equipList = sigils.Select(Protocol.Handlers.SigilSystem.EquipRec).ToArray();
        var servantList = servants.Select(s => (object)new Dictionary<string, object?>
        {
            ["uid"] = (ulong)s.Uid,
            ["id"] = (uint)s.Id,
            ["stage"] = (uint)s.Stage,
            ["is_locked"] = (uint)s.IsLocked
        }).ToArray();
        var heroPieces = await accounts.GetHeroPiecesAsync(uid, cancellationToken);
        var sigilExtra = await new Protocol.Handlers.SigilSystem(accounts, sourceConfig, codec).ExtraAsync(uid, cancellationToken);

        var payloads = new (uint Command, byte[] Payload)[]
        {
            (11001, codec.Encode("sc_11001", new Dictionary<string, object?> { ["activity_list"] = BuildResidentActivities(now) })),
            (10600, codec.Encode("sc_10600", new Dictionary<string, object?>
            {
                ["open_system"] = new uint[]
                {
                    20, 101, 201, 203, 204, 205, 206, 207, 208, 209, 210, 211, 221, 231, 235,
                    301, 302, 303, 304, 305, 306, 307, 308, 309, 311, 312, 313, 314, 316,
                    330, 331, 332, 333, 334, 335, 336, 337, 341, 342, 343, 344, 345, 346,
                    401, 501, 510, 511, 512, 513, 519, 601, 701, 702, 703,
                    801, 802, 803, 804, 901, 902, 904, 905, 1001, 1101, 1201, 1301, 1401,
                    1501, 1601, 1701, 1702, 1802, 1803, 1901, 1902, 1903, 1904, 1905,
                    2001, 2101, 2201, 2301, 2302, 2501, 2510, 2511, 2512, 2513, 2514, 2515,
                    2601, 2701, 2801, 2901, 3001, 3101, 3110, 3120,
                    9901, 9902, 9903, 9904, 9905, 9907, 9908, 9909, 9910, 9911, 9912,
                    10101, 10102, 10301, 10401, 10501, 10601, 10701, 10702, 10703, 10704,
                    11102, 20001, 20002
                }
            })),
            (11013, codec.Encode("sc_11013", new Dictionary<string, object?>
            {
                ["year"] = (uint)today.Year, ["month"] = (uint)today.Month, ["day"] = (uint)today.Day,
                ["sign_list"] = dailyDays.Select(d => (uint)d).ToArray()
            })),
            (11015, codec.Encode("sc_11015", new Dictionary<string, object?>
            {
                ["activity_id"] = 4300101u, ["sign_count"] = (uint)(sevenSign?.Count ?? 0), ["last_sign_time"] = (uint)(sevenSign?.LastSignAt ?? 0)
            })),
            (34023, monthCardState),
            (34101, bigCardState),
            (12033, codec.Encode("sc_12033", new Dictionary<string, object?> { ["month"] = (uint)birthMonth, ["day"] = (uint)birthDay })),
            (12009, codec.Encode("sc_12009", new Dictionary<string, object?> { ["user_level"] = (uint)Math.Max(state.Profile.Level, 100) })),
            (23009, codec.Encode("sc_23009", new Dictionary<string, object?> { ["total_exp"] = sourceConfig.GetMaxUserTotalExp(), ["nick"] = state.Profile.Nickname, ["hero_num"] = state.Heroes.Count, ["plot_progress"] = state.Profile.PlotProgress is > 0 and < 20_000_000 ? state.Profile.PlotProgress : 1030007, ["is_changed_nick"] = 1, ["system_change_nick_times"] = 0 })),
            (32009, codec.Encode("sc_32009", new Dictionary<string, object?>
            {
                ["sign"] = lobby.Sign ?? "",
                ["heroes"] = (lobby.ShowHeroes is { Count: > 0 } ? lobby.ShowHeroes.Select(id => (uint)id) : state.Heroes.Take(3).Select(h => (uint)h.Id)).ToArray(),
                ["poster_girl"] = (uint)(lobby.BoardHero > 0 ? lobby.BoardHero : 1084),
                // The profile view indexes the default decoration collections while building
                // its first card; send the same valid base portraits/titles used by the source.
                ["icon_list"] = sourceConfig.GetDecorationIds("PORTRAIT").Append(2110841u).Append((uint)(lobby.CurPortrait > 0 ? lobby.CurPortrait : 2110841)).Distinct()
                    .Select(id => (object)new Dictionary<string, object?> { ["id"] = id, ["lasted_time"] = 0u, ["obtain_time"] = (uint)now }).ToArray(),
                ["icon"] = (uint)(lobby.CurPortrait > 0 ? lobby.CurPortrait : 2110841),
                ["icon_frame_list"] = sourceConfig.GetDecorationIds("FRAME").Append((uint)(lobby.CurIconFrame > 0 ? lobby.CurIconFrame : 2001)).Distinct()
                    .Select(id => (object)new Dictionary<string, object?> { ["id"] = id, ["lasted_time"] = 0u, ["obtain_time"] = (uint)now }).ToArray(),
                ["icon_frame"] = (uint)(lobby.CurIconFrame > 0 ? lobby.CurIconFrame : 2001),
                ["sticker_show_info"] = await Trimūrti.Server.Protocol.Handlers.StickerWallPrefs.BuildStickerShowInfoAsync(accounts, uid, cancellationToken),
                ["sticker_background"] = (uint)(lobby.CurStickerBg > 0 ? lobby.CurStickerBg : 4002),
                ["all_background_list"] = sourceConfig.GetDecorationIds("STICKER_BG").Append((uint)(lobby.CurStickerBg > 0 ? lobby.CurStickerBg : 4002)).Distinct().ToArray(),
                ["all_sticker_list"] = sourceConfig.GetDecorationIds("STICKER").ToArray(),
                ["poster_background_list"] = sourceConfig.GetAllSceneIds().Append((uint)(lobby.CurScene > 0 ? lobby.CurScene : 6000)).Distinct()
                    .Select(id => (object)new Dictionary<string, object?> { ["id"] = id, ["lasted_time"] = 0u, ["obtain_time"] = (uint)now }).ToArray(),
                ["poster_background_id"] = (uint)(lobby.CurScene > 0 ? lobby.CurScene : 6000),
                ["likes"] = 0u,
                ["today_send_like"] = Array.Empty<ulong>(),
                ["information_background_list"] = sourceConfig.GetDecorationIds("CARD_BG").Append((uint)(lobby.CurCardBg > 0 ? lobby.CurCardBg : 8001)).Distinct()
                    .Select(id => (object)new Dictionary<string, object?> { ["id"] = id, ["lasted_time"] = 0u, ["obtain_time"] = (uint)now }).ToArray(),
                ["tag_info_list"] = sourceConfig.GetDecorationIds("TAG")
                    .Select(id => (object)new Dictionary<string, object?> { ["id"] = id, ["lasted_time"] = 0u, ["obtain_time"] = 0u }).ToArray(),
                ["used_tag_list"] = usedTags.ToArray(),
                ["information_background_id"] = (uint)(lobby.CurCardBg > 0 ? lobby.CurCardBg : 8001),
                ["chat_bubble"] = (uint)(lobby.CurChatBubble > 0 ? lobby.CurChatBubble : 9001),
                ["chat_bubble_list"] = sourceConfig.GetDecorationIds("CHAT_BUBBLE").Append((uint)(lobby.CurChatBubble > 0 ? lobby.CurChatBubble : 9001)).Distinct()
                    .Select(id => (object)new Dictionary<string, object?> { ["id"] = id, ["lasted_time"] = 0u, ["obtain_time"] = (uint)now }).ToArray(),
                ["random_info"] = await Trimūrti.Server.Protocol.Handlers.LobbyRandomPrefs.BuildRandomInfoAsync(accounts, uid, lobby.BoardHero, lobby.CurScene, cancellationToken),
                ["all_foreground_list"] = Array.Empty<uint>(),
                ["info_list"] = Array.Empty<object>(),
                ["admitted_suit_reaward_list"] = Array.Empty<object>(),
                ["game_icon"] = Array.Empty<object>(),
                ["table_setting"] = new Dictionary<string, object?>
                {
                    ["pattern_id"] = 0u,
                    ["pattern_scope"] = 0u,
                    ["switch_frequency"] = 0u,
                    ["entry_time"] = 0u
                },
                ["table_setting_module"] = Array.Empty<object>()
            })),
            (32011, codec.Encode("sc_32011", new Dictionary<string, object?> { ["sub_poster_background_list"] = Array.Empty<object>() })),
            (32051, codec.Encode("sc_32051", new Dictionary<string, object?> { ["list"] = Array.Empty<object>() })),
            (53001, codec.Encode("sc_53001", new Dictionary<string, object?> { ["achievement_list"] = Array.Empty<object>(), ["story_line"] = Array.Empty<uint>() })),
            (53003, codec.Encode("sc_53003", new Dictionary<string, object?> { ["achievement_progress"] = Array.Empty<object>() })),
            (12001, codec.Encode("sc_12001", new Dictionary<string, object?> { ["story_list"] = new[] { 1030001, 1030002, 1030003, 1030004, 1030005, 1030006, 1030007 } })),
            (24009, ChapterProgress(codec, lobby.Chapters)),
                        (13009, codec.Encode("sc_13009", new Dictionary<string, object?> { ["equip_list"] = equipList, ["is_init"] = 0u })),
            (46011, codec.Encode("sc_46011", new Dictionary<string, object?> { ["servant_list"] = servantList })),
            (13057, codec.Encode("sc_13057", new Dictionary<string, object?> { ["type_list"] = sigilExtra.AutoDecompose.OrderBy(x => x).ToArray() })),
            (15005, codec.Encode("sc_15005", new Dictionary<string, object?>())),
            (15007, codec.Encode("sc_15007", new Dictionary<string, object?> { ["left_time_coin"] = 0u, ["left_time_fatigue"] = 0u })),
            (15009, codec.Encode("sc_15009", new Dictionary<string, object?> { ["currency_list"] = currencies, ["last_fatigue_recover_time"] = 0 })),
            (17007, codec.Encode("sc_17007", new Dictionary<string, object?>())),
            (17009, codec.Encode("sc_17009", new Dictionary<string, object?> { ["material_list"] = materials })),
            (17023, codec.Encode("sc_17023", new Dictionary<string, object?>())),
            await new Protocol.Handlers.TrustSystem(accounts, sourceConfig, codec).ExchangeFrameAsync(uid, cancellationToken),
                                    (12011, codec.Encode("sc_12011", new Dictionary<string, object?>
            {
                ["mod_guide_list"] = new object[]
                {
                    1, 2, 3, 4, 5, 6, 8, 11, 13, 21, 22, 27, 28, 29, 35, 36, 37, 41, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55,
                    61, 62, 63, 64, 65, 66, 70, 71, 72, 73, 74, 75, 76, 83, 90, 91, 92, 94, 95, 96, 97, 98, 99,
                    101, 102, 103, 104, 114, 117, 119, 120, 127, 128, 129, 130, 131, 132, 133, 135, 136, 137, 138, 139,
                    140, 141, 142, 143, 144, 145, 146, 147, 148, 149, 150, 151, 152, 153, 154, 155, 158, 159, 160,
                    161, 162, 163, 164, 165, 166, 167, 168, 169, 170, 171, 172, 173, 174, 175, 176, 177, 178, 179, 180,
                    181, 182, 183, 184, 185, 186, 187, 188, 189, 190, 191, 192, 197, 198, 199, 200,
                    201, 202, 203, 204, 205, 206, 207, 208, 209, 210, 211, 212, 213, 214, 215, 216, 217, 218, 219, 220,
                    221, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 241, 242, 243, 244, 245, 246, 247, 248, 249, 250,
                    251, 252, 253, 254, 255, 256, 257, 258, 259, 260, 261, 262, 263, 264, 265, 266,
                    271, 272, 273, 274, 275, 276, 277, 278, 279, 280, 281, 282, 283, 284, 285, 286, 287, 288, 289, 290,
                    291, 292, 293, 294, 295, 296, 297, 298, 299, 300, 301, 302, 303, 304, 305, 306, 307, 308, 309, 310,
                    311, 312, 313, 314, 315, 316, 317, 318, 319, 320, 321, 322, 323, 324, 350, 351, 352, 353, 354,
                    501, 502, 503, 504, 505, 506, 507, 508, 509, 510, 511, 512, 513, 514, 515, 516, 517, 518, 519, 520,
                    521, 522, 523, 524, 525, 526, 1101, 1301, 2201, 2451, 2471, 2481, 2501, 3501, 3601, 4601, 6601
                }
            })),
            (12111, codec.Encode("sc_12111", new Dictionary<string, object?>
            {
                ["mod_guide_list"] = new object[]
                {
                    1005, 1201, 1202, 1301, 1302, 1401, 1402, 1501, 1502, 1701, 1804, 1805, 1901, 2102, 2103,
                    2201, 2202, 2301, 3001, 3201, 3301, 3401, 3601, 10201, 10301, 10401, 10501, 10601, 10701, 10801, 10901
                }
            })),
            // Own newbie state; without it the captured stream's 59011 (another player's save, with a pending
            // first-monthly-card reward) makes the monthly card popup reopen forever.
            (59011, codec.Encode("sc_59011", newbiePayload)),
            (59001, codec.Encode("sc_59001", new Dictionary<string, object?>
            {
                ["is_completed"] = 2u,
                ["trigger_time"] = (uint)now,
                ["newbie_recharge_reward"] = new Dictionary<string, object?>
                {
                    ["first_recharge_reward"] = new Dictionary<string, object?>
                    {
                        ["first_gear_recharge_reward"] = 2u,
                        ["second_gear_recharge_flag"] = true,
                        ["now_sign_times"] = 0u,
                        ["last_sign_timestamp"] = (uint)now,
                        ["is_new_recharge_6"] = false,
                        ["is_new_recharge_18"] = false
                    },
                    ["first_monthly_card_reward"] = new Dictionary<string, object?>
                    {
                        ["flag"] = false,
                        ["role_reward_flag"] = true,
                        ["sign_times"] = 0u,
                        ["sign_reward_flag"] = true,
                        ["is_new_tag_role_reward"] = false,
                        ["is_new_tag_sign_reward"] = false
                    },
                    ["first_battlepass_reward"] = new Dictionary<string, object?>
                    {
                        ["first_battlepass_reward"] = 2u,
                        ["is_new_battlepass_reward"] = false
                    }
                }
            })),
            (14009, codec.Encode("sc_14009", new Dictionary<string, object?>
            {
                ["hero_info_list"] = heroList,
                ["archives"] = archives.Select(a => (object)new Dictionary<string, object?>
                {
                    ["archive_id"] = a.ArchiveId,
                    ["exp"] = a.Exp,
                    ["gift_list"] = Array.Empty<object>(),
                    ["text_list"] = a.TextList.ToArray(),
                    ["video_list"] = a.VideoList.ToArray(),
                    ["selected_picture"] = new Dictionary<string, object?>(),
                    ["hero_story_list"] = Array.Empty<object>(),
                    ["super_heart_link_list"] = Array.Empty<object>()
                }).ToArray(),
                ["favorites"] = favoriteHeroes.Select(id => (object?)id).ToArray(),
                ["piece_list"] = heroPieces
                    .Select(p => (object)new Dictionary<string, object?> { ["id"] = (uint)p.Hero, ["num"] = (uint)p.Num })
                    .ToArray(),
                ["proposal_list"] = Protocol.Handlers.SigilSystem.ProposalRecs(sigilExtra)
            })),
            (63005, codec.Encode("sc_63005", new Dictionary<string, object?>
            {
                ["teams_info_list"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["team_type"] = 0u,
                        ["cont_teams"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["cont_id"] = 0u,
                                ["teams"] = new object[]
                                {
                                    new Dictionary<string, object?>
                                    {
                                        ["team_index"] = 0u,
                                        ["cooperate_unique_skill_id"] = 0u,
                                        ["mimir_info"] = new Dictionary<string, object?> { ["mimir_id"] = 0u, ["hero_id"] = 0u },
                                        ["hero_list"] = new object[]
                                        {
                                            new Dictionary<string, object?> { ["hero_id"] = 1084u, ["owner_id"] = 0UL, ["hero_type"] = 1u },
                                            new Dictionary<string, object?> { ["hero_id"] = 0u, ["owner_id"] = 0UL, ["hero_type"] = 1u },
                                            new Dictionary<string, object?> { ["hero_id"] = 0u, ["owner_id"] = 0UL, ["hero_type"] = 1u }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            })),
            (10201, codec.Encode("sc_10201", new Dictionary<string, object?> { ["timestamp"] = now, ["monday_0oclock_timestamp"] = monday, ["verify_timestamp"] = GameClock.VerifyTimestamp }))
        };

        var playerState = await BuildPlayerStateAsync(uid, lobby, now, cancellationToken);
        var own = payloads.Concat(playerState).ToList();
        own.Add((24061, await new Protocol.Handlers.ChapterMap(accounts, new SourceGameConfig(), codec).LoginFrameAsync(uid, cancellationToken)));
        // Empty fallbacks only for frames no system built: Merge keeps the first frame of each command, so adding them
        // first hid the real ones (28019 activity points → black task window, event modes, autochess, summer board…).
        var built = own.Select(f => f.Item1).ToHashSet();
        foreach (var cmd in FreshPlayerFrames.Where(c => !built.Contains(c)))
            own.Add((cmd, codec.Encode("sc_" + cmd, defaults.Build("sc_" + cmd))));
        own.AddRange(LoginDynamicFrames.Build(codec, defaults, own.Select(f => f.Item1).ToHashSet()).ToList());
        return LoginPushCatalog.Merge(own).Select(item => new GameResponse(
            item.Command,
            item.Command == 10201 ? index : (ushort)0,
            item.Command == 10201 ? serverIndex : (ushort)0,
            item.Payload)).ToArray();
    }

    /// <summary>
    /// Player-progress login frames that the captured stream (another player's save) used to supply. A fresh account
    /// has no progress in them, so they start empty (schema-complete) unless Trimūrti stores real state for them.
    /// </summary>
    private static readonly uint[] FreshPlayerFrames =
    [
        12023, 12123, 14201, 19029, 24019, 24043, 24045, 24051, 25009, 28019, 28801,
        47001, 47003, 51001, 52001, 52021, 52025, 90007, 90051
    ];

    /// <summary>Base chat emoji set unlocked for every account (Alpha-Persei generator 12039).</summary>
    private static readonly uint[] BaseEmoji =
    [
        343, 240, 346, 227, 349, 352, 339, 207, 342, 239, 345, 242, 226, 348, 351, 338, 206, 341, 344, 241, 225, 347, 228, 350, 353
    ];

    private async Task<List<(uint Command, byte[] Payload)>> BuildPlayerStateAsync(long uid, LobbySnapshot lobby, long now, CancellationToken ct)
    {
        var frames = new List<(uint, byte[])>();

        // 10503: server clock + refresh points. The captured one carried the capture's date (August), which set the
        // client clock in the past on every login and made it re-claim the daily sign.
        var nextDaily = GameClock.NextAt(4);
        var cn = GameClock.Now;
        var nextWeekly = new DateTimeOffset(cn.Year, cn.Month, cn.Day, 4, 0, 0, GameClock.Offset).AddDays(((int)DayOfWeek.Monday - (int)cn.DayOfWeek + 7) % 7);
        if (nextWeekly <= cn) nextWeekly = nextWeekly.AddDays(7);
        var nextMonthly = new DateTimeOffset(cn.Year, cn.Month, 1, 4, 0, 0, GameClock.Offset).AddMonths(1);
        frames.Add((10503, codec.Encode("sc_10503", new Dictionary<string, object?>
        {
            ["timestamp"] = (uint)now, ["verify_timestamp"] = (uint)GameClock.VerifyTimestamp,
            ["next_refresh_time"] = (uint)nextDaily.ToUnixTimeSeconds(), ["next_weekly_refresh_time"] = (uint)nextWeekly.ToUnixTimeSeconds(),
            ["next_monthly_refresh_time"] = (uint)nextMonthly.ToUnixTimeSeconds()
        })));

        var today = GameAccountRepository.TodayKey;
        var dessert = new List<object>();
        foreach (var type in new uint[] { 11, 18 })
            dessert.Add(new Dictionary<string, object?> { ["type"] = type, ["is_got"] = await accounts.HasClaimAsync(uid, "dessert_" + type, today, ct) });
        frames.Add((12045, codec.Encode("sc_12045", new Dictionary<string, object?> { ["daily_fatigue_dessert_list"] = dessert.ToArray() })));

        var (_, _, bgm) = await accounts.GetBirthdayAndBgmAsync(uid, ct);
        frames.Add((12029, codec.Encode("sc_12029", new Dictionary<string, object?> { ["id"] = (uint)(bgm > 0 ? bgm : 4) })));
        frames.Add((12039, codec.Encode("sc_12039", new Dictionary<string, object?> { ["emoticon_id_list"] = Array.Empty<uint>(), ["unlocked_emoji_list"] = BaseEmoji })));
        frames.Add((12089, codec.Encode("sc_12089", new Dictionary<string, object?>
        {
            ["is_first_draw"] = 0u, ["is_first_draw_limited"] = 0u, ["is_first_draw_lucky"] = 0u, ["total_buy_fatigue_times"] = 0u
        })));
        frames.Add((24017, await Protocol.Handlers.ChapterChests.StateAsync(accounts, new SourceGameConfig(), codec, uid, ct)));
        // 梦境再构 weekly state (rotation, mode, progress, claimed chests) from the account.
        var bossState = await new Protocol.Handlers.BossChallenge(accounts, new SourceGameConfig()).LoadAsync(uid, ct);
        frames.Add((45201, codec.Encode("sc_45201", bossState.Overview)));
        frames.Add((45001, codec.Encode("sc_45001", bossState.Normal)));
        frames.Add((45101, codec.Encode("sc_45101", bossState.Advance)));
        // 黑区净化: semi-weekly rotation, normal-zone and 失序深区 progress.
        frames.AddRange(await new Protocol.Handlers.Mythic(accounts, new SourceGameConfig()).AllFramesAsync(codec, uid, ct));
        // Event combat modes: each mode's own progress frames (replace captured ones).
        foreach (var mode in modes?.All ?? [])
            frames.AddRange(await mode.LoginFramesAsync(codec, uid, ct));
        // Resident minigames: music, vehicle ball, Kadas race, activity point tiers.
        {
            var cfgSrc = new SourceGameConfig();
            frames.AddRange(await new Protocol.Handlers.MiniGames(accounts, cfgSrc, codec, new Protocol.Handlers.TaskSystem(accounts, cfgSrc, codec)).LoginFramesAsync(uid, ct));
        }
        // 诡谈夜话 (RogueCard) posts, battles and collections.
        frames.Add(await new Protocol.Handlers.RogueCard(accounts, new SourceGameConfig(), codec).LoginFrameAsync(uid, ct));
        // 薇儿丹蒂 daily life: entrusts, schedules, puzzles, barbecue.
        frames.AddRange(await new Protocol.Handlers.SpheroLife(accounts, new SourceGameConfig(), codec).LoginFramesAsync(uid, ct));
        // 刺桐 sandbox quests and unlocked functions.
        frames.AddRange(await new Protocol.Handlers.QuanzhouQuests(accounts, new SourceGameConfig(), codec).LoginFramesAsync(uid, ct));
        // 弥弥尔探索 (admin cat exploration).
        {
            var cfgSrc = new SourceGameConfig();
            frames.Add(await new Protocol.Handlers.Explore(accounts, cfgSrc, codec, new Protocol.Handlers.TaskSystem(accounts, cfgSrc, codec)).LoginFrameAsync(uid, ct));
        }
        // 游园街: dorm / canteen summary, fatigue, idol trainee overview, PVE stages, rank rewards and dance sequences.
        {
            var cfgSrc = new SourceGameConfig();
            frames.AddRange(await new Protocol.Handlers.BackHome(accounts, cfgSrc, codec, new Protocol.Handlers.TaskSystem(accounts, cfgSrc, codec)).LoginFramesAsync(uid, ct));
        }
        // 附魔战 / 装备战 state, activity unlock flags and read momotalks.
        frames.AddRange(await new Protocol.Handlers.SystemStates(accounts, new SourceGameConfig(), codec).LoginFramesAsync(uid, ct));
        // Achievements recomputed from the save.
        frames.Add(await new Protocol.Handlers.AchievementSystem(accounts, new SourceGameConfig(), codec).LoginFrameAsync(uid, ct));
        // Rhythm game stages and read stories.
        frames.Add(await new Protocol.Handlers.RhythmGame(accounts, codec).LoginFrameAsync(uid, ct));
        // Relation network stories and combo skills.
        frames.AddRange(await new Protocol.Handlers.RelationSystem(accounts, new SourceGameConfig(), codec).LoginFramesAsync(uid, ct));
        // Gacha pools with this player's pity counters and chosen UPs.
        frames.Add(await new Protocol.Handlers.GachaSystem(accounts, new SourceGameConfig(), codec).LoginFrameAsync(uid, ct));
        // Shops: dynamic skin shop configs (20007), this player's purchases and the daily shop roll (20009).
        frames.AddRange(await new Protocol.Handlers.ShopSystem(accounts, new SourceGameConfig(), codec).LoginFramesAsync(uid, ct));
        // AI chips (kernels, secondary, hero AI, support) and Mimir proposals.
        frames.Add(await new Protocol.Handlers.ChipSystem(accounts, new SourceGameConfig(), codec).LoginFrameAsync(uid, ct));
        // 誓约: oaths and their stage tasks.
        frames.AddRange(await new Protocol.Handlers.OathSystem(accounts, new SourceGameConfig(), codec).LoginFramesAsync(uid, ct));
        // 人机决斗王 (autochess PVE): cleared stages/medals, session state and board.
        frames.AddRange(await new Protocol.Handlers.AutoChess(accounts, new SourceGameConfig(), codec).LoginFramesAsync(uid, ct));
        // 多维变量: lobby/terminal/beacons/heroes and any active run (18001), plus an unsent end receipt (18005).
        {
            var poly = new Protocol.Handlers.Polyhedron(accounts, new SourceGameConfig());
            var meta = await poly.LoadAsync(uid, ct);
            await poly.SaveAsync(uid, meta, ct);
            frames.Add((18001, codec.Encode("sc_18001", poly.Snapshot(meta))));
        }
        // 因果观测: chapter groups/boxes (49001) and chapter open times (49023).
        {
            var chess = new Protocol.Handlers.WarChess(accounts, new SourceGameConfig());
            var chessSave = await chess.LoadAsync(uid, ct);
            frames.Add((49001, codec.Encode("sc_49001", chess.Overview(chessSave))));
            frames.Add((49023, codec.Encode("sc_49023", chess.OpenInfo())));
        }
        // 迭代校验·挑战关: running season's mode state (89013/89025/89431/89801) and badges (89021).
        frames.AddRange(await new Protocol.Handlers.CoreChallenge(accounts, new SourceGameConfig()).FramesAsync(codec, uid, null, ct));
        // 介质攫取: daily stage, rate, affixes, scores, claimed chests.
        frames.Add((35011, codec.Encode("sc_35011", await new Protocol.Handlers.EquipSeizure(accounts, new SourceGameConfig()).PayloadAsync(uid, ct))));
        // 迭代校验 regular mode: cycle, cleared stages, locks, affixes, claimed tasks.
        frames.Add((75009, codec.Encode("sc_75009", await new Protocol.Handlers.CoreVerification(accounts, new SourceGameConfig()).PayloadAsync(uid, ct))));
        frames.Add((23015, codec.Encode("sc_23015", new Dictionary<string, object?> { ["ip_location"] = "" })));
        // Mail brief and hero letters (due letters are delivered as mails first).
        frames.AddRange(await new Protocol.Handlers.MailSystem(accounts, new SourceGameConfig(), codec).LoginFramesAsync(uid, ct));
        frames.Add((56001, codec.Encode("sc_56001", new Dictionary<string, object?> { ["nothing"] = 0u, ["red_dot"] = Array.Empty<uint>(), ["client_finished_red_dot"] = Array.Empty<uint>() })));
        frames.Add(await new Protocol.Handlers.MomoTalkSystem(accounts, new SourceGameConfig(), codec).LoginFrameAsync(uid, ct));

        // Tasks / assignments: client AssignmentCfg with real progress, daily/weekly cycles and activity points.
        {
            var tasks = new Protocol.Handlers.TaskSystem(accounts, new SourceGameConfig(), codec);
            var taskState = await tasks.OnLoginAsync(uid, ct);
            frames.Add((28001, codec.Encode("sc_28001", tasks.ListPayload(taskState))));
            frames.Add((28019, codec.Encode("sc_28019", tasks.PointPayload(taskState))));
        }
        frames.Add((41001, codec.Encode("sc_41001", new Dictionary<string, object?>
        {
            ["info_list"] = lobby.Tower.Select(t => (object)new Dictionary<string, object?> { ["area"] = (uint)t.Area, ["stage"] = (uint)t.Stage }).ToArray()
        })));

        // Accumulated login (17027/17033), recharge (34007/34021), battle pass (34031) from saved claims.
        await accounts.TryClaimTodayAsync(uid, "login_day", ct);
        var accumulateAwards = (await accounts.GetClaimsAsync(uid, "accumulate_sign", ct)).Select(uint.Parse).ToArray();
        var loginDays = (uint)Math.Max(1, (await accounts.GetClaimsAsync(uid, "login_day", ct)).Count);
        frames.Add((17027, codec.Encode("sc_17027", new Dictionary<string, object?> { ["version"] = 2u, ["open_sign"] = false, ["login_days"] = loginDays, ["award_ids"] = accumulateAwards })));
        frames.Add((17033, codec.Encode("sc_17033", new Dictionary<string, object?> { ["version"] = 2u, ["cumulative_buy_card_num"] = 0u, ["cumulative_buy_battlepass_list"] = Array.Empty<uint>() })));
        frames.Add((34007, codec.Encode("sc_34007", new Dictionary<string, object?>
        {
            ["total_recharge_num"] = 0u, ["total_recharge_version"] = 2u, ["time_limit_recharge_num"] = 0u, ["now_time_limit_version"] = 2u,
            ["receive_total_recharge_list"] = (await accounts.GetClaimsAsync(uid, "recharge_34012", ct)).Select(uint.Parse).ToArray(),
            ["receive_time_limit_recharge_list"] = (await accounts.GetClaimsAsync(uid, "recharge_34118", ct)).Select(uint.Parse).ToArray()
        })));
        frames.Add((34021, codec.Encode("sc_34021", new Dictionary<string, object?> { ["first_recharge_id_list"] = Array.Empty<uint>() })));
        frames.Add((34031, codec.Encode("sc_34031", new Dictionary<string, object?>
        {
            ["battlepass_list_id"] = Protocol.Handlers.BattlePass.CurrentList, ["pay_level"] = 202u, ["is_start"] = 1u, ["weekly_gain_exp"] = 0u,
            ["start_timestamp"] = (uint)(now - 86400), ["end_timestamp"] = (uint)(now + 30 * 86400), ["next_refresh_timestamp"] = (uint)(now + 7 * 86400),
            ["receive_info"] = await Protocol.Handlers.BattlePass.ReceivedAsync(accounts, uid, ct)
        })));
        // No guild locally: empty club. 33001 (guild boss) is excluded: an empty one makes GuildAction index ClubBossCfg[0].
        frames.Add((31001, codec.Encode("sc_31001", defaults.Build("sc_31001"))));
        return frames;
    }

    /// <summary>sc_24009 cleared stages with stars, straight from the chapter table (empty for a new account).
    /// BattleStageData:CalcChapterStarCnt always sums stars[1..3] without nil checks, so star_list must have at least 3 entries.</summary>
    public static byte[] ChapterProgress(ProtoCodec codec, IReadOnlyList<LobbyChapter> chapters) => codec.Encode("sc_24009", new Dictionary<string, object?>
    {
        ["user_chapter_list"] = chapters.Select(chapter => (object?)new Dictionary<string, object?>
        {
            ["id"] = chapter.Id,
            ["star_list"] = Enumerable.Range(0, Math.Max(3, chapter.Stars.Count))
                .Select(i => (object?)(i < chapter.Stars.Count ? chapter.Stars[i] : 0))
                .ToArray(),
            ["clear_times"] = chapter.ClearTimes
        }).ToArray()
    });

    internal static Dictionary<string, object?> BuildHeroNetRec(
        HeroSnapshot hero,
        SourceGameConfig sourceConfig,
        IReadOnlyList<EquipSnapshot> heroEquips,
        IReadOnlyList<GameAccountRepository.HeroWarpSlot> warpSlots,
        IReadOnlyList<long> heroSkins,
        IReadOnlyList<int[]>? intensify = null,
        object? relation = null) => new()
    {
        ["hero_base_info"] = new Dictionary<string, object?>
        {
            ["id"] = hero.Id,
            ["level"] = hero.Level,
            ["star"] = hero.Star,
            // Cumulative exp from level 1 (Alpha-Persei hero_service: the 14007 packet carries the stored hero.exp).
            ["exp"] = hero.Experience,
            ["skill"] = (hero.Skills.Count > 0
                ? hero.Skills
                : sourceConfig.GetHeroSkills(hero.Id).Select(sid => new SkillLevel(sid, 1)))
                .Select(skill => (object?)new Dictionary<string, object?> { ["skill_id"] = skill.SkillId, ["skill_level"] = skill.Level }).ToArray(),
            ["weapon"] = new Dictionary<string, object?> { ["exp"] = hero.WeaponExperience, ["breakthrough"] = hero.WeaponBreakthrough, ["servant_uid"] = hero.ServantUid },
            ["using_astrolabe"] = hero.UsingAstrolabe.Cast<object?>().ToArray(),
            ["unlock_astrolabe"] = hero.UnlockedAstrolabe.Cast<object?>().ToArray(),
            ["using_skin"] = hero.Skin,
            ["break_level"] = hero.BreakLevel,
            ["exclusive_skill_list"] = warpSlots.Select(slot => (object)new Dictionary<string, object?>
            {
                ["slot_id"] = (uint)slot.SlotId,
                ["talent_points"] = (uint)slot.TalentPoints,
                ["skill_list"] = slot.Skills.Select(sk => (object)new Dictionary<string, object?>
                {
                    ["skill_id"] = sk.Id,
                    ["skill_level"] = sk.Level
                }).ToArray()
            }).ToArray(),
            ["weapon_module_level"] = hero.WeaponModuleLevel,
            ["skill_intensify_attribute_list"] = (intensify ?? []).Select(x => (object)new Dictionary<string, object?> { ["index"] = (uint)x[0], ["level"] = (uint)x[1] }).ToArray(),
            ["battle_using_skin"] = hero.BattleSkin
        },
        ["unlock"] = hero.Unlock,
        ["equip"] = Enumerable.Range(1, 6).Select(pos =>
        {
            var eqId = heroEquips.FirstOrDefault(eq => ((eq.PrefabId / 10000) % 10) == pos)?.EquipId ?? 0L;
            return (object)new Dictionary<string, object?>
            {
                ["pos"] = (uint)pos,
                ["equip_id"] = (uint)eqId
            };
        }).ToArray(),
        ["clear_times"] = hero.ClearTimes,
        ["clear_mission_list"] = Array.Empty<int>(),
        ["unlocked_skin"] = heroSkins.Append(hero.Skin).Distinct()
            .Select(sid => (object)new Dictionary<string, object?> { ["skin_id"] = (uint)sid, ["time"] = 0u })
            .ToArray(),
        ["weapon_module_assignment"] = hero.ModuleAssignment,
        ["trust"] = new Dictionary<string, object?>
        {
            ["level"] = hero.TrustLevel,
            ["exp"] = hero.TrustExperience,
            ["mood"] = hero.TrustMood,
            ["relation"] = relation ?? new Dictionary<string, object?> { ["tier_list"] = Array.Empty<object>() }
        }
    };

    internal static async Task<Dictionary<string, object?>?> BuildSingleHeroNetRecAsync(
        GameAccountRepository accounts,
        long uid,
        long heroId,
        CancellationToken cancellationToken)
    {
        var state = await accounts.GetInitialLoginStateAsync(uid, cancellationToken);
        var hero = state?.Heroes.FirstOrDefault(h => h.Id == heroId);
        if (hero is null) return null;

        var sourceConfig = new SourceGameConfig();
        var equips = (await accounts.GetEquipsAsync(uid, cancellationToken)).Where(e => e.HeroId == heroId).ToArray();
        var warpByHero = await accounts.GetHeroWarpAsync(uid, cancellationToken);
        IReadOnlyList<GameAccountRepository.HeroWarpSlot> warpSlots = warpByHero.TryGetValue(heroId, out var ws) ? ws : [];
        var skinHeroMap = sourceConfig.GetSkinHeroMap();
        var heroSkins = (await accounts.GetUnlockedSkinsAsync(uid, cancellationToken))
            .Where(s => skinHeroMap.TryGetValue(s, out var owner) && owner == heroId)
            .ToArray();
        var intensify = (await accounts.GetHeroIntensifyAsync(uid, cancellationToken)).GetValueOrDefault(heroId);
        var relation = Protocol.Handlers.RelationSystem.RelationRec(await accounts.GetJsonAsync<Protocol.Handlers.RelationSystem.State>(uid, "relation_v1", cancellationToken) ?? new(), heroId);
        return BuildHeroNetRec(hero, sourceConfig, equips, warpSlots, heroSkins, intensify, relation);
    }
}
