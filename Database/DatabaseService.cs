using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;

namespace NullLauncher.Database;

/// <summary>
/// SQLite-хранилище метаданных. Бинарные файлы сюда не кладём — только связи и состояние.
/// WAL + busy timeout, одна строка подключения на процесс.
/// </summary>
public sealed class DatabaseService : IDisposable
{
    private readonly string _connectionString;
    public string FilePath { get; }

    /// <summary>Единственный активный экземпляр — репозитории работают через статический фасад.</summary>
    public static DatabaseService? Instance { get; private set; }

    public static List<T> Query<T>(string sql, object? p = null)
    {
        using var c = Instance!.Open();
        return c.Query<T>(sql, p).ToList();
    }

    public static T? QueryFirstOrDefault<T>(string sql, object? p = null)
    {
        using var c = Instance!.Open();
        return c.QueryFirstOrDefault<T>(sql, p);
    }

    public static int Execute(string sql, object? p = null)
    {
        using var c = Instance!.Open();
        return c.Execute(sql, p);
    }

    public static T? Scalar<T>(string sql, object? p = null)
    {
        using var c = Instance!.Open();
        return c.ExecuteScalar<T>(sql, p);
    }

    public static async Task<List<T>> QueryAsync<T>(string sql, object? p = null)
    {
        using var c = Instance!.Open();
        return (await c.QueryAsync<T>(sql, p).ConfigureAwait(false)).ToList();
    }

    public static async Task<int> ExecuteAsync(string sql, object? p = null)
    {
        using var c = Instance!.Open();
        return await c.ExecuteAsync(sql, p).ConfigureAwait(false);
    }

    public DatabaseService(Core.AppPaths paths)
    {
        FilePath = paths.DatabaseFile;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        using var c = Open();
        Init(c);
        Instance = this;
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        try
        {
            conn.Open();
            if (!_journalChecked)
            {
                _journalChecked = true;
                TryEnableWal(conn);
            }
            conn.Execute("PRAGMA busy_timeout=8000;");
            conn.Execute("PRAGMA foreign_keys=ON;");
            return conn;
        }
        catch
        {
            // незакрытое соединение держало бы базу и ломало следующий запуск лаунчера
            conn.Dispose();
            throw;
        }
    }

    private bool _journalChecked;

    /// <summary>
    /// Пытается включить WAL (быстрее и устойчивее к сбоям). Для WAL SQLite создаёт рядом с базой
    /// файлы .db-wal и .db-shm: если их создать нельзя (права, антивирус, песочница, другой
    /// экземпляр лаунчера) — работаем на обычном журнале и предупреждаем в лог, но лаунчер
    /// из-за этого падать не должен.
    /// </summary>
    private static void TryEnableWal(SqliteConnection conn)
    {
        try
        {
            var mode = conn.ExecuteScalar<string>("PRAGMA journal_mode=WAL;");
            if (string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                conn.Execute("PRAGMA synchronous=NORMAL;");
                return;
            }
            Log.Warn($"SQLite: режим WAL недоступен (получен «{mode}») — продолжаю на обычном журнале");
        }
        catch (Exception ex)
        {
            Log.Warn($"SQLite: не удалось включить WAL ({ex.Message}) — продолжаю на обычном журнале");
        }
    }

    public async Task<T> QuerySingleAsync<T>(string sql, object? p = null)
    {
        using var c = Open();
        return (await c.QueryFirstOrDefaultAsync<T>(sql, p).ConfigureAwait(false))!;
    }

    public async Task<T> ScalarAsync<T>(string sql, object? p = null)
    {
        using var c = Open();
        return (await c.ExecuteScalarAsync<T>(sql, p).ConfigureAwait(false))!;
    }

