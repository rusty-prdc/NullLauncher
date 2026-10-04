using System.IO;
using System.IO.Compression;

namespace NullLauncher.Backups;

/// <summary>
/// Резервное копирование: целые сборки, отдельные миры, моды или настроек.
/// Формат — обычный .zip; автоматические копии помечаются флагом automatic и чистятся по retention.
/// </summary>
public static class BackupService
{
    public sealed record BackupInfo(string Id, string? InstanceId, string Name, string Path, string Kind,
        long SizeBytes, DateTime CreatedAt, bool Automatic);

    public static BackupInfo CreateForInstance(Core.AppPaths paths, Instances.InstanceRecord inst, string reason, bool automatic)
    {
        var dir = Path.Combine(paths.BackupsDir, inst.Id);
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var safeName = Sanitize($"{inst.Name}-{stamp}");
        var file = Path.Combine(dir, $"{safeName}.zip");

        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            var meta = Path.Combine(inst.Dir, "instance.json");
            if (File.Exists(meta)) zip.CreateEntryFromFile(meta, "instance.json");

            if (Directory.Exists(inst.GameDir))
            {
                foreach (var f in Directory.EnumerateFiles(inst.GameDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(inst.GameDir, f);
                    zip.CreateEntryFromFile(f, Path.Combine(".minecraft", rel));
                }
            }
            foreach (var img in new[] { inst.IconPath, inst.CoverPath })
                if (!string.IsNullOrEmpty(img) && File.Exists(img))
                    zip.CreateEntryFromFile(img, Path.GetFileName(img));
        }

        var info = new BackupInfo(Guid.NewGuid().ToString("N")[..12], inst.Id,
            $"{reason}: {inst.Name}", file, "full", new FileInfo(file).Length, DateTime.UtcNow, automatic);

        Database.DatabaseService.Execute("""
            INSERT INTO backups (id, instance_id, name, path, kind, size_bytes, created_at, automatic)
            VALUES (@Id, @InstanceId, @Name, @Path, @Kind, @SizeBytes, @CreatedAt, @Automatic)
            """, new
        {
            info.Id, info.InstanceId, info.Name, info.Path, info.Kind, info.SizeBytes,
            CreatedAt = info.CreatedAt.ToString("o"),
            Automatic = info.Automatic ? 1 : 0,
        });

        Log.Info($"Бэкап '{info.Name}' → {file} ({info.SizeBytes} байт)");
        ApplyRetention(paths, inst.Id);
        return info;
    }

    /// <summary>Копия только мира (saves/имя) внутри уже существующей папки backups.</summary>
    public static BackupInfo CreateWorldBackup(Core.AppPaths paths, Instances.InstanceRecord inst, string worldName)
    {
        var worldDir = Path.Combine(inst.SavesDir, worldName);
        if (!Directory.Exists(worldDir))
            throw new LauncherException("Мир не найден", worldDir);

        var dir = Path.Combine(paths.BackupsDir, inst.Id);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"world_{Sanitize(worldName)}_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            foreach (var f in Directory.EnumerateFiles(worldDir, "*", SearchOption.AllDirectories))
                zip.CreateEntryFromFile(f, Path.Combine(worldName, Path.GetRelativePath(worldDir, f)));

        var info = new BackupInfo(Guid.NewGuid().ToString("N")[..12], inst.Id, $"Мир: {worldName}", file,
            "world", new FileInfo(file).Length, DateTime.UtcNow, Automatic: false);

        Database.DatabaseService.Execute("""
            INSERT INTO backups (id, instance_id, name, path, kind, size_bytes, created_at, automatic)
            VALUES (@Id, @InstanceId, @Name, @Path, @Kind, @SizeBytes, @CreatedAt, @Automatic)
            """, new
        {
            info.Id, info.InstanceId, info.Name, info.Path, info.Kind, info.SizeBytes,
            CreatedAt = info.CreatedAt.ToString("o"), Automatic = 0,
        });
        return info;
    }

    public static List<BackupInfo> List(string? instanceId)
    {
        var sql = instanceId is null
            ? "SELECT id, instance_id AS InstanceId, name, path, kind, size_bytes AS SizeBytes, created_at AS CreatedAt, automatic FROM backups ORDER BY created_at DESC"
            : "SELECT id, instance_id AS InstanceId, name, path, kind, size_bytes AS SizeBytes, created_at AS CreatedAt, automatic FROM backups WHERE instance_id=@id ORDER BY created_at DESC";
        var rows = Database.DatabaseService.Query<BackupRow>(sql, new { id = instanceId });
        return rows.Select(r => new BackupInfo(r.Id, r.InstanceId, r.Name, r.Path, r.Kind, r.SizeBytes,
            DateTime.TryParse(r.CreatedAt, out var d) ? d : DateTime.MinValue, r.Automatic == 1)).ToList();
    }

    private sealed class BackupRow
    {
        public string Id { get; set; } = "";
        public string? InstanceId { get; set; }
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Kind { get; set; } = "full";
        public long SizeBytes { get; set; }
        public string CreatedAt { get; set; } = "";
        public int Automatic { get; set; }
    }

    /// <summary>Восстановление: распаковывает .zip поверх папки сборки (всё или по частям).</summary>
    public static void Restore(string backupId, string instanceId, string? what)
    {
        var backups = List(null);
        var b = backups.FirstOrDefault(x => x.Id == backupId)
                ?? throw new LauncherException("Резервная копия не найдена", backupId);
        if (!File.Exists(b.Path))
            throw new LauncherException("Файл резервной копии отсутствует", b.Path);

        var inst = Instances.InstanceRepository.Get(instanceId)
                   ?? throw new LauncherException("Сборка не найдена", instanceId);

        using var zip = ZipFile.OpenRead(b.Path);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // каталог
            var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (rel.EndsWith("instance.json", StringComparison.OrdinalIgnoreCase) && what is not (null or "settings"))
                continue;
            if (what == "worlds" && !rel.Contains("saves", StringComparison.OrdinalIgnoreCase)) continue;
            if (what == "mods" && !rel.Contains("mods", StringComparison.OrdinalIgnoreCase)) continue;
            if (what == "settings" && rel.Contains("saves", StringComparison.OrdinalIgnoreCase)) continue;

            var target = rel.StartsWith("instance.json", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(inst.Dir, "instance.json")
                : Path.Combine(inst.Dir, rel);

            // защита от выхода за пределы папки сборки
            var full = Path.GetFullPath(target);
            if (!full.StartsWith(Path.GetFullPath(inst.Dir), StringComparison.OrdinalIgnoreCase))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            entry.ExtractToFile(full, true);
        }
        Log.Info($"Восстановление {b.Name} → {inst.Name} (что: {what ?? "всё"})");
    }

    public static void Delete(string backupId)
    {
        var b = List(null).FirstOrDefault(x => x.Id == backupId);
        if (b is null) return;
        try { if (File.Exists(b.Path)) File.Delete(b.Path); }
        catch (Exception ex) { Log.Warn($"Не удалось удалить бэкап: {ex.Message}"); }
        Database.DatabaseService.Execute("DELETE FROM backups WHERE id=@id", new { id = backupId });
    }

    /// <summary>Оставляет не более retention автоматических копий сборки.</summary>
    private static void ApplyRetention(Core.AppPaths paths, string instanceId)
    {
        var retention = AppServices.Current?.Settings.Get("backupRetention", 5) ?? 5;
        if (retention <= 0) return;
        var all = List(instanceId).Where(b => b.Automatic).OrderByDescending(b => b.CreatedAt).ToList();
        foreach (var old in all.Skip(retention))
            Delete(old.Id);
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
