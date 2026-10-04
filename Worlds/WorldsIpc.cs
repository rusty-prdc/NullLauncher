using System.Text.Json.Nodes;
using NullLauncher.Core;
using NullLauncher.Instances;
using NullLauncher.Minecraft;

namespace NullLauncher.Worlds;

/// <summary>IPC-методы миров: список, запуск, переименование, удаление, резервные копии, импорт и экспорт.</summary>
public static class WorldsIpc
{
    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("worlds.list", (p, _) => Task.FromResult<object?>(ListWorlds(s, RequireInstance(s, p))));
        r.Register("worlds.play", (p, _) => Play(s, p));
        r.Register("worlds.rename", (p, _) => Rename(s, p));
        r.Register("worlds.delete", (p, _) => Delete(s, p));
        r.Register("worlds.backup", (p, _) => Backup(s, p));
        r.Register("worlds.importZip", async (p, ct) => await ImportZipAsync(s, p, ct).ConfigureAwait(false));
        r.Register("worlds.exportZip", (p, _) => ExportZip(s, p));
        r.Register("worlds.openFolder", (p, _) => Task.FromResult<object?>(OpenFolder(s, p)));
    }

    /* ------------------------------------------------------------ общее */
    private sealed class WorldRow
    {
        public string Name { get; set; } = "";
        public string Dir { get; set; } = "";
        public long SizeBytes { get; set; }
        public string? LastModified { get; set; }
        public string? McVersion { get; set; }
        public string? Icon { get; set; }
    }

    private static InstanceRecord RequireInstance(AppServices s, JsonNode? p)
    {
        var id = p?.Str("instanceId");
        if (string.IsNullOrWhiteSpace(id)) throw new LauncherException("Не указана сборка");
        return s.Instances.Require(id);
    }

    private static string RequireWorld(JsonNode? p)
    {
        var world = p?.Str("world")?.Trim();
        if (string.IsNullOrWhiteSpace(world)) throw new LauncherException("Не указан мир");
        if (world.Contains("..") || world.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || world.Contains('/') || world.Contains('\\'))
            throw new LauncherException("Некорректное название мира", world);
        return world;
    }

    private static string WorldDir(InstanceRecord inst, string world) => Path.Combine(inst.SavesDir, world);

    private static string? ReadIcon(string worldDir)
    {
        foreach (var name in new[] { "level_icon.png", "icon.png" })
        {
            try
            {
                var f = Path.Combine(worldDir, name);
                if (!File.Exists(f)) continue;
                var bytes = File.ReadAllBytes(f);
                if (bytes.Length is > 0 and < 200_000)
                    return "data:image/png;base64," + Convert.ToBase64String(bytes);
            }
            catch { /* иконка не обязательна */ }
        }
        return null;
    }

    private static void OpenInExplorer(string path, bool select)
    {
        var app = System.Windows.Application.Current;
        if (app is null) MainWindow.OpenPath(path, select);
        else app.Dispatcher.Invoke(() => MainWindow.OpenPath(path, select));
    }

    /// <summary>Запуск в фоне: отвечаем сразу, ошибки логируются (StartAsync сам уведомляет UI).</summary>
    private static void StartInBackground(AppServices s, InstanceRecord inst, string worldName, LaunchRequest req)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = s.Launch.StartAsync(req, CancellationToken.None, started);
        _ = task.ContinueWith(t =>
        {
            if (!t.IsFaulted) return;
            var msg = t.Exception?.GetBaseException().Message ?? "неизвестная ошибка";
            Log.Warn($"Запуск '{inst.Name}' с миром '{worldName}' не удался: {msg}");
            // ошибка до внутреннего try в StartAsync — тогда уведомление не показано
            if (started.Task.Status == TaskStatus.Created)
                s.NotifyUser("error", "Не удалось запустить Minecraft", msg);
        }, TaskScheduler.Default);
    }

    /* ------------------------------------------------------------ список */
    private static object ListWorlds(AppServices s, InstanceRecord inst)
    {
        var existing = Database.DatabaseService.Query<WorldRow>("""
            SELECT name, dir, size_bytes AS SizeBytes, last_modified AS LastModified,
                   mc_version AS McVersion, icon
            FROM worlds WHERE instance_id=@id
            """, new { id = inst.Id });
        var byName = existing.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

        var found = new List<(string Name, string Dir, bool HasLevel, long Size, DateTime LastWrite, string? Icon)>();
        try
        {
            if (Directory.Exists(inst.SavesDir))
                foreach (var dir in Directory.EnumerateDirectories(inst.SavesDir))
                {
                    if (Path.GetFileName(dir).StartsWith('.')) continue;
                    found.Add((Path.GetFileName(dir), dir,
                        File.Exists(Path.Combine(dir, "level.dat")),
                        AppPaths.DirectorySize(dir),
                        Directory.GetLastWriteTime(dir),
                        ReadIcon(dir)));
                }
        }
        catch (Exception ex) { Log.Warn($"Не удалось просканировать миры {inst.SavesDir}: {ex.Message}"); }

        foreach (var w in found)
        {
            var mc = byName.TryGetValue(w.Name, out var old) ? old.McVersion : null;
            Database.DatabaseService.Execute("""
                INSERT INTO worlds (instance_id, name, dir, size_bytes, last_modified, mc_version, icon)
                VALUES (@InstanceId, @Name, @Dir, @SizeBytes, @LastModified, @McVersion, @Icon)
                ON CONFLICT(instance_id, name) DO UPDATE SET
                  dir=excluded.dir, size_bytes=excluded.size_bytes, last_modified=excluded.last_modified,
                  mc_version=COALESCE(worlds.mc_version, excluded.mc_version), icon=excluded.icon
                """, new
            {
                InstanceId = inst.Id,
                Name = w.Name,
                Dir = w.Dir,
                SizeBytes = w.Size,
                LastModified = w.LastWrite.ToString("o"),
                McVersion = mc ?? inst.McVersion,
                Icon = w.Icon,
            });
        }

        foreach (var row in existing)
            if (!found.Any(f => string.Equals(f.Name, row.Name, StringComparison.OrdinalIgnoreCase)))
                Database.DatabaseService.Execute(
                    "DELETE FROM worlds WHERE instance_id=@i AND name=@n", new { i = inst.Id, n = row.Name });

        return found
            .OrderByDescending(f => f.LastWrite)
            .Select(f => new
            {
                name = f.Name,
                dir = f.Dir,
                sizeBytes = f.Size,
                lastModified = f.LastWrite.ToString("o"),
                version = (byName.TryGetValue(f.Name, out var old) ? old.McVersion : null) ?? inst.McVersion,
                iconDataUrl = f.Icon,
                hasLevel = f.HasLevel,
            })
            .ToList();
    }

    /* ------------------------------------------------------------ запуск */
    private static Task<object?> Play(AppServices s, JsonNode? p)
    {
        var inst = RequireInstance(s, p);
        var world = p?.Str("world")?.Trim();

        if (string.IsNullOrWhiteSpace(world))
        {
            Log.Debug($"worlds.play без указания мира для '{inst.Name}' — запуск не выполняется");
            return Task.FromResult<object?>(new { ok = true });
        }
        if (world.Contains("..") || world.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new LauncherException("Некорректное название мира", world);

        // желание запустить конкретный мир сохраняется в БД: запуск идёт через общий LaunchService
        Database.DatabaseService.Execute(
            "INSERT OR REPLACE INTO kv (key, value) VALUES (@K, @V)",
            new { K = $"world:{inst.Id}", V = world });

        if (!Directory.Exists(WorldDir(inst, world)))
            Log.Warn($"Мир '{world}' не найден в {inst.SavesDir} — сборка будет запущена без автозагрузки мира");
        if (s.Launch.IsRunning(inst.Id))
            throw new LauncherException("Сборка уже запущена", inst.Name);

        StartInBackground(s, inst, world, new LaunchRequest { InstanceId = inst.Id });
        return Task.FromResult<object?>(new { ok = true });
    }

    /* ------------------------------------------------------------ переименование */
    private static Task<object?> Rename(AppServices s, JsonNode? p)
    {
        var inst = RequireInstance(s, p);
        var world = RequireWorld(p);
        var newName = (p?.Str("newName") ?? "").Trim();
        if (newName.Length == 0) throw new LauncherException("Введите новое название мира");
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || newName.Contains("..") || newName.Contains('/') || newName.Contains('\\'))
            throw new LauncherException("В названии мира недопустимые символы", newName);
        if (string.Equals(newName, world, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<object?>(new { ok = true });
        if (s.Launch.IsRunning(inst.Id))
            throw new LauncherException("Сборка запущена", "Остановите игру, чтобы переименовать мир");

        var src = WorldDir(inst, world);
        if (!Directory.Exists(src)) throw new LauncherException("Мир не найден", world);
        var dst = WorldDir(inst, newName);
        if (Directory.Exists(dst) || File.Exists(dst)) throw new LauncherException("Мир с таким названием уже существует", newName);

        try { Directory.Move(src, dst); }
        catch (Exception ex) { throw LauncherException.Wrap(ex, "Не удалось переименовать мир"); }

        var row = Database.DatabaseService.QueryFirstOrDefault<WorldRow>("""
            SELECT name, dir, size_bytes AS SizeBytes, last_modified AS LastModified,
                   mc_version AS McVersion, icon
            FROM worlds WHERE instance_id=@i AND name=@n
            """, new { i = inst.Id, n = world });
        Database.DatabaseService.Execute("DELETE FROM worlds WHERE instance_id=@i AND name=@n",
            new { i = inst.Id, n = world });
        Database.DatabaseService.Execute("""
            INSERT INTO worlds (instance_id, name, dir, size_bytes, last_modified, mc_version, icon)
            VALUES (@InstanceId, @Name, @Dir, @SizeBytes, @LastModified, @McVersion, @Icon)
            ON CONFLICT(instance_id, name) DO UPDATE SET
              dir=excluded.dir, size_bytes=excluded.size_bytes, last_modified=excluded.last_modified
            """, new
        {
            InstanceId = inst.Id,
            Name = newName,
            Dir = dst,
            SizeBytes = row?.SizeBytes ?? AppPaths.DirectorySize(dst),
            LastModified = (row?.LastModified ?? Directory.GetLastWriteTime(dst).ToString("o")),
            McVersion = row?.McVersion ?? inst.McVersion,
            Icon = row?.Icon,
        });

        Log.Info($"Мир '{world}' переименован в '{newName}' ({inst.Name})");
        return Task.FromResult<object?>(new { ok = true });
    }

    /* ------------------------------------------------------------ удаление */
    private static Task<object?> Delete(AppServices s, JsonNode? p)
    {
        var inst = RequireInstance(s, p);
        var world = RequireWorld(p);
        var withBackup = p?.Bool("backup", false) ?? false;
        var dir = WorldDir(inst, world);
        var exists = Directory.Exists(dir);

        if (!exists &&
            Database.DatabaseService.Scalar<int?>(
                "SELECT COUNT(1) FROM worlds WHERE instance_id=@i AND name=@n", new { i = inst.Id, n = world }) is not > 0)
            throw new LauncherException("Мир не найден", world);

        if (s.Launch.IsRunning(inst.Id))
            throw new LauncherException("Сборка запущена", "Остановите игру, чтобы удалить мир");

        if (withBackup && exists)
        {
            try { Backups.BackupService.CreateWorldBackup(s.Paths, inst, world); }
            catch (Exception ex) { Log.Warn($"Не удалось создать бэкап мира '{world}': {ex.Message}"); }
        }

        if (exists)
        {
            try { Directory.Delete(dir, true); }
            catch (Exception ex) { throw LauncherException.Wrap(ex, "Не удалось удалить мир"); }
        }

        Database.DatabaseService.Execute("DELETE FROM worlds WHERE instance_id=@i AND name=@n",
            new { i = inst.Id, n = world });
        Log.Info($"Мир '{world}' удалён из '{inst.Name}' (бэкап: {withBackup})");
        return Task.FromResult<object?>(new { ok = true });
    }

    /* ------------------------------------------------------------ резервная копия */
    private static Task<object?> Backup(AppServices s, JsonNode? p)
    {
        var inst = RequireInstance(s, p);
        var world = RequireWorld(p);
        if (!Directory.Exists(WorldDir(inst, world))) throw new LauncherException("Мир не найден", world);
        var info = Backups.BackupService.CreateWorldBackup(s.Paths, inst, world);
        Log.Info($"Бэкап мира '{world}' ({inst.Name}) → {info.Path}");
        return Task.FromResult<object?>(info);
    }

    /* ------------------------------------------------------------ импорт .zip */
    private static async Task<object?> ImportZipAsync(AppServices s, JsonNode? p, CancellationToken ct)
    {
        var inst = RequireInstance(s, p);
        var zipPath = p?.Str("zipPath");
        if (string.IsNullOrWhiteSpace(zipPath)) throw new LauncherException("Не выбран архив мира");
        if (!File.Exists(zipPath)) throw new LauncherException("Архив не найден", zipPath);

        var tmp = Path.Combine(s.Paths.TempDir, "world_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
                zip.ExtractToDirectory(tmp, overwriteFiles: true);
            ct.ThrowIfCancellationRequested();

            var worldDir = FindWorldDir(tmp, out var fromRoot);
            if (worldDir is null)
                throw new LauncherException("В архиве нет мира", "Не найден level.dat — это не папка мира Minecraft");

            var name = fromRoot ? Path.GetFileNameWithoutExtension(zipPath).Trim() : Path.GetFileName(worldDir);
            if (string.IsNullOrWhiteSpace(name)) name = "world";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');

            Directory.CreateDirectory(inst.SavesDir);
            var dst = UniqueDestination(inst.SavesDir, name);
            MoveDirectory(worldDir, dst);
            var finalName = Path.GetFileName(dst);

            Database.DatabaseService.Execute("""
                INSERT INTO worlds (instance_id, name, dir, size_bytes, last_modified, mc_version, icon)
                VALUES (@InstanceId, @Name, @Dir, @SizeBytes, @LastModified, @McVersion, @Icon)
                ON CONFLICT(instance_id, name) DO UPDATE SET
                  dir=excluded.dir, size_bytes=excluded.size_bytes, last_modified=excluded.last_modified
                """, new
            {
                InstanceId = inst.Id,
                Name = finalName,
                Dir = dst,
                SizeBytes = AppPaths.DirectorySize(dst),
                LastModified = Directory.GetLastWriteTime(dst).ToString("o"),
                McVersion = inst.McVersion,
                Icon = ReadIcon(dst),
            });

            Log.Info($"Мир импортирован: '{finalName}' → {dst} ({inst.Name})");
            return (object?)new { ok = true, name = finalName };
        }
        catch (LauncherException) { throw; }
        catch (Exception ex) { throw LauncherException.Wrap(ex, "Не удалось импортировать мир"); }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
            catch (Exception ex) { Log.Debug($"Не удалось очистить временный каталог {tmp}: {ex.Message}"); }
        }
    }

    /// <summary>Ищет папку с level.dat: в корне архива, на один или два уровня вложенности.</summary>
    private static string? FindWorldDir(string root, out bool isRoot)
    {
        isRoot = false;
        if (File.Exists(Path.Combine(root, "level.dat"))) { isRoot = true; return root; }
        foreach (var d in Directory.EnumerateDirectories(root))
        {
            if (File.Exists(Path.Combine(d, "level.dat"))) return d;
            foreach (var d2 in Directory.EnumerateDirectories(d))
                if (File.Exists(Path.Combine(d2, "level.dat"))) return d2;
        }
        return null;
    }

    private static string UniqueDestination(string dir, string name)
    {
        var dst = Path.Combine(dir, name);
        if (!Directory.Exists(dst) && !File.Exists(dst)) return dst;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i})");
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{name}_{Guid.NewGuid().ToString("N")[..6]}");
    }

    private static void MoveDirectory(string src, string dst)
    {
        try { Directory.Move(src, dst); return; }
        catch (IOException) { /* возможно, другой диск — копируем */ }
        catch (UnauthorizedAccessException) { /* нет прав на переименование — копируем */ }
        try
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(dst, Path.GetRelativePath(src, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
            Directory.Delete(src, true);
        }
        catch (Exception ex) { throw LauncherException.Wrap(ex, "Не удалось перенести мир в папку сборки"); }
    }

    /* ------------------------------------------------------------ экспорт .zip */
    private static Task<object?> ExportZip(AppServices s, JsonNode? p)
    {
        var inst = RequireInstance(s, p);
        var world = RequireWorld(p);
        var target = p?.Str("targetPath");
        if (string.IsNullOrWhiteSpace(target)) throw new LauncherException("Не выбрана папка для сохранения");

        var src = WorldDir(inst, world);
        if (!Directory.Exists(src)) throw new LauncherException("Мир не найден", world);

        string full;
        try
        {
            full = Path.GetFullPath(target);
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            if (File.Exists(full)) File.Delete(full);
            ZipFile.CreateFromDirectory(src, full, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        catch (LauncherException) { throw; }
        catch (Exception ex) { throw LauncherException.Wrap(ex, "Не удалось экспортировать мир"); }

        Log.Info($"Мир '{world}' экспортирован → {full}");
        return Task.FromResult<object?>(new { ok = true, path = full });
    }

    /* ------------------------------------------------------------ открыть папку */
    private static Task<object?> OpenFolder(AppServices s, JsonNode? p)
    {
        var inst = RequireInstance(s, p);
        var world = p?.Str("world")?.Trim();
        Directory.CreateDirectory(inst.SavesDir);

        if (string.IsNullOrWhiteSpace(world))
        {
            OpenInExplorer(inst.SavesDir, select: false);
            return Task.FromResult<object?>(new { ok = true });
        }

        var dir = WorldDir(inst, world);
        if (!Directory.Exists(dir)) throw new LauncherException("Мир не найден", world);
        OpenInExplorer(dir, select: true);
        return Task.FromResult<object?>(new { ok = true });
    }
}
