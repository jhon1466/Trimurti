using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Trimūrti.Server;

public sealed record TrustRow(long Hero, int Level, int Exp, int Mood, int ClearTimes);
public sealed record ArchiveRow(long Archive, int Exp, List<int> Texts, List<int> Videos);

public sealed partial class GameAccountRepository
{
    public async Task<TrustRow?> GetTrustAsync(long uid, long hero, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(trust_level,0), COALESCE(trust_exp,0), COALESCE(trust_mood,1), COALESCE(clear_times,0) FROM hero WHERE uid=$uid AND id=$id AND COALESCE(unlock,1)=1";
        cmd.Parameters.AddWithValue("$uid", uid);
        cmd.Parameters.AddWithValue("$id", hero);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new TrustRow(hero, (int)r.GetInt64(0), (int)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3)) : null;
    }

    public async Task<ArchiveRow> GetArchiveAsync(long uid, long archive, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await EnsureHeroArchiveStateTableAsync(c, ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT exp, COALESCE(text_list_json,'[]'), COALESCE(video_list_json,'[]') FROM hero_archive_state WHERE uid=$uid AND archive_id=$a";
        cmd.Parameters.AddWithValue("$uid", uid);
        cmd.Parameters.AddWithValue("$a", archive);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new ArchiveRow(archive, (int)r.GetInt64(0), Ints(r.GetString(1)), Ints(r.GetString(2)))
            : new ArchiveRow(archive, 0, [], []);
    }

    /// <summary>One transaction: item costs, optional trust row, optional archive row, grants.</summary>
    public async Task<bool> CommitTrustAsync(long uid, TrustRow? trust, ArchiveRow? archive,
        IReadOnlyList<(long ItemId, long Count)> costs, IReadOnlyList<(long ItemId, long Count)> grants, CancellationToken ct = default)
    {
        if (uid <= 0) return false;
        await _writeGate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var c = await OpenAsync(ct);
            await EnsureHeroArchiveStateTableAsync(c, ct);
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            foreach (var (item, n) in costs.Where(x => x.Count > 0))
                if (!await TryConsumeInTxAsync(c, tx, uid, item, n, ct)) { await tx.RollbackAsync(ct); return false; }
            if (trust is not null)
            {
                await using var up = c.CreateCommand();
                up.Transaction = tx;
                up.CommandText = "UPDATE hero SET trust_level=$l, trust_exp=$e, trust_mood=$m, update_ts=$ts WHERE uid=$uid AND id=$id";
                up.Parameters.AddWithValue("$uid", uid);
                up.Parameters.AddWithValue("$id", trust.Hero);
                up.Parameters.AddWithValue("$l", trust.Level);
                up.Parameters.AddWithValue("$e", trust.Exp);
                up.Parameters.AddWithValue("$m", trust.Mood);
                up.Parameters.AddWithValue("$ts", now);
                if (await up.ExecuteNonQueryAsync(ct) == 0) { await tx.RollbackAsync(ct); return false; }
            }
            if (archive is not null)
            {
                await using var up = c.CreateCommand();
                up.Transaction = tx;
                up.CommandText = """
                    INSERT INTO hero_archive_state(uid, archive_id, exp, text_list_json, video_list_json) VALUES($uid, $a, $e, $t, $v)
                    ON CONFLICT(uid, archive_id) DO UPDATE SET exp=$e, text_list_json=$t, video_list_json=$v
                    """;
                up.Parameters.AddWithValue("$uid", uid);
                up.Parameters.AddWithValue("$a", archive.Archive);
                up.Parameters.AddWithValue("$e", archive.Exp);
                up.Parameters.AddWithValue("$t", JsonSerializer.Serialize(archive.Texts));
                up.Parameters.AddWithValue("$v", JsonSerializer.Serialize(archive.Videos));
                await up.ExecuteNonQueryAsync(ct);
            }
            foreach (var (item, n) in grants.Where(x => x.Count > 0)) await GrantInTxAsync(c, tx, uid, item, n, now, ct);
            await tx.CommitAsync(ct);
            return true;
        }
        finally { _writeGate.Release(); }
    }
}
