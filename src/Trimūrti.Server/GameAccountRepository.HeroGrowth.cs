using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Trimūrti.Server;

/// <summary>Mutable hero growth row (hero table columns used by the growth rules).</summary>
public sealed class HeroRow
{
    public long Id { get; set; }
    public int Level { get; set; } = 1;
    public int Star { get; set; } = 100;
    public long Exp { get; set; }
    public List<int[]> Skills { get; set; } = [];
    public long WeaponExp { get; set; }
    public int WeaponBreak { get; set; }
    public long ServantUid { get; set; }
    public List<int> UsingAstrolabe { get; set; } = [];
    public List<int> UnlockAstrolabe { get; set; } = [];
    public int Skin { get; set; }
    public int BreakLevel { get; set; }
    public int ModuleLevel { get; set; }
    public List<int[]> Intensify { get; set; } = [];
    public int BattleSkin { get; set; }
    public int ClearTimes { get; set; }
    public int ModuleAssignment { get; set; }
}

public sealed partial class GameAccountRepository
{
    private static List<int[]> Pairs(string? json)
    {
        try { return (JsonSerializer.Deserialize<int[][]>(json ?? "[]") ?? []).Where(p => p.Length >= 2).ToList(); }
        catch { return []; }
    }

    private static List<int> Ints(string? json)
    {
        try { return (JsonSerializer.Deserialize<int[]>(json ?? "[]") ?? []).ToList(); }
        catch { return ParseIntList(json ?? "[]").ToList(); }
    }

