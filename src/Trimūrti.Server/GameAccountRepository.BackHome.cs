using Microsoft.Data.Sqlite;

namespace Trimūrti.Server;

public sealed partial class GameAccountRepository
{
    /// <summary>Ids of every hero form the player owns (backhome archives are unlocked by ownership).</summary>
    public async Task<IReadOnlyList<long>> GetOwnedHeroIdsAsync(long uid, CancellationToken ct = default)
    {
        var list = new List<long>();
        if (uid <= 0) return list;
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM hero WHERE uid=$uid";
        cmd.Parameters.AddWithValue("$uid", uid);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(reader.GetInt64(0));
        return list;
    }

    /// <summary>Adds trust (archive) experience, as dorm feeding and furniture gifts do in Alpha-Persei.</summary>
    public async Task AddHeroArchiveExpAsync(long uid, long archiveId, long exp, CancellationToken ct = default)
    {
        if (uid <= 0 || archiveId <= 0 || exp <= 0) return;
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await EnsureHeroArchiveStateTableAsync(connection, ct);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            await using (var up = connection.CreateCommand())
            {
                up.Transaction = transaction;
                up.CommandText = """
                    INSERT INTO hero_archive_state(uid, archive_id, exp, text_list_json, video_list_json)
                    VALUES($uid, $aid, $exp, '[]', '[]')
                    ON CONFLICT(uid, archive_id) DO UPDATE SET exp = exp + $exp
                    """;
                up.Parameters.AddWithValue("$uid", uid);
                up.Parameters.AddWithValue("$aid", archiveId);
                up.Parameters.AddWithValue("$exp", exp);
                await up.ExecuteNonQueryAsync(ct);
            }
            await using (var hero = connection.CreateCommand())
            {
                hero.Transaction = transaction;
                hero.CommandText = "UPDATE hero SET trust_exp=COALESCE(trust_exp,0)+$exp, trust_level=MIN(5, 1 + (COALESCE(trust_exp,0)+$exp)/500), update_ts=$ts WHERE uid=$uid AND id=$aid";
                hero.Parameters.AddWithValue("$uid", uid);
                hero.Parameters.AddWithValue("$aid", archiveId);
                hero.Parameters.AddWithValue("$exp", exp);
                hero.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await hero.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        finally { _writeGate.Release(); }
    }
}
