using Microsoft.Data.Sqlite;

namespace Trimūrti.Server;

/// <summary>Generic idempotent claim ledger + item grants used by ported Alpha-Persei reward operations.</summary>
public sealed partial class GameAccountRepository
{

    private async Task EnsureClaimTableAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS trimurti_claim(uid INTEGER, kind TEXT, claim_key TEXT, value INTEGER DEFAULT 0, ts INTEGER, PRIMARY KEY(uid,kind,claim_key))";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Records a claim; returns false if (kind,key) was already claimed.</summary>
    public async Task<bool> TryClaimAsync(long uid, string kind, string key, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureClaimTableAsync(connection, ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO trimurti_claim(uid,kind,claim_key,ts) VALUES($u,$k,$c,$t)";
        cmd.Parameters.AddWithValue("$u", uid); cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$c", key); cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<string>> GetClaimsAsync(long uid, string kind, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureClaimTableAsync(connection, ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT claim_key FROM trimurti_claim WHERE uid=$u AND kind=$k";
        cmd.Parameters.AddWithValue("$u", uid); cmd.Parameters.AddWithValue("$k", kind);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Simple per-uid key/value (e.g. bgm, language, passport level).</summary>
    public async Task SetValueAsync(long uid, string kind, string key, long value, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureClaimTableAsync(connection, ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO trimurti_claim(uid,kind,claim_key,value,ts) VALUES($u,$k,$c,$v,$t) ON CONFLICT(uid,kind,claim_key) DO UPDATE SET value=$v, ts=$t";
        cmd.Parameters.AddWithValue("$u", uid); cmd.Parameters.AddWithValue("$k", kind); cmd.Parameters.AddWithValue("$c", key);
        cmd.Parameters.AddWithValue("$v", value); cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> GetValueAsync(long uid, string kind, string key, long fallback = 0, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureClaimTableAsync(connection, ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM trimurti_claim WHERE uid=$u AND kind=$k AND claim_key=$c";
        cmd.Parameters.AddWithValue("$u", uid); cmd.Parameters.AddWithValue("$k", kind); cmd.Parameters.AddWithValue("$c", key);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? fallback : Convert.ToInt64(v);
    }

    /// <summary>Adds items (hero ids below 10000 are skipped: hero unlocks go through the hero service).</summary>
    public async Task<IReadOnlyList<(long ItemId, long Count)>> GrantItemsAsync(long uid, IEnumerable<(long ItemId, long Count)> items, CancellationToken ct = default)
    {
        var granted = items.Where(x => x.ItemId > 0 && x.Count > 0).ToList();
        if (uid <= 0 || granted.Count == 0) return [];
        await _writeGate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var connection = await OpenAsync(ct);
            await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(ct);
            // Heroes, hero pieces, sigils and servants become entities (ItemKinds); everything else stacks.
            foreach (var (id, n) in granted) await GrantInTxAsync(connection, tx, uid, id, n, now, ct);
            await tx.CommitAsync(ct);
        }
        finally { _writeGate.Release(); }
        return granted;
    }

    /// <summary>Records today's sign-in day (05:00 cutoff) for a kind; returns false if already done today.</summary>
    public Task<bool> TryClaimTodayAsync(long uid, string kind, CancellationToken ct = default) =>
        TryClaimAsync(uid, kind, TodayKey, ct);

    private static readonly HashSet<string> GameUserIntColumns = ["bgm_id", "birth_month", "birth_day", "cur_bubble", "cur_card_bg", "cur_sticker_bg"];

    /// <summary>Updates a whitelisted integer column of game_user.</summary>
    public async Task SetGameUserIntAsync(long uid, string column, int value, CancellationToken ct = default)
    {
        if (!GameUserIntColumns.Contains(column)) throw new ArgumentException("Column not allowed", nameof(column));
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"UPDATE game_user SET {column}=$v, update_ts=$t WHERE uid=$u";
        cmd.Parameters.AddWithValue("$v", value); cmd.Parameters.AddWithValue("$u", uid);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static string TodayKey => GameClock.Today.ToString("yyyy-MM-dd");

    /// <summary>Game-day start (05:00 local) as unix seconds.</summary>
    public static long TodayStartUnix => GameClock.TodayStartUnix;

    public async Task<bool> HasClaimAsync(long uid, string kind, string key, CancellationToken ct = default) =>
        (await GetClaimsAsync(uid, kind, ct)).Contains(key);

    public async Task DeleteClaimsAsync(long uid, string kind, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureClaimTableAsync(connection, ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM trimurti_claim WHERE uid=$u AND kind=$k";
        cmd.Parameters.AddWithValue("$u", uid); cmd.Parameters.AddWithValue("$k", kind);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public sealed record SignRow(int Year, int Month, int Day, IReadOnlyList<int> Days, int Count, long LastSignAt);

    /// <summary>Saved sign state of an activity (3 = daily calendar sign, others = seven-day signs).</summary>
    public async Task<SignRow?> GetSignRowAsync(long uid, long activityId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(year,0),COALESCE(month,0),COALESCE(day,0),COALESCE(sign_list,'[]'),COALESCE(sign_count,0),COALESCE(last_sign_ts,0) FROM sign WHERE uid=$u AND activity_id=$a";
        cmd.Parameters.AddWithValue("$u", uid); cmd.Parameters.AddWithValue("$a", activityId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        int[] days;
        try { days = System.Text.Json.JsonSerializer.Deserialize<int[]>(r.GetString(3)) ?? []; } catch { days = []; }
        return new SignRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), days, r.GetInt32(4), r.GetInt64(5));
    }

    /// <summary>Claimed chapter star chests (chapter_star_reward table): (chapter id, reward order 1..3).</summary>
    public async Task<IReadOnlyList<(long ChapterId, int Order)>> GetChapterRewardClaimsAsync(long uid, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT chapter_id, reward_order FROM chapter_star_reward WHERE uid=$u";
        cmd.Parameters.AddWithValue("$u", uid);
        var list = new List<(long, int)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetInt64(0), r.GetInt32(1)));
        return list;
    }

    public sealed record StageSettleOutcome(int ClearTimes, bool FirstClear, long PlayerExp, int PlayerLevel);

    /// <summary>
    /// Persists a won stage in one transaction (port of Alpha-Persei pass_stage + grants + growth): merges the earned
    /// stars, adds clear times, grants every drop, deducts fatigue (item 4), adds player EXP (level from the official
    /// curve) and team hero EXP / proficiency (hero.clear_times, capped at 100).
    /// </summary>
    public async Task<StageSettleOutcome> SettleStageAsync(long uid, long stageId, int times, IReadOnlyList<bool> stars,
        IReadOnlyList<(long ItemId, long Count)> grants, int fatigueCost, long userExp, long heroExp, IReadOnlyList<long> heroes,
        Func<long, int> levelForExp, bool recordProgress, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var connection = await OpenAsync(ct);
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            var clear = 0;
            int[] saved = [];
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = "SELECT COALESCE(clear_times,0), COALESCE(star_list,'[]') FROM chapter WHERE uid=$u AND id=$i";
                read.Parameters.AddWithValue("$u", uid); read.Parameters.AddWithValue("$i", stageId);
                await using var r = await read.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    clear = r.GetInt32(0);
                    try { saved = System.Text.Json.JsonSerializer.Deserialize<int[]>(r.GetString(1)) ?? []; } catch { saved = []; }
                }
            }
            var first = clear == 0;
            if (recordProgress)
            {
                var merged = Enumerable.Range(0, Math.Max(3, Math.Max(stars.Count, saved.Length)))
                    .Select(i => (i < saved.Length && saved[i] == 1) || (i < stars.Count && stars[i]) ? 1 : 0).ToArray();
                clear += times;
                await using var up = connection.CreateCommand();
                up.Transaction = tx;
                up.CommandText = """
                    INSERT INTO chapter(uid,id,star_list,clear_times,first_clear_ts,update_ts) VALUES($u,$i,$s,$c,$n,$n)
                    ON CONFLICT(uid,id) DO UPDATE SET star_list=$s, clear_times=$c, update_ts=$n
                    """;
                up.Parameters.AddWithValue("$u", uid); up.Parameters.AddWithValue("$i", stageId);
                up.Parameters.AddWithValue("$s", System.Text.Json.JsonSerializer.Serialize(merged));
                up.Parameters.AddWithValue("$c", clear); up.Parameters.AddWithValue("$n", now);
                await up.ExecuteNonQueryAsync(ct);
            }
            foreach (var (item, count) in grants)
                if (item > 0 && count > 0) await GrantInTxAsync(connection, tx, uid, item, count, now, ct);
            if (fatigueCost > 0)
            {
                await using var cost = connection.CreateCommand();
                cost.Transaction = tx;
                cost.CommandText = "UPDATE currency SET num=MAX(0,num-$c), update_ts=$n WHERE uid=$u AND id=4";
                cost.Parameters.AddWithValue("$c", fatigueCost); cost.Parameters.AddWithValue("$u", uid); cost.Parameters.AddWithValue("$n", now);
                await cost.ExecuteNonQueryAsync(ct);
            }
            long totalExp = 0;
            var level = 1;
            await using (var user = connection.CreateCommand())
            {
                user.Transaction = tx;
                user.CommandText = "SELECT COALESCE(exp,0) FROM users WHERE uid=$u";
                user.Parameters.AddWithValue("$u", uid);
                totalExp = Convert.ToInt64(await user.ExecuteScalarAsync(ct) ?? 0L) + Math.Max(0, userExp);
                level = levelForExp(totalExp);
            }
            await using (var user = connection.CreateCommand())
            {
                user.Transaction = tx;
                user.CommandText = "UPDATE users SET exp=$e, level=MAX(COALESCE(level,1),$l) WHERE uid=$u";
                user.Parameters.AddWithValue("$e", totalExp); user.Parameters.AddWithValue("$l", level); user.Parameters.AddWithValue("$u", uid);
                await user.ExecuteNonQueryAsync(ct);
            }
            foreach (var hero in heroes.Distinct())
            {
                await using var h = connection.CreateCommand();
                h.Transaction = tx;
                h.CommandText = "UPDATE hero SET clear_times=MIN(100,COALESCE(clear_times,0)+$t), exp=COALESCE(exp,0)+$e, update_ts=$n WHERE uid=$u AND id=$h";
                h.Parameters.AddWithValue("$t", times); h.Parameters.AddWithValue("$e", Math.Max(0, heroExp));
                h.Parameters.AddWithValue("$n", now); h.Parameters.AddWithValue("$u", uid); h.Parameters.AddWithValue("$h", hero);
                await h.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return new StageSettleOutcome(clear, first, totalExp, level);
        }
        finally { _writeGate.Release(); }
    }


    private async Task EnsureStateTableAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS trimurti_state(uid INTEGER, kind TEXT, value_json TEXT, ts INTEGER, PRIMARY KEY(uid,kind))";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Per-player JSON state for feature modules (mythic, weekly modes...).</summary>
    public async Task<T?> GetJsonAsync<T>(long uid, string kind, CancellationToken ct = default) where T : class
    {
        await using var connection = await OpenAsync(ct);
        await EnsureStateTableAsync(connection, ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM trimurti_state WHERE uid=$u AND kind=$k";
        cmd.Parameters.AddWithValue("$u", uid); cmd.Parameters.AddWithValue("$k", kind);
        return await cmd.ExecuteScalarAsync(ct) is string json ? System.Text.Json.JsonSerializer.Deserialize<T>(json) : null;
    }

    public async Task SetJsonAsync<T>(long uid, string kind, T value, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureStateTableAsync(connection, ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO trimurti_state(uid,kind,value_json,ts) VALUES($u,$k,$v,$t) ON CONFLICT(uid,kind) DO UPDATE SET value_json=$v, ts=$t";
        cmd.Parameters.AddWithValue("$u", uid); cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$v", System.Text.Json.JsonSerializer.Serialize(value)); cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
