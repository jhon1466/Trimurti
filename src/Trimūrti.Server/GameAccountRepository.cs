using Microsoft.Data.Sqlite;

namespace Trimūrti.Server;

public sealed record GameProfile(long Uid, string Account, string Nickname, int Level, long Experience, int PlotProgress, long RegisteredAt);
public sealed record ItemStack(long Id, long Count, string Source, long UpdatedAt);
public sealed record SkillLevel(int SkillId, int Level);
public sealed record HeroSnapshot(long Id, int Level, int Star, long Experience, IReadOnlyList<SkillLevel> Skills,
    int WeaponExperience, int WeaponBreakthrough, int ServantUid, IReadOnlyList<int> UsingAstrolabe,
    IReadOnlyList<int> UnlockedAstrolabe, int Skin, int BreakLevel, int WeaponModuleLevel,
    int BattleSkin, int Unlock, int ClearTimes, int ModuleAssignment, int TrustLevel, int TrustExperience, int TrustMood);
public sealed record NewbieSnapshot(long CompletedAt, int SignCount, long LastSignAt, long TriggerAt, int MaxPhase, int Version);
public sealed record InitialLoginState(GameProfile Profile, IReadOnlyList<HeroSnapshot> Heroes, IReadOnlyList<ItemStack> Inventory, NewbieSnapshot Newbie);
public sealed record DailySignResult(bool Success, bool AlreadySigned, long ItemId, long ItemCount, int SignCount, IReadOnlyList<(long ItemId, long Count)>? Rewards = null);
public sealed record LobbyTask(long Id, int Progress, int CompleteFlag, long ExpiresAt);
public sealed record LobbyChapter(long Id, IReadOnlyList<int> Stars, int ClearTimes);
public sealed record LobbyTower(long Area, long Stage);
public sealed record LobbySnapshot(IReadOnlyList<LobbyTask> Tasks, IReadOnlyList<LobbyChapter> Chapters,
    IReadOnlyList<LobbyTower> Tower, IReadOnlyList<int> MainGuides, IReadOnlyList<int> WeakGuides,
    int UnreadMailCount, int MailCount, string Sign = "", int BoardHero = 1084, int CurScene = 6000,
    int CurPortrait = 2110841, int CurIconFrame = 2001, int CurCardBg = 8001, int CurChatBubble = 9001,
    int CurStickerBg = 4002, IReadOnlyList<int>? ShowHeroes = null);

/// <summary>SQLite account and inventory primitives used by subsequently ported game operations.</summary>
public sealed partial class GameAccountRepository
{
    private readonly string _path;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private volatile bool _pragmasApplied;

    public GameAccountRepository()
    {
        _path = Environment.GetEnvironmentVariable("TRIMURTI_DB_PATH") ?? Path.Combine(AppContext.BaseDirectory, "Data", "trimurti.db");
    }

