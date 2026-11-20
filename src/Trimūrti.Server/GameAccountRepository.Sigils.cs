using Microsoft.Data.Sqlite;

namespace Trimūrti.Server;

/// <summary>Full sigil (equip) row, including enchant slots (JSON) and race / exclusive-hero reconstruction.</summary>
public sealed record SigilRow(long Id, int Prefab, long Exp, long HeroId, bool Lock, int Break, string Enchant, int Race, int RacePreview);

public sealed partial class GameAccountRepository
{
    private const string SigilCols = "id, COALESCE(prefab_id,0), COALESCE(exp,0), COALESCE(hero_id,0), COALESCE(is_lock,0), COALESCE(now_break_level,0), COALESCE(enchant_slots,'[]'), COALESCE(race,0), COALESCE(race_preview,0)";

    private static SigilRow ReadSigil(SqliteDataReader r) => new(r.GetInt64(0), (int)r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4) != 0,
        (int)r.GetInt64(5), r.IsDBNull(6) ? "[]" : r.GetString(6), (int)r.GetInt64(7), (int)r.GetInt64(8));

    public async Task<IReadOnlyList<SigilRow>> GetSigilsAsync(long uid, CancellationToken ct = default)
    {
        var list = new List<SigilRow>();
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SigilCols} FROM equip WHERE uid=$uid ORDER BY id";
        cmd.Parameters.AddWithValue("$uid", uid);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(ReadSigil(reader));
        return list;
    }

    public async Task<SigilRow?> GetSigilAsync(long uid, long id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SigilCols} FROM equip WHERE uid=$uid AND id=$id";
        cmd.Parameters.AddWithValue("$uid", uid);
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSigil(reader) : null;
    }

    /// <summary>Writes the mutable sigil fields, deletes consumed sigils and applies item costs/grants in one transaction.
    /// Returns false (nothing written) when an item cost cannot be paid or a consumed sigil is locked/equipped/missing.</summary>
    public async Task<bool> CommitSigilsAsync(long uid, IReadOnlyList<SigilRow> updates, IReadOnlyList<long> deletes,
        IReadOnlyList<(long ItemId, long Count)> costs, IReadOnlyList<(long ItemId, long Count)> grants, CancellationToken ct = default)
    {
        if (uid <= 0) return false;
        await _writeGate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var connection = await OpenAsync(ct);
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            foreach (var (item, count) in costs.Where(c => c.Count > 0))
                if (!await TryConsumeInTxAsync(connection, tx, uid, item, count, ct)) { await tx.RollbackAsync(ct); return false; }
            foreach (var id in deletes.Distinct())
            {
                await using var del = connection.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM equip WHERE uid=$uid AND id=$id AND COALESCE(is_lock,0)=0 AND COALESCE(hero_id,0)=0";
                del.Parameters.AddWithValue("$uid", uid);
                del.Parameters.AddWithValue("$id", id);
                if (await del.ExecuteNonQueryAsync(ct) == 0) { await tx.RollbackAsync(ct); return false; }
            }
            foreach (var s in updates)
            {
                await using var up = connection.CreateCommand();
                up.Transaction = tx;
                up.CommandText = """
                    UPDATE equip SET prefab_id=$p, exp=$e, hero_id=$h, is_lock=$l, now_break_level=$b, enchant_slots=$en, race=$r, race_preview=$rp, update_ts=$ts
                    WHERE uid=$uid AND id=$id
                    """;
                up.Parameters.AddWithValue("$uid", uid);
                up.Parameters.AddWithValue("$id", s.Id);
                up.Parameters.AddWithValue("$p", s.Prefab);
                up.Parameters.AddWithValue("$e", s.Exp);
                up.Parameters.AddWithValue("$h", s.HeroId);
                up.Parameters.AddWithValue("$l", s.Lock ? 1 : 0);
                up.Parameters.AddWithValue("$b", s.Break);
                up.Parameters.AddWithValue("$en", s.Enchant);
                up.Parameters.AddWithValue("$r", s.Race);
                up.Parameters.AddWithValue("$rp", s.RacePreview);
                up.Parameters.AddWithValue("$ts", now);
                if (await up.ExecuteNonQueryAsync(ct) == 0) { await tx.RollbackAsync(ct); return false; }
            }
            foreach (var (item, count) in grants.Where(g => g.Count > 0))
                await GrantInTxAsync(connection, tx, uid, item, count, now, ct);
            await tx.CommitAsync(ct);
            return true;
        }
        finally { _writeGate.Release(); }
    }
}
