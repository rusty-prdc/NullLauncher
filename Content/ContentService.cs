using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NullLauncher.Instances;

namespace NullLauncher.Content;

/// <summary>
/// Контент сборки: моды, ресурспаки, шейдеры, датапаки.
/// Каталог сборки — источник истины: список всегда сверяется с файлами на диске,
/// SQLite хранит метаданные и связь с Modrinth (project_id / version_id).
/// </summary>
public static class ContentService
{
    public const string ModrinthApi = "https://api.modrinth.com/v2";

    /// <summary>Колонки content-таблицы с алиасами snake_case → свойства DTO.</summary>
    private const string Columns =
        "id, instance_id AS InstanceId, filename, name, version, author, size_bytes AS SizeBytes, enabled, " +
        "project_id AS ProjectId, version_id AS VersionId, source_url AS SourceUrl, source, " +
        "mc_versions AS McVersions, loaders, updated_at AS UpdatedAt, last_checked_at AS LastCheckedAt";

    /// <summary>Порядок типов контента: mods, resourcepacks, shaderpacks, datapacks.</summary>
    public static readonly string[] AllKinds = { "mods", "resourcepacks", "shaderpacks", "datapacks" };

    /// <summary>
    /// Результат последней проверки обновлений: instanceId → id файлов, для которых реально есть
    /// свежая версия. Раньше плашка «доступно обновление» ставилась просто за наличие project_id,
    /// поэтому не исчезала после обновления.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<string>> UpdateMarks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Запоминает, какие файлы сборки действительно можно обновить (после проверки).</summary>
    public static void RememberUpdates(string instanceId, IEnumerable<string> contentIds)
        => UpdateMarks[instanceId] = new HashSet<string>(contentIds, StringComparer.OrdinalIgnoreCase);

    /// <summary>Снимает отметку «есть обновление» с файла (после успешного обновления).</summary>
    public static void ClearUpdateMark(string instanceId, string contentId)
    {
        if (UpdateMarks.TryGetValue(instanceId, out var set)) set.Remove(contentId);
    }

    /// <summary>Снимает отметки обновлений всей сборки.</summary>
    public static void ClearUpdateMarks(string instanceId) => UpdateMarks.TryRemove(instanceId, out _);

    private static bool HasUpdateMark(string instanceId, string contentId)
        => UpdateMarks.TryGetValue(instanceId, out var set) && set.Contains(contentId);

    /* ---------------------------------------------------------- DTO */

    public sealed class ContentRow
    {
        public string Id { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public string Filename { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Version { get; set; }
        public string? Author { get; set; }
        public long SizeBytes { get; set; }
        public bool Enabled { get; set; } = true;
        public string? ProjectId { get; set; }
        public string? VersionId { get; set; }
        public string? SourceUrl { get; set; }
        public string Source { get; set; } = "local";
        public string? McVersions { get; set; }
        public string? Loaders { get; set; }
        public string? UpdatedAt { get; set; }
        public string? LastCheckedAt { get; set; }
    }

    public sealed class PresetRow
    {
        public string Id { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public string Name { get; set; } = "";
        public string ContentJson { get; set; } = "[]";
        public string CreatedAt { get; set; } = "";
    }

    private sealed class FileEntry
    {
        public string BaseName = "";
        public string Path = "";
        public bool Disabled;
        public long Size;
        public DateTime LastWriteUtc;
    }

    private sealed class Meta
    {
        public string? Name;
        public string? Version;
        public string? Author;
        public readonly List<string> McVersions = new();
        public readonly List<string> Loaders = new();
    }

    internal sealed class MrVersion
    {
        public string Id = "";
        public string Name = "";
        public string? VersionNumber;
        public string VersionType = "release";
        public string? ProjectId;
        public readonly List<string> GameVersions = new();
        public readonly List<string> Loaders = new();
        public readonly List<MrFile> Files = new();
    }

    internal sealed class MrFile
    {
        public string Filename = "";
        public string Url = "";
        public bool Primary;
        public long Size;
        public string? Sha1;
        public string? Sha512;
    }

    /* ---------------------------------------------------------- типы контента */

    /// <summary>kind из UI → канонический вид (mods / resourcepacks / shaderpacks / datapacks).</summary>
    public static string NormalizeKind(string? kind)
        => (kind ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "mods" or "mod" => "mods",
            "resourcepacks" or "resourcepack" or "resources" => "resourcepacks",
            "shaderpacks" or "shaders" or "shader" => "shaderpacks",
            "datapacks" or "datapack" or "data" => "datapacks",
            var other => throw new LauncherException("Неизвестный тип контента", other),
        };

    /// <summary>kind → таблица БД (для шейдеров таблица называется shaders).</summary>
    public static string TableFor(string kind) => NormalizeKind(kind) switch
    {
        "resourcepacks" => "resourcepacks",
        "shaderpacks" => "shaders",
        "datapacks" => "datapacks",
        _ => "mods",
    };

    private static string KindFromEntry(string? kind, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(kind))
        {
            try { return NormalizeKind(kind); }
            catch (LauncherException) { /* имя файла подскажет тип */ }
        }
        return fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? "mods" : "resourcepacks";
    }

    /* ---------------------------------------------------------- файлы */

    private static List<FileEntry> Scan(string dir)
    {
        var list = new List<FileEntry>();
        try
        {
            foreach (var p in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(p);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;

                var disabled = false;
                var baseName = name;
                if (baseName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                {
                    disabled = true;
                    baseName = baseName[..^".disabled".Length];
                }

                var ext = Path.GetExtension(baseName).ToLowerInvariant();
                if (ext is not (".jar" or ".zip")) continue;

                try
                {
                    var fi = new FileInfo(p);
                    list.Add(new FileEntry
                    {
                        BaseName = baseName,
                        Path = p,
                        Disabled = disabled,
                        Size = fi.Length,
                        LastWriteUtc = fi.LastWriteTimeUtc,
                    });
                }
                catch { /* файл недоступен — пропускаем */ }
            }
        }
        catch (DirectoryNotFoundException) { /* каталога ещё нет — пусто */ }
        catch (Exception ex) { Log.Warn($"Не удалось просканировать {dir}: {ex.Message}"); }
        return list;
    }

    private static Dictionary<string, FileEntry> IndexFiles(IEnumerable<FileEntry> files)
    {
        var dict = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in files.OrderBy(x => x.Disabled))   // включённый файл предпочтительнее .disabled
            if (!dict.ContainsKey(entry.BaseName)) dict[entry.BaseName] = entry;
        return dict;
    }

    /// <summary>Имя файла без расширения и без .disabled — так контент показывается в UI.</summary>
    public static string DisplayName(string fileName)
    {
        var n = fileName;
        if (n.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) n = n[..^".disabled".Length];
        var ext = Path.GetExtension(n);
        return ext.Length > 0 ? n[..^ext.Length] : n;
    }

    private static bool IsSha1Hex(string? s)
        => !string.IsNullOrWhiteSpace(s) && s!.Length == 40 && s.All(Uri.IsHexDigit);

    private static string Sha1(string path)
    {
        using var sha = SHA1.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static string? TrySha1(string path)
    {
        try { return File.Exists(path) ? Sha1(path) : null; }
        catch (Exception ex) { Log.Debug($"sha1 {Path.GetFileName(path)}: {ex.Message}"); return null; }
    }

    private static string? ToJson(List<string>? values)
        => values is { Count: > 0 } ? JsonSerializer.Serialize(values, IpcRouter.JsonOptions) : null;

    private static List<string> SplitJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            var arr = JsonNode.Parse(json!) as JsonArray;
            if (arr is null) return new List<string>();
            return arr.Select(x =>
            {
                var s = x?.ToString().Trim('"');
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }).Where(s => s is not null).Select(s => s!).ToList();
        }
        catch { return new List<string>(); }
    }

