using Dapper;

namespace NullLauncher.Instances;

/// <summary>SQL-доступ к таблице instances. Алиасы в SELECT приводят snake_case к свойствам DTO.</summary>
public static class InstanceRepository
{
    private const string Select = """
        SELECT id, name, description, icon_path AS IconPath, cover_path AS CoverPath, color, favorite,
               mc_version AS McVersion, loader, loader_version AS LoaderVersion,
               java_mode AS JavaMode, java_major AS JavaMajor, java_path AS JavaPath,
               ram_min_mb AS RamMinMb, ram_max_mb AS RamMaxMb, jvm_args AS JvmArgs, game_args AS GameArgs,
               env_vars AS EnvVars, window_mode AS WindowMode, width, height, fullscreen, vsync,
               fps_limit AS FpsLimit, dir, server_ip AS ServerIp, server_port AS ServerPort,
               account_uuid AS AccountUuid, safe_mode AS SafeMode,
               modpack_name AS ModpackName, modpack_version AS ModpackVersion,
               modpack_project_id AS ModpackProjectId, modpack_version_id AS ModpackVersionId,
               created_at AS CreatedAt, last_played_at AS LastPlayedAt, play_time_ms AS PlayTimeMs,
               launch_count AS LaunchCount, sort_order AS SortOrder
        FROM instances
        """;

    public static List<InstanceRecord> GetAll() => Database.DatabaseService.Query<InstanceRecord>($"{Select} ORDER BY sort_order, name");

    public static InstanceRecord? Get(string id)
    {
        var rec = Database.DatabaseService.QueryFirstOrDefault<InstanceRecord>($"{Select} WHERE id=@id", new { id });
        if (rec is not null) rec.GroupIds = GroupIdsOf(rec.Id);
        return rec;
    }

    public static void Upsert(InstanceRecord r)
    {
        Database.DatabaseService.Execute("""
            INSERT INTO instances (id, name, description, icon_path, cover_path, color, favorite, mc_version, loader,
              loader_version, java_mode, java_major, java_path, ram_min_mb, ram_max_mb, jvm_args, game_args, env_vars,
              window_mode, width, height, fullscreen, vsync, fps_limit, dir, server_ip, server_port, account_uuid,
              safe_mode, modpack_name, modpack_version, modpack_project_id, modpack_version_id,
              created_at, last_played_at, play_time_ms, launch_count, sort_order)
            VALUES (@Id, @Name, @Description, @IconPath, @CoverPath, @Color, @Favorite, @McVersion, @Loader,
              @LoaderVersion, @JavaMode, @JavaMajor, @JavaPath, @RamMinMb, @RamMaxMb, @JvmArgs, @GameArgs, @EnvVars,
              @WindowMode, @Width, @Height, @Fullscreen, @Vsync, @FpsLimit, @Dir, @ServerIp, @ServerPort, @AccountUuid,
              @SafeMode, @ModpackName, @ModpackVersion, @ModpackProjectId, @ModpackVersionId,
              @CreatedAt, @LastPlayedAt, @PlayTimeMs, @LaunchCount, @SortOrder)
            ON CONFLICT(id) DO UPDATE SET
              name=excluded.name, description=excluded.description, icon_path=excluded.icon_path,
              cover_path=excluded.cover_path, color=excluded.color, favorite=excluded.favorite,
              mc_version=excluded.mc_version, loader=excluded.loader, loader_version=excluded.loader_version,
              java_mode=excluded.java_mode, java_major=excluded.java_major, java_path=excluded.java_path,
              ram_min_mb=excluded.ram_min_mb, ram_max_mb=excluded.ram_max_mb, jvm_args=excluded.jvm_args,
              game_args=excluded.game_args, env_vars=excluded.env_vars, window_mode=excluded.window_mode,
              width=excluded.width, height=excluded.height, fullscreen=excluded.fullscreen, vsync=excluded.vsync,
              fps_limit=excluded.fps_limit, server_ip=excluded.server_ip, server_port=excluded.server_port,
              account_uuid=excluded.account_uuid, safe_mode=excluded.safe_mode,
              modpack_name=excluded.modpack_name, modpack_version=excluded.modpack_version,
              modpack_project_id=excluded.modpack_project_id, modpack_version_id=excluded.modpack_version_id,
              last_played_at=excluded.last_played_at, play_time_ms=excluded.play_time_ms,
              launch_count=excluded.launch_count, sort_order=excluded.sort_order
            """, r);
    }

    public static void Delete(string id)
    {
        Database.DatabaseService.Execute("DELETE FROM instances WHERE id=@id", new { id });
        Database.DatabaseService.Execute("DELETE FROM group_instances WHERE instance_id=@id", new { id });
        foreach (var t in new[] { "mods", "resourcepacks", "shaders", "datapacks" })
            Database.DatabaseService.Execute($"DELETE FROM {t} WHERE instance_id=@id", new { id });
        Database.DatabaseService.Execute("DELETE FROM worlds WHERE instance_id=@id", new { id });
        Database.DatabaseService.Execute("DELETE FROM servers WHERE instance_id=@id", new { id });
        Database.DatabaseService.Execute("DELETE FROM presets WHERE instance_id=@id", new { id });
    }

    public static List<string> GroupIdsOf(string instanceId)
        => Database.DatabaseService.Query<string>(
            "SELECT group_id FROM group_instances WHERE instance_id=@id ORDER BY sort_order", new { id = instanceId });

    /* Для восстановления из папок — внутри внешней транзакции */
    public static void Insert(Microsoft.Data.Sqlite.SqliteConnection c, InstanceRecord r, string dir)
    {
        r.Dir = dir;
        c.Execute("""
            INSERT OR IGNORE INTO instances (id, name, description, icon_path, cover_path, color, favorite, mc_version,
              loader, loader_version, dir, created_at, last_played_at, play_time_ms, launch_count, ram_min_mb,
              ram_max_mb, java_mode, window_mode, width, height, fullscreen, vsync, fps_limit)
            VALUES (@Id, @Name, @Description, @IconPath, @CoverPath, @Color, @Favorite, @McVersion,
              @Loader, @LoaderVersion, @Dir, @CreatedAt, @LastPlayedAt, @PlayTimeMs, @LaunchCount, @RamMinMb,
              @RamMaxMb, @JavaMode, @WindowMode, @Width, @Height, @Fullscreen, @Vsync, @FpsLimit)
            """, r);
    }
}