    public async Task<HeroRow?> GetHeroRowAsync(long uid, long heroId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, COALESCE(level,1), COALESCE(star,100), COALESCE(exp,0), COALESCE(skill_list,'[]'), COALESCE(weapon_exp,0), COALESCE(weapon_break,0),
                   COALESCE(weapon_servant_uid,0), COALESCE(using_astrolabe,'[]'), COALESCE(unlock_astrolabe,'[]'), COALESCE(using_skin,0), COALESCE(break_level,0),
                   COALESCE(weapon_module_level,0), COALESCE(skill_intensify,'[]'), COALESCE(battle_using_skin,0), COALESCE(clear_times,0), COALESCE(module_assignment,0)
            FROM hero WHERE uid=$uid AND id=$id AND COALESCE(unlock,1)=1
            """;
        cmd.Parameters.AddWithValue("$uid", uid);
        cmd.Parameters.AddWithValue("$id", heroId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new HeroRow
        {
            Id = r.GetInt64(0), Level = (int)r.GetInt64(1), Star = (int)r.GetInt64(2), Exp = r.GetInt64(3),
            Skills = ParseSkillPairs(r.GetString(4)).Select(s => new[] { s.SkillId, s.Level }).ToList(),
            WeaponExp = r.GetInt64(5), WeaponBreak = (int)r.GetInt64(6), ServantUid = r.GetInt64(7),
            UsingAstrolabe = Ints(r.GetString(8)), UnlockAstrolabe = Ints(r.GetString(9)), Skin = (int)r.GetInt64(10), BreakLevel = (int)r.GetInt64(11),
            ModuleLevel = (int)r.GetInt64(12), Intensify = Pairs(r.GetString(13)), BattleSkin = (int)r.GetInt64(14), ClearTimes = (int)r.GetInt64(15),
            ModuleAssignment = (int)r.GetInt64(16)
        };
    }

    /// <summary>Hero skill-attribute levels (skill_intensify) for every hero, for hero_base_net_rec.</summary>
    public async Task<Dictionary<long, List<int[]>>> GetHeroIntensifyAsync(long uid, CancellationToken ct = default)
    {
        var map = new Dictionary<long, List<int[]>>();
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, COALESCE(skill_intensify,'[]') FROM hero WHERE uid=$uid";
        cmd.Parameters.AddWithValue("$uid", uid);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetInt64(0)] = Pairs(r.GetString(1));
        return map;
    }

    /// <summary>Atomically pays costs, applies grants, saves the hero growth row and an optional 跃迁 slot.</summary>
    public async Task<bool> CommitHeroAsync(long uid, HeroRow? hero, IReadOnlyList<(long ItemId, long Count)> costs, IReadOnlyList<(long ItemId, long Count)> grants,
        (long Hero, int Slot, int Talent, string Skills)? warp = null, CancellationToken ct = default)
    {
        if (uid <= 0) return false;
        await _writeGate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var c = await OpenAsync(ct);
            await EnsureHeroWarpTableAsync(c, ct);
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            foreach (var (item, n) in costs.Where(x => x.Count > 0))
                if (!await TryConsumeInTxAsync(c, tx, uid, item, n, ct)) { await tx.RollbackAsync(ct); return false; }
            if (hero is not null)
            {
                await using var up = c.CreateCommand();
                up.Transaction = tx;
                up.CommandText = """
                    UPDATE hero SET level=$lv, star=$star, exp=$exp, skill_list=$skills, weapon_exp=$wexp, weapon_break=$wbrk, weapon_servant_uid=$srv,
                        using_astrolabe=$using, unlock_astrolabe=$unlock, using_skin=$skin, break_level=$brk, weapon_module_level=$mod, module_level=$mod,
                        skill_intensify=$int, battle_using_skin=$bskin, update_ts=$ts
                    WHERE uid=$uid AND id=$id
                    """;
                up.Parameters.AddWithValue("$uid", uid);
                up.Parameters.AddWithValue("$id", hero.Id);
                up.Parameters.AddWithValue("$lv", hero.Level);
                up.Parameters.AddWithValue("$star", hero.Star);
                up.Parameters.AddWithValue("$exp", hero.Exp);
                up.Parameters.AddWithValue("$skills", JsonSerializer.Serialize(hero.Skills));
                up.Parameters.AddWithValue("$wexp", hero.WeaponExp);
                up.Parameters.AddWithValue("$wbrk", hero.WeaponBreak);
                up.Parameters.AddWithValue("$srv", hero.ServantUid);
                up.Parameters.AddWithValue("$using", JsonSerializer.Serialize(hero.UsingAstrolabe));
                up.Parameters.AddWithValue("$unlock", JsonSerializer.Serialize(hero.UnlockAstrolabe));
                up.Parameters.AddWithValue("$skin", hero.Skin);
                up.Parameters.AddWithValue("$brk", hero.BreakLevel);
                up.Parameters.AddWithValue("$mod", hero.ModuleLevel);
                up.Parameters.AddWithValue("$int", JsonSerializer.Serialize(hero.Intensify));
                up.Parameters.AddWithValue("$bskin", hero.BattleSkin);
                up.Parameters.AddWithValue("$ts", now);
                if (await up.ExecuteNonQueryAsync(ct) == 0) { await tx.RollbackAsync(ct); return false; }
            }
            if (warp is { } w)
            {
                await using var ws = c.CreateCommand();
                ws.Transaction = tx;
                ws.CommandText = """
                    INSERT INTO hero_warp(uid, hero_id, slot_id, talent_points, skills_json) VALUES($uid, $hid, $slot, $tp, $sk)
                    ON CONFLICT(uid, hero_id, slot_id) DO UPDATE SET talent_points=$tp, skills_json=$sk
                    """;
                ws.Parameters.AddWithValue("$uid", uid);
                ws.Parameters.AddWithValue("$hid", w.Hero);
                ws.Parameters.AddWithValue("$slot", w.Slot);
                ws.Parameters.AddWithValue("$tp", w.Talent);
                ws.Parameters.AddWithValue("$sk", w.Skills);
                await ws.ExecuteNonQueryAsync(ct);
            }
            foreach (var (item, n) in grants.Where(x => x.Count > 0)) await GrantInTxAsync(c, tx, uid, item, n, now, ct);
            await tx.CommitAsync(ct);
            return true;
        }
        finally { _writeGate.Release(); }
    }
}
