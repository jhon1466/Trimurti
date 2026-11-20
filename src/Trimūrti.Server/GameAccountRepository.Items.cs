using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Trimūrti.Server;

/// <summary>Item id kinds from the client tables (HeroCfg, EquipCfg, WeaponServantCfg): heroes, hero pieces
/// (10000 + hero id), sigil prefabs and weapon servants are entities, not stackable materials.</summary>
public static class ItemKinds
{
    private static readonly Lazy<(Dictionary<long, (int UnlockStar, int[] Skills)> Heroes, HashSet<long> Equips, Dictionary<long, int> EquipRace, HashSet<long> Servants, long[] Recovery)> Data = new(() =>
    {
        var cfg = new SourceGameConfig();
        var heroes = new Dictionary<long, (int, int[])>();
        var equips = new HashSet<long>();
        var race = new Dictionary<long, int>();
        var servants = new HashSet<long>();
        long[] recovery = [12, 18, 30];
        if (cfg.Get("hero_cfg.json") is { } h)
        {
            foreach (var p in h.GetProperty("heroes").EnumerateObject())
                heroes[long.Parse(p.Name)] = (p.Value.GetProperty("unlock_star").GetInt32(),
                    p.Value.GetProperty("skills").EnumerateArray().Select(x => x.GetInt32()).ToArray());
            foreach (var p in h.GetProperty("servants").EnumerateObject()) servants.Add(long.Parse(p.Name));
            recovery = h.GetProperty("setting").GetProperty("recovery_fragment").EnumerateArray().Select(x => x.GetInt64()).ToArray();
        }
        if (cfg.Get("sigil_cfg.json") is { } s)
            foreach (var p in s.GetProperty("equips").EnumerateObject())
            {
                equips.Add(long.Parse(p.Name));
                race[long.Parse(p.Name)] = p.Value.TryGetProperty("race", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0;
            }
        return (heroes, equips, race, servants, recovery);
    });

    private static readonly Lazy<HashSet<long>> SkinIds = new(() =>
    {
        var set = new HashSet<long>();
        if (new SourceGameConfig().Get("skin_hero_map.json") is { } m)
            foreach (var p in m.EnumerateObject()) if (long.TryParse(p.Name, out var id) && id >= 100000) set.Add(id);
        return set;
    });
    public static bool IsSkin(long id) => SkinIds.Value.Contains(id);
    public static bool IsHero(long id) => Data.Value.Heroes.ContainsKey(id);
    public static bool IsPiece(long id) => id is > 10000 and < 20000 && Data.Value.Heroes.ContainsKey(id - 10000);
    public static bool IsSigil(long id) => Data.Value.Equips.Contains(id);
    public static bool IsServant(long id) => Data.Value.Servants.Contains(id);
    public static int UnlockStar(long hero) => Data.Value.Heroes.TryGetValue(hero, out var h) ? h.UnlockStar : 100;
    public static int[] Skills(long hero) => Data.Value.Heroes.TryGetValue(hero, out var h) ? h.Skills : [];
    public static int SigilRace(long prefab) => Data.Value.EquipRace.GetValueOrDefault(prefab);
    /// <summary>Pieces given for a duplicate hero (GameSetting recovery_fragment by unlock star 1/2/3).</summary>
    public static long DuplicatePieces(long hero) { var r = Data.Value.Recovery; var i = Math.Clamp(UnlockStar(hero) / 100 - 1, 0, r.Length - 1); return r[i]; }
}

public sealed partial class GameAccountRepository
{
    /// <summary>Grants an entity-like item (hero, hero piece, sigil, servant). Returns false for plain stackable items.</summary>
    private static async Task<bool> GrantEntityInTxAsync(SqliteConnection c, SqliteTransaction tx, long uid, long itemId, long count, long now, CancellationToken ct)
    {
        if (ItemKinds.IsPiece(itemId)) { await AddPiecesInTxAsync(c, tx, uid, itemId - 10000, count, ct); return true; }
        if (ItemKinds.IsSkin(itemId))
        {
            await using var sk = c.CreateCommand();
            sk.Transaction = tx;
            sk.CommandText = "INSERT OR IGNORE INTO player_skin_unlocked(uid,skin_id,unlock_ts,update_ts) VALUES($uid,$sid,$now,$now)";
            sk.Parameters.AddWithValue("$uid", uid);
            sk.Parameters.AddWithValue("$sid", itemId);
            sk.Parameters.AddWithValue("$now", now);
            await sk.ExecuteNonQueryAsync(ct);
            return true;
        }
        if (ItemKinds.IsHero(itemId))
        {
            for (var i = 0; i < count; i++)
            {
                if (!await HeroExistsAsync(c, tx, uid, itemId, ct))
                {
                    await InsertHeroAsync(c, tx, uid, itemId, ItemKinds.Skills(itemId), ItemKinds.UnlockStar(itemId), now, ct);
                    await SyncHeroCountAsync(c, tx, uid, ct);
                }
                else await AddPiecesInTxAsync(c, tx, uid, itemId, ItemKinds.DuplicatePieces(itemId), ct);
            }
            return true;
        }
        if (ItemKinds.IsSigil(itemId))
        {
            for (var i = 0; i < count; i++)
            {
                await using var ins = c.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO equip(uid,id,prefab_id,exp,hero_id,is_lock,now_break_level,enchant_slots,race,race_preview,update_ts)
                    VALUES($uid,(SELECT COALESCE(MAX(id),0)+1 FROM equip WHERE uid=$uid),$p,0,0,0,0,'[]',$race,0,$ts)
                    """;
                ins.Parameters.AddWithValue("$uid", uid);
                ins.Parameters.AddWithValue("$p", itemId);
                ins.Parameters.AddWithValue("$race", ItemKinds.SigilRace(itemId));
                ins.Parameters.AddWithValue("$ts", now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            return true;
        }
        if (ItemKinds.IsServant(itemId))
        {
            for (var i = 0; i < count; i++)
            {
                await using var ins = c.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO servant(uid,id,prefab_id,stage,owned,is_locked,update_ts)
                    VALUES($uid,(SELECT COALESCE(MAX(id),0)+1 FROM servant WHERE uid=$uid),$p,1,1,0,$ts)
                    """;
                ins.Parameters.AddWithValue("$uid", uid);
                ins.Parameters.AddWithValue("$p", itemId);
                ins.Parameters.AddWithValue("$ts", now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            return true;
        }
        return false;
    }

    private static async Task AddPiecesInTxAsync(SqliteConnection c, SqliteTransaction tx, long uid, long hero, long count, CancellationToken ct)
    {
        await using var up = c.CreateCommand();
        up.Transaction = tx;
        up.CommandText = "INSERT INTO hero_piece(uid,hero_id,num) VALUES($uid,$h,$n) ON CONFLICT(uid,hero_id) DO UPDATE SET num=num+$n";
        up.Parameters.AddWithValue("$uid", uid);
        up.Parameters.AddWithValue("$h", hero);
        up.Parameters.AddWithValue("$n", count);
        await up.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> PiecesInTxAsync(SqliteConnection c, SqliteTransaction? tx, long uid, long hero, CancellationToken ct)
    {
        await using var read = c.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT COALESCE(num,0) FROM hero_piece WHERE uid=$uid AND hero_id=$h";
        read.Parameters.AddWithValue("$uid", uid);
        read.Parameters.AddWithValue("$h", hero);
        return Convert.ToInt64(await read.ExecuteScalarAsync(ct) ?? 0L);
    }

    private static async Task<bool> ConsumePiecesInTxAsync(SqliteConnection c, SqliteTransaction tx, long uid, long hero, long count, CancellationToken ct)
    {
        if (await PiecesInTxAsync(c, tx, uid, hero, ct) < count) return false;
        await using var up = c.CreateCommand();
        up.Transaction = tx;
        up.CommandText = "UPDATE hero_piece SET num=num-$n WHERE uid=$uid AND hero_id=$h";
        up.Parameters.AddWithValue("$uid", uid);
        up.Parameters.AddWithValue("$h", hero);
        up.Parameters.AddWithValue("$n", count);
        await up.ExecuteNonQueryAsync(ct);
        return true;
    }

    /// <summary>Hero pieces owned (sc_14009 / sc_14019 piece_list).</summary>
    public async Task<IReadOnlyList<(long Hero, long Num)>> GetHeroPiecesAsync(long uid, CancellationToken ct = default)
    {
        var list = new List<(long, long)>();
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT hero_id, COALESCE(num,0) FROM hero_piece WHERE uid=$uid AND num>0 ORDER BY hero_id";
        cmd.Parameters.AddWithValue("$uid", uid);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add((reader.GetInt64(0), reader.GetInt64(1)));
        return list;
    }
}
