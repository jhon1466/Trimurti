using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Trimūrti.Server;

public sealed record CreateAccountRequest(string Login, string Password);
public sealed record LoginRequest(string Login, string Password);
public sealed record PublicAccount(string Id, string Login, DateTimeOffset CreatedAt);
public sealed record LoginSession(string AccountId, string Token, DateTimeOffset ExpiresAt);
public sealed record GatewayIdentity(long UserId, string Login, string GameToken, long RegisteredAt);

/// <summary>Local account identity bound to the game tables from Alpha Persei's database schema.</summary>
public sealed class AccountStore
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 210_000;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AccountStore(ILogger<AccountStore> logger)
    {
        _path = Environment.GetEnvironmentVariable("TRIMURTI_DB_PATH") ?? Path.Combine(AppContext.BaseDirectory, "Data", "trimurti.db");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "Data", "db_schema.sql");
        using var connection = Open();
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = File.ReadAllText(schemaPath, Encoding.UTF8);
            command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS trimurti_auth (
                    uid INTEGER PRIMARY KEY,
                    login TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    password_salt TEXT NOT NULL,
                    password_hash TEXT NOT NULL,
                    created_ts INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_sdk_token ON sdk(token);
                """;
            command.ExecuteNonQuery();
        }
        MigrateOutOfRangeUids(connection);
        var tableCount = Convert.ToInt32(Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table'"));
        logger.LogInformation("Trimūrti SQLite initialized at {DatabasePath} ({TableCount} tables)", _path, tableCount);
    }

    public async Task<PublicAccount> CreateAsync(string login, string password, CancellationToken cancellationToken)
    {
        login = NormalizeLogin(login);
        ValidatePassword(password);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
            var token = NewToken();
            var gameToken = NewToken();
            using var connection = Open();
            connection.Open();
            var uid = NextUid(connection);
            using var transaction = connection.BeginTransaction();
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO trimurti_auth(uid, login, password_salt, password_hash, created_ts) VALUES($uid,$login,$salt,$hash,$created)";
                    command.Parameters.AddWithValue("$uid", uid);
                    command.Parameters.AddWithValue("$login", login);
                    command.Parameters.AddWithValue("$salt", Convert.ToBase64String(salt));
                    command.Parameters.AddWithValue("$hash", Convert.ToBase64String(hash));
                    command.Parameters.AddWithValue("$created", now.ToUnixTimeSeconds());
                    command.ExecuteNonQuery();
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    var seed = LoadSeed();
                    var settings = seed.GetProperty("settings");
                    command.CommandText = "INSERT INTO users(uid, account, nick, level, exp, hero_num, plot_progress, portrait, icon_frame, register_ts) VALUES($uid,$login,$login,100,10000000,1,0,$portrait,$frame,$created)";
                    command.Parameters.AddWithValue("$uid", uid);
                    command.Parameters.AddWithValue("$login", login);
                    command.Parameters.AddWithValue("$portrait", settings.GetProperty("profile_avatar_default").GetInt32());
                    command.Parameters.AddWithValue("$frame", settings.GetProperty("profile_avatar_frame_default").GetInt32());
                    command.Parameters.AddWithValue("$created", now.ToUnixTimeSeconds());
                    command.ExecuteNonQuery();
                }
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO sdk(uid, token, gstoken, uid_sign, register_ts) VALUES($uid,$token,$gstoken,$sign,$created)";
                    command.Parameters.AddWithValue("$uid", uid);
                    command.Parameters.AddWithValue("$token", token);
                    command.Parameters.AddWithValue("$gstoken", gameToken);
                    command.Parameters.AddWithValue("$sign", NewToken());
                    command.Parameters.AddWithValue("$created", now.ToUnixTimeSeconds());
                    command.ExecuteNonQuery();
                }
                SeedInitialProfile(connection, transaction, uid, now.ToUnixTimeSeconds());
                transaction.Commit();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                transaction.Rollback();
                throw new InvalidOperationException("Ese usuario ya existe.", ex);
            }
            return new PublicAccount(uid.ToString(), login, now);
        }
        finally { _gate.Release(); }
    }

    public async Task<LoginSession?> LoginAsync(string login, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password)) return null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT uid, password_salt, password_hash FROM trimurti_auth WHERE login=$login";
            command.Parameters.AddWithValue("$login", login.Trim().ToLowerInvariant());
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var uid = reader.GetInt64(0);
            var salt = Convert.FromBase64String(reader.GetString(1));
            var expected = Convert.FromBase64String(reader.GetString(2));
            reader.Close();
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return null;
            using (var transaction = connection.BeginTransaction())
            {
                EnsureLobbyBaseline(connection, transaction, uid);
                transaction.Commit();
            }
            var expires = DateTimeOffset.UtcNow.AddHours(12);
            var accessToken = NewToken();
            var gameToken = NewToken();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE sdk SET token=$token, gstoken=$gstoken WHERE uid=$uid";
            update.Parameters.AddWithValue("$token", accessToken);
            update.Parameters.AddWithValue("$gstoken", gameToken);
            update.Parameters.AddWithValue("$uid", uid);
            update.ExecuteNonQuery();
            return new LoginSession(uid.ToString(), accessToken, expires);
        }
        finally { _gate.Release(); }
    }

    public async Task<LoginSession?> LoginOrCreateAsync(string login, string password, CancellationToken cancellationToken)
    {
        var existing = await LoginAsync(login, password, cancellationToken);
        if (existing is not null) return existing;
        try { await CreateAsync(login, password, cancellationToken); }
        catch (InvalidOperationException) { return null; }
        catch (ArgumentException) { return null; }
        return await LoginAsync(login, password, cancellationToken);
    }

    public async Task<GatewayIdentity?> ResolveGatewayTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.uid, a.login, s.gstoken, u.register_ts
            FROM trimurti_auth AS a
            JOIN sdk AS s ON s.uid=a.uid
            JOIN users AS u ON u.uid=a.uid
            WHERE s.token=$token
            """;
        command.Parameters.AddWithValue("$token", token);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new GatewayIdentity(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3));
    }

    public async Task<bool> ValidateGameTokenAsync(long uid, string gameToken, CancellationToken cancellationToken)
    {
        if (uid <= 0 || string.IsNullOrWhiteSpace(gameToken)) return false;
        using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sdk JOIN trimurti_auth USING(uid) WHERE uid=$uid AND gstoken=$token";
        command.Parameters.AddWithValue("$uid", uid);
        command.Parameters.AddWithValue("$token", gameToken);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private SqliteConnection Open() => new(new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString());

    private static JsonElement LoadSeed()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "new_player_seed.json")));
        return doc.RootElement.Clone();
    }

    private static void SeedInitialProfile(SqliteConnection connection, SqliteTransaction transaction, long uid, long now)
    {
        var seed = LoadSeed();
        var settings = seed.GetProperty("settings");
        var hero = seed.GetProperty("starter_hero");
        var heroId = hero.GetProperty("id").GetInt32();
        var skills = JsonSerializer.Serialize(hero.GetProperty("skills").EnumerateArray().Select(x => new[] { x.GetInt32(), 1 }));

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO game_user(uid,cur_portrait,cur_icon_frame,cur_bubble,cur_card_bg,cur_sticker_bg,show_hero,board_hero,cur_scene,update_ts)
                VALUES($uid,$portrait,$frame,$bubble,$card,$sticker,$show,$hero,$scene,$now)
                """;
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$portrait", settings.GetProperty("profile_avatar_default").GetInt32());
            command.Parameters.AddWithValue("$frame", settings.GetProperty("profile_avatar_frame_default").GetInt32());
            command.Parameters.AddWithValue("$bubble", settings.GetProperty("profile_chat_bubble_default").GetInt32());
            command.Parameters.AddWithValue("$card", settings.GetProperty("profile_business_card_default").GetInt32());
            command.Parameters.AddWithValue("$sticker", settings.GetProperty("sticker_background_default").GetInt32());
            command.Parameters.AddWithValue("$show", JsonSerializer.Serialize(new[] { heroId }));
            command.Parameters.AddWithValue("$hero", heroId);
            command.Parameters.AddWithValue("$scene", settings.GetProperty("home_scene_default").GetInt32());
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO hero(uid,id,level,star,exp,skill_list,weapon_exp,weapon_break,weapon_servant_uid,servant,
                    using_astrolabe,unlock_astrolabe,using_skin,break_level,exclusive_skill_list,weapon_module_level,
                    skill_intensify,battle_using_skin,clear_times,unlock,equip_slot,module_level,module_assignment,chip_state,update_ts,name,trust_level,trust_mood)
                VALUES($uid,$id,1,$star,0,$skills,0,0,0,'[]','[]','[]',$id,0,'[]',0,'[]',$id,0,1,'[]',0,0,'[]',$now,'',1,1)
                """;
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$id", heroId);
            command.Parameters.AddWithValue("$star", hero.GetProperty("star").GetInt32());
            command.Parameters.AddWithValue("$skills", skills);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO currency(uid,id,num,update_ts) VALUES($uid,$id,$num,$now)";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$num", 0);
            command.Parameters.AddWithValue("$now", now);
            var id = command.Parameters.Add("$id", SqliteType.Integer);
            foreach (var currencyId in new[] { 1, 2, 4, 5, 19, 30, 31, 32, 36, 38, 101 })
            {
                id.Value = currencyId;
                command.Parameters["$num"].Value = currencyId == 4 ? 100 : 0;
                command.ExecuteNonQuery();
            }
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO accumulate_sign(uid,version,open_sign,login_days,update_ts) VALUES($uid,2,1,0,$now)";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO newbie_activity(uid,trigger_time,max_phase,version_id,update_ts) VALUES($uid,$now,1,$version,$now)";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$version", seed.GetProperty("newbie_version").GetInt32());
            command.ExecuteNonQuery();
        }
        EnsureLobbyBaseline(connection, transaction, uid);
    }

    private static void EnsureLobbyBaseline(SqliteConnection connection, SqliteTransaction transaction, long uid)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO guide(uid,guide_id,name,kind) VALUES($uid,$id,'','main')";
            command.Parameters.AddWithValue("$uid", uid);
            var guideId = command.Parameters.Add("$id", SqliteType.Integer);
            for (var id = 501; id <= 526; id++)
            {
                guideId.Value = id;
                command.ExecuteNonQuery();
            }
        }
        SeedNewPlayerTasks(connection, transaction, uid);
    }

    private static void SeedNewPlayerTasks(SqliteConnection connection, SqliteTransaction transaction, long uid)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "Configs", "new_player_cfg.json");
        using var config = JsonDocument.Parse(File.ReadAllBytes(path));
        var tasks = config.RootElement.GetProperty("tasks");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO task(uid,task_id,task_type,progress,complete_flag,expired_ts,claimed_ts) VALUES($uid,$id,603,0,0,0,0)";
        command.Parameters.AddWithValue("$uid", uid);
        var taskId = command.Parameters.Add("$id", SqliteType.Integer);
        foreach (var task in tasks.EnumerateObject())
        {
            if (!task.Value.TryGetProperty("type", out var type) || type.GetInt32() != 603 || !int.TryParse(task.Name, out var id)) continue;
            taskId.Value = id;
            command.ExecuteNonQuery();
        }
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
    }

    private static long NextUid(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(uid), 3000000000) + 1 FROM trimurti_auth WHERE uid > 2147483647 AND uid < 100000000000";
        return Convert.ToInt64(command.ExecuteScalar() ?? 3_000_000_001L);
    }

    private static void MigrateOutOfRangeUids(SqliteConnection connection)
    {
        var badUids = new List<long>();
        using (var cmd = connection.CreateCommand())
        {
            // LitJson in the PC client throws if id/userId fits in Int32 (<= 2147483647),
            // and 19-digit random UIDs lose precision in JS and are hard to type.
            cmd.CommandText = "SELECT uid FROM trimurti_auth WHERE uid <= 2147483647 OR uid >= 100000000000 ORDER BY created_ts, uid";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) badUids.Add(reader.GetInt64(0));
        }
        if (badUids.Count == 0) return;

        var tablesWithUid = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) tablesWithUid.Add(reader.GetString(0));
        }
        var targetTables = new List<string>();
        foreach (var table in tablesWithUid)
        {
            using var pragma = connection.CreateCommand();
            pragma.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
            using var r = pragma.ExecuteReader();
            while (r.Read())
            {
                if (string.Equals(r.GetString(1), "uid", StringComparison.OrdinalIgnoreCase))
                {
                    targetTables.Add(table);
                    break;
                }
            }
        }

        var nextUid = NextUid(connection);
        using var tx = connection.BeginTransaction();
        foreach (var oldUid in badUids)
        {
            var newUid = nextUid++;
            foreach (var table in targetTables)
            {
                using var upd = connection.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = $"UPDATE \"{table.Replace("\"", "\"\"")}\" SET uid=$newUid WHERE uid=$oldUid";
                upd.Parameters.AddWithValue("$newUid", newUid);
                upd.Parameters.AddWithValue("$oldUid", oldUid);
                upd.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string NormalizeLogin(string login)
    {
        var value = login?.Trim().ToLowerInvariant() ?? "";
        if (value.Length is < 3 or > 32 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new ArgumentException("El usuario debe tener 3–32 caracteres: letras ASCII, números, guion o guion bajo.");
        return value;
    }

    private static void ValidatePassword(string password)
    {
        if (password is null || password.Length is < 10 or > 128)
            throw new ArgumentException("La contraseña debe tener entre 10 y 128 caracteres.");
    }
}
