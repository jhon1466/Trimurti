using System.Collections.Concurrent;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>Tracks active battle sessions per account and battle ID.</summary>
public sealed class BattleSessionStore
{
    private long _nextBattleId = 1000;
    private readonly ConcurrentDictionary<ulong, (long AccountId, uint StageId, uint BattleTimes)> _byBattleId = new();
    private readonly ConcurrentDictionary<ulong, bool> _story = new();
    private readonly ConcurrentDictionary<ulong, IReadOnlyList<long>> _heroes = new();

    public void SetHeroes(ulong battleId, IReadOnlyList<long> heroes) => _heroes[battleId] = heroes;
    private readonly ConcurrentDictionary<ulong, (uint Type, uint Index)> _meta = new();
    public void SetMeta(ulong battleId, uint type, uint index) => _meta[battleId] = (type, index);
    public (uint Type, uint Index) TakeMeta(ulong battleId) => _meta.TryRemove(battleId, out var m) ? m : (0u, 0u);
    private readonly ConcurrentDictionary<ulong, uint> _activity = new();
    public void SetActivity(ulong battleId, uint activity) => _activity[battleId] = activity;
    public uint TakeActivity(ulong battleId) => _activity.TryRemove(battleId, out var a) ? a : 0u;
    public IReadOnlyList<long> TakeHeroes(ulong battleId) => _heroes.TryRemove(battleId, out var h) ? h : [];

    /// <summary>Story/quick battles (54034) have no UDP combat report and count as completed.</summary>
    public void MarkStory(ulong battleId) => _story[battleId] = true;
    public bool IsStory(ulong battleId) => _story.TryRemove(battleId, out var s) && s;
    private readonly ConcurrentDictionary<long, ulong> _latestByAccount = new();

    public ulong Start(long accountId, uint stageId, uint battleTimes = 1)
    {
        var battleId = (ulong)Interlocked.Increment(ref _nextBattleId);
        _byBattleId[battleId] = (accountId, stageId, Math.Max(1u, battleTimes));
        _latestByAccount[accountId] = battleId;
        return battleId;
    }

    public bool TryTake(long accountId, ulong battleId, out uint stageId, out uint battleTimes)
    {
        if (battleId > 0 && _byBattleId.TryRemove(battleId, out var entry) && entry.AccountId == accountId)
        {
            stageId = entry.StageId;
            battleTimes = entry.BattleTimes;
            return true;
        }
        if (_latestByAccount.TryRemove(accountId, out var lastId) && _byBattleId.TryRemove(lastId, out var lastEntry))
        {
            stageId = lastEntry.StageId;
            battleTimes = lastEntry.BattleTimes;
            return true;
        }
        stageId = 0;
        battleTimes = 1;
        return false;
    }
}

