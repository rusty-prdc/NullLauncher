using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NullLauncher.Instances;

/// <summary>
/// Дополнительные IPC-методы сборок: проверка и восстановление файлов, смена версии,
/// экспорт/импорт модпаков .mrpack и история запусков.
/// </summary>
public static class InstanceExtrasIpc
{
    public static void Register(IpcRouter r, AppServices s)
    {
        /* ========================================================== проверка файлов */
        r.Register("instances.verify", (p, _) =>
        {
            var inst = s.Instances.Require(p?.Str("id") ?? "");
            var map = VerifyFiles(s, inst);
            var total = map.Count;
            var ok = map.Count(kv => kv.Value);
            return Task.FromResult<object?>(new { total, ok, missing = total - ok, repaired = 0 });
        });

        /* ========================================================== восстановление */
        r.Register("instances.repair", async (p, ct) =>
        {
            var inst = s.Instances.Require(p?.Str("id") ?? "");
            var before = VerifyFiles(s, inst);
            try
            {
                await s.Provisioner.PrepareAsync(inst.McVersion, inst.Loader, inst.LoaderVersion, inst.Id, ct)
                    .ConfigureAwait(false);
            }
            catch (LauncherException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new LauncherException("Не удалось восстановить файлы игры",
                    "Проверьте подключение к интернету и повторите попытку: " + ex.Message, ex);
            }

            var after = VerifyFiles(s, inst);
            var downloaded = after.Count(kv => kv.Value && before.TryGetValue(kv.Key, out var wasOk) && !wasOk);
            Log.Info($"Восстановление '{inst.Name}': скачано {downloaded}, осталось проблем {after.Count(kv => !kv.Value)}");
            return (object?)new { downloaded, failed = 0 };
        });

        /* ========================================================== смена версии */
        r.Register("instances.changeVersion", async (p, ct) =>
        {
            var inst = s.Instances.Require(p?.Str("id") ?? "");
            var mcVersion = (p?.Str("mcVersion") ?? "").Trim();
            if (mcVersion.Length == 0) throw new LauncherException("Укажите версию Minecraft");
            var loader = (p?.Str("loader") ?? "vanilla").Trim().ToLowerInvariant();
            if (loader.Length == 0) loader = "vanilla";
            var loaderVersion = p?.Str("loaderVersion");
            if (string.IsNullOrWhiteSpace(loaderVersion)) loaderVersion = null;

            // 1) авто-бэкап перед изменением — сбой не должен блокировать операцию
            try
            {
                Backups.BackupService.CreateForInstance(s.Paths, inst, "Смена версии", automatic: true);
            }
            catch (Exception ex) { Log.Warn($"Не удалось сделать бэкап перед сменой версии: {ex.Message}"); }

            // 2) заранее убеждаемся, что нужная версия существует (скачает version json при необходимости)
            try
            {
                await s.Versions.EnsureVersionJsonAsync(mcVersion, ct).ConfigureAwait(false);
            }
            catch (LauncherException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new LauncherException("Не удалось получить версию Minecraft",
                    $"{mcVersion}: {ex.Message}", ex);
            }

            // 3) метаданные: SaveMetadata пишет instance.json, Upsert обновляет строку в БД
            inst.McVersion = mcVersion;
            inst.Loader = loader;
            inst.LoaderVersion = loaderVersion;
            s.Instances.SaveMetadata(inst);
            InstanceRepository.Upsert(inst);

            // 4) проверка модов на совместимость — предупреждаем, но не блокируем
            var incompatible = await FindIncompatibleModsAsync(inst, mcVersion, ct).ConfigureAwait(false);
            if (incompatible.Count > 0)
            {
                var shown = incompatible.Take(5).ToList();
                var tail = incompatible.Count > shown.Count ? $" и ещё {incompatible.Count - shown.Count}" : "";
                s.NotifyUser("warn", "Моды могут не подойти",
                    $"Для Minecraft {mcVersion} не подходят: {string.Join(", ", shown)}{tail}. " +
                    "Сборка изменена, при проблемах обновите или отключите эти моды.");
            }

            // 5) сообщаем UI, что содержимое изменилось
            s.Emit("instance.changed", new { id = inst.Id });
            Log.Info($"Сборка '{inst.Name}': {mcVersion} / {loader}" + (loaderVersion is null ? "" : $" {loaderVersion}"));
            return (object?)new { ok = true, incompatibleMods = incompatible };
        });

        /* ========================================================== экспорт .mrpack */
        r.Register("instances.exportMrpack", (p, _) =>
        {
            var inst = s.Instances.Require(p?.Str("id") ?? "");
            var meta = p?["meta"] as JsonObject ?? new JsonObject();

            var packName = Str(meta["name"]) ?? inst.Name;
            var versionId = Str(meta["versionId"]) ?? "1.0.0";
            var mcVersion = Str(meta["mcVersion"]) ?? inst.McVersion;
            var loader = (Str(meta["loader"]) ?? inst.Loader ?? "vanilla").ToLowerInvariant();
            var loaderVersion = inst.LoaderVersion;

            var target = (p?.Str("targetPath") ?? "").Trim();
            if (target.Length == 0)
                throw new LauncherException("Укажите, куда сохранить модпак", "Путь к файлу .mrpack");
            if (Directory.Exists(target) || string.IsNullOrEmpty(Path.GetFileName(target)))
                target = Path.Combine(target, Sanitize(packName) + ".mrpack");
            else if (string.IsNullOrEmpty(Path.GetExtension(target)))
                target += ".mrpack";

            var dir = Path.GetDirectoryName(Path.GetFullPath(target));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(target)) File.Delete(target);

            int skipped;
            using (var zip = ZipFile.Open(target, ZipArchiveMode.Create))
            {
                var (index, skippedFiles) = BuildIndex(inst, packName, versionId, mcVersion, loader, loaderVersion);
                skipped = skippedFiles;
                WriteText(zip, "modrinth.index.json", index.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                // overrides: config + базовые файлы настроек; ресурпаки/шейдеры/датапаки не переносим
                var configDir = inst.ConfigDir;
                if (Directory.Exists(configDir))
                    foreach (var file in Directory.EnumerateFiles(configDir, "*", SearchOption.AllDirectories))
                        AddFile(zip, file, "overrides/config/" + Path.GetRelativePath(configDir, file));
                foreach (var extra in new[] { "options.txt", "server.properties" })
                {
                    var file = Path.Combine(inst.GameDir, extra);
                    if (File.Exists(file)) AddFile(zip, file, "overrides/" + extra);
                }

                // честная служебная мета: у formatVersion 1 нет поля author
                var localMeta = new JsonObject
                {
                    ["name"] = packName,
                    ["description"] = Str(meta["description"]) ?? inst.Description,
                    ["author"] = Str(meta["author"]),
                    ["versionId"] = versionId,
                    ["mcVersion"] = mcVersion,
                    ["loader"] = loader,
                    ["loaderVersion"] = loaderVersion,
                    ["instanceId"] = inst.Id,
                    ["exportedAt"] = DateTime.UtcNow.ToString("o"),
                };
                WriteText(zip, "overrides/.nulllauncher-meta.json",
                    localMeta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            var size = new FileInfo(target).Length;
            Log.Info($"Экспорт .mrpack '{packName}' → {target} ({size} байт, пропущено файлов: {skipped})");
            if (skipped > 0)
                s.NotifyUser("info", "Часть файлов не перенесена",
                    $"{skipped} файл(ов) без ссылки на Modrinth не попали в модпак — они не скачиваются по сети.");

            return Task.FromResult<object?>(new { path = target });
        });

        /* ========================================================== импорт .mrpack */
        r.Register("instances.importMrpack", async (p, ct) =>
        {
            var source = (p?.Str("sourcePath") ?? "").Trim();
            if (source.Length == 0) throw new LauncherException("Укажите файл модпака");
            if (!File.Exists(source)) throw new LauncherException("Файл модпака не найден", source);

            var tmp = Path.Combine(s.Paths.TempDir, "mrpack-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tmp);
            try
            {
                try { ZipFile.ExtractToDirectory(source, tmp, overwriteFiles: true); }
                catch (Exception ex) { throw new LauncherException("Не удалось распаковать модпак", ex.Message, ex); }

                var indexFile = Path.Combine(tmp, "modrinth.index.json");
                if (!File.Exists(indexFile))
                    throw new LauncherException("Это не модпак Modrinth", "В архиве нет modrinth.index.json");

                JsonObject index;
                try { index = JsonNode.Parse(await File.ReadAllTextAsync(indexFile, ct).ConfigureAwait(false)) as JsonObject
                              ?? throw new LauncherException("Повреждённый modrinth.index.json"); }
                catch (LauncherException) { throw; }
                catch (Exception ex) { throw new LauncherException("Повреждённый modrinth.index.json", ex.Message, ex); }

                var deps = index["dependencies"] as JsonObject;
                var mcVersion = Str(deps?["minecraft"]);
                if (string.IsNullOrWhiteSpace(mcVersion))
                    throw new LauncherException("В модпаке не указана версия Minecraft", source);

                var (loader, loaderVersion) = ResolveLoader(deps);
                var name = (p?.Str("instanceName") ?? "").Trim();
                if (name.Length == 0) name = Str(index["name"])?.Trim() ?? "";
                if (name.Length == 0) name = Path.GetFileNameWithoutExtension(source);
                var description = Str(index["description"]) ?? "";

                var rec = s.Instances.Create(new CreateInstanceRequest(
                    Name: name,
                    Description: description,
                    IconPath: null, CoverPath: null, Color: null,
                    McVersion: mcVersion, Loader: loader, LoaderVersion: loaderVersion,
                    JavaMode: null, JavaMajor: null, JavaPath: null,
                    RamMinMb: 512, RamMaxMb: 4096,
                    Width: null, Height: null, Fullscreen: null, GroupIds: null));

                // файлы по downloads (последовательно — не более одной загрузки за раз)
                var (downloaded, failed, skipped) = await DownloadPackFilesAsync(s, rec, index, ct).ConfigureAwait(false);

                // overrides поверх папки сборки
                var overridesDir = Path.Combine(tmp, "overrides");
                if (Directory.Exists(overridesDir))
                {
                    foreach (var file in Directory.EnumerateFiles(overridesDir, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(overridesDir, file);
                        if (rel.StartsWith(".nulllauncher-meta.json", StringComparison.OrdinalIgnoreCase)) continue;
                        var target = SafeCombine(rec.GameDir, rel);
                        if (target is null) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target, true);
                    }
                }

                Log.Info($"Импорт .mrpack → '{rec.Name}' ({mcVersion}/{loader}): " +
                         $"скачано {downloaded}, ошибок {failed}, пропущено {skipped}");
                if (failed > 0 || skipped > 0)
                    s.NotifyUser("warn", "Модпак импортирован частично",
                        $"Скачано: {downloaded}, не удалось: {failed}, без ссылок: {skipped}. " +
                        "Недостающие моды можно доустановить из вкладки Mods.");

                s.Emit("instance.changed", new { id = rec.Id });
                return (object?)(s.Instances.Get(rec.Id) ?? rec);
            }
            catch (LauncherException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new LauncherException("Не удалось импортировать модпак", ex.Message, ex); }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
                catch (Exception ex) { Log.Debug($"Не удалось удалить временную папку {tmp}: {ex.Message}"); }
            }
        });

        /* ========================================================== история запусков */
        r.Register("instances.recentLaunches", (p, _) =>
        {
            var limit = p?.Int("limit", 5) ?? 5;
            if (limit < 1) limit = 5;
            limit = Math.Min(limit, 50);

            var rows = Database.DatabaseService.Query<RecentRow>(@"
                SELECT h.instance_id AS InstanceId,
                       COALESCE(i.name, '') AS Name,
                       h.started_at AS StartedAt,
                       h.exit_code AS ExitCode,
                       h.duration_ms AS DurationMs,
                       h.error AS Error
                FROM launch_history h
                LEFT JOIN instances i ON i.id = h.instance_id
                WHERE h.started_at IS NOT NULL AND h.started_at <> ''
                ORDER BY h.started_at DESC
                LIMIT @limit", new { limit });

            var items = rows.Select(x => new
            {
                instanceId = x.InstanceId,
                name = x.Name,
                startedAt = x.StartedAt,
                exitCode = x.ExitCode,
                durationMs = x.DurationMs,
                error = x.Error,
            }).ToList();
            return Task.FromResult<object?>(items);
        });
    }

    /* ========================================================== проверка файлов */

    private static Dictionary<string, bool> VerifyFiles(AppServices s, InstanceRecord inst)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        void Check(string? path, long expectedSize = 0, string? sha1 = null, bool checkSha1 = false)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var ok = File.Exists(path);
            if (ok && expectedSize > 0)
            {
                try { ok = new FileInfo(path).Length == expectedSize; }
                catch { ok = false; }
            }
            if (ok && checkSha1 && !string.IsNullOrEmpty(sha1))
            {
                try { ok = string.Equals(Sha1File(path), sha1, StringComparison.OrdinalIgnoreCase); }
                catch { ok = false; }
            }
            map[path] = map.TryGetValue(path, out var prev) ? prev && ok : ok;
        }

        // version json базовой версии
        var baseJson = s.Versions.VersionJsonPath(inst.McVersion);
        Check(baseJson);

        var chain = new List<Minecraft.VersionNode>();
        Minecraft.VersionNode? baseNode = null;
        if (File.Exists(baseJson))
        {
            try { baseNode = Minecraft.VersionNode.Load(baseJson); chain.Add(baseNode); }
            catch (Exception ex)
            {
                Log.Warn($"version json повреждён ({inst.McVersion}): {ex.Message}");
                map[baseJson] = false;
            }
        }

        // json загрузчика, если он уже установлен
        Minecraft.VersionNode? loaderNode = null;
        var loaderJson = LoaderJsonPath(s, inst);
        if (loaderJson is not null)
        {
            Check(loaderJson);
            if (File.Exists(loaderJson))
            {
                try { loaderNode = Minecraft.VersionNode.Load(loaderJson); chain.Insert(0, loaderNode); }
                catch (Exception ex) { Log.Warn($"json загрузчика повреждён: {ex.Message}"); map[loaderJson] = false; }
            }
        }

        // библиотеки
        foreach (var node in chain)
            foreach (var lib in node.ApplicableLibraries())
            {
                if (string.IsNullOrEmpty(lib.Name)) continue;
                var rel = lib.RelativePath;
                if (string.IsNullOrEmpty(rel)) continue;
                Check(Path.Combine(s.Paths.LibrariesDir, rel.Replace('/', Path.DirectorySeparatorChar)), lib.Size);
            }

        // клиентская jar (у загрузочной версии — копия базовой)
        var clientNode = chain.FirstOrDefault(n => !string.IsNullOrEmpty(n.ClientJarUrl)) ?? baseNode;
        if (clientNode is not null)
        {
            Check(Path.Combine(s.Paths.VersionsDir, clientNode.Id, clientNode.Id + ".jar"),
                clientNode.ClientJarSize, clientNode.ClientJarSha1, checkSha1: true);
        }
        if (loaderNode is not null && !string.Equals(loaderNode.Id, clientNode?.Id, StringComparison.OrdinalIgnoreCase))
            Check(Path.Combine(s.Paths.VersionsDir, loaderNode.Id, loaderNode.Id + ".jar"));

        // индекс ассетов + выборка объектов
        var assetNode = chain.FirstOrDefault(n => n.AssetIndexUrl is not null) ?? baseNode;
        var indexId = assetNode?.AssetIndexId ?? assetNode?.Assets;
        if (assetNode is not null && !string.IsNullOrWhiteSpace(indexId))
        {
            var indexFile = Path.Combine(s.Paths.AssetsDir, "indexes", indexId + ".json");
            Check(indexFile);
            if (File.Exists(indexFile))
            {
                try
                {
                    var obj = JsonNode.Parse(File.ReadAllText(indexFile)) as JsonObject;
                    if (obj is not null)
                    {
                        var entries = new List<(string Hash, long Size)>();
                        foreach (var (_, val) in obj)
                        {
                            if (val is not JsonObject o) continue;
                            var hash = Str(o["hash"]);
                            if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;
                            var size = o["size"] is JsonValue sv && sv.TryGetValue<long>(out var sz) ? sz : 0;
                            entries.Add((hash, size));
                        }
                        foreach (var e in entries.OrderBy(_ => Random.Shared.Next()).Take(50))
                            Check(Path.Combine(s.Paths.AssetsDir, "objects", e.Hash[..2], e.Hash), e.Size);
                    }
                }
                catch (Exception ex) { Log.Warn($"Не удалось прочитать индекс ассетов: {ex.Message}"); }
            }
        }

        return map;
    }

    /// <summary>Путь к json загрузчика, если он уже установлен (по имени или по каталогу версий).</summary>
    private static string? LoaderJsonPath(AppServices s, InstanceRecord inst)
    {
        var loader = (inst.Loader ?? "vanilla").ToLowerInvariant();
        if (loader is "vanilla" or "") return null;
        var mc = inst.McVersion;

        if (!string.IsNullOrWhiteSpace(inst.LoaderVersion))
        {
            var id = loader switch
            {
                "fabric" => $"fabric-loader-{inst.LoaderVersion}-{mc}",
                "quilt" => $"quilt-loader-{inst.LoaderVersion}-{mc}",
                _ => null,
            };
            if (id is not null)
            {
                var path = s.Versions.VersionJsonPath(id);
                if (File.Exists(path)) return path;
            }
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(s.Paths.VersionsDir))
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith(loader + "-", StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith("-" + mc, StringComparison.OrdinalIgnoreCase)) continue;
                var json = Path.Combine(dir, name + ".json");
                if (File.Exists(json)) return json;
            }
        }
        catch (Exception ex) { Log.Debug($"Поиск json загрузчика: {ex.Message}"); }
        return null;
    }

    private static string Sha1File(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static string Sha512File(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA512.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /* ========================================================== смена версии: моды */

    private static async Task<List<string>> FindIncompatibleModsAsync(InstanceRecord inst, string mcVersion, CancellationToken ct)
    {
        var bad = new List<string>();
        try
        {
            if (!Directory.Exists(inst.ModsDir)) return bad;
            foreach (var file in Directory.EnumerateFiles(inst.ModsDir)
                         .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).Take(300))
            {
                ct.ThrowIfCancellationRequested();
                var declared = await Minecraft.LaunchService.ReadDeclaredMcAsync(file).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(declared)) continue;
                if (!MatchesMc(declared, mcVersion)) bad.Add(Path.GetFileName(file));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* парсинг манифестов не должен валить смену версии */ }
        return bad;
    }

    /// <summary>Простая проверка диапазонов из манифеста мода ("1.21.x", ">=1.20.1", "[1.20,1.21)", "1.20-1.21").</summary>
    private static bool MatchesMc(string declared, string actual)
    {
        declared = declared.Trim();
        if (declared.EndsWith(".x", StringComparison.OrdinalIgnoreCase))
            return actual.StartsWith(declared[..^2], StringComparison.OrdinalIgnoreCase);
        if (declared.Contains('*'))
            return actual.StartsWith(declared.Replace("*", ""), StringComparison.OrdinalIgnoreCase);
        if (declared.Contains("..") || declared.Contains(",") || declared.StartsWith('[') || declared.StartsWith('('))
        {
            var nums = Regex.Matches(declared, @"\d+(\.\d+)+").Select(m => m.Value).ToList();
            if (nums.Count == 0) return true;
            var cmp = CompareVersion(actual, nums[0]) >= 0;
            if (nums.Count > 1)
            {
                var upper = CompareVersion(actual, nums[1]);
                cmp = cmp && (declared.Contains(']') || declared.Contains(')') ? upper < 0 : upper <= 0);
            }
            return cmp;
        }
        if (declared.Contains('-'))
        {
            var parts = declared.Split('-');
            return parts.Length == 2 &&
                   CompareVersion(actual, parts[0]) >= 0 &&
                   CompareVersion(actual, parts[1]) <= 0;
        }
        return actual.Equals(declared, StringComparison.OrdinalIgnoreCase) ||
               actual.StartsWith(declared, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareVersion(string a, string b)
    {
        var pa = a.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var pb = b.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /* ========================================================== .mrpack */

    private sealed class PackContentRow
    {
        public string Filename { get; set; } = "";
        public string Name { get; set; } = "";
        public string? SourceUrl { get; set; }
        public string Folder { get; set; } = "mods";
    }

    private static (JsonObject index, int skipped) BuildIndex(
        InstanceRecord inst, string packName, string versionId, string mcVersion,
        string loader, string? loaderVersion)
    {
        var rows = Database.DatabaseService.Query<PackContentRow>(@"
            SELECT filename, name, source_url AS SourceUrl, 'mods' AS Folder
              FROM mods WHERE instance_id=@id
            UNION ALL
            SELECT filename, name, source_url, 'resourcepacks' FROM resourcepacks WHERE instance_id=@id
            UNION ALL
            SELECT filename, name, source_url, 'shaderpacks' FROM shaders WHERE instance_id=@id
            UNION ALL
            SELECT filename, name, source_url, 'datapacks' FROM datapacks WHERE instance_id=@id", new { id = inst.Id });

        var files = new JsonArray();
        var skipped = 0;
        var noHash = 0;

        foreach (var row in rows)
        {
            var filename = row.Filename;
            if (filename.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                filename = filename[..^".disabled".Length];
            if (filename.Length == 0) { skipped++; continue; }
            if (string.IsNullOrWhiteSpace(row.SourceUrl)) { skipped++; continue; }

            var folder = row.Folder is "resourcepacks" or "shaderpacks" or "datapacks" ? row.Folder : "mods";
            var entry = new JsonObject
            {
                ["path"] = $"{folder}/{filename}",
                ["env"] = new JsonObject { ["client"] = "required", ["server"] = "required" },
                ["downloads"] = new JsonArray(JsonValue.Create(row.SourceUrl)!),
            };

            var local = Path.Combine(inst.ContentDir(folder), filename);
            if (File.Exists(local))
            {
                try
                {
                    entry["hashes"] = new JsonObject
                    {
                        ["sha1"] = Sha1File(local),
                        ["sha512"] = Sha512File(local),
                    };
                }
                catch (Exception ex)
                {
                    Log.Warn($"Не удалось посчитать хэши {filename}: {ex.Message}");
                    noHash++;
                }
            }
            else noHash++;

            files.Add(entry);
        }

        var deps = new JsonObject { ["minecraft"] = mcVersion };
        var depKey = loader switch
        {
            "fabric" => "fabric-loader",
            "quilt" => "quilt-loader",
            "forge" => "forge",
            "neoforge" => "neoforge",
            _ => null,
        };
        if (depKey is not null && !string.IsNullOrWhiteSpace(loaderVersion)) deps[depKey] = loaderVersion;

        var index = new JsonObject
        {
            ["formatVersion"] = 1,
            ["game"] = "minecraft",
            ["versionId"] = versionId,
            ["name"] = string.IsNullOrWhiteSpace(packName) ? inst.Name : packName,
            ["files"] = files,
            ["dependencies"] = deps,
        };

        if (noHash > 0)
            Log.Info($".mrpack: у {noHash} файла(ов) нет локальной копии — хэши не указаны");
        return (index, skipped);
    }

    private static void WriteText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName.Replace('\\', '/'));
        using var s = entry.Open();
        using var w = new StreamWriter(s);
        w.Write(text);
    }

    private static void AddFile(ZipArchive zip, string file, string entryName)
    {
        try { zip.CreateEntryFromFile(file, entryName.Replace('\\', '/').TrimStart('/')); }
        catch (Exception ex) { Log.Debug($"Не удалось добавить в архив {entryName}: {ex.Message}"); }
    }

    private static (string loader, string? loaderVersion) ResolveLoader(JsonObject? deps)
    {
        if (deps is null) return ("vanilla", null);
        foreach (var (key, value) in deps)
        {
            var loader = key switch
            {
                "fabric-loader" => "fabric",
                "quilt-loader" => "quilt",
                "forge" => "forge",
                "neoforge" => "neoforge",
                _ => "",
            };
            if (loader.Length == 0) continue;
            var ver = Str(value);
            return (loader, string.IsNullOrWhiteSpace(ver) ? null : ver);
        }
        return ("vanilla", null);
    }

    private static async Task<(int downloaded, int failed, int skipped)> DownloadPackFilesAsync(
        AppServices s, InstanceRecord rec, JsonObject index, CancellationToken ct)
    {
        var downloaded = 0;
        var failed = 0;
        var skipped = 0;

        if (index["files"] is not JsonArray files) return (0, 0, 0);
        foreach (var node in files)
        {
            ct.ThrowIfCancellationRequested();
            if (node is not JsonObject o) { skipped++; continue; }

            var rel = Str(o["path"]);
            if (string.IsNullOrWhiteSpace(rel)) { skipped++; continue; }
            var url = (o["downloads"] as JsonArray)?.Select(Str).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
            if (url is null) { skipped++; continue; }

            var target = SafeCombine(rec.GameDir, rel);
            if (target is null) { skipped++; continue; }

            var hashes = o["hashes"] as JsonObject;
            var sha1 = Str(hashes?["sha1"]);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await s.Downloads.DownloadAsync(url, target, Path.GetFileName(target),
                        kind: "mod", hashAlgo: sha1 is null ? null : "sha1", hash: sha1,
                        instanceId: rec.Id, ct: ct)
                    .ConfigureAwait(false);
                downloaded++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                Log.Warn($"Не удалось скачать {rel}: {ex.Message}");
            }
        }
        return (downloaded, failed, skipped);
    }

    /// <summary>Собирает путь внутри корня и защищает от выхода за его пределы (zip slip).</summary>
    private static string? SafeCombine(string root, string relative)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(root, relative));
            var baseFull = Path.GetFullPath(root);
            if (!baseFull.EndsWith(Path.DirectorySeparatorChar)) baseFull += Path.DirectorySeparatorChar;
            return full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch { return null; }
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        var trimmed = name.Trim();
        return trimmed.Length == 0 ? "modpack" : trimmed;
    }

    private static string? Str(JsonNode? n)
    {
        if (n is null || n.GetValueKind() == JsonValueKind.Null) return null;
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        var text = n.ToJsonString();
        return text.Length > 1 && text[0] == '"' ? text.Trim('"') : text;
    }

    private sealed class RecentRow
    {
        public string InstanceId { get; set; } = "";
        public string Name { get; set; } = "";
        public string StartedAt { get; set; } = "";
        public int? ExitCode { get; set; }
        public int? DurationMs { get; set; }
        public string? Error { get; set; }
    }
}