    private void Init(SqliteConnection c)
    {
        c.Execute("""
        CREATE TABLE IF NOT EXISTS instances (
          id            TEXT PRIMARY KEY,
          name          TEXT NOT NULL,
          description   TEXT NOT NULL DEFAULT '',
          icon_path     TEXT,
          cover_path    TEXT,
          color         TEXT NOT NULL DEFAULT '#3ecf8e',
          favorite      INTEGER NOT NULL DEFAULT 0,
          mc_version    TEXT NOT NULL,
          loader        TEXT NOT NULL DEFAULT 'vanilla',
          loader_version TEXT,
          java_major    INTEGER,
          java_path     TEXT,
          java_mode     TEXT NOT NULL DEFAULT 'auto',
          ram_min_mb    INTEGER NOT NULL DEFAULT 512,
          ram_max_mb    INTEGER NOT NULL DEFAULT 4096,
          jvm_args      TEXT NOT NULL DEFAULT '',
          game_args     TEXT NOT NULL DEFAULT '',
          env_vars      TEXT NOT NULL DEFAULT '',
          window_mode   TEXT NOT NULL DEFAULT 'windowed',
          width         INTEGER NOT NULL DEFAULT 1920,
          height        INTEGER NOT NULL DEFAULT 1080,
          fullscreen    INTEGER NOT NULL DEFAULT 0,
          vsync         INTEGER NOT NULL DEFAULT 1,
          fps_limit     INTEGER NOT NULL DEFAULT 120,
          dir           TEXT NOT NULL,
          server_ip     TEXT,
          server_port   INTEGER,
          account_uuid  TEXT,
          safe_mode     INTEGER NOT NULL DEFAULT 0,
          modpack_name  TEXT,
          modpack_version TEXT,
          modpack_project_id TEXT,
          modpack_version_id TEXT,
          created_at    TEXT NOT NULL,
          last_played_at TEXT,
          play_time_ms  INTEGER NOT NULL DEFAULT 0,
          launch_count  INTEGER NOT NULL DEFAULT 0,
          sort_order    INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS groups (
          id TEXT PRIMARY KEY, name TEXT NOT NULL, color TEXT NOT NULL DEFAULT '#3ecf8e',
          icon TEXT NOT NULL DEFAULT 'cube', sort_order INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS group_instances (
          group_id TEXT NOT NULL, instance_id TEXT NOT NULL, sort_order INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY (group_id, instance_id)
        );
        CREATE TABLE IF NOT EXISTS profiles (
          id TEXT PRIMARY KEY, name TEXT NOT NULL, is_default INTEGER NOT NULL DEFAULT 0,
          account_uuid TEXT, settings_json TEXT NOT NULL DEFAULT '{}', sort_order INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS accounts (
          uuid TEXT PRIMARY KEY, name TEXT NOT NULL, type TEXT NOT NULL DEFAULT 'microsoft',
          expires_at TEXT, is_default INTEGER NOT NULL DEFAULT 0, skin_url TEXT,
          token_enc BLOB, refresh_enc BLOB, created_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS java_installations (
          id TEXT PRIMARY KEY, path TEXT NOT NULL, version TEXT, major INTEGER, arch TEXT,
          vendor TEXT, source TEXT, valid INTEGER NOT NULL DEFAULT 0, last_seen TEXT
        );
        CREATE TABLE IF NOT EXISTS downloads (
          id TEXT PRIMARY KEY, kind TEXT NOT NULL, title TEXT NOT NULL, url TEXT NOT NULL,
          target TEXT NOT NULL, instance_id TEXT, total_bytes INTEGER NOT NULL DEFAULT 0,
          received_bytes INTEGER NOT NULL DEFAULT 0, progress REAL NOT NULL DEFAULT 0,
          state TEXT NOT NULL DEFAULT 'queued', hash_algo TEXT, hash_value TEXT,
          error TEXT, retry_count INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL,
          updated_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS backups (
          id TEXT PRIMARY KEY, instance_id TEXT, name TEXT NOT NULL, path TEXT NOT NULL,
          kind TEXT NOT NULL DEFAULT 'full', size_bytes INTEGER NOT NULL DEFAULT 0,
          created_at TEXT NOT NULL, automatic INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS worlds (
          instance_id TEXT NOT NULL, name TEXT NOT NULL, dir TEXT NOT NULL,
          size_bytes INTEGER NOT NULL DEFAULT 0, last_modified TEXT, mc_version TEXT,
          icon TEXT, PRIMARY KEY (instance_id, name)
        );
        CREATE TABLE IF NOT EXISTS servers (
          instance_id TEXT NOT NULL, id TEXT NOT NULL, name TEXT NOT NULL, ip TEXT NOT NULL,
          port INTEGER NOT NULL DEFAULT 25565, icon TEXT, PRIMARY KEY (instance_id, id)
        );
        CREATE TABLE IF NOT EXISTS presets (
          id TEXT PRIMARY KEY, instance_id TEXT NOT NULL, name TEXT NOT NULL,
          content_json TEXT NOT NULL DEFAULT '{}', created_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS launch_history (
          id INTEGER PRIMARY KEY AUTOINCREMENT, instance_id TEXT NOT NULL, started_at TEXT NOT NULL,
          ended_at TEXT, exit_code INTEGER, duration_ms INTEGER, safe_mode INTEGER NOT NULL DEFAULT 0,
          error TEXT, account_uuid TEXT
        );
        CREATE TABLE IF NOT EXISTS api_cache (
          key TEXT PRIMARY KEY, value TEXT NOT NULL, expires_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS kv (
          key TEXT PRIMARY KEY, value TEXT NOT NULL
        );
        """);

        /* Одинаковая схема для четырёх типов контента */
        foreach (var table in new[] { "mods", "resourcepacks", "shaders", "datapacks" })
        {
            c.Execute($"""
            CREATE TABLE IF NOT EXISTS {table} (
              id TEXT PRIMARY KEY,
              instance_id TEXT NOT NULL,
              filename TEXT NOT NULL,
              name TEXT NOT NULL,
              version TEXT,
              author TEXT,
              size_bytes INTEGER NOT NULL DEFAULT 0,
              enabled INTEGER NOT NULL DEFAULT 1,
              project_id TEXT,
              version_id TEXT,
              source_url TEXT,
              source TEXT NOT NULL DEFAULT 'local',
              mc_versions TEXT,
              loaders TEXT,
              updated_at TEXT,
              last_checked_at TEXT,
              UNIQUE(instance_id, filename)
            );
            CREATE INDEX IF NOT EXISTS ix_{table}_instance ON {table}(instance_id);
            """);
        }

        c.Execute("CREATE INDEX IF NOT EXISTS ix_launch_history_instance ON launch_history(instance_id);");
        c.Execute("CREATE INDEX IF NOT EXISTS ix_downloads_state ON downloads(state);");

        Log.Debug($"SQLite готов: {FilePath}");
    }

    /// <summary>
    /// Восстановление метаданных из папок instances, если база повреждена или пуста.
    /// Читает instance.json каждой сборки и пересоздаёт записи.
    /// </summary>
    public int RecoverFromInstanceFolders(IEnumerable<string> instanceDirs)
    {
        int recovered = 0;
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var dir in instanceDirs)
        {
            var metaFile = Path.Combine(dir, "instance.json");
            if (!File.Exists(metaFile)) continue;
            try
            {
                var id = Path.GetFileName(dir);
                var exists = c.ExecuteScalar<int>("SELECT COUNT(1) FROM instances WHERE id=@id", new { id });
                if (exists > 0) continue;
                var json = File.ReadAllText(metaFile);
                var dto = System.Text.Json.JsonSerializer.Deserialize<Instances.InstanceRecord>(json, Core.IpcRouter.JsonOptions);
                if (dto is null) continue;
                Instances.InstanceRepository.Insert(c, dto, dir);
                recovered++;
            }
            catch (Exception ex) { Log.Warn($"Не удалось восстановить метаданные {dir}: {ex.Message}"); }
        }
        tx.Commit();
        if (recovered > 0) Log.Warn($"Восстановлено {recovered} сборок из папок (база была неполной)");
        return recovered;
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); }
}
