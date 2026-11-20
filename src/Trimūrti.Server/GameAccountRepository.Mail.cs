using Microsoft.Data.Sqlite;

namespace Trimūrti.Server;

public sealed partial class GameAccountRepository
{
    /// <summary>mail id → collect time (0 = not collected).</summary>
    public async Task<Dictionary<long, long>> GetMailCollectTimesAsync(long uid, CancellationToken ct = default)
    {
        var map = new Dictionary<long, long>();
        await using var c = await OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT mail_id, COALESCE(collect_time,0) FROM mail WHERE uid=$uid";
        cmd.Parameters.AddWithValue("$uid", uid);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetInt64(0)] = r.GetInt64(1);
        return map;
    }

    public async Task<bool> SetMailCollectAsync(long uid, long mailId, bool collect, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var c = await OpenAsync(ct);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE mail SET star_state=$s, collect_time=$t, update_ts=$t2 WHERE uid=$uid AND mail_id=$id";
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$id", mailId);
            cmd.Parameters.AddWithValue("$s", collect ? 1 : 0);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            cmd.Parameters.AddWithValue("$t", collect ? now : 0);
            cmd.Parameters.AddWithValue("$t2", now);
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Deletes the given mails only if read, with no pending attachment and not collected.</summary>
    public async Task<IReadOnlyList<long>> DeleteFinishedMailsAsync(long uid, IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        var deleted = new List<long>();
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var c = await OpenAsync(ct);
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            foreach (var id in ids)
            {
                await using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM mail WHERE uid=$uid AND mail_id=$id AND read_flag=1 AND attach_flag=1 AND COALESCE(star_state,0)=0";
                cmd.Parameters.AddWithValue("$uid", uid);
                cmd.Parameters.AddWithValue("$id", id);
                if (await cmd.ExecuteNonQueryAsync(ct) > 0) deleted.Add(id);
            }
            await tx.CommitAsync(ct);
        }
        finally { _writeGate.Release(); }
        return deleted;
    }

    /// <summary>Sends a template mail (MailTemplateCfg) with attachments.</summary>
    public async Task<long> SendTemplateMailAsync(long uid, int template, string title, string sender, string content, IReadOnlyList<(long ItemId, long Count)> attachments, CancellationToken ct = default)
    {
        var id = await SendSystemMailAsync(uid, title, content, sender, attachments, ct);
        if (id <= 0 || template == 0) return id;
        await using var c = await OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE mail SET template_id=$t WHERE uid=$uid AND mail_id=$id";
        cmd.Parameters.AddWithValue("$uid", uid);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$t", template);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }
}
