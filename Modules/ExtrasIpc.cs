using System.Text.Json;
using System.Text.Json.Nodes;

namespace NullLauncher.Modules;

/// <summary>
/// Глобальный поиск по палитре команд и горячие клавиши.
/// search.global работает только с локальными данными — сетевых запросов здесь нет,
/// проекты Modrinth берутся исключительно из уже сохранённого кэша api_cache.
/// </summary>
public static class ExtrasIpc
{
    private const int MaxPerGroup = 8;

    public static void Register(IpcRouter r, AppServices s)
    {
        /* ---------------------------------------------------------- глобальный поиск */
        r.Register("search.global", async (p, ct) =>
        {
            var q = (p?.Str("query") ?? "").Trim();
            if (q.Length < 2) return EmptyResult();

            var instances = SafeInstances(s);
            var names = instances.ToDictionary(i => i.Id, i => i.Name, StringComparer.OrdinalIgnoreCase);

            var pattern = "%" + q + "%";

            // сборки
            var instHits = instances
                .Where(i =>
                    i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    (i.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    i.McVersion.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Take(MaxPerGroup)
                .Select(i => new { id = i.Id, name = i.Name, mcVersion = i.McVersion, color = i.Color, loader = i.Loader })
                .ToList();

            // моды и прочий контент по таблицам всех сборок
            var contentHits = SearchContent(pattern, names);

            // настройки (ключи, заголовки, описания)
            var settingsHits = SettingsRegistry.Search(q)
                .Take(MaxPerGroup)
                .Select(x => new
                {
                    key = x.Key,
                    title = x.Title,
                    description = x.Hint,
                    section = x.Section,
                    sectionId = x.SectionId,
                })
                .ToList();

            // миры: таблица worlds + папки saves как резерв
            var worldHits = SearchWorlds(s, instances, q, pattern);

            // версии Minecraft (манифест Mojang читается из кэша, при недоступности — тихо пусто)
            var versionHits = await SearchVersionsAsync(s, q, ct).ConfigureAwait(false);

            // только локальный кэш Modrinth, без сетевых запросов
            var modrinthHits = SearchModrinthCache(q);

            return (object?)new
            {
                instances = instHits,
                mods = contentHits,
                settings = settingsHits,
                worlds = worldHits,
                versions = versionHits,
                modrinth = modrinthHits,
            };
        });

        /* ---------------------------------------------------------- горячие клавиши */
        r.Register("hotkeys.get", (_, _) =>
        {
            var map = DefaultHotkeys();
            try
            {
                var saved = s.Settings.Get("hotkeys", new Dictionary<string, string>());
                foreach (var (k, v) in saved)
                    if (!string.IsNullOrWhiteSpace(k)) map[k] = v ?? "";
            }
            catch (Exception ex) { Log.Debug($"Не удалось прочитать горячие клавиши: {ex.Message}"); }
            return Task.FromResult<object?>(map);
        });

        r.Register("hotkeys.set", (p, _) =>
        {
            var node = p?["map"] as JsonObject
                       ?? throw new LauncherException("Не переданы сочетания клавиш", "Ожидается объект { действие: \"Ctrl+K\" }");

            var map = DefaultHotkeys();
            try
            {
                foreach (var (k, v) in s.Settings.Get("hotkeys", new Dictionary<string, string>()))
                    if (!string.IsNullOrWhiteSpace(k)) map[k] = v ?? "";
            }
            catch (Exception ex) { Log.Debug($"Не удалось прочитать горячие клавиши: {ex.Message}"); }

            foreach (var (key, value) in node)
            {
                if (key is null || key.Length == 0) continue;
                if (map.Count > 64) throw new LauncherException("Слишком много сочетаний клавиш", "Максимум 64 действия");

                string? text;
                if (value is null || value.GetValueKind() == JsonValueKind.Null) text = null;
                else if (value is JsonValue jv && jv.TryGetValue<string>(out var str)) text = str;
                else throw new LauncherException("Сочетание клавиш должно быть строкой", key);

                text = (text ?? "").Trim();
                if (text.Length == 0) { map[key] = ""; continue; }
                if (text.Length > 40) throw new LauncherException("Сочетание клавиш слишком длинное", $"{key}: {text}");

                var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0) throw new LauncherException("Пустое сочетание клавиш", key);
                if (parts.Length > 3)
                    throw new LauncherException("В сочетании не может быть больше трёх клавиш",
                        $"{key}: {text} — например Ctrl+Shift+L");

                map[key] = string.Join("+", parts);
            }

            s.Settings.Set("hotkeys", map);
            return Task.FromResult<object?>(new { ok = true });
        });
    }

    /* ---------------------------------------------------------- поиск: разделы */

    private static List<Instances.InstanceRecord> SafeInstances(AppServices s)
    {
        try { return s.Instances.List(); }
        catch (Exception ex) { Log.Warn($"Поиск по сборкам недоступен: {ex.Message}"); return new List<Instances.InstanceRecord>(); }
    }

    private static object EmptyResult() => new
    {
        instances = new List<object>(),
        mods = new List<object>(),
        settings = new List<object>(),
        worlds = new List<object>(),
        versions = new List<object>(),
        modrinth = new List<object>(),
    };

    private sealed class ContentRow
    {
        public string InstanceId { get; set; } = "";
        public string Filename { get; set; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "mods";
    }

    private static List<object> SearchContent(string pattern, Dictionary<string, string> instanceNames)
    {
        var result = new List<object>();
        try
        {
            var rows = Database.DatabaseService.Query<ContentRow>(@$"
                SELECT instance_id AS InstanceId, filename AS Filename, name AS Name, 'mods' AS Kind
                  FROM mods WHERE filename LIKE @pat OR name LIKE @pat
                UNION ALL
                SELECT instance_id, filename, name, 'resourcepacks' FROM resourcepacks
                  WHERE filename LIKE @pat OR name LIKE @pat
                UNION ALL
                SELECT instance_id, filename, name, 'shaderpacks' FROM shaders
                  WHERE filename LIKE @pat OR name LIKE @pat
                UNION ALL
                SELECT instance_id, filename, name, 'datapacks' FROM datapacks
                  WHERE filename LIKE @pat OR name LIKE @pat
                LIMIT {MaxPerGroup}", new { pat = pattern });

            foreach (var row in rows.Take(MaxPerGroup))
            {
                instanceNames.TryGetValue(row.InstanceId, out var instanceName);
                result.Add(new
                {
                    instanceId = row.InstanceId,
                    instanceName = instanceName ?? "",
                    kind = row.Kind,
                    filename = row.Filename,
                    name = row.Name,
                });
            }
        }
        catch (Exception ex) { Log.Debug($"Поиск по контенту недоступен: {ex.Message}"); }
        return result;
    }

    private sealed class WorldRow
    {
        public string InstanceId { get; set; } = "";
        public string Name { get; set; } = "";
        public long SizeBytes { get; set; }
    }

    private static List<object> SearchWorlds(AppServices s, List<Instances.InstanceRecord> instances, string q, string pattern)
    {
        var result = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var rows = Database.DatabaseService.Query<WorldRow>(@"
                SELECT instance_id AS InstanceId, name AS Name, size_bytes AS SizeBytes
                FROM worlds WHERE name LIKE @pat
                ORDER BY last_modified DESC LIMIT " + MaxPerGroup, new { pat = pattern });
            foreach (var row in rows)
            {
                if (!seen.Add(row.InstanceId + "/" + row.Name)) continue;
                result.Add(new
                {
                    instanceId = row.InstanceId,
                    instanceName = InstanceName(instances, row.InstanceId),
                    name = row.Name,
                    sizeBytes = row.SizeBytes,
                });
            }
        }
        catch (Exception ex) { Log.Debug($"Поиск по мирам (таблица) недоступен: {ex.Message}"); }

        // резерв: папки saves, если миры ещё не синхронизированы в БД
        if (result.Count < MaxPerGroup)
        {
            foreach (var inst in instances)
            {
                if (result.Count >= MaxPerGroup) break;
                try
                {
                    if (!Directory.Exists(inst.SavesDir)) continue;
                    foreach (var dir in Directory.EnumerateDirectories(inst.SavesDir))
                    {
                        if (result.Count >= MaxPerGroup) break;
                        var name = Path.GetFileName(dir);
                        if (!name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!seen.Add(inst.Id + "/" + name)) continue;
                        result.Add(new
                        {
                            instanceId = inst.Id,
                            instanceName = inst.Name,
                            name,
                            sizeBytes = SafeDirSize(dir),
                        });
                    }
                }
                catch (Exception ex) { Log.Debug($"Обход saves {inst.Id}: {ex.Message}"); }
            }
        }
        return result;
    }

    private static string InstanceName(List<Instances.InstanceRecord> instances, string id)
        => instances.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase))?.Name ?? "";

    private static long SafeDirSize(string dir)
    {
        try { return AppPaths.DirectorySize(dir); } catch { return 0; }
    }

    private static async Task<List<object>> SearchVersionsAsync(AppServices s, string q, CancellationToken ct)
    {
        try
        {
            var (list, _) = await s.Versions.ListAsync(false, ct).ConfigureAwait(false);
            return list
                .Where(v => v.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Take(10)
                .Select(v => new { id = v.Id, type = v.Type, installed = v.Installed, javaMajor = v.JavaMajor })
                .Cast<object>()
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Debug($"Поиск по версиям недоступен: {ex.Message}");
            return new List<object>();
        }
    }

    /* ---------------------------------------------------------- кэш Modrinth (без сети) */

    private sealed class CacheRow
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    private static List<object> SearchModrinthCache(string q)
    {
        var result = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var rows = Database.DatabaseService.Query<CacheRow>(
                "SELECT key AS Key, value AS Value FROM api_cache WHERE key LIKE 'modrinth:%'");
            foreach (var row in rows)
            {
                if (result.Count >= MaxPerGroup) break;
                JsonArray? arr;
                try
                {
                    var node = JsonNode.Parse(row.Value);
                    arr = node as JsonArray ?? (node as JsonObject)?["hits"] as JsonArray;
                }
                catch { continue; }
                if (arr is null) continue;

                var keyMatches = row.Key.Contains(q, StringComparison.OrdinalIgnoreCase);
                foreach (var item in arr)
                {
                    if (result.Count >= MaxPerGroup) break;
                    if (item is not JsonObject o) continue;
                    var title = Str(o["title"]) ?? Str(o["name"]) ?? Str(o["slug"]) ?? "";
                    var slug = Str(o["slug"]);
                    var description = Str(o["description"]) ?? "";
                    var match = keyMatches ||
                                title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                (slug?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                description.Contains(q, StringComparison.OrdinalIgnoreCase);
                    if (!match) continue;

                    var projectId = Str(o["projectId"]) ?? Str(o["id"]);
                    var dedup = projectId ?? slug ?? title;
                    if (!seen.Add(dedup)) continue;

                    result.Add(new
                    {
                        projectId,
                        slug,
                        title,
                        projectType = Str(o["projectType"]) ?? "project",
                        downloads = Num(o["downloads"]),
                    });
                }
            }
        }
        catch (Exception ex) { Log.Debug($"Кэш Modrinth для поиска недоступен: {ex.Message}"); }
        return result;
    }

    private static string? Str(JsonNode? n)
    {
        if (n is null || n.GetValueKind() == JsonValueKind.Null) return null;
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return null;
    }

    private static long Num(JsonNode? n)
    {
        if (n is JsonValue v && v.TryGetValue<double>(out var d)) return (long)d;
        return 0;
    }

    /* ---------------------------------------------------------- горячие клавиши: значения по умолчанию */

    /// <summary>Дублирует ui.DefaultHotkeys из UI/app.js: search, refresh, newInstance, settings, logs, play.</summary>
    private static Dictionary<string, string> DefaultHotkeys() => new()
    {
        ["search"] = "Ctrl+K",
        ["refresh"] = "Ctrl+R",
        ["newInstance"] = "Ctrl+N",
        ["settings"] = "Ctrl+,",
        ["logs"] = "Ctrl+Shift+L",
        ["play"] = "Ctrl+Enter",
    };
}