/// <summary>Starts a stage battle (54030 -> 54031).</summary>
public sealed class BattleStartHandler(BattleSessionStore battles, GameAccountRepository accounts, TrialHeroes trials, ProtoCodec codec, ILogger<BattleStartHandler> logger) : IGameCommandHandler
{
    public uint Command => 54030;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        uint stageId = 0;
        uint times = 1;
        var heroIds = new List<long>();
        if (request.Body.TryGetValue("common_info", out var raw) && raw is IReadOnlyDictionary<string, object?> info)
        {
            stageId = HandlerHelpers.ReadUInt(info, "dest");
            times = HandlerHelpers.ReadUInt(info, "battle_times", 1);
            if (info.TryGetValue("hero_list", out var list) && list is System.Collections.IEnumerable heroes)
                foreach (var h in heroes)
                {
                    if (h is not IReadOnlyDictionary<string, object?> hd) continue;
                    // Official captures: hero_list[].hero_base_info is a bare varint hero id (the schema says a nested
                    // hero_base_net_rec); accept both, plus hero_id / id.
                    long id = 0;
                    if (hd.TryGetValue("hero_base_info", out var hb))
                        id = hb is IReadOnlyDictionary<string, object?> hbd ? HandlerHelpers.ReadUInt(hbd, "id", HandlerHelpers.ReadUInt(hbd, "hero_id")) : Convert.ToInt64(hb ?? 0);
                    if (id == 0) id = HandlerHelpers.ReadUInt(hd, "hero_id", HandlerHelpers.ReadUInt(hd, "id"));
                    if (id > 0) heroIds.Add(id);
                }
        }
        if (stageId == 0) return [HandlerHelpers.ResultOnly(codec, request, 54031, 2)];
        if (heroIds.Count == 0)
            logger.LogWarning("54030 without team heroes: body={Body}", System.Text.Json.JsonSerializer.Serialize(request.Body));
        var battleId = battles.Start(request.AccountId, stageId, times);
        battles.SetHeroes(battleId, heroIds);
        if (request.Body.TryGetValue("common_info", out var ci) && ci is IReadOnlyDictionary<string, object?> cinfo)
        {
            battles.SetMeta(battleId, HandlerHelpers.ReadUInt(cinfo, "type"), HandlerHelpers.ReadUInt(cinfo, "index"));
            battles.SetActivity(battleId, HandlerHelpers.ReadUInt(cinfo, "activity_id"));
        }
        logger.LogInformation("Battle start id={Battle} stage={Stage} heroes={Heroes}", battleId, stageId, string.Join(",", heroIds));
        var srv = (ushort)(request.ServerIndex + 1);
        return
        [
            new GameResponse(54031, request.Index, srv, codec.Encode("sc_54031", new Dictionary<string, object?> { ["result"] = 0u })),
            new GameResponse(54003, 0, srv, codec.Encode("sc_54003", await BattlePayload.BuildPlayerInfoAsync(accounts, trials, request.AccountId, heroIds, cancellationToken))),
            new GameResponse(54007, 0, srv, BattlePayload.BattleStart(codec, battleId))
        ];
    }
}

/// <summary>
/// Battle initialization data (port of Alpha-Persei battle_payload.py): sc_54003 player_net_rec with each team hero's
/// full hero_base_info, sigils and trust from the account, and sc_54007 pointing the client at the UDP battle server.
/// </summary>
public static class BattlePayload
{
    public static byte[] BattleStart(ProtoCodec codec, ulong battleId) => codec.Encode("sc_54007", new Dictionary<string, object?>
    {
        ["battle_start"] = new Dictionary<string, object?>
        {
            ["result"] = 0u, ["battle_id"] = battleId, ["battle_server_ip"] = "127.0.0.1",
            ["battle_server_port"] = BattleUdpServer.Port.ToString()
        }
    });

    public static async Task<Dictionary<string, object?>> BuildPlayerInfoAsync(GameAccountRepository accounts, TrialHeroes trials, long uid, IReadOnlyList<long> heroIds, CancellationToken ct)
    {
        var profile = await accounts.GetProfileAsync(uid, ct);
        var equips = await accounts.GetEquipsAsync(uid, ct);
        var heroes = new List<object>();
        foreach (var heroId in heroIds)
        {
            var rec = await LoginBootstrap.BuildSingleHeroNetRecAsync(accounts, uid, heroId, ct);
            if (rec is null)
            {
                // Not an account hero: a story/tutorial trial template (HeroStandardSystemCfg id).
                if (trials.BuildBattleHero(heroId) is { } trial) heroes.Add(trial);
                continue;
            }
            heroes.Add(new Dictionary<string, object?>
            {
                ["hero_base_info"] = rec["hero_base_info"],
                ["hero_type"] = 1u,
                ["equip_list"] = equips.Where(e => e.HeroId == heroId).Select(e => (object)new Dictionary<string, object?>
                {
                    ["equip_id"] = (uint)e.EquipId, ["prefab_id"] = (uint)e.PrefabId, ["exp"] = (uint)e.Exp, ["hero_id"] = (uint)heroId,
                    ["is_lock"] = e.IsLocked, ["now_break_level"] = (uint)e.BreakLevel, ["race"] = (uint)e.Race,
                    // The client schema requires both enchant slot records even when empty.
                    ["enchant_slot_list"] = new object[]
                    {
                        new Dictionary<string, object?> { ["id"] = 1u, ["effect_list"] = Array.Empty<object>() },
                        new Dictionary<string, object?> { ["id"] = 2u, ["effect_list"] = Array.Empty<object>() }
                    }
                }).ToArray(),
                ["dorm_level"] = 0u,
                ["trust"] = rec.TryGetValue("trust", out var trust) ? trust : null,
                ["main_damage_type"] = 0u
            });
        }
        var card = await accounts.GetPlayerCardAsync(uid, ct);
        return new Dictionary<string, object?>
        {
            ["player_info"] = new Dictionary<string, object?>
            {
                ["player_id"] = (ulong)uid,
                ["player_battle_info"] = new Dictionary<string, object?>
                {
                    ["channel"] = 0u, ["server"] = 0u, ["hero_list"] = heroes.ToArray(),
                    ["nick"] = profile?.Nickname ?? "", ["level"] = (uint)(profile?.Level ?? 1),
                    ["icon"] = (uint)(card?.Portrait ?? 0), ["frame"] = (uint)(card?.IconFrame ?? 0)
                },
                ["player_room_info"] = new Dictionary<string, object?> { ["is_master"] = 0u, ["is_ready"] = 1u, ["be_liked_uids"] = Array.Empty<ulong>() }
            }
        };
    }
}

