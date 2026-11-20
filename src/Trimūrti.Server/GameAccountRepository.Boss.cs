using Microsoft.Data.Sqlite;

namespace Trimūrti.Server;

/// <summary>梦境再构 (boss challenge) persistence, port of Alpha-Persei account_db boss_challenge_* methods.</summary>
public sealed partial class GameAccountRepository
{
    public sealed record BossProgress(int Mode, int AreaId, int AdvanceId, int UseTimes, long LastWeeklyTs, long CycleId);
    public sealed record BossNormalRow(long GroupId, long FinishStage, string UsedHeroes, string LastHeroesCfg, string StarInfo);
    public sealed record BossAdvanceRow(long BossId, long Score, string UsedHeroes, string LastHeroesCfg);
    public sealed record BossAffixRow(long BossId, string Affixes, string Times, int Difficulty);

    private async Task<SqliteConnection> BossConnAsync(CancellationToken ct) => await OpenAsync(ct);

    private static async Task<int> ExecAsync(SqliteConnection c, string sql, CancellationToken ct, params (string, object)[] args)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Lazy weekly refresh (boss_challenge.check_and_lazy_refresh): a new Thursday-05:00 cycle wipes progress,
    /// scores and claimed rewards and stores the cycle seed; returns the current progress.</summary>
    public async Task<BossProgress> GetBossProgressAsync(long uid, long cycleStart, long cycleSeed, CancellationToken ct = default)
    {
        await using var c = await BossConnAsync(ct);
        BossProgress? p = null;
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(mode,0),COALESCE(area_id,4),COALESCE(advance_id,101),COALESCE(use_times,0),COALESCE(last_weekly_ts,0),COALESCE(cycle_id,0) FROM boss_challenge_progress WHERE uid=$u";
            cmd.Parameters.AddWithValue("$u", uid);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) p = new BossProgress(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt64(4), r.GetInt64(5));
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (p is null)
        {
            await ExecAsync(c, "INSERT OR IGNORE INTO boss_challenge_progress(uid,mode,cycle_id,area_id,advance_id,use_times,last_daily_ts,last_weekly_ts,update_ts) VALUES($u,0,$c,4,101,0,0,$w,$n)", ct,
                ("$u", uid), ("$c", cycleSeed), ("$w", cycleStart), ("$n", now));
            return new BossProgress(0, 4, 101, 0, cycleStart, cycleSeed);
        }
        if (p.LastWeeklyTs < cycleStart)
        {
            foreach (var table in new[] { "boss_challenge_normal", "boss_challenge_advance", "boss_challenge_scores", "boss_challenge_claimed_rewards", "boss_challenge_exchange" })
                await ExecAsync(c, $"DELETE FROM {table} WHERE uid=$u", ct, ("$u", uid));
            await ExecAsync(c, "UPDATE boss_challenge_progress SET cycle_id=$c, use_times=0, last_weekly_ts=$w, update_ts=$n WHERE uid=$u", ct,
                ("$u", uid), ("$c", cycleSeed), ("$w", cycleStart), ("$n", now));
            p = p with { UseTimes = 0, LastWeeklyTs = cycleStart, CycleId = cycleSeed };
        }
        return p;
    }

    public async Task SetBossModeAsync(long uid, int select, CancellationToken ct = default)
    {
        await using var c = await BossConnAsync(ct);
        var (mode, area, adv) = select switch
        {
            3 or 4 => (1, (int?)select, (int?)null),
            101 or 102 => (2, null, (int?)select),
            1 or 2 => (select, null, null),
            _ => (0, null, null)
        };
        await ExecAsync(c, "UPDATE boss_challenge_progress SET mode=$m, area_id=COALESCE($a,area_id), advance_id=COALESCE($d,advance_id), update_ts=$n WHERE uid=$u", ct,
            ("$m", mode), ("$a", (object?)area ?? DBNull.Value), ("$d", (object?)adv ?? DBNull.Value), ("$n", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$u", uid));
    }

    public async Task<IReadOnlyList<BossNormalRow>> GetBossNormalAsync(long uid, CancellationToken ct = default)
    {
        await using var c = await BossConnAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT group_id,COALESCE(finish_stage,0),COALESCE(used_heroes,'[]'),COALESCE(last_heroes_cfg,'[]'),COALESCE(star_info,'[]') FROM boss_challenge_normal WHERE uid=$u";
        cmd.Parameters.AddWithValue("$u", uid);
        var list = new List<BossNormalRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new BossNormalRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return list;
    }

    public async Task<IReadOnlyList<BossAdvanceRow>> GetBossAdvanceAsync(long uid, CancellationToken ct = default)
    {
        await using var c = await BossConnAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT boss_id,COALESCE(score,0),COALESCE(used_heroes,'[]'),COALESCE(last_heroes_cfg,'[]') FROM boss_challenge_advance WHERE uid=$u";
        cmd.Parameters.AddWithValue("$u", uid);
        var list = new List<BossAdvanceRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new BossAdvanceRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    public async Task<IReadOnlyList<BossAffixRow>> GetBossAffixesAsync(long uid, CancellationToken ct = default)
    {
        await using var c = await BossConnAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT boss_id,COALESCE(affix_index_list,'[]'),COALESCE(time_index_list,'[]'),COALESCE(diffculty_index,1) FROM boss_challenge_affixes WHERE uid=$u";
        cmd.Parameters.AddWithValue("$u", uid);
        var list = new List<BossAffixRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new BossAffixRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));
        return list;
    }

    public async Task<IReadOnlyList<(int Type, long Id)>> GetBossClaimedAsync(long uid, CancellationToken ct = default)
    {
        await using var c = await BossConnAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT reward_type,reward_id FROM boss_challenge_claimed_rewards WHERE uid=$u";
        cmd.Parameters.AddWithValue("$u", uid);
        var list = new List<(int, long)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetInt32(0), r.GetInt64(1)));
        return list;
    }

    public async Task<bool> ClaimBossRewardAsync(long uid, int type, long id, CancellationToken ct = default)
    {
        await using var c = await BossConnAsync(ct);
        return await ExecAsync(c, "INSERT OR IGNORE INTO boss_challenge_claimed_rewards(uid,reward_type,reward_id,update_ts) VALUES($u,$t,$i,$n)", ct,
            ("$u", uid), ("$t", type), ("$i", id), ("$n", DateTimeOffset.UtcNow.ToUnixTimeSeconds())) > 0;
    }

    public async Task SaveBossAffixesAsync(long uid, long bossId, string affixes, string times, int difficulty, CancellationToken ct = default)
    {
        await using var c = await BossConnAsync(ct);
        await ExecAsync(c, """
            INSERT INTO boss_challenge_affixes(uid,boss_id,affix_index_list,time_index_list,diffculty_index,update_ts) VALUES($u,$b,$a,$t,$d,$n)
            ON CONFLICT(uid,boss_id) DO UPDATE SET affix_index_list=$a,time_index_list=$t,diffculty_index=$d,update_ts=$n
            """, ct, ("$u", uid), ("$b", bossId), ("$a", affixes), ("$t", times), ("$d", difficulty), ("$n", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    public async Task SaveBossTeamAsync(long uid, int kind, long id, string heroesCfg, CancellationToken ct = default)
    {
        await using var c = await BossConnAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (kind == 1)
            await ExecAsync(c, "INSERT INTO boss_challenge_normal(uid,group_id,last_heroes_cfg,update_ts) VALUES($u,$i,$h,$n) ON CONFLICT(uid,group_id) DO UPDATE SET last_heroes_cfg=$h,update_ts=$n", ct,
                ("$u", uid), ("$i", id), ("$h", heroesCfg), ("$n", now));
        else
            await ExecAsync(c, "INSERT INTO boss_challenge_advance(uid,boss_id,last_heroes_cfg,update_ts) VALUES($u,$i,$h,$n) ON CONFLICT(uid,boss_id) DO UPDATE SET last_heroes_cfg=$h,update_ts=$n", ct,
                ("$u", uid), ("$i", id), ("$h", heroesCfg), ("$n", now));
    }

    /// <summary>Normal boss clear: stage stars per stage, locked heroes, +1 use (record_boss_challenge_normal_clear).</summary>
    public async Task RecordBossNormalClearAsync(long uid, long groupId, long stageId, IReadOnlyList<int> stars, IReadOnlyList<long> heroes, CancellationToken ct = default)
    {
        var rows = await GetBossNormalAsync(uid, ct);
        var info = new List<Dictionary<string, object>>();
        var existing = rows.FirstOrDefault(r => r.GroupId == groupId);
        if (existing is not null)
            try
            {
                foreach (var e in System.Text.Json.JsonDocument.Parse(existing.StarInfo).RootElement.EnumerateArray())
                    info.Add(new() { ["stage_id"] = e.GetProperty("stage_id").GetInt64(), ["star_list"] = e.GetProperty("star_list").EnumerateArray().Select(x => x.GetInt32()).ToArray() });
            }
            catch (System.Text.Json.JsonException) { }
        info.RemoveAll(e => Convert.ToInt64(e["stage_id"]) == stageId);
        info.Add(new() { ["stage_id"] = stageId, ["star_list"] = stars.ToArray() });
        await using var c = await BossConnAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await ExecAsync(c, """
            INSERT INTO boss_challenge_normal(uid,group_id,finish_stage,used_heroes,star_info,update_ts) VALUES($u,$g,$s,$h,$i,$n)
            ON CONFLICT(uid,group_id) DO UPDATE SET finish_stage=MAX(finish_stage,$s),used_heroes=$h,star_info=$i,update_ts=$n
            """, ct, ("$u", uid), ("$g", groupId), ("$s", stageId), ("$h", System.Text.Json.JsonSerializer.Serialize(heroes)),
            ("$i", System.Text.Json.JsonSerializer.Serialize(info)), ("$n", now));
        await ExecAsync(c, "UPDATE boss_challenge_progress SET use_times=use_times+1, update_ts=$n WHERE uid=$u", ct, ("$u", uid), ("$n", now));
    }

    /// <summary>Advanced boss clear: keeps the best score and locks the team (record_boss_challenge_advance_clear).</summary>
    public async Task RecordBossAdvanceClearAsync(long uid, long bossId, long score, IReadOnlyList<long> heroes, CancellationToken ct = default)
    {
        await using var c = await BossConnAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await ExecAsync(c, """
            INSERT INTO boss_challenge_advance(uid,boss_id,score,used_heroes,update_ts) VALUES($u,$b,$s,$h,$n)
            ON CONFLICT(uid,boss_id) DO UPDATE SET score=MAX(score,$s),used_heroes=$h,update_ts=$n
            """, ct, ("$u", uid), ("$b", bossId), ("$s", score), ("$h", System.Text.Json.JsonSerializer.Serialize(heroes)), ("$n", now));
        await ExecAsync(c, "INSERT INTO boss_challenge_scores(uid,boss_id,score,update_ts) VALUES($u,$b,$s,$n) ON CONFLICT(uid,boss_id) DO UPDATE SET score=MAX(score,$s),update_ts=$n", ct,
            ("$u", uid), ("$b", bossId), ("$s", score), ("$n", now));
    }

    /// <summary>Resets (45006 normal boss, 45106 advanced boss, 45204 all advanced bosses).</summary>
    public async Task ResetBossAsync(long uid, int kind, long target, CancellationToken ct = default)
    {
        await using var c = await BossConnAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (kind == 1)
            await ExecAsync(c, "UPDATE boss_challenge_normal SET finish_stage=0,used_heroes='[]',star_info='[]',update_ts=$n WHERE uid=$u AND group_id=$g", ct, ("$n", now), ("$u", uid), ("$g", target));
        else if (kind == 2)
        {
            await ExecAsync(c, "UPDATE boss_challenge_advance SET score=0,used_heroes='[]',update_ts=$n WHERE uid=$u AND boss_id=$b", ct, ("$n", now), ("$u", uid), ("$b", target));
            await ExecAsync(c, "DELETE FROM boss_challenge_scores WHERE uid=$u AND boss_id=$b", ct, ("$u", uid), ("$b", target));
        }
        else
        {
            await ExecAsync(c, "UPDATE boss_challenge_advance SET score=0,used_heroes='[]',update_ts=$n WHERE uid=$u", ct, ("$n", now), ("$u", uid));
            await ExecAsync(c, "DELETE FROM boss_challenge_scores WHERE uid=$u", ct, ("$u", uid));
        }
    }
}
