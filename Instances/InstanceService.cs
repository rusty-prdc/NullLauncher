using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NullLauncher.Instances;

public sealed record CreateInstanceRequest(
    string Name, string? Description, string? IconPath, string? CoverPath, string? Color,
    string McVersion, string Loader, string? LoaderVersion,
    string? JavaMode, int? JavaMajor, string? JavaPath,
    int RamMinMb, int RamMaxMb, int? Width, int? Height, bool? Fullscreen, List<string>? GroupIds);

public sealed class InstanceService
{
    private readonly Core.AppPaths _paths;

    public InstanceService(Core.AppPaths paths) => _paths = paths;

    private static readonly JsonSerializerOptions JsonOpts = Core.IpcRouter.JsonOptions;

    /* ------------------------------------------------------------ список */
    public List<InstanceRecord> List()
    {
        var all = InstanceRepository.GetAll();
        foreach (var i in all)
        {
            i.GroupIds = InstanceRepository.GroupIdsOf(i.Id);
            i.ModCount = CountContent(i.ModsDir);
        }
        return all;
    }

    /// <summary>Считает реальные файлы контента (.jar / .jar.disabled / .zip / .zip.disabled).</summary>
    public static int CountContent(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            return Directory.EnumerateFiles(dir)
                .Count(f =>
                {
                    var name = Path.GetFileName(f);
                    if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) name = name[..^9];
                    var ext = Path.GetExtension(name).ToLowerInvariant();
                    return ext is ".jar" or ".zip";
                });
        }
        catch { return 0; }
    }

    public InstanceRecord? Get(string id)
    {
        var rec = InstanceRepository.Get(id);
        if (rec is not null && !Directory.Exists(rec.Dir))
            Log.Warn($"Сборка {rec.Id} найдена в БД, но папка отсутствует: {rec.Dir}");
        return rec;
    }

    public InstanceRecord Require(string id)
        => Get(id) ?? throw new Core.LauncherException("Сборка не найдена", $"id={id}");

    /* ------------------------------------------------------------ создание */
    public InstanceRecord Create(CreateInstanceRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new Core.LauncherException("Укажите название сборки");
        if (string.IsNullOrWhiteSpace(req.McVersion))
            throw new Core.LauncherException("Выберите версию Minecraft");

        var name = req.Name.Trim();
        var id = Guid.NewGuid().ToString("N")[..16];
        var dir = _paths.InstanceDir(id);
        var gameDir = Path.Combine(dir, ".minecraft");
        Directory.CreateDirectory(gameDir);
        foreach (var sub in new[] { "mods", "config", "saves", "resourcepacks", "shaderpacks", "datapacks", "logs", "crash-reports", "screenshots" })
            Directory.CreateDirectory(Path.Combine(gameDir, sub));

        var rec = new InstanceRecord
        {
            Id = id,
            Name = name,
            Description = (req.Description ?? "").Trim(),
            Color = string.IsNullOrWhiteSpace(req.Color) ? "#3ecf8e" : req.Color!,
            McVersion = req.McVersion,
            Loader = string.IsNullOrWhiteSpace(req.Loader) ? "vanilla" : req.Loader.ToLowerInvariant(),
            LoaderVersion = string.IsNullOrWhiteSpace(req.LoaderVersion) ? null : req.LoaderVersion,
            JavaMode = req.JavaMode ?? "auto",
            JavaMajor = req.JavaMajor,
            JavaPath = req.JavaPath,
            RamMinMb = Math.Max(256, req.RamMinMb),
            RamMaxMb = Math.Max(512, req.RamMaxMb),
            Width = req.Width ?? 1920,
            Height = req.Height ?? 1080,
            Fullscreen = req.Fullscreen ?? false,
            Dir = dir,
            GroupIds = req.GroupIds ?? new List<string>(),
            SortOrder = List().Count,
        };

        // Иконка/обложка копируются внутрь папки сборки, чтобы не потеряться при удалении исходника
        rec.IconPath = CopyImageInto(req.IconPath, dir, "icon");
        rec.CoverPath = CopyImageInto(req.CoverPath, dir, "cover");

        SaveMetadata(rec);
        InstanceRepository.Upsert(rec);
        foreach (var g in rec.GroupIds) AddToGroup(g, rec.Id);

        Log.Info($"Создана сборка '{rec.Name}' ({rec.McVersion}, {rec.Loader}) → {dir}");
        return Get(rec.Id)!;
    }

    /// <summary>Копирует изображение внутрь папки сборки и возвращает относительный путь.</summary>
    private string? CopyImageInto(string? sourcePath, string instanceDir, string baseName)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;
        try
        {
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp"))
                throw new Core.LauncherException("Формат изображения не поддерживается", $"Ожидался PNG/JPG/WEBP, получен {ext}");
            var target = Path.Combine(instanceDir, baseName + ext);
            File.Copy(sourcePath, target, true);
            return target;
        }
        catch (Core.LauncherException) { throw; }
        catch (Exception ex) { throw Core.LauncherException.Wrap(ex, "Не удалось сохранить изображение"); }
    }

    /* ------------------------------------------------------------ обновление */
    public InstanceRecord Update(string id, JsonNode? patch)
    {
        var rec = Require(id);
        if (patch is null) return rec;

        var changed = false;

        if (patch["name"] is not null) { var v = patch.Str("name")!.Trim(); if (v.Length > 0 && v != rec.Name) { rec.Name = v; changed = true; } }
        if (patch["description"] is not null) { rec.Description = patch.Str("description") ?? ""; changed = true; }
        if (patch["color"] is not null) { rec.Color = patch.Str("color") ?? rec.Color; changed = true; }
        if (patch["favorite"] is not null) { rec.Favorite = patch.Bool("favorite", rec.Favorite); changed = true; }
        if (patch["loader"] is not null) { rec.Loader = (patch.Str("loader") ?? "vanilla").ToLowerInvariant(); changed = true; }
        if (patch["loaderVersion"] is not null) { rec.LoaderVersion = patch.Str("loaderVersion"); changed = true; }
        if (patch["javaMode"] is not null) { rec.JavaMode = patch.Str("javaMode") ?? "auto"; changed = true; }
        if (patch["javaMajor"] is not null) { rec.JavaMajor = patch.Int("javaMajor", rec.JavaMajor ?? 0); changed = true; }
        if (patch["javaPath"] is not null) { rec.JavaPath = patch.Str("javaPath"); changed = true; }
        if (patch["ramMinMb"] is not null) { rec.RamMinMb = Math.Max(256, patch.Int("ramMinMb", rec.RamMinMb)); changed = true; }
        if (patch["ramMaxMb"] is not null) { rec.RamMaxMb = Math.Max(512, patch.Int("ramMaxMb", rec.RamMaxMb)); changed = true; }
        if (patch["jvmArgs"] is not null) { rec.JvmArgs = patch.Str("jvmArgs") ?? ""; changed = true; }
        if (patch["gameArgs"] is not null) { rec.GameArgs = patch.Str("gameArgs") ?? ""; changed = true; }
        if (patch["envVars"] is not null) { rec.EnvVars = patch.Str("envVars") ?? ""; changed = true; }
        if (patch["windowMode"] is not null) { rec.WindowMode = patch.Str("windowMode") ?? "windowed"; changed = true; }
        if (patch["width"] is not null) { rec.Width = patch.Int("width", rec.Width); changed = true; }
        if (patch["height"] is not null) { rec.Height = patch.Int("height", rec.Height); changed = true; }
        if (patch["fullscreen"] is not null) { rec.Fullscreen = patch.Bool("fullscreen", rec.Fullscreen); changed = true; }
        if (patch["vsync"] is not null) { rec.Vsync = patch.Bool("vsync", rec.Vsync); changed = true; }
        if (patch["fpsLimit"] is not null) { rec.FpsLimit = patch.Int("fpsLimit", rec.FpsLimit); changed = true; }
        if (patch["serverIp"] is not null) { rec.ServerIp = patch.Str("serverIp"); changed = true; }
        if (patch["serverPort"] is not null) { rec.ServerPort = patch.Int("serverPort", rec.ServerPort ?? 25565); changed = true; }
        if (patch["accountUuid"] is not null) { rec.AccountUuid = patch.Str("accountUuid"); changed = true; }
        if (patch["safeMode"] is not null) { rec.SafeMode = patch.Bool("safeMode", rec.SafeMode); changed = true; }
        if (patch["modpackName"] is not null) { rec.ModpackName = patch.Str("modpackName"); changed = true; }
        if (patch["modpackVersion"] is not null) { rec.ModpackVersion = patch.Str("modpackVersion"); changed = true; }
        if (patch["mcVersion"] is not null) { rec.McVersion = patch.Str("mcVersion") ?? rec.McVersion; changed = true; }
        if (patch["sortOrder"] is not null) { rec.SortOrder = patch.Int("sortOrder", rec.SortOrder); changed = true; }

        if (patch["iconPath"] is not null)
        {
            var p = patch.Str("iconPath");
            rec.IconPath = string.IsNullOrEmpty(p) ? null : CopyImageInto(p, rec.Dir, "icon");
            changed = true;
        }
        if (patch["coverPath"] is not null)
        {
            var p = patch.Str("coverPath");
            rec.CoverPath = string.IsNullOrEmpty(p) ? null : CopyImageInto(p, rec.Dir, "cover");
            changed = true;
        }
        if (patch["groupIds"] is not null)
        {
            rec.GroupIds = patch.StrList("groupIds");
            SyncGroups(rec);
            changed = true;
        }

        if (!changed) return rec;
        SaveMetadata(rec);
        InstanceRepository.Upsert(rec);
        return Get(id)!;
    }

    private void SyncGroups(InstanceRecord rec)
    {
        var existing = Database.DatabaseService.Query<string>(
            "SELECT group_id FROM group_instances WHERE instance_id=@id", new { id = rec.Id });
        foreach (var g in existing.Except(rec.GroupIds))
            Database.DatabaseService.Execute("DELETE FROM group_instances WHERE group_id=@g AND instance_id=@i",
                new { g, i = rec.Id });
        foreach (var g in rec.GroupIds.Except(existing))
            AddToGroup(g, rec.Id);
    }

    private void AddToGroup(string groupId, string instanceId)
    {
        Database.DatabaseService.Execute("""
            INSERT OR IGNORE INTO group_instances (group_id, instance_id, sort_order)
            VALUES (@g, @i, (SELECT COALESCE(MAX(sort_order),0)+1 FROM group_instances WHERE group_id=@g))
            """, new { g = groupId, i = instanceId });
    }

    /* ------------------------------------------------------------ метаданные */
    public void SaveMetadata(InstanceRecord rec)
    {
        try
        {
            Directory.CreateDirectory(rec.Dir);
            var file = Path.Combine(rec.Dir, "instance.json");
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(rec, JsonOpts));
            File.Move(tmp, file, true);
        }
        catch (Exception ex) { Log.Error($"Не удалось сохранить instance.json для {rec.Id}", ex); }
    }

    /* ------------------------------------------------------------ удаление */
    public void Delete(string id, bool backup)
    {
        var rec = Require(id);
        if (backup)
        {
            try { Backups.BackupService.CreateForInstance(_paths, rec, "Удаление сборки", automatic: true); }
            catch (Exception ex) { Log.Warn($"Не удалось сделать бэкап перед удалением: {ex.Message}"); }
        }
        try
        {
            if (Directory.Exists(rec.Dir)) Directory.Delete(rec.Dir, true);
        }
        catch (Exception ex)
        {
            throw new Core.LauncherException(
                "Не удалось удалить папку сборки", $"Закройте Minecraft и повторите: {rec.Dir}\n{ex.Message}", ex);
        }
        InstanceRepository.Delete(id);
        Log.Info($"Сборка '{rec.Name}' удалена (бэкап: {backup})");
    }

    /* ------------------------------------------------------------ дубликат */
    public InstanceRecord Duplicate(string id, string? newName, bool includeWorlds, bool includeMods)
    {
        var src = Require(id);
        var newId = Guid.NewGuid().ToString("N")[..16];
        var dir = _paths.InstanceDir(newId);
        CopyDirectory(src.Dir, dir, rel =>
        {
            if (!includeWorlds && rel.StartsWith("saves", StringComparison.OrdinalIgnoreCase)) return false;
            if (!includeMods && rel.StartsWith("mods", StringComparison.OrdinalIgnoreCase)) return false;
            if (rel.EndsWith("instance.json", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        });

        var clone = JsonSerializer.Deserialize<InstanceRecord>(JsonSerializer.Serialize(src, JsonOpts), JsonOpts)!;
        clone.Id = newId;
        clone.Name = string.IsNullOrWhiteSpace(newName) ? src.Name + " (копия)" : newName!.Trim();
        clone.CreatedAt = DateTime.UtcNow;
        clone.LastPlayedAt = null;
        clone.LaunchCount = 0;
        clone.PlayTimeMs = 0;
        clone.Dir = dir;
        clone.GroupIds = new List<string>(src.GroupIds);
        SaveMetadata(clone);
        InstanceRepository.Upsert(clone);
        foreach (var g in clone.GroupIds) AddToGroup(g, clone.Id);
        Log.Info($"Сборка склонирована: '{src.Name}' → '{clone.Name}'");
        return Get(clone.Id)!;
    }

    private static void CopyDirectory(string src, string dst, Func<string, bool> filter)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, dir);
            if (rel.StartsWith(".git", StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            if (!filter(rel)) continue;
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    /* ------------------------------------------------------------ экспорт / импорт */
    public (string path, long bytes) Export(string id, string targetPath, Dictionary<string, bool> include)
    {
        var rec = Require(id);
        bool pick(string key) => include.TryGetValue(key, out var v) && v;

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        if (File.Exists(targetPath)) File.Delete(targetPath);

        using var zip = ZipFile.Open(targetPath, ZipArchiveMode.Create);
        var metaJson = JsonSerializer.Serialize(rec, JsonOpts);
        var metaEntry = zip.CreateEntry("instance.json");
        using (var s = metaEntry.Open()) using (var w = new StreamWriter(s)) w.Write(metaJson);

        var gameDir = rec.GameDir;
        if (Directory.Exists(gameDir))
        {
            foreach (var file in Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(gameDir, file);
                var top = rel.Split(Path.DirectorySeparatorChar)[0].ToLowerInvariant();
                bool take = top switch
                {
                    "mods" => pick("mods"),
                    "resourcepacks" => pick("resourcepacks"),
                    "shaderpacks" => pick("shaders"),
                    "config" => pick("configs"),
                    "saves" => pick("worlds"),
                    "options.txt" or "optionsof.txt" or "servers.dat" => pick("options"),
                    "screenshots" => pick("screenshots"),
                    _ => false,
                };
                if (rel.Equals("options.txt", StringComparison.OrdinalIgnoreCase)) take = pick("options");
                if (!take) continue;
                zip.CreateEntryFromFile(file, Path.Combine(".minecraft", rel));
            }
        }
        // иконка/обложка
        foreach (var img in new[] { rec.IconPath, rec.CoverPath })
            if (!string.IsNullOrEmpty(img) && File.Exists(img))
                zip.CreateEntryFromFile(img, Path.GetFileName(img));

        var fi = new FileInfo(targetPath);
        Log.Info($"Экспорт '{rec.Name}' → {targetPath} ({fi.Length} байт)");
        return (targetPath, fi.Length);
    }

    public InstanceRecord Import(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new Core.LauncherException("Файл не найден", sourcePath);
        var tmp = Path.Combine(_paths.TempDir, "import-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            ZipFile.ExtractToDirectory(sourcePath, tmp, true);
            var metaFile = Path.Combine(tmp, "instance.json");
            if (!File.Exists(metaFile))
                throw new Core.LauncherException("Это не файл сборки NullLauncher", "В архиве нет instance.json");

            var rec = JsonSerializer.Deserialize<InstanceRecord>(File.ReadAllText(metaFile), JsonOpts)
                      ?? throw new Core.LauncherException("Повреждённый файл instance.json");
            rec.Id = Guid.NewGuid().ToString("N")[..16];
            rec.Name = string.IsNullOrWhiteSpace(rec.Name) ? "Импортированная сборка" : rec.Name;
            rec.CreatedAt = DateTime.UtcNow;
            rec.LastPlayedAt = null;
            rec.Dir = _paths.InstanceDir(rec.Id);

            if (Directory.Exists(rec.Dir)) Directory.Delete(rec.Dir, true);
            Directory.CreateDirectory(Path.GetDirectoryName(rec.Dir)!);
            Directory.Move(tmp, rec.Dir);

            // Переносим картинки внутрь, если они лежали в корне архива
            foreach (var img in new[] { rec.IconPath, rec.CoverPath })
                if (!string.IsNullOrEmpty(img))
                {
                    var local = Path.Combine(rec.Dir, Path.GetFileName(img));
                    if (File.Exists(local)) { if (img != local) { } }
                }
            SaveMetadata(rec);
            InstanceRepository.Upsert(rec);
            foreach (var g in rec.GroupIds) AddToGroup(g, rec.Id);
            Log.Info($"Импортирована сборка '{rec.Name}' из {sourcePath}");
            return Get(rec.Id)!;
        }
        catch (Exception ex) when (ex is not Core.LauncherException)
        {
            throw new Core.LauncherException("Не удалось импортировать сборку", ex.Message, ex);
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { /* ignore */ }
        }
    }
}