    public async Task<GameProfile?> GetProfileAsync(long uid, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT uid, COALESCE(account,''), COALESCE(nick,''), COALESCE(level,1), COALESCE(exp,0), COALESCE(plot_progress,0), COALESCE(register_ts,0) FROM users WHERE uid=$uid";
        command.Parameters.AddWithValue("$uid", uid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new GameProfile(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt64(4), reader.GetInt32(5), reader.GetInt64(6))
            : null;
    }

    public async Task<IReadOnlyList<ItemStack>> GetInventoryAsync(long uid, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var items = new Dictionary<long, ItemStack>();
        foreach (var (table, source) in new[] { ("currency", "currency"), ("material", "material") })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT id, num, COALESCE(update_ts,0) FROM {table} WHERE uid=$uid";
            command.Parameters.AddWithValue("$uid", uid);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                var item = new ItemStack(id, reader.GetInt64(1), source, reader.GetInt64(2));
                if (!items.TryGetValue(id, out var old) || item.UpdatedAt > old.UpdatedAt ||
                    item.UpdatedAt == old.UpdatedAt && source == "currency") items[id] = item;
            }
        }
        return items.Values.OrderBy(x => x.Id).ToArray();
    }

    public async Task<InitialLoginState?> GetInitialLoginStateAsync(long uid, CancellationToken cancellationToken = default)
    {
        var profile = await GetProfileAsync(uid, cancellationToken);
        if (profile is null) return null;
        var heroes = new List<HeroSnapshot>();
        NewbieSnapshot? newbie = null;
        await using (var connection = await OpenAsync(cancellationToken))
        {
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id,level,star,exp,skill_list,weapon_exp,weapon_break,weapon_servant_uid,using_astrolabe,unlock_astrolabe,using_skin,break_level,weapon_module_level,battle_using_skin,unlock,clear_times,module_assignment,trust_level,trust_exp,trust_mood FROM hero WHERE uid=$uid ORDER BY id";
                command.Parameters.AddWithValue("$uid", uid);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    heroes.Add(new HeroSnapshot(reader.GetInt64(0), I(reader, 1), I(reader, 2), L(reader, 3),
                        ParseSkillPairs(reader.IsDBNull(4) ? "[]" : reader.GetString(4)), I(reader, 5), I(reader, 6), I(reader, 7),
                        ParseIntList(reader.IsDBNull(8) ? "[]" : reader.GetString(8)), ParseIntList(reader.IsDBNull(9) ? "[]" : reader.GetString(9)),
                        I(reader, 10), I(reader, 11), I(reader, 12), I(reader, 13), I(reader, 14), I(reader, 15), I(reader, 16), I(reader, 17), I(reader, 18), I(reader, 19)));
                }
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT completed_time,now_sign_times,last_sign_ts,trigger_time,max_phase,version_id FROM newbie_activity WHERE uid=$uid";
                command.Parameters.AddWithValue("$uid", uid);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken)) newbie = new NewbieSnapshot(L(reader, 0), I(reader, 1), L(reader, 2), L(reader, 3), I(reader, 4), I(reader, 5));
            }
        }
        if (heroes.Count == 0 || newbie is null) return null;
        return new InitialLoginState(profile, heroes, await GetInventoryAsync(uid, cancellationToken), newbie);
    }

    public async Task<LobbySnapshot> GetLobbySnapshotAsync(long uid, CancellationToken cancellationToken = default)
    {
        var tasks = new List<LobbyTask>();
        var chapters = new List<LobbyChapter>();
        var tower = new List<LobbyTower>();
        var mainGuides = new List<int>();
        var weakGuides = new List<int>();
        var unread = 0;
        var mailCount = 0;
        await using var connection = await OpenAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT task_id,progress,complete_flag,expired_ts FROM task WHERE uid=$uid ORDER BY task_id";
            command.Parameters.AddWithValue("$uid", uid);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                tasks.Add(new LobbyTask(L(reader, 0), I(reader, 1), I(reader, 2), L(reader, 3)));
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,star_list,clear_times FROM chapter WHERE uid=$uid ORDER BY id";
            command.Parameters.AddWithValue("$uid", uid);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                chapters.Add(new LobbyChapter(L(reader, 0), ParseIntList(reader.IsDBNull(1) ? "[]" : reader.GetString(1)), I(reader, 2)));
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT area,stage FROM tower WHERE uid=$uid ORDER BY area";
            command.Parameters.AddWithValue("$uid", uid);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var area = L(reader, 0);
                var stage = L(reader, 1);
                if (area < 1000) { area = 4010100 + area; stage = 4010000 + stage; }
                tower.Add(new LobbyTower(area, stage));
            }
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT guide_id,kind FROM guide WHERE uid=$uid ORDER BY guide_id";
            command.Parameters.AddWithValue("$uid", uid);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                (string.Equals(reader.IsDBNull(1) ? "main" : reader.GetString(1), "weak", StringComparison.OrdinalIgnoreCase) ? weakGuides : mainGuides).Add(I(reader, 0));
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*),COALESCE(SUM(CASE WHEN read_flag=0 THEN 1 ELSE 0 END),0) FROM mail WHERE uid=$uid";
            command.Parameters.AddWithValue("$uid", uid);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) { mailCount = reader.GetInt32(0); unread = reader.GetInt32(1); }
        }
        var sign = "";
        var boardHero = 1084;
        var curScene = 6000;
        var curPortrait = 2110841;
        var curIconFrame = 2001;
        var curCardBg = 8001;
        var curChatBubble = 9001;
        var curStickerBg = 4002;
        IReadOnlyList<int> showHeroes = [];
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COALESCE(u.sign,''), COALESCE(g.board_hero,1084), COALESCE(g.cur_scene,6000), COALESCE(g.cur_portrait,2110841), COALESCE(g.cur_icon_frame,2001), COALESCE(g.cur_card_bg,8001), COALESCE(g.cur_bubble,9001), COALESCE(g.cur_sticker_bg,4002), COALESCE(g.show_hero,'[]') FROM users u LEFT JOIN game_user g ON g.uid=u.uid WHERE u.uid=$uid";
            command.Parameters.AddWithValue("$uid", uid);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                sign = reader.GetString(0);
                if (I(reader, 1) > 0) boardHero = I(reader, 1);
                if (I(reader, 2) > 0) curScene = I(reader, 2);
                if (I(reader, 3) > 0) curPortrait = I(reader, 3);
                if (I(reader, 4) > 0) curIconFrame = I(reader, 4);
                if (I(reader, 5) > 0) curCardBg = I(reader, 5);
                if (I(reader, 6) > 0) curChatBubble = I(reader, 6);
                if (I(reader, 7) > 0) curStickerBg = I(reader, 7);
                showHeroes = ParseIntList(reader.GetString(8));
            }
        }
        return new LobbySnapshot(tasks, chapters, tower, mainGuides, weakGuides, unread, mailCount, sign, boardHero, curScene, curPortrait, curIconFrame, curCardBg, curChatBubble, curStickerBg, showHeroes);
    }

    private static int I(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? 0 : reader.GetInt32(index);
    private static long L(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? 0 : reader.GetInt64(index);
    private static IReadOnlyList<int> ParseIntList(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<int[][]>(json)?.SelectMany(x => x.Take(1)).ToArray() ?? []; }
        catch { try { return System.Text.Json.JsonSerializer.Deserialize<int[]>(json) ?? []; } catch { return []; } }
    }

    private static IReadOnlyList<SkillLevel> ParseSkillPairs(string json)
    {
        try
        {
            var pairs = System.Text.Json.JsonSerializer.Deserialize<int[][]>(json);
            if (pairs is not null)
                return pairs.Where(p => p.Length >= 1).Select(p => new SkillLevel(p[0], p.Length >= 2 ? Math.Max(1, p[1]) : 1)).ToArray();
        }
        catch { }
        try
        {
            return (System.Text.Json.JsonSerializer.Deserialize<int[]>(json) ?? [])
                .Select(id => new SkillLevel(id, 1)).ToArray();
        }
        catch { return []; }
    }

    public async Task<long> AddItemAsync(long uid, long itemId, long delta, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || itemId <= 0) throw new ArgumentOutOfRangeException(nameof(uid), "Account and item IDs must be positive.");
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var isCurrency = await IsCurrencyAsync(itemId, cancellationToken);
            var target = isCurrency ? "currency" : "material";
            var other = isCurrency ? "material" : "currency";
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = $"""
                    INSERT INTO {target}(uid,id,num,update_ts) VALUES($uid,$id,MAX(0,$delta),$ts)
                    ON CONFLICT(uid,id) DO UPDATE SET num=MAX(0,{target}.num+$delta), update_ts=$ts
                    """;
                update.Parameters.AddWithValue("$uid", uid);
                update.Parameters.AddWithValue("$id", itemId);
                update.Parameters.AddWithValue("$delta", delta);
                update.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {other} WHERE uid=$uid AND id=$id";
                delete.Parameters.AddWithValue("$uid", uid);
                delete.Parameters.AddWithValue("$id", itemId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return await GetItemCountAsync(uid, itemId, cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    private static readonly (long ItemId, long Count)[] MonthlySignRewards =
    [
        (2, 10000), (40103, 5), (2, 15000), (40203, 5), (20002, 1), (40803, 5), (1, 100),
        (2, 15000), (40503, 5), (40103, 6), (2, 20000), (40203, 6), (20002, 1), (1, 100),
        (40803, 6), (2, 20000), (40603, 5), (40103, 8), (2, 25000), (20002, 2), (1, 150),
        (40203, 8), (2, 25000), (40803, 8), (40503, 8), (2, 30000), (20002, 2), (1, 150),
        (40603, 8), (1, 200), (1, 200)
    ];

    /// <summary>Claims daily activity 3 using the source server's 05:00 game-day boundary.</summary>
    public async Task<DailySignResult> ClaimDailySignAsync(long uid, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var gameDay = GameClock.GameDay(now);
            var year = gameDay.Year;
            var month = gameDay.Month;
            var day = gameDay.Day;
            var nowTs = now.ToUnixTimeSeconds();
            var signCount = 0;
            var signList = new List<int>();
            var signedToday = false;

            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT year,month,sign_list,sign_count FROM sign WHERE uid=$uid AND activity_id=3";
                read.Parameters.AddWithValue("$uid", uid);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var storedYear = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                    var storedMonth = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                    signCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                    if (storedYear == year && storedMonth == month)
                    {
                        try { signList = System.Text.Json.JsonSerializer.Deserialize<List<int>>(reader.IsDBNull(2) ? "[]" : reader.GetString(2)) ?? []; }
                        catch (System.Text.Json.JsonException) { signList = []; }
                        signedToday = signList.Contains(day);
                    }
                    else signCount = 0;
                }
            }

            if (signedToday)
            {
                await transaction.CommitAsync(cancellationToken);
                return new DailySignResult(true, true, 0, 0, signCount);
            }

            long itemId;
            long itemCount;
            await using (var reward = connection.CreateCommand())
            {
                reward.Transaction = transaction;
                reward.CommandText = "SELECT reward_id,reward_num FROM sign_reward_cfg WHERE month=$month AND day=$day UNION ALL SELECT reward_id,reward_num FROM sign_reward_cfg WHERE month=0 AND day=$day LIMIT 1";
                reward.Parameters.AddWithValue("$month", month);
                reward.Parameters.AddWithValue("$day", signCount + 1);
                await using var reader = await reward.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    var idx = Math.Clamp(signCount, 0, MonthlySignRewards.Length - 1);
                    (itemId, itemCount) = MonthlySignRewards[idx];
                }
                else
                {
                    if (reader.IsDBNull(0) || reader.GetInt64(0) <= 0 || reader.IsDBNull(1) || reader.GetInt64(1) <= 0)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new DailySignResult(false, false, 0, 0, signCount);
                    }
                    itemId = reader.GetInt64(0);
                    itemCount = reader.GetInt64(1);
                }
            }

            var category = "";
            await using (var lookup = connection.CreateCommand())
            {
                lookup.Transaction = transaction;
                lookup.CommandText = "SELECT category FROM item_catalog WHERE id=$id";
                lookup.Parameters.AddWithValue("$id", itemId);
                category = Convert.ToString(await lookup.ExecuteScalarAsync(cancellationToken)) ?? "";
            }
            var targetTable = itemId < 100 || itemId is 53029 or 53111 || string.Equals(category, "currency", StringComparison.OrdinalIgnoreCase)
                ? "currency" : "material";
            await using (var grant = connection.CreateCommand())
            {
                grant.Transaction = transaction;
                grant.CommandText = $"INSERT INTO {targetTable}(uid,id,num,update_ts) VALUES($uid,$id,$num,$ts) ON CONFLICT(uid,id) DO UPDATE SET num={targetTable}.num+$num,update_ts=$ts";
                grant.Parameters.AddWithValue("$uid", uid);
                grant.Parameters.AddWithValue("$id", itemId);
                grant.Parameters.AddWithValue("$num", itemCount);
                grant.Parameters.AddWithValue("$ts", nowTs);
                await grant.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var clearOther = connection.CreateCommand())
            {
                clearOther.Transaction = transaction;
                clearOther.CommandText = $"DELETE FROM {(targetTable == "currency" ? "material" : "currency")} WHERE uid=$uid AND id=$id";
                clearOther.Parameters.AddWithValue("$uid", uid);
                clearOther.Parameters.AddWithValue("$id", itemId);
                await clearOther.ExecuteNonQueryAsync(cancellationToken);
            }

            signCount++;
            signList.Add(day);
            await using (var save = connection.CreateCommand())
            {
                save.Transaction = transaction;
                save.CommandText = "INSERT INTO sign(uid,activity_id,year,month,day,sign_list,sign_count,last_sign_ts) VALUES($uid,3,$year,$month,$day,$list,$count,$ts) ON CONFLICT(uid,activity_id) DO UPDATE SET year=excluded.year,month=excluded.month,day=excluded.day,sign_list=excluded.sign_list,sign_count=excluded.sign_count,last_sign_ts=excluded.last_sign_ts";
                save.Parameters.AddWithValue("$uid", uid);
                save.Parameters.AddWithValue("$year", year);
                save.Parameters.AddWithValue("$month", month);
                save.Parameters.AddWithValue("$day", day);
                save.Parameters.AddWithValue("$list", System.Text.Json.JsonSerializer.Serialize(signList));
                save.Parameters.AddWithValue("$count", signCount);
                save.Parameters.AddWithValue("$ts", nowTs);
                await save.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return new DailySignResult(true, false, itemId, itemCount, signCount);
        }
        finally { _writeGate.Release(); }
    }

    public async Task<DailySignResult> ClaimCumulativeSignAsync(long uid, int activityId, SourceGameConfig config, CancellationToken cancellationToken = default)
    {
        if (activityId <= 0) return new DailySignResult(false, false, 0, 0, 0);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var nowTs = now.ToUnixTimeSeconds();
            var gameDay = GameClock.GameDay(now);
            var (year, month, day) = (gameDay.Year, gameDay.Month, gameDay.Day);
            var count = 0;
            var lastSignAt = 0L;
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT sign_count,last_sign_ts FROM sign WHERE uid=$uid AND activity_id=$activity";
                read.Parameters.AddWithValue("$uid", uid);
                read.Parameters.AddWithValue("$activity", activityId);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    count = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                    lastSignAt = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                }
            }

            if (lastSignAt > 0 && GameClock.GameDay(DateTimeOffset.FromUnixTimeSeconds(lastSignAt)) == gameDay.Date)
            {
                await transaction.CommitAsync(cancellationToken);
                return new DailySignResult(true, true, 0, 0, count);
            }

            var rewards = config.GetSevenDaySignRewards(activityId, count + 1);
            if (rewards.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new DailySignResult(false, false, 0, 0, count);
            }
            foreach (var (itemId, itemCount) in rewards)
            {
                await using var categoryCommand = connection.CreateCommand();
                categoryCommand.Transaction = transaction;
                categoryCommand.CommandText = "SELECT category FROM item_catalog WHERE id=$id";
                categoryCommand.Parameters.AddWithValue("$id", itemId);
                var category = Convert.ToString(await categoryCommand.ExecuteScalarAsync(cancellationToken)) ?? "";
                var table = itemId < 100 || itemId is 53029 or 53111 || string.Equals(category, "currency", StringComparison.OrdinalIgnoreCase) ? "currency" : "material";
                await using (var grant = connection.CreateCommand())
                {
                    grant.Transaction = transaction;
                    grant.CommandText = $"INSERT INTO {table}(uid,id,num,update_ts) VALUES($uid,$id,$num,$ts) ON CONFLICT(uid,id) DO UPDATE SET num={table}.num+$num,update_ts=$ts";
                    grant.Parameters.AddWithValue("$uid", uid);
                    grant.Parameters.AddWithValue("$id", itemId);
                    grant.Parameters.AddWithValue("$num", itemCount);
                    grant.Parameters.AddWithValue("$ts", nowTs);
                    await grant.ExecuteNonQueryAsync(cancellationToken);
                }
                await using (var clearOther = connection.CreateCommand())
                {
                    clearOther.Transaction = transaction;
                    clearOther.CommandText = $"DELETE FROM {(table == "currency" ? "material" : "currency")} WHERE uid=$uid AND id=$id";
                    clearOther.Parameters.AddWithValue("$uid", uid);
                    clearOther.Parameters.AddWithValue("$id", itemId);
                    await clearOther.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            count++;
            await using (var save = connection.CreateCommand())
            {
                save.Transaction = transaction;
                save.CommandText = "INSERT INTO sign(uid,activity_id,year,month,day,sign_list,sign_count,last_sign_ts) VALUES($uid,$activity,$year,$month,$day,'[]',$count,$ts) ON CONFLICT(uid,activity_id) DO UPDATE SET year=excluded.year,month=excluded.month,day=excluded.day,sign_count=excluded.sign_count,last_sign_ts=excluded.last_sign_ts";
                save.Parameters.AddWithValue("$uid", uid);
                save.Parameters.AddWithValue("$activity", activityId);
                save.Parameters.AddWithValue("$year", year);
                save.Parameters.AddWithValue("$month", month);
                save.Parameters.AddWithValue("$day", day);
                save.Parameters.AddWithValue("$count", count);
                save.Parameters.AddWithValue("$ts", nowTs);
                await save.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            var primaryReward = rewards[0];
            return new DailySignResult(true, false, primaryReward.ItemId, primaryReward.Count, count, rewards);
        }
        finally { _writeGate.Release(); }
    }

    public async Task<long> GetItemCountAsync(long uid, long itemId, CancellationToken cancellationToken = default)
    {
        if (ItemKinds.IsPiece(itemId))
        {
            await using var pc = await OpenAsync(cancellationToken);
            return await PiecesInTxAsync(pc, null, uid, itemId - 10000, cancellationToken);
        }
        var table = await IsCurrencyAsync(itemId, cancellationToken) ? "currency" : "material";
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COALESCE(num,0) FROM {table} WHERE uid=$uid AND id=$id";
        command.Parameters.AddWithValue("$uid", uid);
        command.Parameters.AddWithValue("$id", itemId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static readonly SourceGameConfig SharedConfig = new();

    private async Task<bool> IsCurrencyAsync(long itemId, CancellationToken cancellationToken)
    {
        if (itemId < 100 || itemId is 53029 or 53111 || SharedConfig.IsCurrencyItem(itemId)) return true;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT category FROM item_catalog WHERE id=$id LIMIT 1";
        command.Parameters.AddWithValue("$id", itemId);
        try { return string.Equals(Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)), "currency", StringComparison.OrdinalIgnoreCase); }
        catch (SqliteException) { return false; }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString());
        await connection.OpenAsync(cancellationToken);
        if (!_pragmasApplied)
        {
            _pragmasApplied = true;
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }
        return connection;
    }
}