/// <summary>Starts a story/quick battle with battle_id (54034 -> 54035).</summary>
public sealed class BattleQuickStartHandler(BattleSessionStore battles, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 54034;

    public ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var stageId = HandlerHelpers.ReadUInt(request.Body, "stage_id");
        if (stageId == 0) return ValueTask.FromResult<IReadOnlyList<GameResponse>>([HandlerHelpers.ResultOnly(codec, request, 54035, 2)]);
        var battleId = battles.Start(request.AccountId, stageId, 1);
        battles.MarkStory(battleId);
        return ValueTask.FromResult<IReadOnlyList<GameResponse>>([
            HandlerHelpers.Reply(codec, request, 54035, new Dictionary<string, object?>
            {
                ["result"] = 0u,
                ["battle_id"] = battleId
            })
        ]);
    }
}

/// <summary>Settles a normal battle (54032 -> 54033).</summary>
public sealed class BattleSettleHandler(BattleSessionStore battles, BattleResultStore reports, GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec, BossChallenge boss, Mythic mythic, CoreVerification core, EquipSeizure seizure, CoreChallenge challenge, WarChess chess, Polyhedron poly, StageModes modes) : IGameCommandHandler
{
    public uint Command => 54032;

    public Task<IReadOnlyList<GameResponse>> SettleAsync(GameRequest request, uint responseCommand, CancellationToken cancellationToken)
        => SettleCoreAsync(battles, reports, accounts, config, codec, boss, mythic, core, seizure, challenge, chess, poly, modes, request, responseCommand, cancellationToken);

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken) =>
        await SettleCoreAsync(battles, reports, accounts, config, codec, boss, mythic, core, seizure, challenge, chess, poly, modes, request, 54033, cancellationToken);

    internal static async Task<IReadOnlyList<GameResponse>> SettleCoreAsync(
        BattleSessionStore battles, BattleResultStore reports, GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec,
        BossChallenge boss, Mythic mythic, CoreVerification core, EquipSeizure seizure, CoreChallenge challenge, WarChess chess, Polyhedron poly, StageModes modes, GameRequest request, uint responseCommand, CancellationToken cancellationToken)
    {
        var battleId = HandlerHelpers.ReadULong(request.Body, "battle_id");
        var story = battles.IsStory(battleId);
        if (!battles.TryTake(request.AccountId, battleId, out var stageId, out var times))
            return [HandlerHelpers.ResultOnly(codec, request, responseCommand, 2)];

        // Real outcome from the UDP battle report (opcode 132): 1 win, 2 defeat, 3 quit.
        // No report: a story battle has no combat and counts as completed; a combat battle was abandoned.
        var report = reports.Take(battleId);
        var outcome = report?.Result is > 0 ? (uint)report.Result : story ? 1u : 3u;
        var heroes = battles.TakeHeroes(battleId);
        var (stageType, bossIndex) = battles.TakeMeta(battleId);
        var activityId = battles.TakeActivity(battleId);
        // Event modes (ash, osiris, skuld, pub, hodur, pinball, sphero, quanzhou, board, rogue, autochess …) settle their own
        // stages: only the mode's rewards, never ordinary stage drops/EXP/fatigue.
        if (modes.Find(stageType, stageId) is { } mode)
        {
            List<GameResponse> modeFrames;
            IReadOnlyList<(long, long)> modeDrops;
            try { (modeFrames, modeDrops) = await mode.SettleAsync(new ModeBattle(request, battleId, stageId, stageType, activityId, outcome, heroes, report?.Stats ?? BattleStats.Empty), cancellationToken); }
            catch (ModeError) { (modeFrames, modeDrops) = ([], []); }
            return [.. modeFrames, HandlerHelpers.Reply(codec, request, responseCommand, new Dictionary<string, object?>
            {
                ["result"] = 0u,
                ["battle_result"] = new Dictionary<string, object?>
                {
                    ["battle_id"] = battleId, ["result"] = outcome, ["dest"] = stageId,
                    ["timestamp"] = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ["clear_times"] = outcome == 1 ? 1u : 0u, ["target_times"] = times,
                    ["star_list"] = Array.Empty<object>(), ["use_seconds"] = (uint)((report?.BattleTimeMs ?? 0) / 1000),
                    ["all_drop_list"] = modeDrops.Count == 0 ? Array.Empty<object>() : new object[]
                    {
                        new Dictionary<string, object?> { ["battle_times"] = 1u, ["gain_list"] = InventorySync.Rewards(modeDrops), ["extra_list"] = Array.Empty<object>() }
                    },
                    ["hero_id_list"] = heroes.Select(h => (uint)h).ToArray()
                }
            })];
        }

        // 多维变量 scenes grant no ordinary stage rewards: a won fight only advances the run node.
        var polyStage = poly.IsStage(stageType, stageId);
        if (outcome != 1 || polyStage)
        {
            // 介质攫取 scores every run, won or not (Alpha-Persei settles seizure before the win check).
            var lossPushes = new List<GameResponse>();
            if (EquipSeizure.IsStage(stageType, stageId))
                lossPushes.Add(await seizure.SettleAsync(codec, request, report?.Stats ?? BattleStats.Empty, false, heroes, cancellationToken));
            // 因果观测: a lost battle keeps its tile (new attempt allowed).
            if (chess.IsStage(stageType, stageId))
                lossPushes.AddRange(await chess.SettleAsync(codec, request, stageId, false, heroes, cancellationToken));
            if (polyStage)
                lossPushes.AddRange(await poly.SettleBattleAsync(codec, request, outcome == 1, report?.Stats ?? BattleStats.Empty, cancellationToken));
            return [.. lossPushes, HandlerHelpers.Reply(codec, request, responseCommand, new Dictionary<string, object?>
            {
                ["result"] = 0u,
                ["battle_result"] = new Dictionary<string, object?>
                {
                    ["battle_id"] = battleId, ["result"] = outcome, ["dest"] = stageId,
                    ["timestamp"] = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ["clear_times"] = outcome == 1 ? 1u : 0u, ["target_times"] = times,
                    ["star_list"] = Array.Empty<object>(), ["use_seconds"] = (uint)((report?.BattleTimeMs ?? 0) / 1000),
                    ["all_drop_list"] = Array.Empty<object>(), ["hero_id_list"] = Array.Empty<uint>()
                }
            })];
        }

        var (pushes, battleResult) = await StageSettleFlow.WinAsync(accounts, config, codec, request, battleId, stageId, times, heroes,
            report?.Stats ?? BattleStats.Empty, sweep: false, cancellationToken);

        // 黑区净化: normal-zone stars/partitions (44009) or 失序深区 team relay (44023).
        pushes.AddRange(await MythicSettlement.OnWinAsync(mythic, codec, request, stageType, stageId, report?.Stats ?? BattleStats.Empty, battleResult, cancellationToken));

        if (EquipSeizure.IsStage(stageType, stageId))
            pushes.Add(await seizure.SettleAsync(codec, request, report?.Stats ?? BattleStats.Empty, true, heroes, cancellationToken));

        // 因果观测: a won battle marks its tile and continues the tile's event queue.
        if (chess.IsStage(stageType, stageId))
            pushes.AddRange(await chess.SettleAsync(codec, request, stageId, true, heroes, cancellationToken));

        // 迭代校验·挑战关 (modes 1–4): record the clear/score/team and push the mode state + badges.
        if (CoreChallenge.IsStage(stageType, stageId) && challenge.StageCfg(stageId) is { } clInfo)
        {
            await challenge.RecordAsync(request.AccountId, stageId, report?.Stats.BattleTimeMs ?? 0, heroes, cancellationToken);
            pushes.AddRange(await challenge.PushAsync(codec, request, clInfo.Activity, cancellationToken));
        }

        // 迭代校验 (regular): record the stage clear / score stage and push 75009.
        if (CoreVerification.IsStage(stageType, stageId))
        {
            await core.RecordClearAsync(request.AccountId, stageId, report?.Stats.BattleTimeMs ?? 0, heroes, cancellationToken);
            pushes.Add(new GameResponse(75009, 0, (ushort)(request.ServerIndex + 1), codec.Encode("sc_75009", await core.PayloadAsync(request.AccountId, cancellationToken))));
        }

        // 梦境再构: record the boss clear (normal: stage stars; advanced: score of the chosen difficulty/affixes) and push
        // its refreshed state (45001+45003 / 45101), as Alpha-Persei _settle_frames does.
        var bossKind = BossChallenge.Kind(stageType, stageId);
        if (bossKind != 0)
        {
            var state = await boss.LoadAsync(request.AccountId, cancellationToken);
            var bossId = boss.Resolve(bossKind, stageId, bossIndex, state);
            if (bossId != 0 && state.Mode == bossKind)
            {
                var srv = (ushort)(request.ServerIndex + 1);
                if (bossKind == 1)
                {
                    var stars = ((object[])battleResult["star_list"]!).Cast<Dictionary<string, object?>>().Select(x => (int)(uint)x["is_achieve"]!).ToArray();
                    await accounts.RecordBossNormalClearAsync(request.AccountId, bossId, stageId, stars, heroes, cancellationToken);
                    var after = await boss.LoadAsync(request.AccountId, cancellationToken);
                    pushes.Add(new GameResponse(45001, 0, srv, codec.Encode("sc_45001", after.Normal)));
                    pushes.Add(new GameResponse(45003, 0, srv, codec.Encode("sc_45003", new Dictionary<string, object?> { ["use_times"] = after.Normal["use_times"] })));
                }
                else
                {
                    var b = ((object[])state.Advance["boss_list"]!).Cast<Dictionary<string, object?>>().First(x => (uint)x["id"]! == bossId);
                    var score = boss.AdvanceScore(state.AdvanceId, (int)(uint)b["diffculty_index"]!,
                        ((uint[])b["time_index_list"]!).Select(x => (long)x), ((uint[])b["affix_index_list"]!).Select(x => (long)x));
                    await accounts.RecordBossAdvanceClearAsync(request.AccountId, bossId, score, heroes, cancellationToken);
                    var after = await boss.LoadAsync(request.AccountId, cancellationToken);
                    pushes.Add(new GameResponse(45101, 0, srv, codec.Encode("sc_45101", after.Advance)));
                }
            }
        }
        return [.. pushes, HandlerHelpers.Reply(codec, request, responseCommand, new Dictionary<string, object?> { ["result"] = 0u, ["battle_result"] = battleResult })];
    }
}

