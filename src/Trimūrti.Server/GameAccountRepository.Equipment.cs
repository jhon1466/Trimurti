using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Trimūrti.Server;

public sealed record EquipSnapshot(long EquipId, int PrefabId, int Exp, long HeroId, bool IsLocked, int BreakLevel, int Race, int RacePreview = 0);
public sealed record ServantSnapshot(long Uid, int Id, int Stage, int IsLocked);

public sealed partial class GameAccountRepository
{
    public async Task<IReadOnlyList<EquipSnapshot>> GetEquipsAsync(long uid, CancellationToken cancellationToken = default)
    {
        var list = new List<EquipSnapshot>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, COALESCE(prefab_id,0), COALESCE(exp,0), COALESCE(hero_id,0), COALESCE(is_lock,0), COALESCE(now_break_level,0), COALESCE(race,0), COALESCE(race_preview,0) FROM equip WHERE uid=$uid ORDER BY id";
        command.Parameters.AddWithValue("$uid", uid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(new EquipSnapshot(reader.GetInt64(0), I(reader, 1), I(reader, 2), L(reader, 3), I(reader, 4) != 0, I(reader, 5), I(reader, 6), I(reader, 7)));
        return list;
    }

    public async Task<IReadOnlyList<ServantSnapshot>> GetServantsAsync(long uid, CancellationToken cancellationToken = default)
    {
        var list = new List<ServantSnapshot>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, COALESCE(prefab_id, id), COALESCE(stage,1), COALESCE(is_locked,0) FROM servant WHERE uid=$uid ORDER BY id";
        command.Parameters.AddWithValue("$uid", uid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(new ServantSnapshot(reader.GetInt64(0), I(reader, 1), Math.Max(1, I(reader, 2)), I(reader, 3)));
        return list;
    }

    public async Task<bool> EquipServantAsync(long uid, long heroId, long servantUid, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE hero SET weapon_servant_uid=$sid, update_ts=$ts WHERE uid=$uid AND id=$hid";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$hid", heroId);
            command.Parameters.AddWithValue("$sid", servantUid);
            command.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> LockServantAsync(long uid, long servantUid, bool locked, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || servantUid <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE servant SET is_locked=$lock, update_ts=$ts WHERE uid=$uid AND id=$id";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$id", servantUid);
            command.Parameters.AddWithValue("$lock", locked ? 1 : 0);
            command.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> RefineServantAsync(long uid, long servantUid, long costServantUid, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || servantUid <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            if (costServantUid > 0 && costServantUid != servantUid)
            {
                await using var del = connection.CreateCommand();
                del.Transaction = transaction;
                del.CommandText = "DELETE FROM servant WHERE uid=$uid AND id=$cid AND COALESCE(is_locked,0)=0";
                del.Parameters.AddWithValue("$uid", uid);
                del.Parameters.AddWithValue("$cid", costServantUid);
                if (await del.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE servant SET stage=MIN(5, COALESCE(stage,1)+1), update_ts=$ts WHERE uid=$uid AND id=$id";
                update.Parameters.AddWithValue("$uid", uid);
                update.Parameters.AddWithValue("$id", servantUid);
                update.Parameters.AddWithValue("$ts", now);
                if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> UpgradeServantAsync(long uid, long heroId, IReadOnlyList<(long ItemId, long Count)> materials, IReadOnlyList<long> consumeServantUids, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            long servantUid = 0;
            await using (var readHero = connection.CreateCommand())
            {
                readHero.Transaction = transaction;
                readHero.CommandText = "SELECT COALESCE(weapon_servant_uid, 0) FROM hero WHERE uid=$uid AND id=$hid";
                readHero.Parameters.AddWithValue("$uid", uid);
                readHero.Parameters.AddWithValue("$hid", heroId);
                var scalar = await readHero.ExecuteScalarAsync(cancellationToken);
                if (scalar is not null && scalar != DBNull.Value) servantUid = Convert.ToInt64(scalar);
            }
            if (servantUid > 0)
            {
                await using var up = connection.CreateCommand();
                up.Transaction = transaction;
                up.CommandText = "UPDATE servant SET stage=MIN(5, COALESCE(stage,1)+1), update_ts=$ts WHERE uid=$uid AND id=$sid";
                up.Parameters.AddWithValue("$uid", uid);
                up.Parameters.AddWithValue("$sid", servantUid);
                up.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await up.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> OneClickUpgradeWeaponAsync(long uid, long heroId, int targetLevel, int breakthroughTimes, int targetExp, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var minBreakForLevel = Math.Clamp((targetLevel - 20 + 9) / 10, 0, 4);
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE hero
                SET weapon_exp = MAX(COALESCE(weapon_exp, 0), $exp),
                    weapon_break = MIN(4, MAX(COALESCE(weapon_break, 0) + $brkDelta, $minBrk)),
                    update_ts = $ts
                WHERE uid = $uid AND id = $hid
                """;
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$hid", heroId);
            command.Parameters.AddWithValue("$exp", Math.Max(0, targetExp));
            command.Parameters.AddWithValue("$brkDelta", Math.Max(0, breakthroughTimes));
            command.Parameters.AddWithValue("$minBrk", minBreakForLevel);
            command.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> SaveFormationAsync(long uid, int teamType, int contId, string teamJson, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || teamType <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO formation_team(uid,team_type,cont_id,cont_team,data_rec,update_ts)
                VALUES($uid,$type,$cid,$blob,$blob,$ts)
                ON CONFLICT(uid,team_type,cont_id) DO UPDATE SET cont_team=$blob, data_rec=$blob, update_ts=$ts
                """;
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$type", teamType);
            command.Parameters.AddWithValue("$cid", contId);
            command.Parameters.AddWithValue("$blob", System.Text.Encoding.UTF8.GetBytes(teamJson));
            command.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> DeleteFormationAsync(long uid, int teamType, int contId, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || teamType <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM formation_team WHERE uid=$uid AND team_type=$type AND cont_id=$cid";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$type", teamType);
            command.Parameters.AddWithValue("$cid", contId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }
}

public sealed partial class GameAccountRepository
{
    /// <summary>Every unlocked hero of every account with its astrolabe set and equipped sigil prefabs (文化图鉴 statistics).</summary>
    public async Task<IReadOnlyList<(long Hero, string Astrolabe, List<int> Equips)>> GetHeroBuildsAsync(CancellationToken ct = default)
    {
        var builds = new Dictionary<(long, long), (string, List<int>)>();
        await using var connection = await OpenAsync(ct);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT uid, id, COALESCE(using_astrolabe,'[]') FROM hero WHERE COALESCE(unlock,1)=1";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                builds[(reader.GetInt64(0), reader.GetInt64(1))] = (reader.GetString(2), []);
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT uid, hero_id, prefab_id FROM equip WHERE COALESCE(hero_id,0)<>0";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (builds.TryGetValue((reader.GetInt64(0), reader.GetInt64(1)), out var b)) b.Item2.Add(reader.GetInt32(2));
        }
        return builds.Select(b => (b.Key.Item2, b.Value.Item1, b.Value.Item2)).ToList();
    }
}

public sealed partial class GameAccountRepository
{
    public async Task<IReadOnlyList<(long Id, int Level, long Exp)>> GetHeroLevelsAsync(long uid, CancellationToken ct = default)
    {
        var list = new List<(long, int, long)>();
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, COALESCE(level,1), COALESCE(exp,0) FROM hero WHERE uid=$uid";
        command.Parameters.AddWithValue("$uid", uid);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetInt64(2)));
        return list;
    }

    public async Task SetHeroExpAsync(long uid, long heroId, long exp, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE hero SET exp=$exp, update_ts=$ts WHERE uid=$uid AND id=$id";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$id", heroId);
            command.Parameters.AddWithValue("$exp", exp);
            command.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(ct);
        }
        finally { _writeGate.Release(); }
    }
}
