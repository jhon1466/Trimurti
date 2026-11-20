using Microsoft.Data.Sqlite;

namespace Trimūrti.Server;

public sealed record ServantRow(long Uid, int Prefab, int Stage, bool Lock);

public sealed partial class GameAccountRepository
{
    public async Task<IReadOnlyList<ServantRow>> GetServantRowsAsync(long uid, CancellationToken ct = default)
    {
        var list = new List<ServantRow>();
        await using var c = await OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, COALESCE(prefab_id,0), COALESCE(stage,1), COALESCE(is_locked,0) FROM servant WHERE uid=$uid ORDER BY id";
        cmd.Parameters.AddWithValue("$uid", uid);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ServantRow(r.GetInt64(0), (int)r.GetInt64(1), (int)r.GetInt64(2), r.GetInt64(3) != 0));
        return list;
    }

    /// <summary>hero id → equipped servant uid.</summary>
    public async Task<Dictionary<long, long>> GetWornServantsAsync(long uid, CancellationToken ct = default)
    {
        var map = new Dictionary<long, long>();
        await using var c = await OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, COALESCE(weapon_servant_uid,0) FROM hero WHERE uid=$uid AND COALESCE(weapon_servant_uid,0)<>0";
        cmd.Parameters.AddWithValue("$uid", uid);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetInt64(0)] = r.GetInt64(1);
        return map;
    }

    /// <summary>One transaction: item costs, servant updates / deletions, hero weapon fields (exp, break, servant), grants.</summary>
    public async Task<bool> CommitServantsAsync(long uid, IReadOnlyList<ServantRow> updates, IReadOnlyList<long> deletes,
        IReadOnlyList<(long Hero, long Exp, int Break, long Servant)> heroes, IReadOnlyList<(long ItemId, long Count)> costs,
        IReadOnlyList<(long ItemId, long Count)> grants, CancellationToken ct = default)
    {
        if (uid <= 0) return false;
        await _writeGate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var c = await OpenAsync(ct);
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            async Task<bool> Exec(string sql, params (string, object)[] ps)
            {
                await using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$uid", uid);
                cmd.Parameters.AddWithValue("$ts", now);
                foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
                return await cmd.ExecuteNonQueryAsync(ct) > 0;
            }
            foreach (var (item, n) in costs.Where(x => x.Count > 0))
                if (!await TryConsumeInTxAsync(c, tx, uid, item, n, ct)) { await tx.RollbackAsync(ct); return false; }
            foreach (var id in deletes.Distinct())
                if (!await Exec("DELETE FROM servant WHERE uid=$uid AND id=$id AND COALESCE(is_locked,0)=0 AND NOT EXISTS (SELECT 1 FROM hero WHERE uid=$uid AND weapon_servant_uid=$id)", ("$id", id)))
                { await tx.RollbackAsync(ct); return false; }
            foreach (var s in updates)
                if (!await Exec("UPDATE servant SET prefab_id=$p, stage=$s, is_locked=$l, update_ts=$ts WHERE uid=$uid AND id=$id", ("$id", s.Uid), ("$p", s.Prefab), ("$s", s.Stage), ("$l", s.Lock ? 1 : 0)))
                { await tx.RollbackAsync(ct); return false; }
            foreach (var h in heroes)
            {
                if (h.Servant != 0) await Exec("UPDATE hero SET weapon_servant_uid=0 WHERE uid=$uid AND weapon_servant_uid=$s AND id<>$h", ("$s", h.Servant), ("$h", h.Hero));
                if (!await Exec("UPDATE hero SET weapon_exp=$e, weapon_break=$b, weapon_servant_uid=$s, update_ts=$ts WHERE uid=$uid AND id=$h", ("$h", h.Hero), ("$e", h.Exp), ("$b", h.Break), ("$s", h.Servant)))
                { await tx.RollbackAsync(ct); return false; }
            }
            foreach (var (item, n) in grants.Where(x => x.Count > 0)) await GrantInTxAsync(c, tx, uid, item, n, now, ct);
            await tx.CommitAsync(ct);
            return true;
        }
        finally { _writeGate.Release(); }
    }
}