/// <summary>
/// Win settlement shared by battles (54032/54110) and sweeps (54038), port of Alpha-Persei _settle_frames: drops per
/// run from DropCfg, first-clear vs repeat rewards, fatigue cost, three-star evaluation from the battle report,
/// player/hero EXP; then the inventory, chapter-progress pushes and the battle_result_net_rec.
/// </summary>
public static class StageSettleFlow
{
    public static async Task<(List<GameResponse> Pushes, Dictionary<string, object?> BattleResult)> WinAsync(
        GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec, GameRequest request, ulong battleId,
        uint stageId, uint times, IReadOnlyList<long> heroes, BattleStats stats, bool sweep, CancellationToken cancellationToken)
    {
        var rules = new StageSettlement(config);
        var starResults = rules.EvaluateStars(stageId, true, stats, heroes);
        var previous = (await accounts.GetLobbySnapshotAsync(request.AccountId, cancellationToken)).Chapters.FirstOrDefault(c => c.Id == stageId);
        var firstClear = !sweep && (previous is null || previous.ClearTimes == 0);
        var runs = new List<RunDrops>();
        for (var t = 0; t < times; t++) runs.Add(rules.RollDrops(stageId, firstClear && t == 0, Random.Shared));
        var grants = runs.SelectMany(r => r.Gain.Concat(r.Extra)).GroupBy(x => x.ItemId).Select(g => (g.Key, g.Sum(x => x.Count))).ToArray();
        var fatigue = rules.StaminaCost(stageId, firstClear || sweep) * (int)times;
        var grantExp = rules.GrantsExp(stageId, firstClear);
        var userExp = grantExp ? (long)rules.UserExp(stageId, rules.StaminaCost(stageId, true)) * times : 0;
        var heroExp = grantExp ? (long)rules.HeroExp(stageId) * times : 0;
        // Sweeps never advance stage progress (Alpha-Persei: `if dest and not sweep`).
        await accounts.SettleStageAsync(request.AccountId, stageId, (int)times, starResults.Select(r => r.Achieved).ToArray(),
            grants, fatigue, userExp, heroExp, heroes.Where(h => h is >= 1000 and < 10000).ToArray(), rules.LevelForExp, !sweep, cancellationToken);

        var lobby = await accounts.GetLobbySnapshotAsync(request.AccountId, cancellationToken);
        var pushes = await InventorySync.FramesAsync(accounts, codec, request, grants.Select(d => d.Item1).Append(4).Append(12), cancellationToken);
        if (stageId < 2000000 && !sweep)
            pushes.Add(new GameResponse(24009, 0, (ushort)(request.ServerIndex + 1), LoginBootstrap.ChapterProgress(codec, lobby.Chapters)));
        // Tasks: stage clear / clear count / fatigue spent (task_listener.on_stage_pass, on_stamina_cost).
        if (await new TaskSystem(accounts, config, codec).StagePassAsync(request.AccountId, (ushort)(request.ServerIndex + 1), stageId, (int)times, heroes, fatigue, cancellationToken) is { } taskDiff)
            pushes.Add(taskDiff);
        // 连携奥义 progress: wins fought by both combo heroes.
        if (!sweep && await new RelationSystem(accounts, config, codec).BattleWinAsync(request.AccountId, (ushort)(request.ServerIndex + 1), heroes.ToHashSet(), (int)times, cancellationToken) is { } comboPush)
            pushes.Add(comboPush);
        // Combat & stage-clear achievements (100001..100008, 1010, 120024..120029 + state reconcile).
        if (!sweep && await new AchievementSystem(accounts, config, codec).BattleWinAsync(request.AccountId, (ushort)(request.ServerIndex + 1), stageId, (int)times, heroes, stats, cancellationToken) is { } achDiff)
            pushes.Add(achDiff);
        var battleResult = new Dictionary<string, object?>
        {
            ["battle_id"] = battleId == 0 ? 1UL : battleId,
            ["result"] = 1u,
            ["dest"] = stageId,
            ["timestamp"] = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            ["clear_times"] = times,
            ["target_times"] = times,
            ["star_list"] = starResults.Select(r => (object)new Dictionary<string, object?>
            {
                ["star_id"] = (uint)r.StarId, ["now_progress"] = (uint)r.Now, ["need_progress"] = (uint)r.Need, ["is_achieve"] = r.Achieved ? 1u : 0u
            }).ToArray(),
            ["use_seconds"] = (uint)Math.Max(0, stats.BattleTimeMs / 1000),
            ["all_drop_list"] = runs.Select((r, i) => (object)new Dictionary<string, object?>
            {
                ["battle_times"] = (uint)(i + 1), ["gain_list"] = InventorySync.Rewards(r.Gain), ["extra_list"] = InventorySync.Rewards(r.Extra)
            }).ToArray(),
            ["hero_id_list"] = heroes.Select(h => (uint)h).ToArray()
        };
        // 装备战: guarantee counter per difficulty (BattleEquipAction.CaculateGuaranteeNum), shown again at login in 43001.
        await SystemStates.RecordEquipRunsAsync(accounts, config, request.AccountId, stageId,
            runs.Select(r => r.Gain.Concat(r.Extra).Select(x => x.ItemId)), cancellationToken);
        return (pushes, battleResult);
    }

