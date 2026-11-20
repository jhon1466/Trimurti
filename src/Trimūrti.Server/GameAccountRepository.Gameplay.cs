using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Trimūrti.Server;

public sealed record NicknameUpdateResult(bool Success, int IsChangedNick, int ChangeNickTimes);
public sealed record HeroUpgradeResult(bool Success, int Level, long Experience, int BreakLevel);
public sealed record BattleSettleResult(bool Success, int ClearTimes, long PlayerExp, int PlayerLevel, IReadOnlyList<(long ItemId, long Count)> Drops);
public sealed record DrawPullItem(long ItemId, long Count, bool IsSsr, bool IsNewHero);
public sealed record DrawResult(bool Success, IReadOnlyList<DrawPullItem> Items, int SsrPityCount, bool FirstSsrFlag);
public sealed record DrawHistoryEntry(long ItemId, long Count, long Timestamp);
public sealed record DrawHistoryResult(int SsrPityCount, IReadOnlyList<DrawHistoryEntry> Records);
public sealed record MailSummary(long Id, long SendTime, string Title, int AttachFlag, int ReadFlag,
    IReadOnlyList<(long Id, long Count)> Attachments, long ExpireTime, int StarState, int TemplateId);
public sealed record MailDetail(long Id, string Sender, string Content, IReadOnlyList<(long Id, long Count)> Attachments, int TemplateId);
public sealed record MailClaimResult(bool Success, IReadOnlyList<(long Id, long Count)> Attachments, IReadOnlyList<long> ClaimedIds);

