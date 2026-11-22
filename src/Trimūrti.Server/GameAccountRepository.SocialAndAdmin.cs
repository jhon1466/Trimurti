using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Trimūrti.Server;

public sealed record PlayerCardSnapshot(
    long Uid, string Nickname, int Level, string Sign, int Portrait, int IconFrame,
    int BoardHero, int HeroCount, int Likes, IReadOnlyList<int> ShowHeroes);

public sealed partial class GameAccountRepository
{
    public async Task<bool> UpdateSignatureAsync(long uid, string sign, CancellationToken cancellationToken = default)
    {
        if (uid <= 0) return false;
        var trimmed = (sign ?? "").Trim();
        if (trimmed.Length > 120) trimmed = trimmed[..120];
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE users SET sign=$sign WHERE uid=$uid";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$sign", trimmed);
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> UpdateShowcaseHeroesAsync(long uid, IReadOnlyList<long> heroIds, CancellationToken cancellationToken = default)
    {
        if (uid <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE game_user SET show_hero=$show, update_ts=$ts WHERE uid=$uid";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$show", JsonSerializer.Serialize(heroIds.Take(3)));
            command.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> UpdateBoardHeroAsync(long uid, int heroOrSkinId, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroOrSkinId <= 0) return false;
        var heroId = heroOrSkinId > 100000 ? heroOrSkinId / 100 : heroOrSkinId;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            if (heroOrSkinId > 100000)
            {
                await using var skinCmd = connection.CreateCommand();
                skinCmd.CommandText = "UPDATE hero SET using_skin=$skin, battle_using_skin=$skin, update_ts=$ts WHERE uid=$uid AND id=$id";
                skinCmd.Parameters.AddWithValue("$uid", uid);
                skinCmd.Parameters.AddWithValue("$id", heroId);
                skinCmd.Parameters.AddWithValue("$skin", heroOrSkinId);
                skinCmd.Parameters.AddWithValue("$ts", now);
                await skinCmd.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE game_user SET board_hero=$hero, update_ts=$ts WHERE uid=$uid";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$hero", heroId);
            command.Parameters.AddWithValue("$ts", now);
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> UpdateHomeSceneAsync(long uid, int sceneId, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || sceneId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE game_user SET cur_scene=$scene, cur_home_bg=$scene, update_ts=$ts WHERE uid=$uid";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$scene", sceneId);
            command.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> UpdatePortraitAsync(long uid, int iconId, bool isFrame, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || iconId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var u1 = connection.CreateCommand())
            {
                u1.Transaction = transaction;
                u1.CommandText = isFrame ? "UPDATE users SET icon_frame=$val WHERE uid=$uid" : "UPDATE users SET portrait=$val WHERE uid=$uid";
                u1.Parameters.AddWithValue("$uid", uid);
                u1.Parameters.AddWithValue("$val", iconId);
                if (await u1.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }
            }
            await using (var u2 = connection.CreateCommand())
            {
                u2.Transaction = transaction;
                u2.CommandText = isFrame ? "UPDATE game_user SET cur_icon_frame=$val, update_ts=$ts WHERE uid=$uid" : "UPDATE game_user SET cur_portrait=$val, update_ts=$ts WHERE uid=$uid";
                u2.Parameters.AddWithValue("$uid", uid);
                u2.Parameters.AddWithValue("$val", iconId);
                u2.Parameters.AddWithValue("$ts", now);
                await u2.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<PlayerCardSnapshot?> GetPlayerCardAsync(long targetUid, CancellationToken cancellationToken = default)
    {
        if (targetUid <= 0) return null;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT u.uid, COALESCE(u.nick,''), COALESCE(u.level,1), COALESCE(u.sign,''),
                   COALESCE(u.portrait,0), COALESCE(u.icon_frame,0), COALESCE(u.hero_num,1), COALESCE(u.likes,0),
                   COALESCE(g.board_hero,1084), COALESCE(g.show_hero,'[]')
            FROM users u
            LEFT JOIN game_user g ON g.uid = u.uid
            WHERE u.uid = $uid
            """;
        command.Parameters.AddWithValue("$uid", targetUid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new PlayerCardSnapshot(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            ParseIntList(reader.GetString(9)));
    }

    public async Task<bool> ClaimAchievementAsync(long uid, int achievementId, IReadOnlyList<(long ItemId, long Count)> rewards, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || achievementId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT COALESCE(complete_flag,0) FROM achievement WHERE uid=$uid AND achievement_id=$id";
                check.Parameters.AddWithValue("$uid", uid);
                check.Parameters.AddWithValue("$id", achievementId);
                var state = await check.ExecuteScalarAsync(cancellationToken);
                if (state is not null && state != DBNull.Value && Convert.ToInt32(state) >= 2)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return false;
                }
            }
            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO achievement(uid,achievement_id,progress,complete_flag,achieve_time) VALUES($uid,$id,1,2,$now)
                    ON CONFLICT(uid,achievement_id) DO UPDATE SET complete_flag=2, achieve_time=$now
                    """;
                upsert.Parameters.AddWithValue("$uid", uid);
                upsert.Parameters.AddWithValue("$id", achievementId);
                upsert.Parameters.AddWithValue("$now", now);
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var (itemId, count) in rewards)
            {
                if (itemId > 0 && count > 0)
                    await GrantInTxAsync(connection, transaction, uid, itemId, count, now, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<long> SendSystemMailAsync(long uid, string title, string content, string sender, IReadOnlyList<(long ItemId, long Count)> attachments, CancellationToken cancellationToken = default)
    {
        if (uid <= 0) return 0;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            long nextId;
            await using (var maxCmd = connection.CreateCommand())
            {
                maxCmd.Transaction = transaction;
                maxCmd.CommandText = "SELECT COALESCE(MAX(mail_id), 0) + 1 FROM mail WHERE uid=$uid";
                maxCmd.Parameters.AddWithValue("$uid", uid);
                nextId = Convert.ToInt64(await maxCmd.ExecuteScalarAsync(cancellationToken) ?? 1L);
            }
            var attachJson = JsonSerializer.Serialize(attachments.Select(a => new[] { a.ItemId, a.Count }));
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO mail(uid,mail_id,mail_type,template_id,title,sender,content_json,send_time,expire_time,read_flag,attach_flag,star_state,attachment_json,update_ts)
                    VALUES($uid,$mid,1,0,$title,$sender,$content,$now,$exp,0,$aflag,0,$attach,$now)
                    """;
                insert.Parameters.AddWithValue("$uid", uid);
                insert.Parameters.AddWithValue("$mid", nextId);
                insert.Parameters.AddWithValue("$title", title);
                insert.Parameters.AddWithValue("$sender", sender);
                insert.Parameters.AddWithValue("$content", content);
                insert.Parameters.AddWithValue("$now", now);
                insert.Parameters.AddWithValue("$exp", now + 30 * 86400);
                insert.Parameters.AddWithValue("$aflag", attachments.Count > 0 ? 0 : 1);
                insert.Parameters.AddWithValue("$attach", attachJson);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return nextId;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<int> UnlockAllHeroesAsync(long uid, IReadOnlyList<long> heroIds, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroIds.Count == 0) return 0;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var added = 0;
            var sourceConfig = new SourceGameConfig();
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (var heroId in heroIds.Distinct())
            {
                if (heroId <= 1001) continue;
                if (await HeroExistsAsync(connection, transaction, uid, heroId, cancellationToken)) continue;
                await InsertHeroAsync(connection, transaction, uid, heroId, sourceConfig.GetHeroSkills(heroId), 300, now, cancellationToken);
                added++;
            }
            await SyncHeroCountAsync(connection, transaction, uid, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return added;
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// Monthly card (恒定观测) daily claim, ported from Alpha-Persei paid_entitlement_service.claim_small:
    /// 90 × item 1 once per game day (05:00 cutoff). Returns false when already claimed today.
    /// </summary>
    public async Task<bool> ClaimMonthCardDailyAsync(long uid, CancellationToken cancellationToken = default)
    {
        if (uid <= 0) return false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var dayStart = GameClock.TodayStartUnix;
        await using (var connection = await OpenAsync(cancellationToken))
        {
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT COALESCE(is_sign,0), COALESCE(update_ts,0) FROM month_card WHERE uid=$uid";
            read.Parameters.AddWithValue("$uid", uid);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken) && reader.GetInt64(0) == 1 && reader.GetInt64(1) >= dayStart
                && await HasClaimAsync(uid, "monthcard_day", GameClock.Today.ToString("yyyy-MM-dd"), cancellationToken))
                return false;
        }
        await AddItemAsync(uid, 1, 90, cancellationToken);
        await using (var connection = await OpenAsync(cancellationToken))
        {
            await using var write = connection.CreateCommand();
            write.CommandText = """
                INSERT INTO month_card(uid,monthly_card_num,monthly_card_timestamp,is_sign,update_ts) VALUES($uid,1,0,1,$ts)
                ON CONFLICT(uid) DO UPDATE SET is_sign=1, update_ts=$ts
                """;
            write.Parameters.AddWithValue("$uid", uid);
            write.Parameters.AddWithValue("$ts", now);
            await write.ExecuteNonQueryAsync(cancellationToken);
        }
        return true;
    }

    /// <summary>Reads birthday and lobby BGM (0 = unset) for the login push.</summary>
    public async Task<(int Month, int Day, int Bgm)> GetBirthdayAndBgmAsync(long uid, CancellationToken cancellationToken = default)
    {
        if (uid <= 0) return (0, 0, 0);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(birth_month,0), COALESCE(birth_day,0), COALESCE(bgm_id,0) FROM game_user WHERE uid=$uid";
        command.Parameters.AddWithValue("$uid", uid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (0, 0, 0);
        int month = reader.GetInt32(0), day = reader.GetInt32(1);
        if (month is < 1 or > 12 || day is < 1 or > 31) (month, day) = (0, 0);
        return (month, day, reader.GetInt32(2));
    }

    public async Task<IReadOnlyList<long>> GetUnlockedSkinsAsync(long uid, CancellationToken cancellationToken = default)
    {
        if (uid <= 0) return [];
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT skin_id FROM player_skin_unlocked WHERE uid=$uid ORDER BY skin_id";
        command.Parameters.AddWithValue("$uid", uid);
        var skins = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            skins.Add(reader.GetInt64(0));
        return skins;
    }

    public async Task<int> UnlockAllSkinsAsync(long uid, IReadOnlyDictionary<long, long> skinHeroMap, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || skinHeroMap.Count == 0) return 0;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var added = 0;
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "INSERT OR IGNORE INTO player_skin_unlocked(uid,skin_id,unlock_ts,update_ts) VALUES($uid,$sid,$now,$now)";
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$now", now);
            var sidParam = cmd.Parameters.Add("$sid", SqliteType.Integer);
            foreach (var skinId in skinHeroMap.Keys.Distinct())
            {
                if (skinId <= 0) continue;
                sidParam.Value = skinId;
                added += await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return added;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<int> GrantAllItemsAsync(long uid, IReadOnlyList<long> itemIds, long count, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || itemIds.Count == 0 || count <= 0) return 0;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var currencyIds = new HashSet<long>();
            await using var connection = await OpenAsync(cancellationToken);
            await using (var catCmd = connection.CreateCommand())
            {
                catCmd.CommandText = "SELECT id FROM item_catalog WHERE LOWER(category)='currency'";
                try
                {
                    await using var reader = await catCmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        currencyIds.Add(reader.GetInt64(0));
                }
                catch (SqliteException) { }
            }

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var curCmd = connection.CreateCommand();
            curCmd.Transaction = transaction;
            curCmd.CommandText = """
                INSERT INTO currency(uid,id,num,update_ts) VALUES($uid,$id,$num,$now)
                ON CONFLICT(uid,id) DO UPDATE SET num=MAX(num,$num), update_ts=$now
                """;
            curCmd.Parameters.AddWithValue("$uid", uid);
            curCmd.Parameters.AddWithValue("$num", count);
            curCmd.Parameters.AddWithValue("$now", now);
            var curIdParam = curCmd.Parameters.Add("$id", SqliteType.Integer);

            await using var matCmd = connection.CreateCommand();
            matCmd.Transaction = transaction;
            matCmd.CommandText = """
                INSERT INTO material(uid,id,num,update_ts) VALUES($uid,$id,$num,$now)
                ON CONFLICT(uid,id) DO UPDATE SET num=MAX(num,$num), update_ts=$now
                """;
            matCmd.Parameters.AddWithValue("$uid", uid);
            matCmd.Parameters.AddWithValue("$num", count);
            matCmd.Parameters.AddWithValue("$now", now);
            var matIdParam = matCmd.Parameters.Add("$id", SqliteType.Integer);

            var granted = 0;
            foreach (var itemId in itemIds.Distinct())
            {
                if (itemId <= 0) continue;
                var isCurrency = SharedConfig.IsCurrencyItem(itemId) || currencyIds.Contains(itemId);
                if (isCurrency)
                {
                    curIdParam.Value = itemId;
                    await curCmd.ExecuteNonQueryAsync(cancellationToken);
                }
                else
                {
                    matIdParam.Value = itemId;
                    await matCmd.ExecuteNonQueryAsync(cancellationToken);
                }
                granted++;
            }

            var cfg = new SourceGameConfig();
            await using (var pieceCmd = connection.CreateCommand())
            {
                pieceCmd.Transaction = transaction;
                pieceCmd.CommandText = """
                    INSERT INTO hero_piece(uid,hero_id,num) VALUES($uid,$hid,$num)
                    ON CONFLICT(uid,hero_id) DO UPDATE SET num=MAX(num,$num)
                    """;
                pieceCmd.Parameters.AddWithValue("$uid", uid);
                pieceCmd.Parameters.AddWithValue("$num", (int)Math.Min(count, 9999));
                var hidParam = pieceCmd.Parameters.Add("$hid", SqliteType.Integer);
                foreach (var heroId in cfg.GetAllHeroIds())
                {
                    hidParam.Value = heroId;
                    await pieceCmd.ExecuteNonQueryAsync(cancellationToken);
                    granted++;
                }
            }

            var equipPrefabs = cfg.GetAllFiveStarEquipPrefabs();
            if (equipPrefabs.Count > 0)
            {
                await using var eqCmd = connection.CreateCommand();
                eqCmd.Transaction = transaction;
                eqCmd.CommandText = """
                    INSERT INTO equip(uid,id,prefab_id,exp,hero_id,is_lock,now_break_level,enchant_slots,race,race_preview,update_ts)
                    VALUES($uid,$id,$prefab,0,0,0,0,'[]',0,0,$now)
                    ON CONFLICT(uid,id) DO UPDATE SET prefab_id=excluded.prefab_id, update_ts=$now
                    """;
                eqCmd.Parameters.AddWithValue("$uid", uid);
                eqCmd.Parameters.AddWithValue("$now", now);
                var idParam = eqCmd.Parameters.Add("$id", SqliteType.Integer);
                var prefabParam = eqCmd.Parameters.Add("$prefab", SqliteType.Integer);
                for (var i = 0; i < equipPrefabs.Count; i++)
                {
                    idParam.Value = 1000 + i + 1;
                    prefabParam.Value = equipPrefabs[i];
                    await eqCmd.ExecuteNonQueryAsync(cancellationToken);
                    granted++;
                }
            }

            var servantIds = cfg.GetAllWeaponServantIds();
            if (servantIds.Count > 0)
            {
                await using var svCmd = connection.CreateCommand();
                svCmd.Transaction = transaction;
                svCmd.CommandText = """
                    INSERT INTO servant(uid,id,prefab_id,stage,is_locked,update_ts)
                    VALUES($uid,$id,$prefab,5,0,$now)
                    ON CONFLICT(uid,id) DO UPDATE SET prefab_id=excluded.prefab_id, stage=MAX(stage,5), update_ts=$now
                    """;
                svCmd.Parameters.AddWithValue("$uid", uid);
                svCmd.Parameters.AddWithValue("$now", now);
                var idParam = svCmd.Parameters.Add("$id", SqliteType.Integer);
                var prefabParam = svCmd.Parameters.Add("$prefab", SqliteType.Integer);
                for (var i = 0; i < servantIds.Count; i++)
                {
                    idParam.Value = 1000 + i + 1;
                    prefabParam.Value = servantIds[i];
                    await svCmd.ExecuteNonQueryAsync(cancellationToken);
                    granted++;
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return granted;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<IReadOnlyList<(long Uid, string Account, string Nickname, int Level, int HeroCount)>> ListAccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT u.uid, COALESCE(u.account,''), COALESCE(u.nick,''), COALESCE(u.level,1), COALESCE(u.hero_num,1)
            FROM users u
            ORDER BY CASE WHEN u.account='local-player' THEN 0 ELSE 1 END, u.hero_num DESC, u.uid ASC
            """;
        var list = new List<(long, string, string, int, int)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4)));
        return list;
    }

    public async Task<bool> UpdateAccountAdminAsync(long uid, string? nickname, int? level, CancellationToken cancellationToken = default)
    {
        if (uid <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            var sets = new List<string>();
            if (!string.IsNullOrWhiteSpace(nickname))
            {
                sets.Add("nick=$nick, is_changed_nick=1");
                cmd.Parameters.AddWithValue("$nick", nickname.Trim());
            }
            if (level is int lvl and >= 1 and <= 100)
            {
                var exp = lvl >= 100 ? 10_000_000L : (long)lvl * 100_000L;
                sets.Add("level=$lvl, exp=$exp");
                cmd.Parameters.AddWithValue("$lvl", lvl);
                cmd.Parameters.AddWithValue("$exp", exp);
            }
            if (sets.Count == 0) return false;
            cmd.CommandText = $"UPDATE users SET {string.Join(", ", sets)} WHERE uid=$uid";
            cmd.Parameters.AddWithValue("$uid", uid);
            return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> DeleteAccountAsync(long uid, CancellationToken cancellationToken = default)
    {
        if (uid <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var tables = new List<string>();
            await using (var listCmd = connection.CreateCommand())
            {
                listCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
                await using var reader = await listCmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    tables.Add(reader.GetString(0));
            }

            var uidTables = new List<string>();
            foreach (var table in tables)
            {
                await using var pragma = connection.CreateCommand();
                pragma.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
                await using var r = await pragma.ExecuteReaderAsync(cancellationToken);
                while (await r.ReadAsync(cancellationToken))
                {
                    if (string.Equals(r.GetString(1), "uid", StringComparison.OrdinalIgnoreCase))
                    {
                        uidTables.Add(table);
                        break;
                    }
                }
            }

            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var affected = 0;
            foreach (var table in uidTables)
            {
                await using var del = connection.CreateCommand();
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM \"{table.Replace("\"", "\"\"")}\" WHERE uid=$uid";
                del.Parameters.AddWithValue("$uid", uid);
                affected += await del.ExecuteNonQueryAsync(cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);
            return affected > 0;
        }
        finally { _writeGate.Release(); }
    }
}