    /// <summary>Reads common_info (dest, battle_times, team) from 54030/54038; hero ids accept the official bare-varint form.</summary>
    public static (uint Stage, uint Times, List<long> Heroes) ParseCommon(IReadOnlyDictionary<string, object?> body)
    {
        uint stage = 0, times = 1;
        var heroIds = new List<long>();
        if (body.TryGetValue("common_info", out var raw) && raw is IReadOnlyDictionary<string, object?> info)
        {
            stage = HandlerHelpers.ReadUInt(info, "dest");
            times = Math.Max(1u, HandlerHelpers.ReadUInt(info, "battle_times", 1));
            if (info.TryGetValue("hero_list", out var list) && list is System.Collections.IEnumerable heroes)
                foreach (var h in heroes)
                {
                    if (h is not IReadOnlyDictionary<string, object?> hd) continue;
                    long id = 0;
                    if (hd.TryGetValue("hero_base_info", out var hb))
                        id = hb is IReadOnlyDictionary<string, object?> hbd ? HandlerHelpers.ReadUInt(hbd, "id", HandlerHelpers.ReadUInt(hbd, "hero_id")) : Convert.ToInt64(hb ?? 0);
                    if (id == 0) id = HandlerHelpers.ReadUInt(hd, "hero_id", HandlerHelpers.ReadUInt(hd, "id"));
                    if (id > 0) heroIds.Add(id);
                }
        }
        return (stage, times, heroIds);
    }
}