    private static string? StringOrList(JsonNode? node, string key)
    {
        var v = node?[key];
        if (v is null) return null;
        if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) return string.IsNullOrWhiteSpace(s) ? null : s;
        if (v is JsonArray arr)
        {
            var items = arr.Select(x => x?.ToString().Trim('"'))
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();
            return items.Count > 0 ? items[0] : null;
        }
        return null;
    }

    private static List<string> StringArray(JsonNode? node, string key)
    {
        var v = node?[key];
        var list = new List<string>();
        if (v is JsonValue jv && jv.TryGetValue<string>(out var s))
        {
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
        }
        else if (v is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var text = item?.ToString().Trim('"');
                if (!string.IsNullOrWhiteSpace(text)) list.Add(text!);
            }
        }
        return list;
    }

    /* ---------------------------------------------------------- метаданные jar */

    /// <summary>Метаданные из содержимого файла. Повреждённый архив не валит общий список.</summary>
    private static Meta? TryParseMeta(string path, string kind)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (kind == "shaderpacks") return null;            // у шейдеров метаданных нет — имя берётся из файла
            if (kind is "resourcepacks" or "datapacks") return ParsePackMeta(path);
            return ParseModMeta(path);
        }
        catch (Exception ex)
        {
            Log.Debug($"Метаданные {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static Meta? ParseModMeta(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        var fabric = zip.GetEntry("fabric.mod.json");
        if (fabric is not null)
        {
            var m = ParseFabric(ReadEntry(fabric));
            if (m is not null) return m;
        }

        var quilt = zip.GetEntry("quilt.mod.json");
        if (quilt is not null)
        {
            var m = ParseQuilt(ReadEntry(quilt));
            if (m is not null) return m;
        }

        var forge = zip.GetEntry("META-INF/mods.toml");
        if (forge is not null)
        {
            var text = ReadEntry(forge);
            return ParseForge(text, "forge", zip);
        }

        var neo = zip.GetEntry("META-INF/neoforge.mods.toml");
        if (neo is not null)
        {
            var text = ReadEntry(neo);
            return ParseForge(text, "neoforge", zip);
        }

        return null;
    }

    private static Meta? ParseFabric(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is null) return null;
        var m = new Meta
        {
            Name = node.Str("name"),
            Version = node.Str("version"),
            Author = AuthorsOf(node["authors"]),
        };

        var depends = node["depends"];
        if (depends is JsonObject obj)
        {
            var mc = StringOrList(obj, "minecraft");
            if (mc is not null) m.McVersions.Add(mc);
            if (obj.ContainsKey("fabric-loader")) m.Loaders.Add("fabric");
            if (obj.ContainsKey("quilt-loader") || obj.ContainsKey("quilt")) m.Loaders.Add("quilt");
            if (obj.ContainsKey("forge")) m.Loaders.Add("forge");
            if (obj.ContainsKey("neoforge")) m.Loaders.Add("neoforge");
        }
        return m;
    }

    private static Meta? ParseQuilt(string json)
    {
        var node = JsonNode.Parse(json);
        var loader = node?["quilt_loader"];
        if (loader is null) return null;
        var meta = loader["metadata"];
        var m = new Meta
        {
            Name = meta.Str("name"),
            Version = loader.Str("version"),
            Author = AuthorsOf(meta?["authors"]),
        };
        foreach (var dep in loader["depends"] as JsonArray ?? new JsonArray())
        {
            var depId = dep?.Str("id");
            if (depId is null) continue;
            if (depId == "minecraft")
            {
                var v = StringOrList(dep, "versions");
                if (v is not null) m.McVersions.Add(v);
            }
            else if (depId is "quilt-loader" or "fabric-loader") { /* загрузчик известен */ }
        }
        m.Loaders.Add("quilt");
        return m;
    }

    private static Meta? ParseForge(string text, string loader, ZipArchive archive)
    {
        var m = new Meta { Author = TomlValue(text, "authors") };
        m.Loaders.Add(loader);
        m.Name = TomlValue(text, "displayName");
        m.Version = TomlValue(text, "version");

        // зависимость от minecraft лежит в блоке [[dependencies.<modId>]]
        foreach (var block in Regex.Split(text, @"\[\[dependencies"))
        {
            if (!Regex.IsMatch(block, @"modId\s*=\s*""minecraft""")) continue;
            var range = TomlValue(block, "versionRange");
            if (range is not null) m.McVersions.AddRange(SplitRange(range));
            break;
        }

        // "${file.jarVersion}" подставляется из манифеста
        if (string.IsNullOrWhiteSpace(m.Version) || m.Version.Contains("${", StringComparison.Ordinal))
        {
            var manifest = archive.GetEntry("META-INF/MANIFEST.MF");
            var impl = manifest is null ? null : ManifestValue(ReadEntry(manifest), "Implementation-Version");
            m.Version = string.IsNullOrWhiteSpace(impl) ? null : impl;
        }
        return m;
    }

    private static Meta? ParsePackMeta(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("pack.mcmeta");
        if (entry is null) return null;
        var node = JsonNode.Parse(ReadEntry(entry));
        var pack = node?["pack"];
        if (pack is null) return null;

        var m = new Meta();
        var format = pack["pack_format"];
        if (format is JsonValue jv && jv.TryGetValue<string>(out var fs)) m.Version = fs;
        else if (format is not null) m.Version = format.ToString();
        return m;
    }

    private static string? AuthorsOf(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue jv && jv.TryGetValue<string>(out var s)) return string.IsNullOrWhiteSpace(s) ? null : s;
        if (node is JsonArray arr)
        {
            var names = arr.Select(x =>
            {
                if (x is JsonValue v && v.TryGetValue<string>(out var str)) return str;
                return x?.Str("name");
            }).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();
            return names.Count > 0 ? string.Join(", ", names) : null;
        }
        return null;
    }

    private static string? TomlValue(string text, string key)
    {
        var m = Regex.Match(text, @"^\s*" + Regex.Escape(key) + @"\s*=\s*""([^""]*)""", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? ManifestValue(string text, string key)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)) continue;
            return line[(key.Length + 1)..].Trim();
        }
        return null;
    }

    /// <summary>"[1.20.1,1.21)" → ["1.20.1", "1.21"].</summary>
    private static List<string> SplitRange(string range)
    {
        var inner = range.Trim().TrimStart('[', '(').TrimEnd(']', ')');
        return inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /* ---------------------------------------------------------- БД */

    private static List<ContentRow> QueryRows(string table, string instanceId)
        => Database.DatabaseService.Query<ContentRow>(
            $"SELECT {Columns} FROM {table} WHERE instance_id = @iid ORDER BY filename", new { iid = instanceId });

    private static ContentRow RequireRow(string table, string instanceId, string id)
        => Database.DatabaseService.QueryFirstOrDefault<ContentRow>(
               $"SELECT {Columns} FROM {table} WHERE id = @id AND instance_id = @iid", new { id, iid = instanceId })
           ?? throw new LauncherException("Файл не найден", $"id={id}");

    private static void Upsert(string table, ContentRow r) => Database.DatabaseService.Execute($"""
        INSERT INTO {table} (id, instance_id, filename, name, version, author, size_bytes, enabled,
                             project_id, version_id, source_url, source, mc_versions, loaders, updated_at, last_checked_at)
        VALUES (@Id, @InstanceId, @Filename, @Name, @Version, @Author, @SizeBytes, @Enabled,
                @ProjectId, @VersionId, @SourceUrl, @Source, @McVersions, @Loaders, @UpdatedAt, @LastCheckedAt)
        ON CONFLICT(instance_id, filename) DO UPDATE SET
          name = CASE WHEN excluded.name IS NOT NULL AND excluded.name <> '' THEN excluded.name ELSE name END,
          version = COALESCE(excluded.version, version),
          author = COALESCE(excluded.author, author),
          size_bytes = excluded.size_bytes,
          enabled = excluded.enabled,
          project_id = COALESCE(excluded.project_id, project_id),
          version_id = COALESCE(excluded.version_id, version_id),
          source_url = COALESCE(excluded.source_url, source_url),
          source = CASE WHEN excluded.project_id IS NOT NULL THEN excluded.source ELSE source END,
          mc_versions = COALESCE(excluded.mc_versions, mc_versions),
          loaders = COALESCE(excluded.loaders, loaders),
          updated_at = excluded.updated_at,
          last_checked_at = excluded.last_checked_at
        """, r);

    /// <summary>Строка для файла, который уже лежит в каталоге контента (имя, версия, автор — из jar).</summary>
    private static ContentRow BuildRow(string instanceId, string kind, string dir, string fileName, bool enabled, string source)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path) && File.Exists(path + ".disabled")) path += ".disabled";
        var exists = File.Exists(path);
        var meta = exists ? TryParseMeta(path, kind) : null;

        return new ContentRow
        {
            Id = exists ? Sha1(path) : Guid.NewGuid().ToString("N"),
            InstanceId = instanceId,
            Filename = fileName,
            Name = meta?.Name ?? DisplayName(fileName),
            Version = meta?.Version,
            Author = meta?.Author,
            SizeBytes = exists ? new FileInfo(path).Length : 0,
            Enabled = enabled,
            Source = source,
            McVersions = meta is null ? null : ToJson(meta.McVersions),
            Loaders = meta is null ? null : ToJson(meta.Loaders),
            UpdatedAt = DateTime.UtcNow.ToString("o"),
        };
    }

    public static void EmitChanged(AppServices s, string instanceId, string kind)
    {
        s.Emit("content.changed", new { instanceId, kind });
        s.Emit("instance.changed", new { id = instanceId });
    }

    /* ---------------------------------------------------------- список */

    /// <summary>
    /// Список контента: каталог сверяется с БД (нет файла — убрать строку, нет строки — вставить),
    /// выдаются только реальные файлы, отсортированные по имени.
    /// </summary>
    public static List<object> List(AppServices s, string instanceId, string kind, CancellationToken ct)
    {
        kind = NormalizeKind(kind);
        var table = TableFor(kind);
        var inst = s.Instances.Require(instanceId);
        var dir = inst.ContentDir(kind);
        Directory.CreateDirectory(dir);

        var rows = QueryRows(table, instanceId);
        var files = Scan(dir);
        var filesByName = IndexFiles(files);

        // 1) файл исчез — строку убираем
        foreach (var row in rows.Where(r => !filesByName.ContainsKey(r.Filename)).ToList())
        {
            Database.DatabaseService.Execute(
                $"DELETE FROM {table} WHERE id = @Id AND instance_id = @iid",
                new { row.Id, iid = instanceId });
            rows.Remove(row);
        }

        var rowsByName = new Dictionary<string, ContentRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            if (!rowsByName.ContainsKey(row.Filename)) rowsByName[row.Filename] = row;

        // 2) файла нет в БД — вставляем (метаданные разбираем из jar)
        foreach (var f in files)
        {
            if (rowsByName.ContainsKey(f.BaseName)) continue;
            var row = BuildRow(instanceId, kind, dir, f.BaseName, !f.Disabled, "local");
            Upsert(table, row);
            rows.Add(row);
            rowsByName[row.Filename] = row;
        }

        // 3) размер и состояние включения держим в актуальном виде
        foreach (var row in rows)
        {
            if (!filesByName.TryGetValue(row.Filename, out var f)) continue;
            var enabled = !f.Disabled;
            if (row.SizeBytes == f.Size && row.Enabled == enabled) continue;
            Database.DatabaseService.Execute(
                $"UPDATE {table} SET size_bytes = @Size, enabled = @Enabled WHERE id = @Id AND instance_id = @iid",
                new { Size = f.Size, Enabled = enabled, row.Id, iid = instanceId });
            row.SizeBytes = f.Size;
            row.Enabled = enabled;
        }

        var result = new List<object>(files.Count);
        foreach (var f in files.OrderBy(x => x.BaseName, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!rowsByName.TryGetValue(f.BaseName, out var row)) continue;
            result.Add(new
            {
                id = row.Id,
                filename = row.Filename,
                name = string.IsNullOrWhiteSpace(row.Name) ? DisplayName(f.BaseName) : row.Name,
                version = row.Version,
                author = row.Author,
                sizeBytes = f.Size,
                enabled = !f.Disabled,
                updatable = HasUpdateMark(instanceId, row.Id),
                latestVersion = (string?)null,
                mcVersions = SplitJsonArray(row.McVersions),
                loaders = SplitJsonArray(row.Loaders),
                sourceUrl = row.SourceUrl,
                source = row.Source,
                lastModified = f.LastWriteUtc.ToString("o"),
                projectId = row.ProjectId,
            });
        }
        return result;
    }

    /* ---------------------------------------------------------- включение / удаление / импорт */

    public static void Toggle(AppServices s, string instanceId, string kind, string id, bool enabled)
    {
        kind = NormalizeKind(kind);
        var table = TableFor(kind);
        var inst = s.Instances.Require(instanceId);
        var row = RequireRow(table, instanceId, id);

        var dir = inst.ContentDir(kind);
        var baseName = Path.Combine(dir, row.Filename);
        var disabledName = baseName + ".disabled";

        var current = File.Exists(baseName) ? baseName
            : File.Exists(disabledName) ? disabledName
            : null;
        if (current is null)
            throw new LauncherException("Файл отсутствует на диске", row.Filename);

        var target = enabled ? baseName : disabledName;
        if (!string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Move(current, target); }
            catch (IOException ex)
            {
                throw new LauncherException("Не удалось переименовать файл",
                    $"{row.Filename}: {ex.Message} (файл может быть занят другой программой)", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new LauncherException("Нет доступа к файлу", $"{row.Filename}: {ex.Message}", ex);
            }
        }

        Database.DatabaseService.Execute(
            $"UPDATE {table} SET enabled = @Enabled, updated_at = @Now WHERE id = @Id AND instance_id = @iid",
            new { Enabled = enabled, Now = DateTime.UtcNow.ToString("o"), row.Id, iid = instanceId });

        Log.Info($"Контент: {row.Filename} → {(enabled ? "включён" : "отключён")} ({kind}, {instanceId})");
        EmitChanged(s, instanceId, kind);
    }

    public static void Remove(AppServices s, string instanceId, string kind, string id)
    {
        kind = NormalizeKind(kind);
        var table = TableFor(kind);
        var inst = s.Instances.Require(instanceId);
        var row = RequireRow(table, instanceId, id);

        var dir = inst.ContentDir(kind);
        foreach (var path in new[] { Path.Combine(dir, row.Filename), Path.Combine(dir, row.Filename + ".disabled") })
        {
            if (!File.Exists(path)) continue;
            try { File.Delete(path); }
            catch (Exception ex)
            {
                throw new LauncherException("Не удалось удалить файл",
                    $"{row.Filename}: {ex.Message} (файл может быть занят другой программой)", ex);
            }
        }

        Database.DatabaseService.Execute(
            $"DELETE FROM {table} WHERE id = @Id AND instance_id = @iid", new { row.Id, iid = instanceId });
        Log.Info($"Контент: удалён {row.Filename} ({kind}, {instanceId})");
        EmitChanged(s, instanceId, kind);
    }

    /// <summary>Копирует выбранные файлы в каталог контента, уже существующие пропускает.</summary>
    public static (int imported, int skipped) Import(
        AppServices s, string instanceId, string kind, IReadOnlyList<string> paths, CancellationToken ct)
    {
        kind = NormalizeKind(kind);
        var table = TableFor(kind);
        var inst = s.Instances.Require(instanceId);
        var dir = inst.ContentDir(kind);
        Directory.CreateDirectory(dir);

        var imported = 0;
        var skipped = 0;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { skipped++; continue; }

                var rawName = Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(rawName)) { skipped++; continue; }
                var disabled = rawName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                var baseName = disabled ? rawName[..^".disabled".Length] : rawName;
                var destPath = Path.Combine(dir, rawName);

                var same = string.Equals(Path.GetFullPath(path), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase);
                if (!same)
                {
                    var busy = File.Exists(destPath)
                        || File.Exists(Path.Combine(dir, baseName))
                        || File.Exists(Path.Combine(dir, baseName + ".disabled"));
                    if (busy) { skipped++; continue; }
                    File.Copy(path, destPath);
                }

                var row = BuildRow(instanceId, kind, dir, baseName, !disabled, "local");
                Upsert(table, row);
                imported++;
            }
            catch (Exception ex)
            {
                Log.Warn($"Импорт {path}: {ex.Message}");
                skipped++;
            }
        }

        Log.Info($"Контент: импорт {imported} из {paths.Count} ({kind}, {instanceId})");
        if (imported > 0) EmitChanged(s, instanceId, kind);
        return (imported, skipped);
    }

    /* ---------------------------------------------------------- Modrinth */

    private static async Task<string> GetAsync(AppServices s, string url, string detail, CancellationToken ct)
    {
        try
        {
            var http = HttpFactory.Get(s);   // общий клиент — не освобождаем
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new LauncherException("Не найдено на Modrinth", detail);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (LauncherException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Modrinth {url}: {ex.Message}");
            throw new LauncherException("Modrinth недоступен", $"{detail}: {ex.Message}", ex);
        }
    }

    internal static async Task<List<MrVersion>> FetchVersionsAsync(AppServices s, string projectId, CancellationToken ct)
    {
        var json = await GetAsync(s, $"{ModrinthApi}/project/{Uri.EscapeDataString(projectId)}/version",
            $"проект {projectId}", ct).ConfigureAwait(false);
        try
        {
            var list = new List<MrVersion>();
            if (JsonNode.Parse(json) is JsonArray arr)
                foreach (var el in arr)
                {
                    var v = ParseVersion(el);
                    if (v is not null) list.Add(v);
                }
            return list;
        }
        catch (Exception ex)
        {
            throw new LauncherException("Некорректный ответ Modrinth", ex.Message, ex);
        }
    }

    internal static async Task<MrVersion> FetchVersionAsync(AppServices s, string versionId, CancellationToken ct)
    {
        var json = await GetAsync(s, $"{ModrinthApi}/version/{Uri.EscapeDataString(versionId)}",
            $"версия {versionId}", ct).ConfigureAwait(false);
        try
        {
            return ParseVersion(JsonNode.Parse(json))
                   ?? throw new LauncherException("Версия не найдена на Modrinth", versionId);
        }
        catch (LauncherException) { throw; }
        catch (Exception ex)
        {
            throw new LauncherException("Некорректный ответ Modrinth", ex.Message, ex);
        }
    }

    private static MrVersion? ParseVersion(JsonNode? el)
    {
        if (el is null) return null;
        var v = new MrVersion
        {
            Id = el.Str("id") ?? "",
            Name = el.Str("name") ?? "",
            VersionNumber = el.Str("version_number"),
            VersionType = el.Str("version_type") ?? "release",
            ProjectId = el.Str("project_id"),
        };
        foreach (var g in el.StrList("game_versions")) v.GameVersions.Add(g);
        foreach (var l in el.StrList("loaders")) v.Loaders.Add(l);

        if (el["files"] is JsonArray files)
            foreach (var f in files)
            {
                var url = f?.Str("url");
                if (string.IsNullOrWhiteSpace(url)) continue;
                v.Files.Add(new MrFile
                {
                    Filename = f?.Str("filename") ?? "",
                    Url = url!,
                    Primary = f?.Bool("primary") ?? false,
                    Size = f?.Long("size", 0) ?? 0,
                    Sha1 = f?["hashes"].Str("sha1"),
                    Sha512 = f?["hashes"].Str("sha512"),
                });
            }
        return v;
    }

    /// <summary>Свежайшая release-версия, подходящая сборке; при отсутствии фильтра — первая release.</summary>
    internal static MrVersion SelectUpdateVersion(InstanceRecord inst, List<MrVersion> versions, bool releaseOnly = true)
    {
        var pool = releaseOnly ? versions.Where(v => v.VersionType == "release").ToList() : versions.ToList();
        if (pool.Count == 0 && releaseOnly) pool = versions.ToList();
        if (pool.Count == 0)
            throw new LauncherException("Обновления не найдены", "У проекта нет версий");

        var suitable = pool.Where(v =>
            v.GameVersions.Contains(inst.McVersion, StringComparer.OrdinalIgnoreCase) &&
            (string.Equals(inst.Loader, "vanilla", StringComparison.OrdinalIgnoreCase) ||
             v.Loaders.Contains(inst.Loader, StringComparer.OrdinalIgnoreCase))).ToList();

        return suitable.FirstOrDefault() ?? pool[0];
    }

    /// <summary>Скачивает версию во временный файл и ставит её в каталог контента (заменяя старый файл).</summary>
    internal static async Task InstallVersionAsync(
        AppServices s, InstanceRecord inst, string kind, string table,
        ContentRow? existing, MrVersion version, string? projectId, bool enabled, CancellationToken ct)
    {
        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault()
            ?? throw new LauncherException("Файл версии не найден", $"{version.Id}: {version.Name}");

        var dir = inst.ContentDir(kind);
        Directory.CreateDirectory(dir);

        var fileName = SanitizeFileName(file.Filename, file.Url);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = existing?.Filename ?? throw new LauncherException("Не удалось определить имя файла", version.Id);

        var temp = Path.Combine(s.Paths.TempDir, $"content-{Guid.NewGuid():N}.tmp");
        var useSha512 = !string.IsNullOrWhiteSpace(file.Sha512);
        try
        {
            await s.Downloads.DownloadAsync(
                file.Url, temp, $"{inst.Name}: {version.Name}", kind: "content",
                hashAlgo: useSha512 ? "sha512" : !string.IsNullOrWhiteSpace(file.Sha1) ? "sha1" : null,
                hash: useSha512 ? file.Sha512 : file.Sha1,
                instanceId: inst.Id, ct: ct).ConfigureAwait(false);

            // состояние включённости сохраняем
            string? oldPath = null;
            var oldBase = existing is null ? null : Path.Combine(dir, existing.Filename);
            if (oldBase is not null)
            {
                if (File.Exists(oldBase)) oldPath = oldBase;
                else if (File.Exists(oldBase + ".disabled")) oldPath = oldBase + ".disabled";
            }
            bool disabled;
            if (oldPath is not null) disabled = oldPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            else if (existing is not null) disabled = !existing.Enabled;
            else disabled = !enabled;

            var target = Path.Combine(dir, fileName) + (disabled ? ".disabled" : "");

            if (oldBase is not null)
                foreach (var old in new[] { oldBase, oldBase + ".disabled" })
                    if (File.Exists(old) && !string.Equals(old, target, StringComparison.OrdinalIgnoreCase))
                        TryDelete(old);
            if (File.Exists(target)) TryDelete(target);

            try { File.Move(temp, target); }
            catch (Exception ex)
            {
                throw new LauncherException("Не удалось заменить файл", $"{fileName}: {ex.Message}", ex);
            }

            // строка БД: новая — для свежей установки, обновление — при апдейте
            var meta = TryParseMeta(target, kind);
            var now = DateTime.UtcNow.ToString("o");
            var size = new FileInfo(target).Length;
            var pid = projectId ?? existing?.ProjectId;
            var mcJson = ToJson(vCount(version.GameVersions) ? version.GameVersions : meta?.McVersions?.ToList());
            var ldJson = ToJson(vCount(version.Loaders) ? version.Loaders : meta?.Loaders?.ToList());

            if (existing is null)
            {
                Upsert(table, new ContentRow
                {
                    Id = Sha1(target),
                    InstanceId = inst.Id,
                    Filename = Path.GetFileName(target),
                    Name = meta?.Name ?? DisplayName(fileName),
                    Version = version.VersionNumber ?? version.Name ?? meta?.Version,
                    Author = meta?.Author,
                    SizeBytes = size,
                    Enabled = !disabled,
                    ProjectId = pid,
                    VersionId = version.Id,
                    SourceUrl = string.IsNullOrWhiteSpace(pid) ? null : $"https://modrinth.com/project/{pid}",
                    Source = "modrinth",
                    McVersions = mcJson,
                    Loaders = ldJson,
                    UpdatedAt = now,
                    LastCheckedAt = now,
                });
            }
            else
            {
                try
                {
                    Database.DatabaseService.Execute($"""
                        UPDATE {table} SET filename = @Filename, name = @Name, version = @Version, author = @Author,
                          size_bytes = @SizeBytes, enabled = @Enabled, project_id = @ProjectId, version_id = @VersionId,
                          source_url = @SourceUrl, source = @Source, mc_versions = @McVersions, loaders = @Loaders,
                          updated_at = @Now, last_checked_at = @Now
                        WHERE id = @Id AND instance_id = @InstanceId
                        """, new
                    {
                        Filename = Path.GetFileName(target),
                        Name = meta?.Name ?? existing.Name ?? DisplayName(fileName),
                        Version = version.VersionNumber ?? version.Name ?? meta?.Version ?? existing.Version,
                        Author = meta?.Author ?? existing.Author,
                        SizeBytes = size,
                        Enabled = !disabled,
                        ProjectId = pid,
                        VersionId = version.Id,
                        SourceUrl = string.IsNullOrWhiteSpace(existing.SourceUrl)
                            ? (string.IsNullOrWhiteSpace(pid) ? null : $"https://modrinth.com/project/{pid}")
                            : existing.SourceUrl,
                        Source = "modrinth",
                        McVersions = mcJson,
                        Loaders = ldJson,
                        Now = now,
                        Id = existing.Id,
                        InstanceId = inst.Id,
                    });
                }
                catch (Exception ex)
                {
                    throw new LauncherException("Не удалось обновить запись", ex.Message, ex);
                }
            }

            Log.Info($"Контент: {fileName} установлен ({version.Name}, {kind}, {inst.Id})");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch { /* временный файл подчистится позже */ }
        }
    }

    private static bool vCount(List<string> list) => list.Count > 0;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { Log.Warn($"Не удалось удалить {Path.GetFileName(path)}: {ex.Message}"); }
    }

    private static string? SanitizeFileName(string filename, string url)
    {
        var name = string.IsNullOrWhiteSpace(filename) ? "" : Path.GetFileName(filename.Trim());
        if (!string.IsNullOrWhiteSpace(name)) return name;
        try { name = Path.GetFileName(new Uri(url).LocalPath); }
        catch { /* некорректный URL */ }
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /* ---------------------------------------------------------- обновление */

    public static async Task UpdateAsync(
        AppServices s, string instanceId, string kind, string id, string? versionId, CancellationToken ct)
    {
        await UpdateCoreAsync(s, instanceId, kind, id, versionId, ct).ConfigureAwait(false);
        EmitChanged(s, instanceId, NormalizeKind(kind));
    }

    private static async Task UpdateCoreAsync(
        AppServices s, string instanceId, string kind, string id, string? versionId, CancellationToken ct)
    {
        kind = NormalizeKind(kind);
        var table = TableFor(kind);
        var inst = s.Instances.Require(instanceId);
        var row = RequireRow(table, instanceId, id);

        if (string.IsNullOrWhiteSpace(row.ProjectId))
            throw new LauncherException("Обновление недоступно", "Файл не связан с проектом Modrinth");

        MrVersion version;
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            version = await FetchVersionAsync(s, versionId!, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(version.ProjectId) &&
                !string.Equals(version.ProjectId, row.ProjectId, StringComparison.OrdinalIgnoreCase))
                throw new LauncherException("Версия не найдена", $"версия {versionId} принадлежит другому проекту");
        }
        else
        {
            var versions = await FetchVersionsAsync(s, row.ProjectId, ct).ConfigureAwait(false);
            version = SelectUpdateVersion(inst, versions);
        }

        await InstallVersionAsync(s, inst, kind, table, row, version, row.ProjectId, enabled: true, ct: ct)
            .ConfigureAwait(false);

        // файл обновлён — плашка «доступно обновление» больше не нужна
        ClearUpdateMark(instanceId, id);
    }

    /// <summary>Обновляет все файлы с project_id. Ошибки считаются, но не прерывают процесс.</summary>
    public static async Task<(int updated, int failed)> UpdateAllAsync(
        AppServices s, string instanceId, string kind, CancellationToken ct)
    {
        kind = NormalizeKind(kind);
        var table = TableFor(kind);
        s.Instances.Require(instanceId);

        var rows = QueryRows(table, instanceId)
            .Where(r => !string.IsNullOrWhiteSpace(r.ProjectId))
            .OrderBy(r => r.Filename, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var updated = 0;
        var failed = 0;
        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await UpdateCoreAsync(s, instanceId, kind, row.Id, null, ct).ConfigureAwait(false);
                updated++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failed++;
                Log.Warn($"Обновление {row.Filename} не удалось: {ex.Message}");
            }
        }

        if (updated > 0) EmitChanged(s, instanceId, kind);
        if (failed > 0 || updated > 0)
            Log.Info($"Контент: обновлено {updated}, ошибок {failed} ({kind}, {instanceId})");
        return (updated, failed);
    }

    /* ---------------------------------------------------------- целостность */

    public static object CheckIntegrity(AppServices s, string instanceId, string kind)
    {
        kind = NormalizeKind(kind);
        var table = TableFor(kind);
        var inst = s.Instances.Require(instanceId);
        var dir = inst.ContentDir(kind);

        var rows = QueryRows(table, instanceId);
        var files = Scan(dir);
        var filesByName = IndexFiles(files);
        var tracked = new HashSet<string>(rows.Select(r => r.Filename), StringComparer.OrdinalIgnoreCase);
        var issues = new List<object>();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string HashOf(string path)
        {
            if (hashes.TryGetValue(path, out var cached)) return cached;
            var h = TrySha1(path) ?? "";
            hashes[path] = h;
            return h;
        }

        foreach (var row in rows)
        {
            if (!filesByName.TryGetValue(row.Filename, out var f))
            {
                issues.Add(new
                {
                    id = row.Id,
                    filename = row.Filename,
                    problem = "missing",
                    message = "Файл отсутствует на диске",
                });
                continue;
            }

            if (row.SizeBytes > 0 && row.SizeBytes != f.Size)
                issues.Add(new
                {
                    id = row.Id,
                    filename = row.Filename,
                    problem = "size",
                    message = $"Размер файла изменился: ожидалось {row.SizeBytes} Б, найдено {f.Size} Б",
                });

            // для локальных файлов id — это sha1, он же служит контрольной суммой
            if (string.Equals(row.Source, "local", StringComparison.OrdinalIgnoreCase) && IsSha1Hex(row.Id))
            {
                var hash = HashOf(f.Path);
                if (hash.Length > 0 && !string.Equals(hash, row.Id, StringComparison.OrdinalIgnoreCase))
                    issues.Add(new
                    {
                        id = row.Id,
                        filename = row.Filename,
                        problem = "hash",
                        message = "Контрольная сумма файла не совпадает с базой данных",
                    });
            }
        }

        var total = rows.Count;
        foreach (var f in files)
        {
            if (tracked.Contains(f.BaseName)) continue;
            total++;
            issues.Add(new
            {
                id = (string?)null,
                filename = f.BaseName,
                problem = "notTracked",
                message = "Файл не отмечен в базе данных",
            });
        }

        // дубликаты — одинаковая контрольная сумма у нескольких файлов
        foreach (var group in files.GroupBy(f => HashOf(f.Path)).Where(g => g.Key.Length > 0 && g.Count() > 1))
        {
            foreach (var f in group.OrderBy(x => x.BaseName, StringComparer.OrdinalIgnoreCase))
            {
                var others = string.Join(", ", group.Where(x => !ReferenceEquals(x, f)).Select(x => x.BaseName));
                issues.Add(new
                {
                    id = (string?)null,
                    filename = f.BaseName,
                    problem = "duplicate",
                    message = $"Дубликат файла: {others}",
                });
            }
        }

        return new { total, issues };
    }

    /* ---------------------------------------------------------- пресеты */

    public static object ListPresets(AppServices s, string instanceId)
    {
        s.Instances.Require(instanceId);
        var rows = Database.DatabaseService.Query<PresetRow>(
            "SELECT id, instance_id AS InstanceId, name, content_json AS ContentJson, created_at AS CreatedAt " +
            "FROM presets WHERE instance_id = @iid ORDER BY created_at DESC", new { iid = instanceId });
        return rows.Select(x => new { id = x.Id, name = x.Name, createdAt = x.CreatedAt }).ToList();
    }

    /// <summary>Снимок текущего контента всех четырёх типов в JSON-массив.</summary>
    public static object SavePreset(AppServices s, string instanceId, string name)
    {
        var inst = s.Instances.Require(instanceId);
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new LauncherException("Укажите название пресета");

        var entries = new List<object>();
        foreach (var kind in AllKinds)
        {
            var table = TableFor(kind);
            var dir = inst.ContentDir(kind);
            var files = IndexFiles(Scan(dir));
            foreach (var row in QueryRows(table, instanceId))
            {
                if (!files.TryGetValue(row.Filename, out var f)) continue;
                entries.Add(new
                {
                    kind,
                    filename = row.Filename,
                    sha1 = TrySha1(f.Path),
                    size = f.Size,
                    projectId = row.ProjectId,
                    versionId = row.VersionId,
                    enabled = !f.Disabled,
                });
            }
        }

        var json = JsonSerializer.Serialize(entries, IpcRouter.JsonOptions);
        var id = Guid.NewGuid().ToString("N")[..16];
        var createdAt = DateTime.UtcNow.ToString("o");
        Database.DatabaseService.Execute(
            "INSERT INTO presets (id, instance_id, name, content_json, created_at) VALUES (@Id, @Iid, @Name, @Json, @CreatedAt)",
            new { Id = id, Iid = instanceId, Name = name, Json = json, CreatedAt = createdAt });

        Log.Info($"Контент: сохранён пресет '{name}' ({entries.Count} файлов, {instanceId})");
        return new { id, name, createdAt };
    }

    public static object DeletePreset(AppServices s, string instanceId, string presetId)
    {
        s.Instances.Require(instanceId);
        var deleted = Database.DatabaseService.Execute(
            "DELETE FROM presets WHERE id = @pid AND instance_id = @iid", new { pid = presetId, iid = instanceId });
        if (deleted == 0) throw new LauncherException("Пресет не найден", presetId);
        return new { ok = true };
    }

    /// <summary>
    /// Восстанавливает файлы пресета: существующие приводятся к нужному состоянию включения,
    /// отсутствующие качаются с Modrinth (если известны project/version). Лишние файлы не трогаем.
    /// </summary>
    public static async Task<object> ApplyPresetAsync(
        AppServices s, string instanceId, string presetId, CancellationToken ct)
    {
        var inst = s.Instances.Require(instanceId);
        var preset = Database.DatabaseService.QueryFirstOrDefault<PresetRow>(
            "SELECT id, instance_id AS InstanceId, name, content_json AS ContentJson, created_at AS CreatedAt " +
            "FROM presets WHERE id = @pid AND instance_id = @iid", new { pid = presetId, iid = instanceId })
            ?? throw new LauncherException("Пресет не найден", presetId);

        var installed = 0;
        var skipped = 0;
        var missing = 0;

        JsonArray? arr = null;
        try { arr = JsonNode.Parse(preset.ContentJson) as JsonArray; }
        catch (Exception ex) { throw new LauncherException("Пресет повреждён", ex.Message, ex); }
        if (arr is null) return new { ok = true, installed, skipped, missing };

        var changed = false;
        foreach (var node in arr)
        {
            ct.ThrowIfCancellationRequested();
            if (node is not JsonObject o) continue;

            var rawName = o.Str("filename");
            if (string.IsNullOrWhiteSpace(rawName)) continue;
            var fileName = Path.GetFileName(rawName);
            var disabledEntry = !o.Bool("enabled", true);
            var kind = KindFromEntry(o.Str("kind"), fileName);
            var table = TableFor(kind);
            var dir = inst.ContentDir(kind);
            Directory.CreateDirectory(dir);

            var baseName = Path.Combine(dir, fileName);
            var target = disabledEntry ? baseName + ".disabled" : baseName;
            var current = File.Exists(baseName) ? baseName
                : File.Exists(baseName + ".disabled") ? baseName + ".disabled"
                : null;

            if (current is not null)
            {
                // файл уже есть — приводим состояние включения и строку БД в порядок
                if (!string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Move(current, target); }
                    catch (Exception ex) { Log.Warn($"Пресет: {fileName} не удалось переименовать: {ex.Message}"); }
                }
                var enabledNow = !disabledEntry;
                Database.DatabaseService.Execute(
                    $"UPDATE {table} SET enabled = @Enabled WHERE filename = @Filename AND instance_id = @iid",
                    new { Enabled = enabledNow, Filename = fileName, iid = instanceId });
                var present = Database.DatabaseService.Scalar<int>(
                    $"SELECT COUNT(1) FROM {table} WHERE filename = @Filename AND instance_id = @iid",
                    new { Filename = fileName, iid = instanceId });
                if (present == 0)
                {
                    var hasProject = !string.IsNullOrWhiteSpace(o.Str("projectId"));
                    Upsert(table, BuildRow(inst.Id, kind, dir, fileName, enabledNow,
                        hasProject ? "modrinth" : "local"));
                    if (hasProject || !string.IsNullOrWhiteSpace(o.Str("versionId")))
                    {
                        Database.DatabaseService.Execute(
                            $"UPDATE {table} SET project_id = COALESCE(@ProjectId, project_id), " +
                            "version_id = COALESCE(@VersionId, version_id) " +
                            "WHERE filename = @Filename AND instance_id = @iid",
                            new
                            {
                                ProjectId = o.Str("projectId"),
                                VersionId = o.Str("versionId"),
                                Filename = fileName,
                                iid = instanceId,
                            });
                    }
                }
                skipped++;
                changed = true;
                continue;
            }

            var projectId = o.Str("projectId");
            var versionId = o.Str("versionId");
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(versionId))
            {
                missing++;
                continue;
            }

            try
            {
                var version = await FetchVersionAsync(s, versionId!, ct).ConfigureAwait(false);
                await InstallVersionAsync(s, inst, kind, table, existing: null, version, projectId,
                    enabled: !disabledEntry, ct: ct).ConfigureAwait(false);
                installed++;
                changed = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                missing++;
                Log.Warn($"Пресет '{preset.Name}': не удалось установить {fileName}: {ex.Message}");
            }
        }

        if (changed) EmitChanged(s, instanceId, "mods");
        Log.Info($"Пресет '{preset.Name}': установлено {installed}, пропущено {skipped}, недоступно {missing}");
        return new { ok = true, installed, skipped, missing };
    }
}