public sealed partial class GameAccountRepository
{
    public async Task<NicknameUpdateResult> UpdateNicknameAsync(long uid, string nick, CancellationToken cancellationToken = default)
    {
        var trimmed = (nick ?? "").Trim();
        if (uid <= 0 || trimmed.Length is < 1 or > 24)
            return new NicknameUpdateResult(false, 0, 0);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE users SET nick=$nick, is_changed_nick=1, change_nick_times=COALESCE(change_nick_times,0)+1 WHERE uid=$uid";
                update.Parameters.AddWithValue("$uid", uid);
                update.Parameters.AddWithValue("$nick", trimmed);
                if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new NicknameUpdateResult(false, 0, 0);
                }
            }
            var times = 1;
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT COALESCE(change_nick_times,1) FROM users WHERE uid=$uid";
                read.Parameters.AddWithValue("$uid", uid);
                times = Convert.ToInt32(await read.ExecuteScalarAsync(cancellationToken) ?? 1);
            }
            await transaction.CommitAsync(cancellationToken);
            return new NicknameUpdateResult(true, 1, times);
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> UpdatePlotProgressAsync(long uid, int storyId, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || storyId <= 0) return false;
        if (storyId >= 20_000_000) return true;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE users SET plot_progress=MAX(COALESCE(plot_progress,0), $story) WHERE uid=$uid";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$story", storyId);
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> CompleteGuideAsync(long uid, int guideId, string kind, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || guideId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO guide(uid,guide_id,kind) VALUES($uid,$guide,$kind)";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$guide", guideId);
            command.Parameters.AddWithValue("$kind", kind);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<(bool Success, bool AlreadyOwned)> UnlockHeroAsync(long uid, long heroId, IReadOnlyList<int>? skills = null, int star = 100, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0) return (false, false);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var exists = await HeroExistsAsync(connection, transaction, uid, heroId, cancellationToken);
            if (exists)
            {
                await transaction.CommitAsync(cancellationToken);
                return (true, true);
            }
            await InsertHeroAsync(connection, transaction, uid, heroId, skills, star, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), cancellationToken);
            await SyncHeroCountAsync(connection, transaction, uid, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (true, false);
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> UnlockOrTranscendHeroAsync(long uid, long heroId, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var exists = await HeroExistsAsync(connection, transaction, uid, heroId, cancellationToken);
            if (exists)
            {
                long curStar = 300;
                await using (var readStar = connection.CreateCommand())
                {
                    readStar.Transaction = transaction;
                    readStar.CommandText = "SELECT COALESCE(star, 300) FROM hero WHERE uid=$uid AND id=$hid";
                    readStar.Parameters.AddWithValue("$uid", uid);
                    readStar.Parameters.AddWithValue("$hid", heroId);
                    var scalar = await readStar.ExecuteScalarAsync(cancellationToken);
                    if (scalar is not null && scalar != DBNull.Value) curStar = Convert.ToInt64(scalar);
                }
                var nextStar = curStar >= 600
                    ? 600
                    : (curStar % 100 >= 3 ? ((curStar / 100) + 1) * 100 : curStar + 1);

                await using var up = connection.CreateCommand();
                up.Transaction = transaction;
                up.CommandText = "UPDATE hero SET star=$star, update_ts=$ts WHERE uid=$uid AND id=$hid";
                up.Parameters.AddWithValue("$uid", uid);
                up.Parameters.AddWithValue("$hid", heroId);
                up.Parameters.AddWithValue("$star", nextStar);
                up.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await up.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await InsertHeroAsync(connection, transaction, uid, heroId, null, 300, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), cancellationToken);
                await SyncHeroCountAsync(connection, transaction, uid, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }


    public async Task<HeroUpgradeResult> BreakHeroAsync(long uid, long heroId, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0) return new HeroUpgradeResult(false, 0, 0, 0);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var (found, level, exp, breakLevel) = await ReadHeroProgressAsync(connection, transaction, uid, heroId, cancellationToken);
            if (!found || breakLevel >= 6) { await transaction.RollbackAsync(cancellationToken); return new HeroUpgradeResult(false, level, exp, breakLevel); }

            breakLevel++;
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE hero SET break_level=$brk, update_ts=$ts WHERE uid=$uid AND id=$id";
                update.Parameters.AddWithValue("$uid", uid);
                update.Parameters.AddWithValue("$id", heroId);
                update.Parameters.AddWithValue("$brk", breakLevel);
                update.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return new HeroUpgradeResult(true, level, exp, breakLevel);
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> LevelUpHeroSkillAsync(long uid, long heroId, int skillId, int delta, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0 || skillId <= 0 || delta <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            string? rawSkills = null;
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT skill_list FROM hero WHERE uid=$uid AND id=$id";
                read.Parameters.AddWithValue("$uid", uid);
                read.Parameters.AddWithValue("$id", heroId);
                rawSkills = await read.ExecuteScalarAsync(cancellationToken) as string;
            }
            if (rawSkills is null) { await transaction.RollbackAsync(cancellationToken); return false; }

            var pairs = ParseSkillPairs(rawSkills).ToList();
            var idx = pairs.FindIndex(x => x.SkillId == skillId);
            if (idx >= 0) pairs[idx] = pairs[idx] with { Level = Math.Min(35, pairs[idx].Level + delta) };
            else pairs.Add(new SkillLevel(skillId, Math.Min(35, 1 + delta)));

            var serialized = JsonSerializer.Serialize(pairs.Select(p => new[] { p.SkillId, p.Level }));
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE hero SET skill_list=$skills, update_ts=$ts WHERE uid=$uid AND id=$id";
                update.Parameters.AddWithValue("$uid", uid);
                update.Parameters.AddWithValue("$id", heroId);
                update.Parameters.AddWithValue("$skills", serialized);
                update.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> UpdateAstrolabeAsync(long uid, long heroId, int astrolabeId, bool unlockOnly, uint operation, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0 || astrolabeId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            string? usingJson = null;
            string? unlockJson = null;
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT using_astrolabe, unlock_astrolabe FROM hero WHERE uid=$uid AND id=$id";
                read.Parameters.AddWithValue("$uid", uid);
                read.Parameters.AddWithValue("$id", heroId);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) { await transaction.RollbackAsync(cancellationToken); return false; }
                usingJson = reader.IsDBNull(0) ? "[]" : reader.GetString(0);
                unlockJson = reader.IsDBNull(1) ? "[]" : reader.GetString(1);
            }

            var unlocked = ParseIntList(unlockJson).ToHashSet();
            var equipped = ParseIntList(usingJson).ToHashSet();
            if (unlockOnly)
            {
                unlocked.Add(astrolabeId);
            }
            else
            {
                unlocked.Add(astrolabeId);
                if (operation == 1) equipped.Add(astrolabeId);
                else if (operation == 2) equipped.Remove(astrolabeId);
                else if (!equipped.Add(astrolabeId)) equipped.Remove(astrolabeId);
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE hero SET unlock_astrolabe=$unlock, using_astrolabe=$using, update_ts=$ts WHERE uid=$uid AND id=$id";
                update.Parameters.AddWithValue("$uid", uid);
                update.Parameters.AddWithValue("$id", heroId);
                update.Parameters.AddWithValue("$unlock", JsonSerializer.Serialize(unlocked.OrderBy(x => x)));
                update.Parameters.AddWithValue("$using", JsonSerializer.Serialize(equipped.OrderBy(x => x)));
                update.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> SetHeroSkinAsync(long uid, long heroId, int skinId, bool battleSkin, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0 || skinId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = battleSkin
                ? "UPDATE hero SET battle_using_skin=$skin, update_ts=$ts WHERE uid=$uid AND id=$id"
                : "UPDATE hero SET using_skin=$skin, battle_using_skin=$skin, update_ts=$ts WHERE uid=$uid AND id=$id";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$id", heroId);
            command.Parameters.AddWithValue("$skin", skinId);
            command.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> UpgradeHeroWeaponAsync(long uid, long heroId, bool breakthrough, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = breakthrough
                ? "UPDATE hero SET weapon_break=MIN(4, COALESCE(weapon_break,0)+1), update_ts=$ts WHERE uid=$uid AND id=$id"
                : "UPDATE hero SET weapon_exp=COALESCE(weapon_exp,0)+2000, update_ts=$ts WHERE uid=$uid AND id=$id";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$id", heroId);
            command.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<IReadOnlyList<uint>> GetHeroFavoritesAsync(long uid, CancellationToken cancellationToken = default)
    {
        if (uid <= 0) return [];
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureHeroFavoritesTableAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT hero_id FROM hero_favorites WHERE uid=$uid ORDER BY hero_id";
        command.Parameters.AddWithValue("$uid", uid);
        var list = new List<uint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add((uint)reader.GetInt64(0));
        return list;
    }

    public async Task<bool> SetHeroFavoriteAsync(long uid, long heroId, bool isFavorite, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureHeroFavoritesTableAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = isFavorite
                ? "INSERT OR IGNORE INTO hero_favorites(uid, hero_id) VALUES($uid, $hid)"
                : "DELETE FROM hero_favorites WHERE uid=$uid AND hero_id=$hid";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$hid", heroId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> TranscendHeroAsync(long uid, long heroId, int num, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            if (num > 0)
            {
                await using var dec = connection.CreateCommand();
                dec.Transaction = transaction;
                dec.CommandText = "UPDATE hero_piece SET num=MAX(0, num-$num) WHERE uid=$uid AND hero_id=$hid";
                dec.Parameters.AddWithValue("$uid", uid);
                dec.Parameters.AddWithValue("$hid", heroId);
                dec.Parameters.AddWithValue("$num", num);
                await dec.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE hero SET star=MIN(600, COALESCE(star,300)+100), update_ts=$ts WHERE uid=$uid AND id=$hid";
                update.Parameters.AddWithValue("$uid", uid);
                update.Parameters.AddWithValue("$hid", heroId);
                update.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }


    private static async Task EnsureHeroFavoritesTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS hero_favorites (uid INTEGER NOT NULL, hero_id INTEGER NOT NULL, PRIMARY KEY(uid, hero_id))";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ResetAstrolabeAsync(long uid, long heroId, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE hero SET using_astrolabe='[]', update_ts=$ts WHERE uid=$uid AND id=$hid";
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$hid", heroId);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { _writeGate.Release(); }
    }

    public sealed record HeroWarpSlot(int SlotId, int TalentPoints, IReadOnlyList<(uint Id, uint Level)> Skills);

    public async Task<Dictionary<long, List<HeroWarpSlot>>> GetHeroWarpAsync(long uid, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<long, List<HeroWarpSlot>>();
        if (uid <= 0) return result;
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureHeroWarpTableAsync(connection, cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT hero_id, slot_id, talent_points, skills_json FROM hero_warp WHERE uid=$uid ORDER BY hero_id, slot_id";
        cmd.Parameters.AddWithValue("$uid", uid);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var heroId = reader.GetInt64(0);
            var slotId = reader.GetInt32(1);
            var tp = reader.GetInt32(2);
            var skills = ParseWarpSkills(reader.IsDBNull(3) ? "[]" : reader.GetString(3));
            if (!result.TryGetValue(heroId, out var list))
                result[heroId] = list = [];
            list.Add(new HeroWarpSlot(slotId, tp, skills));
        }
        return result;
    }

    public async Task<bool> UpdateHeroWarpSlotAsync(long uid, long heroId, int slotId, int lvUpNum, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0 || slotId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureHeroWarpTableAsync(connection, cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO hero_warp(uid, hero_id, slot_id, talent_points, skills_json)
                VALUES($uid, $hid, $slot, $tp, '[]')
                ON CONFLICT(uid, hero_id, slot_id) DO UPDATE SET talent_points = talent_points + $tp
                """;
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$hid", heroId);
            cmd.Parameters.AddWithValue("$slot", slotId);
            cmd.Parameters.AddWithValue("$tp", Math.Max(1, lvUpNum));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> SetHeroWarpSkillsAsync(long uid, long heroId, int slotId, IReadOnlyList<(uint Id, uint Level)> skills, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0 || slotId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureHeroWarpTableAsync(connection, cancellationToken);
            var json = JsonSerializer.Serialize(skills.Select(s => new[] { s.Id, s.Level }));
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO hero_warp(uid, hero_id, slot_id, talent_points, skills_json)
                VALUES($uid, $hid, $slot, 0, $skills)
                ON CONFLICT(uid, hero_id, slot_id) DO UPDATE SET skills_json = excluded.skills_json
                """;
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$hid", heroId);
            cmd.Parameters.AddWithValue("$slot", slotId);
            cmd.Parameters.AddWithValue("$skills", json);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    private static async Task EnsureHeroWarpTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS hero_warp (uid INTEGER NOT NULL, hero_id INTEGER NOT NULL, slot_id INTEGER NOT NULL, talent_points INTEGER NOT NULL DEFAULT 0, skills_json TEXT NOT NULL DEFAULT '[]', PRIMARY KEY(uid, hero_id, slot_id))";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static List<(uint Id, uint Level)> ParseWarpSkills(string json)
    {
        var list = new List<(uint Id, uint Level)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                if (elem.ValueKind == JsonValueKind.Array && elem.GetArrayLength() >= 2 &&
                    elem[0].TryGetUInt32(out var id) && elem[1].TryGetUInt32(out var lvl))
                    list.Add((id, lvl));
            }
        }
        catch { }
        return list;
    }

    public sealed record HeroArchiveState(uint ArchiveId, uint Exp, IReadOnlyList<uint> TextList, IReadOnlyList<uint> VideoList);

    public async Task<IReadOnlyList<HeroArchiveState>> GetHeroArchivesAsync(long uid, CancellationToken cancellationToken = default)
    {
        var list = new List<HeroArchiveState>();
        if (uid <= 0) return list;
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureHeroArchiveStateTableAsync(connection, cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT archive_id, exp, text_list_json, video_list_json FROM hero_archive_state WHERE uid=$uid ORDER BY archive_id";
        cmd.Parameters.AddWithValue("$uid", uid);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new HeroArchiveState(
                (uint)reader.GetInt64(0),
                (uint)reader.GetInt64(1),
                ParseIntList(reader.IsDBNull(2) ? "[]" : reader.GetString(2)).Select(x => (uint)x).ToArray(),
                ParseIntList(reader.IsDBNull(3) ? "[]" : reader.GetString(3)).Select(x => (uint)x).ToArray()));
        }
        return list;
    }

    public async Task<bool> SendHeroArchiveGiftsAsync(long uid, uint archiveId, IReadOnlyList<(long ItemId, long Count)> gifts, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || archiveId == 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureHeroArchiveStateTableAsync(connection, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            long gainedExp = 0;
            foreach (var (itemId, count) in gifts)
            {
                if (itemId <= 0 || count <= 0) continue;
                await TryConsumeUpToInTxAsync(connection, transaction, uid, itemId, count, cancellationToken);
                gainedExp += count * 100;
            }
            if (gainedExp == 0) gainedExp = 100;

            await using (var upArchive = connection.CreateCommand())
            {
                upArchive.Transaction = transaction;
                upArchive.CommandText = """
                    INSERT INTO hero_archive_state(uid, archive_id, exp, text_list_json, video_list_json)
                    VALUES($uid, $aid, $exp, '[]', '[]')
                    ON CONFLICT(uid, archive_id) DO UPDATE SET exp = exp + $exp
                    """;
                upArchive.Parameters.AddWithValue("$uid", uid);
                upArchive.Parameters.AddWithValue("$aid", (long)archiveId);
                upArchive.Parameters.AddWithValue("$exp", gainedExp);
                await upArchive.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var upHero = connection.CreateCommand())
            {
                upHero.Transaction = transaction;
                upHero.CommandText = "UPDATE hero SET trust_exp=COALESCE(trust_exp,0)+$exp, trust_level=MIN(5, 1 + (COALESCE(trust_exp,0)+$exp)/500), update_ts=$ts WHERE uid=$uid AND id=$aid";
                upHero.Parameters.AddWithValue("$uid", uid);
                upHero.Parameters.AddWithValue("$aid", (long)archiveId);
                upHero.Parameters.AddWithValue("$exp", gainedExp);
                upHero.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await upHero.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> UnlockHeroArchiveMediaAsync(long uid, uint archiveId, IReadOnlyList<uint>? textIds, IReadOnlyList<uint>? videoIds, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || archiveId == 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureHeroArchiveStateTableAsync(connection, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var texts = new HashSet<int>();
            var videos = new HashSet<int>();
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT text_list_json, video_list_json FROM hero_archive_state WHERE uid=$uid AND archive_id=$aid";
                read.Parameters.AddWithValue("$uid", uid);
                read.Parameters.AddWithValue("$aid", (long)archiveId);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    foreach (var t in ParseIntList(reader.IsDBNull(0) ? "[]" : reader.GetString(0))) texts.Add(t);
                    foreach (var v in ParseIntList(reader.IsDBNull(1) ? "[]" : reader.GetString(1))) videos.Add(v);
                }
            }
            if (textIds is not null) foreach (var t in textIds) texts.Add((int)t);
            if (videoIds is not null) foreach (var v in videoIds) videos.Add((int)v);

            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO hero_archive_state(uid, archive_id, exp, text_list_json, video_list_json)
                    VALUES($uid, $aid, 0, $texts, $videos)
                    ON CONFLICT(uid, archive_id) DO UPDATE SET text_list_json=excluded.text_list_json, video_list_json=excluded.video_list_json
                    """;
                upsert.Parameters.AddWithValue("$uid", uid);
                upsert.Parameters.AddWithValue("$aid", (long)archiveId);
                upsert.Parameters.AddWithValue("$texts", JsonSerializer.Serialize(texts.Order()));
                upsert.Parameters.AddWithValue("$videos", JsonSerializer.Serialize(videos.Order()));
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> EquipAstrolabeSuitAsync(long uid, long heroId, int suitId, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE hero SET using_astrolabe=$suit, update_ts=$ts WHERE uid=$uid AND id=$hid";
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$hid", heroId);
            cmd.Parameters.AddWithValue("$suit", suitId > 0 ? JsonSerializer.Serialize(new[] { suitId }) : "[]");
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally { _writeGate.Release(); }
    }

    private static int GetTrustGiftBaseExp(long itemId) => itemId switch
    {
        30012 => 40,
        30013 => 80,
        30014 => 160,
        30015 => 320,
        _ => 20
    };

    private static (int Level, int Exp) ComputeTrustProgress(int currentLevel, int currentExp, long addedExp)
    {
        var level = Math.Clamp(currentLevel, 1, 5);
        var exp = Math.Max(0L, currentExp) + Math.Max(0L, addedExp);
        while (level < 5)
        {
            var cap = level switch { 1 => 1500L, 2 => 1700L, 3 => 2300L, 4 => 2500L, _ => 0L };
            if (cap <= 0 || exp < cap) break;
            exp -= cap;
            level++;
        }
        if (level >= 5) exp = 0;
        return (level, (int)Math.Min(int.MaxValue, exp));
    }

    public async Task<bool> ApplyHeroItemsAsync(long uid, long heroId, IReadOnlyList<(long ItemId, long Count)> items, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureHeroArchiveStateTableAsync(connection, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            int curLevel = 1, curExp = 0, curMood = 1;
            await using (var readHero = connection.CreateCommand())
            {
                readHero.Transaction = transaction;
                readHero.CommandText = "SELECT COALESCE(trust_level,1), COALESCE(trust_exp,0), COALESCE(trust_mood,1) FROM hero WHERE uid=$uid AND id=$hid";
                readHero.Parameters.AddWithValue("$uid", uid);
                readHero.Parameters.AddWithValue("$hid", heroId);
                await using var reader = await readHero.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    curLevel = Math.Max(1, I(reader, 0));
                    curExp = Math.Max(0, I(reader, 1));
                    curMood = Math.Clamp(I(reader, 2), 1, 3);
                }
            }

            double moodRate = curMood switch { 2 => 1.1, 3 => 1.3, _ => 1.0 };
            long gainedTrustExp = 0;
            foreach (var (itemId, count) in items)
            {
                if (itemId <= 0 || count <= 0) continue;
                await TryConsumeUpToInTxAsync(connection, transaction, uid, itemId, count, cancellationToken);
                gainedTrustExp += (long)Math.Round(count * GetTrustGiftBaseExp(itemId) * moodRate);
            }

            if (gainedTrustExp > 0)
            {
                var (newLevel, newExp) = ComputeTrustProgress(curLevel, curExp, gainedTrustExp);
                var syncedArchiveExp = newLevel switch
                {
                    1 => Math.Min(999, newExp),
                    2 => 1000,
                    3 => 1500,
                    4 => 2000,
                    _ => 3000
                };
                await using (var upArchive = connection.CreateCommand())
                {
                    upArchive.Transaction = transaction;
                    upArchive.CommandText = """
                        INSERT INTO hero_archive_state(uid, archive_id, exp, text_list_json, video_list_json)
                        VALUES($uid, $aid, $exp, '[]', '[]')
                        ON CONFLICT(uid, archive_id) DO UPDATE SET exp = $exp
                        """;
                    upArchive.Parameters.AddWithValue("$uid", uid);
                    upArchive.Parameters.AddWithValue("$aid", heroId);
                    upArchive.Parameters.AddWithValue("$exp", syncedArchiveExp);
                    await upArchive.ExecuteNonQueryAsync(cancellationToken);
                }

                var newMood = Math.Clamp(curMood + 1, 1, 3);
                await using var upHero = connection.CreateCommand();
                upHero.Transaction = transaction;
                upHero.CommandText = "UPDATE hero SET trust_exp=$exp, trust_level=$lvl, trust_mood=$mood, update_ts=$ts WHERE uid=$uid AND id=$hid";
                upHero.Parameters.AddWithValue("$uid", uid);
                upHero.Parameters.AddWithValue("$hid", heroId);
                upHero.Parameters.AddWithValue("$exp", newExp);
                upHero.Parameters.AddWithValue("$lvl", newLevel);
                upHero.Parameters.AddWithValue("$mood", newMood);
                upHero.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await upHero.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    private static async Task EnsureHeroTrustTablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS hero_trust_claimed (
                uid INTEGER NOT NULL,
                reward_id INTEGER NOT NULL,
                PRIMARY KEY(uid, reward_id)
            );
            CREATE TABLE IF NOT EXISTS hero_trust_relation (
                uid INTEGER NOT NULL,
                relation_id INTEGER NOT NULL,
                group_index INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(uid, relation_id)
            );
            """;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<uint>> GetClaimedTrustRewardsAsync(long uid, CancellationToken cancellationToken = default)
    {
        var list = new List<uint>();
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureHeroTrustTablesAsync(connection, cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT reward_id FROM hero_trust_claimed WHERE uid=$uid ORDER BY reward_id";
        cmd.Parameters.AddWithValue("$uid", uid);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add((uint)reader.GetInt64(0));
        return list;
    }

    public async Task<IReadOnlyList<(long ItemId, long Count)>> ClaimHeroTrustStageRewardAsync(long uid, long heroId, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0) return [];
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureHeroTrustTablesAsync(connection, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var rewardKey = heroId * 100 + 1;
            await using (var ins = connection.CreateCommand())
            {
                ins.Transaction = transaction;
                ins.CommandText = "INSERT OR IGNORE INTO hero_trust_claimed(uid, reward_id) VALUES($uid, $rid)";
                ins.Parameters.AddWithValue("$uid", uid);
                ins.Parameters.AddWithValue("$rid", rewardKey);
                await ins.ExecuteNonQueryAsync(cancellationToken);
            }
            var rewards = new (long ItemId, long Count)[] { (1, 50) };
            await GrantInTxAsync(connection, transaction, uid, 1, 50, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return rewards;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> UnlockHeroTrustRelationAsync(long uid, long relationId, uint groupIndex, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || relationId <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureHeroTrustTablesAsync(connection, cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO hero_trust_relation(uid, relation_id, group_index)
                VALUES($uid, $rid, $grp)
                ON CONFLICT(uid, relation_id) DO UPDATE SET group_index=$grp
                """;
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$rid", relationId);
            cmd.Parameters.AddWithValue("$grp", (int)groupIndex);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<uint> InteractHeroTrustMoodAsync(long uid, long heroId, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || heroId <= 0) return 1;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            int curMood = 1;
            await using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT COALESCE(trust_mood,1) FROM hero WHERE uid=$uid AND id=$hid";
                read.Parameters.AddWithValue("$uid", uid);
                read.Parameters.AddWithValue("$hid", heroId);
                var scalar = await read.ExecuteScalarAsync(cancellationToken);
                if (scalar is not null && scalar != DBNull.Value)
                    curMood = Math.Clamp(Convert.ToInt32(scalar), 1, 3);
            }
            var nextMood = curMood >= 3 ? 3 : curMood + 1;
            await using (var up = connection.CreateCommand())
            {
                up.CommandText = "UPDATE hero SET trust_mood=$mood, update_ts=$ts WHERE uid=$uid AND id=$hid";
                up.Parameters.AddWithValue("$uid", uid);
                up.Parameters.AddWithValue("$hid", heroId);
                up.Parameters.AddWithValue("$mood", nextMood);
                up.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await up.ExecuteNonQueryAsync(cancellationToken);
            }
            return (uint)nextMood;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<IReadOnlyList<(long ItemId, long Count)>> ClaimTrustRewardByIdAsync(long uid, long rewardId, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || rewardId <= 0) return [];
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var connection = await OpenAsync(cancellationToken);
            await EnsureHeroTrustTablesAsync(connection, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var ins = connection.CreateCommand())
            {
                ins.Transaction = transaction;
                ins.CommandText = "INSERT OR IGNORE INTO hero_trust_claimed(uid, reward_id) VALUES($uid, $rid)";
                ins.Parameters.AddWithValue("$uid", uid);
                ins.Parameters.AddWithValue("$rid", rewardId);
                if (await ins.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return [];
                }
            }
            var rewards = new (long ItemId, long Count)[] { (1, 50) };
            await GrantInTxAsync(connection, transaction, uid, 1, 50, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return rewards;
        }
        finally { _writeGate.Release(); }
    }

    private static async Task EnsureHeroArchiveStateTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS hero_archive_state (uid INTEGER NOT NULL, archive_id INTEGER NOT NULL, exp INTEGER NOT NULL DEFAULT 0, text_list_json TEXT NOT NULL DEFAULT '[]', video_list_json TEXT NOT NULL DEFAULT '[]', PRIMARY KEY(uid, archive_id))";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<BattleSettleResult> SettleBattleAsync(long uid, long stageId, IReadOnlyList<(long ItemId, long Count)> firstClearRewards, IReadOnlyList<(long ItemId, long Count)> repeatRewards, long playerExpGain, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || stageId <= 0) return new BattleSettleResult(false, 0, 0, 1, []);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            var clearTimes = 0;
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT COALESCE(clear_times,0) FROM chapter WHERE uid=$uid AND id=$id";
                read.Parameters.AddWithValue("$uid", uid);
                read.Parameters.AddWithValue("$id", stageId);
                var scalar = await read.ExecuteScalarAsync(cancellationToken);
                if (scalar is not null && scalar != DBNull.Value) clearTimes = Convert.ToInt32(scalar);
            }

            var isFirstClear = clearTimes == 0;
            clearTimes++;
            var starsJson = "[1,1,1]";
            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO chapter(uid,id,star_list,clear_times,first_clear_ts,update_ts)
                    VALUES($uid,$id,$stars,$clear,$now,$now)
                    ON CONFLICT(uid,id) DO UPDATE SET star_list=$stars, clear_times=chapter.clear_times+1, update_ts=$now
                    """;
                upsert.Parameters.AddWithValue("$uid", uid);
                upsert.Parameters.AddWithValue("$id", stageId);
                upsert.Parameters.AddWithValue("$stars", starsJson);
                upsert.Parameters.AddWithValue("$clear", clearTimes);
                upsert.Parameters.AddWithValue("$now", now);
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            var drops = (isFirstClear && firstClearRewards.Count > 0 ? firstClearRewards : repeatRewards).ToList();
            foreach (var (itemId, count) in drops)
            {
                if (itemId <= 0 || count <= 0) continue;
                await GrantInTxAsync(connection, transaction, uid, itemId, count, now, cancellationToken);
            }

            long totalExp = 0;
            int level = 1;
            await using (var readUser = connection.CreateCommand())
            {
                readUser.Transaction = transaction;
                readUser.CommandText = "SELECT COALESCE(exp,0), COALESCE(level,1) FROM users WHERE uid=$uid";
                readUser.Parameters.AddWithValue("$uid", uid);
                await using var reader = await readUser.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    totalExp = reader.GetInt64(0) + Math.Max(0, playerExpGain);
                    level = Math.Max(1, Math.Min(100, (int)(totalExp / 1000) + 1));
                }
            }
            await using (var updateUser = connection.CreateCommand())
            {
                updateUser.Transaction = transaction;
                updateUser.CommandText = "UPDATE users SET exp=$exp, level=$lvl WHERE uid=$uid";
                updateUser.Parameters.AddWithValue("$uid", uid);
                updateUser.Parameters.AddWithValue("$exp", totalExp);
                updateUser.Parameters.AddWithValue("$lvl", level);
                await updateUser.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new BattleSettleResult(true, clearTimes, totalExp, level, drops);
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> ClaimChapterRewardAsync(long uid, long chapterId, int rewardOrder, IReadOnlyList<(long ItemId, long Count)> rewards, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || chapterId <= 0 || rewardOrder <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            await using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT 1 FROM chapter_star_reward WHERE uid=$uid AND chapter_id=$cid AND reward_order=$ord";
                check.Parameters.AddWithValue("$uid", uid);
                check.Parameters.AddWithValue("$cid", chapterId);
                check.Parameters.AddWithValue("$ord", rewardOrder);
                if (await check.ExecuteScalarAsync(cancellationToken) is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return false;
                }
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO chapter_star_reward(uid,chapter_id,reward_order,receive_time) VALUES($uid,$cid,$ord,$now)";
                insert.Parameters.AddWithValue("$uid", uid);
                insert.Parameters.AddWithValue("$cid", chapterId);
                insert.Parameters.AddWithValue("$ord", rewardOrder);
                insert.Parameters.AddWithValue("$now", now);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var (itemId, count) in rewards)
            {
                if (itemId > 0 && count > 0)
                    await GrantInTxAsync(connection, transaction, uid, itemId, count, now, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<DrawResult> ExecuteDrawAsync(long uid, int poolId, int pullCount, long? costItemId, long costAmount,
        Func<int, (long ItemId, long Count, bool IsSsr, bool IsHero, IReadOnlyList<int> Skills)> rollFunc, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || poolId <= 0 || pullCount is not (1 or 10))
            return new DrawResult(false, [], 0, false);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            if (costItemId is > 0 && costAmount > 0)
            {
                if (!await TryConsumeInTxAsync(connection, transaction, uid, costItemId.Value, costAmount, cancellationToken))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new DrawResult(false, [], 0, false);
                }
            }

            var sinceSsr = 0;
            var totalDraws = 0;
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT COALESCE(since_ssr,0), COALESCE(total_draws,0) FROM draw_state WHERE uid=$uid AND pool_id=$pool";
                read.Parameters.AddWithValue("$uid", uid);
                read.Parameters.AddWithValue("$pool", poolId);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    sinceSsr = reader.GetInt32(0);
                    totalDraws = reader.GetInt32(1);
                }
            }

            var firstSsr = totalDraws == 0;
            var results = new List<DrawPullItem>(pullCount);
            for (var i = 0; i < pullCount; i++)
            {
                sinceSsr++;
                totalDraws++;
                var rolled = rollFunc(sinceSsr);
                if (rolled.IsSsr) sinceSsr = 0;

                var isNewHero = false;
                if (rolled.IsHero)
                {
                    var owned = await HeroExistsAsync(connection, transaction, uid, rolled.ItemId, cancellationToken);
                    if (!owned)
                    {
                        await InsertHeroAsync(connection, transaction, uid, rolled.ItemId, rolled.Skills, rolled.IsSsr ? 300 : 200, now, cancellationToken);
                        isNewHero = true;
                    }
                    else
                    {
                        // Duplicate hero converts to generic shards/currency
                        await GrantInTxAsync(connection, transaction, uid, 10000 + rolled.ItemId, rolled.IsSsr ? 30 : 12, now, cancellationToken);
                    }
                }
                else
                {
                    await GrantInTxAsync(connection, transaction, uid, rolled.ItemId, rolled.Count, now, cancellationToken);
                }

                await using (var rec = connection.CreateCommand())
                {
                    rec.Transaction = transaction;
                    rec.CommandText = "INSERT INTO draw_record(uid,pool_id,pool_group,item_id,item_num,draw_ts) VALUES($uid,$pool,$grp,$item,$num,$ts)";
                    rec.Parameters.AddWithValue("$uid", uid);
                    rec.Parameters.AddWithValue("$pool", poolId);
                    rec.Parameters.AddWithValue("$grp", poolId.ToString());
                    rec.Parameters.AddWithValue("$item", rolled.ItemId);
                    rec.Parameters.AddWithValue("$num", rolled.Count);
                    rec.Parameters.AddWithValue("$ts", now);
                    await rec.ExecuteNonQueryAsync(cancellationToken);
                }

                results.Add(new DrawPullItem(rolled.ItemId, rolled.Count, rolled.IsSsr, isNewHero));
            }

            await SyncHeroCountAsync(connection, transaction, uid, cancellationToken);
            await using (var saveState = connection.CreateCommand())
            {
                saveState.Transaction = transaction;
                saveState.CommandText = """
                    INSERT INTO draw_state(uid,pool_id,since_ssr,total_draws,pool_group) VALUES($uid,$pool,$ssr,$tot,$grp)
                    ON CONFLICT(uid,pool_id) DO UPDATE SET since_ssr=$ssr, total_draws=$tot
                    """;
                saveState.Parameters.AddWithValue("$uid", uid);
                saveState.Parameters.AddWithValue("$pool", poolId);
                saveState.Parameters.AddWithValue("$ssr", sinceSsr);
                saveState.Parameters.AddWithValue("$tot", totalDraws);
                saveState.Parameters.AddWithValue("$grp", poolId.ToString());
                await saveState.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new DrawResult(true, results, sinceSsr, firstSsr && results.Any(x => x.IsSsr));
        }
        finally { _writeGate.Release(); }
    }

    public async Task<DrawHistoryResult> GetDrawHistoryAsync(long uid, int poolId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var pity = 0;
        await using (var readState = connection.CreateCommand())
        {
            readState.CommandText = "SELECT COALESCE(since_ssr,0) FROM draw_state WHERE uid=$uid AND pool_id=$pool";
            readState.Parameters.AddWithValue("$uid", uid);
            readState.Parameters.AddWithValue("$pool", poolId);
            pity = Convert.ToInt32(await readState.ExecuteScalarAsync(cancellationToken) ?? 0);
        }
        var records = new List<DrawHistoryEntry>();
        await using (var readRec = connection.CreateCommand())
        {
            readRec.CommandText = "SELECT item_id, item_num, draw_ts FROM draw_record WHERE uid=$uid AND pool_id=$pool ORDER BY id DESC LIMIT 50";
            readRec.Parameters.AddWithValue("$uid", uid);
            readRec.Parameters.AddWithValue("$pool", poolId);
            await using var reader = await readRec.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                records.Add(new DrawHistoryEntry(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
        }
        return new DrawHistoryResult(pity, records);
    }

    public async Task<bool> ConsumeAndGrantAsync(long uid, IReadOnlyList<(long ItemId, long Count)> costs, IReadOnlyList<(long ItemId, long Count)> grants, CancellationToken cancellationToken = default)
    {
        if (uid <= 0) return false;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (var (itemId, count) in costs)
            {
                if (itemId <= 0 || count <= 0) continue;
                if (!await TryConsumeInTxAsync(connection, transaction, uid, itemId, count, cancellationToken))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }
            }
            foreach (var (itemId, count) in grants)
            {
                if (itemId <= 0 || count <= 0) continue;
                await GrantInTxAsync(connection, transaction, uid, itemId, count, now, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<IReadOnlyList<MailSummary>> GetMailsAsync(long uid, CancellationToken cancellationToken = default)
    {
        var list = new List<MailSummary>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT mail_id, send_time, title, attach_flag, read_flag, attachment_json, expire_time, star_state, template_id FROM mail WHERE uid=$uid ORDER BY send_time DESC, mail_id DESC";
        command.Parameters.AddWithValue("$uid", uid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new MailSummary(
                reader.GetInt64(0), L(reader, 1), reader.IsDBNull(2) ? "" : reader.GetString(2),
                I(reader, 3), I(reader, 4), ParseAttachments(reader.IsDBNull(5) ? "[]" : reader.GetString(5)),
                L(reader, 6), I(reader, 7), I(reader, 8)));
        }
        return list;
    }

    public async Task<MailDetail?> GetMailDetailAsync(long uid, long mailId, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            MailDetail? detail = null;
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT sender, content_json, attachment_json, template_id FROM mail WHERE uid=$uid AND mail_id=$id";
                read.Parameters.AddWithValue("$uid", uid);
                read.Parameters.AddWithValue("$id", mailId);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    detail = new MailDetail(mailId,
                        reader.IsDBNull(0) ? "System" : reader.GetString(0),
                        reader.IsDBNull(1) ? "" : reader.GetString(1),
                        ParseAttachments(reader.IsDBNull(2) ? "[]" : reader.GetString(2)),
                        I(reader, 3));
                }
            }
            if (detail is not null)
            {
                await using var mark = connection.CreateCommand();
                mark.Transaction = transaction;
                mark.CommandText = "UPDATE mail SET read_flag=1, update_ts=$ts WHERE uid=$uid AND mail_id=$id";
                mark.Parameters.AddWithValue("$uid", uid);
                mark.Parameters.AddWithValue("$id", mailId);
                mark.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await mark.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return detail;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<MailClaimResult> ClaimMailAsync(long uid, long mailId, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var targetIds = new List<(long MailId, IReadOnlyList<(long Id, long Count)> Attachments)>();
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = mailId == 0
                    ? "SELECT mail_id, attachment_json FROM mail WHERE uid=$uid AND attach_flag=0"
                    : "SELECT mail_id, attachment_json FROM mail WHERE uid=$uid AND mail_id=$id AND attach_flag=0";
                read.Parameters.AddWithValue("$uid", uid);
                if (mailId != 0) read.Parameters.AddWithValue("$id", mailId);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    targetIds.Add((reader.GetInt64(0), ParseAttachments(reader.IsDBNull(1) ? "[]" : reader.GetString(1))));
            }

            if (targetIds.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return new MailClaimResult(false, [], []);
            }

            var allAttachments = new List<(long Id, long Count)>();
            var claimedIds = new List<long>();
            foreach (var (id, attachments) in targetIds)
            {
                claimedIds.Add(id);
                foreach (var (itemId, count) in attachments)
                {
                    if (itemId <= 0 || count <= 0) continue;
                    allAttachments.Add((itemId, count));
                    await GrantInTxAsync(connection, transaction, uid, itemId, count, now, cancellationToken);
                }
                await using var mark = connection.CreateCommand();
                mark.Transaction = transaction;
                mark.CommandText = "UPDATE mail SET read_flag=1, attach_flag=1, update_ts=$ts WHERE uid=$uid AND mail_id=$id";
                mark.Parameters.AddWithValue("$uid", uid);
                mark.Parameters.AddWithValue("$id", id);
                mark.Parameters.AddWithValue("$ts", now);
                await mark.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new MailClaimResult(true, allAttachments, claimedIds);
        }
        finally { _writeGate.Release(); }
    }

    public async Task<IReadOnlyList<long>> DeleteMailAsync(long uid, long mailId, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var deleted = new List<long>();
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = mailId == 0
                    ? "SELECT mail_id FROM mail WHERE uid=$uid AND read_flag=1 AND attach_flag=1"
                    : "SELECT mail_id FROM mail WHERE uid=$uid AND mail_id=$id";
                read.Parameters.AddWithValue("$uid", uid);
                if (mailId != 0) read.Parameters.AddWithValue("$id", mailId);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) deleted.Add(reader.GetInt64(0));
            }
            foreach (var id in deleted)
            {
                await using var del = connection.CreateCommand();
                del.Transaction = transaction;
                del.CommandText = "DELETE FROM mail WHERE uid=$uid AND mail_id=$id";
                del.Parameters.AddWithValue("$uid", uid);
                del.Parameters.AddWithValue("$id", id);
                await del.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return deleted;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<IReadOnlyList<(long ItemId, long Count)>> ClaimTasksAsync(long uid, IReadOnlyList<long> taskIds, Func<long, IReadOnlyList<(long ItemId, long Count)>> rewardLookup, CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || taskIds.Count == 0) return [];
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var granted = new List<(long ItemId, long Count)>();
            foreach (var taskId in taskIds.Distinct())
            {
                await using (var check = connection.CreateCommand())
                {
                    check.Transaction = transaction;
                    check.CommandText = "SELECT COALESCE(complete_flag,0) FROM task WHERE uid=$uid AND task_id=$tid";
                    check.Parameters.AddWithValue("$uid", uid);
                    check.Parameters.AddWithValue("$tid", taskId);
                    var flag = await check.ExecuteScalarAsync(cancellationToken);
                    if (flag is not null && flag != DBNull.Value && Convert.ToInt32(flag) >= 2) continue;
                }
                await using (var upsert = connection.CreateCommand())
                {
                    upsert.Transaction = transaction;
                    upsert.CommandText = """
                        INSERT INTO task(uid,task_id,progress,complete_flag,claimed_ts) VALUES($uid,$tid,1,2,$now)
                        ON CONFLICT(uid,task_id) DO UPDATE SET complete_flag=2, claimed_ts=$now
                        """;
                    upsert.Parameters.AddWithValue("$uid", uid);
                    upsert.Parameters.AddWithValue("$tid", taskId);
                    upsert.Parameters.AddWithValue("$now", now);
                    await upsert.ExecuteNonQueryAsync(cancellationToken);
                }
                foreach (var reward in rewardLookup(taskId))
                {
                    if (reward.ItemId <= 0 || reward.Count <= 0) continue;
                    granted.Add(reward);
                    await GrantInTxAsync(connection, transaction, uid, reward.ItemId, reward.Count, now, cancellationToken);
                }
            }
            await transaction.CommitAsync(cancellationToken);
            return granted;
        }
        finally { _writeGate.Release(); }
    }

    private static async Task<bool> HeroExistsAsync(SqliteConnection connection, SqliteTransaction transaction, long uid, long heroId, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT 1 FROM hero WHERE uid=$uid AND id=$id";
        cmd.Parameters.AddWithValue("$uid", uid);
        cmd.Parameters.AddWithValue("$id", heroId);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task InsertHeroAsync(SqliteConnection connection, SqliteTransaction transaction, long uid, long heroId, IReadOnlyList<int>? skills, int star, long now, CancellationToken cancellationToken)
    {
        var skillPairs = (skills is { Count: > 0 } ? skills : new[] { (int)heroId * 1000 + 101, (int)heroId * 1000 + 201, (int)heroId * 1000 + 202, (int)heroId * 1000 + 203, (int)heroId * 1000 + 209, (int)heroId * 1000 + 305 })
            .Select(s => new[] { s, 1 });
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO hero(uid,id,level,star,exp,skill_list,weapon_exp,weapon_break,weapon_servant_uid,servant,
                using_astrolabe,unlock_astrolabe,using_skin,break_level,exclusive_skill_list,weapon_module_level,
                skill_intensify,battle_using_skin,clear_times,unlock,equip_slot,module_level,module_assignment,chip_state,update_ts,name,trust_level,trust_mood)
            VALUES($uid,$id,1,$star,0,$skills,0,0,0,'[]','[]','[]',$id,0,'[]',0,'[]',$id,0,1,'[]',0,0,'[]',$now,'',1,1)
            """;
        cmd.Parameters.AddWithValue("$uid", uid);
        cmd.Parameters.AddWithValue("$id", heroId);
        cmd.Parameters.AddWithValue("$star", star);
        cmd.Parameters.AddWithValue("$skills", JsonSerializer.Serialize(skillPairs));
        cmd.Parameters.AddWithValue("$now", now);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SyncHeroCountAsync(SqliteConnection connection, SqliteTransaction transaction, long uid, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "UPDATE users SET hero_num=(SELECT COUNT(*) FROM hero WHERE uid=$uid) WHERE uid=$uid";
        cmd.Parameters.AddWithValue("$uid", uid);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(bool Found, int Level, long Exp, int BreakLevel)> ReadHeroProgressAsync(SqliteConnection connection, SqliteTransaction transaction, long uid, long heroId, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT COALESCE(level,1), COALESCE(exp,0), COALESCE(break_level,0) FROM hero WHERE uid=$uid AND id=$id";
        cmd.Parameters.AddWithValue("$uid", uid);
        cmd.Parameters.AddWithValue("$id", heroId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (true, Math.Max(1, reader.GetInt32(0)), reader.GetInt64(1), reader.GetInt32(2))
            : (false, 0, 0, 0);
    }

    private static async Task<string> ResolveTableInTxAsync(SqliteConnection connection, SqliteTransaction transaction, long itemId, CancellationToken cancellationToken)
    {
        if (itemId < 100 || itemId is 53029 or 53111) return "currency";
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT category FROM item_catalog WHERE id=$id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", itemId);
        try
        {
            var cat = Convert.ToString(await cmd.ExecuteScalarAsync(cancellationToken)) ?? "";
            return string.Equals(cat, "currency", StringComparison.OrdinalIgnoreCase) ? "currency" : "material";
        }
        catch (SqliteException) { return "material"; }
    }

    private static async Task GrantInTxAsync(SqliteConnection connection, SqliteTransaction transaction, long uid, long itemId, long count, long now, CancellationToken cancellationToken)
    {
        if (await GrantEntityInTxAsync(connection, transaction, uid, itemId, count, now, cancellationToken)) return;
        var table = await ResolveTableInTxAsync(connection, transaction, itemId, cancellationToken);
        var other = table == "currency" ? "material" : "currency";
        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = $"INSERT INTO {table}(uid,id,num,update_ts) VALUES($uid,$id,$num,$ts) ON CONFLICT(uid,id) DO UPDATE SET num={table}.num+$num, update_ts=$ts";
            upsert.Parameters.AddWithValue("$uid", uid);
            upsert.Parameters.AddWithValue("$id", itemId);
            upsert.Parameters.AddWithValue("$num", count);
            upsert.Parameters.AddWithValue("$ts", now);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = $"DELETE FROM {other} WHERE uid=$uid AND id=$id";
        clear.Parameters.AddWithValue("$uid", uid);
        clear.Parameters.AddWithValue("$id", itemId);
        await clear.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TryConsumeInTxAsync(SqliteConnection connection, SqliteTransaction transaction, long uid, long itemId, long count, CancellationToken cancellationToken)
    {
        if (ItemKinds.IsPiece(itemId)) return await ConsumePiecesInTxAsync(connection, transaction, uid, itemId - 10000, count, cancellationToken);
        var table = await ResolveTableInTxAsync(connection, transaction, itemId, cancellationToken);
        long current = 0;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = $"SELECT COALESCE(num,0) FROM {table} WHERE uid=$uid AND id=$id";
            read.Parameters.AddWithValue("$uid", uid);
            read.Parameters.AddWithValue("$id", itemId);
            var scalar = await read.ExecuteScalarAsync(cancellationToken);
            if (scalar is not null && scalar != DBNull.Value) current = Convert.ToInt64(scalar);
        }
        if (current < count) return false;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = $"UPDATE {table} SET num=num-$cnt, update_ts=$ts WHERE uid=$uid AND id=$id";
        update.Parameters.AddWithValue("$uid", uid);
        update.Parameters.AddWithValue("$id", itemId);
        update.Parameters.AddWithValue("$cnt", count);
        update.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await update.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    private static async Task TryConsumeUpToInTxAsync(SqliteConnection connection, SqliteTransaction transaction, long uid, long itemId, long count, CancellationToken cancellationToken)
    {
        var table = await ResolveTableInTxAsync(connection, transaction, itemId, cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = $"UPDATE {table} SET num=MAX(0, num-$cnt), update_ts=$ts WHERE uid=$uid AND id=$id";
        update.Parameters.AddWithValue("$uid", uid);
        update.Parameters.AddWithValue("$id", itemId);
        update.Parameters.AddWithValue("$cnt", count);
        update.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static long GetHeroExpValue(long itemId) => itemId switch
    {
        40101 => 100,
        40102 => 500,
        40103 => 2000,
        40104 => 10000,
        _ => 1000
    };


    private static IReadOnlyList<(long Id, long Count)> ParseAttachments(string json)
    {
        var list = new List<(long Id, long Count)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                if (elem.ValueKind == JsonValueKind.Array && elem.GetArrayLength() >= 2 &&
                    elem[0].TryGetInt64(out var id) && elem[1].TryGetInt64(out var cnt))
                    list.Add((id, cnt));
                else if (elem.ValueKind == JsonValueKind.Object &&
                    elem.TryGetProperty("id", out var idEl) && elem.TryGetProperty("number", out var numEl) &&
                    idEl.TryGetInt64(out var oid) && numEl.TryGetInt64(out var ocnt))
                    list.Add((oid, ocnt));
            }
        }
        catch { }
        return list;
    }
}