/// <summary>Sweep (54038 -> 54039): only already-cleared stages; same drops/fatigue/EXP as a won battle, no progress.</summary>
public sealed class SweepHandler(GameAccountRepository accounts, SourceGameConfig config, TrialHeroes trials, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 54038;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var (stage, times, heroes) = StageSettleFlow.ParseCommon(request.Body);
        var cleared = (await accounts.GetLobbySnapshotAsync(request.AccountId, cancellationToken)).Chapters.Any(c => c.Id == stage && c.ClearTimes > 0);
        if (stage == 0 || !cleared) return [HandlerHelpers.ResultOnly(codec, request, 54039, 2)];
        var (pushes, battleResult) = await StageSettleFlow.WinAsync(accounts, config, codec, request, 0, stage, times, heroes, BattleStats.Empty, sweep: true, cancellationToken);
        var playerInfo = await BattlePayload.BuildPlayerInfoAsync(accounts, trials, request.AccountId, heroes, cancellationToken);
        return [.. pushes, HandlerHelpers.Reply(codec, request, 54039, new Dictionary<string, object?>
        {
            ["result"] = 0u, ["player_info"] = playerInfo["player_info"], ["battle_result"] = battleResult
        })];
    }
}

/// <summary>Settles an activity/story battle (54110 -> 54111).</summary>
public sealed class BattleActivitySettleHandler(BattleSessionStore battles, BattleResultStore reports, GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec, BossChallenge boss, Mythic mythic, CoreVerification core, EquipSeizure seizure, CoreChallenge challenge, WarChess chess, Polyhedron poly, StageModes modes) : IGameCommandHandler
{
    public uint Command => 54110;

    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken) =>
        await BattleSettleHandler.SettleCoreAsync(battles, reports, accounts, config, codec, boss, mythic, core, seizure, challenge, chess, poly, modes, request, 54111, cancellationToken);
}

/// <summary>Claims chapter star/treasure rewards (24014 -> 24015).</summary>
public sealed class ChapterTreasureHandler(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec) : IGameCommandHandler
{
    public uint Command => 24014;

    /// <summary>Port of Alpha-Persei chapter_star_reward_service.claim_and_prepare: rewards from
    /// chapter_star_rewards.json, requires the chapter's earned stars (sum of its stages' stars) to reach star_need.</summary>
    public async ValueTask<IReadOnlyList<GameResponse>> HandleAsync(GameRequest request, CancellationToken cancellationToken)
    {
        var chapterId = HandlerHelpers.ReadUInt(request.Body, "id");
        var order = (int)HandlerHelpers.ReadUInt(request.Body, "treasure_id", 1);
        var cfg = config.Get("chapter_star_rewards.json");
        if (cfg is not { } root || !root.TryGetProperty(chapterId.ToString(), out var row) || order is < 1 or > 3)
            return [HandlerHelpers.ResultOnly(codec, request, 24015, 2)];
        var need = row.GetProperty("star_need").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        if (need.Length < order) return [HandlerHelpers.ResultOnly(codec, request, 24015, 2)];
        var stages = row.GetProperty("section_id_list").EnumerateArray().Select(x => x.GetInt64()).ToHashSet();
        var lobby = await accounts.GetLobbySnapshotAsync(request.AccountId, cancellationToken);
        var stars = lobby.Chapters.Where(c => stages.Contains(c.Id)).Sum(c => c.Stars.Count(s => s == 1));
        if (stars < need[order - 1]) return [HandlerHelpers.ResultOnly(codec, request, 24015, 2)];
        var rewards = InventorySync.Pairs(row.GetProperty(new[] { "first_reward", "second_reward", "third_reward" }[order - 1]));
        if (!await accounts.ClaimChapterRewardAsync(request.AccountId, chapterId, order, rewards, cancellationToken))
            return [HandlerHelpers.ResultOnly(codec, request, 24015, 2)];
        // Same frames as the original: currencies/materials, claimed chest state (24017), then the acknowledgement.
        var frames = await InventorySync.FramesAsync(accounts, codec, request, rewards.Select(r => r.Item1), cancellationToken);
        frames.Add(new GameResponse(24017, 0, (ushort)(request.ServerIndex + 1), await ChapterChests.StateAsync(accounts, config, codec, request.AccountId, cancellationToken)));
        frames.Add(HandlerHelpers.Reply(codec, request, 24015, new Dictionary<string, object?>
        {
            ["result"] = 0u, ["reward_list"] = InventorySync.Rewards(rewards)
        }));
        return frames;
    }
}

/// <summary>sc_24017: per chapter, which of the three star chests are received (chapter_star_reward table).</summary>
public static class ChapterChests
{
    public static async Task<byte[]> StateAsync(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec, long uid, CancellationToken ct)
    {
        var claimed = (await accounts.GetChapterRewardClaimsAsync(uid, ct)).ToHashSet();
        var gain = new List<object>();
        if (config.Get("chapter_star_rewards.json") is { } root)
            foreach (var chapter in root.EnumerateObject())
            {
                if (!long.TryParse(chapter.Name, out var id)) continue;
                gain.Add(new Dictionary<string, object?>
                {
                    ["id"] = (uint)id,
                    ["reward_list"] = Enumerable.Range(1, 3).Select(o => (object)new Dictionary<string, object?>
                    {
                        ["reward_order"] = (uint)o, ["is_received"] = claimed.Contains((id, o)) ? 1u : 0u
                    }).ToArray()
                });
            }
        return codec.Encode("sc_24017", new Dictionary<string, object?> { ["gain_list"] = gain.ToArray() });
    }
}
