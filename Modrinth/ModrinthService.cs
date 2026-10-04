using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NullLauncher.Content;
using NullLauncher.Instances;

namespace NullLauncher.Modrinth;

/// <summary>
/// Клиент Modrinth API v2 для NullLauncher: поиск, карточка проекта, версии, теги,
/// установка файлов, проверка обновлений и сопоставление локальных файлов по хешу.
/// GET-ответы кэшируются в api_cache (ключи вида «modrinth:*» — их же читает search.global):
/// свежий кэш отдаётся сразу, при недоступности сети — устаревший (если modrinthOfflineCache
/// включён) с признаком offline=true. Каждый запрос проходит паузу ~220 мс: Modrinth
/// принимает не более 300 запросов в минуту.
/// </summary>
internal sealed class ModrinthService
{
    private const string ApiBase = "https://api.modrinth.com/v2";
    private const string CachePrefix = "modrinth:";
    private const string NotFoundValue = "\"__not_found__\"";
    private const long MinIntervalMs = 220;

    private static long _lastRequestTicks;
    private static bool _offlineKnown;
    private static bool _offlineState;

    private readonly AppServices _s;

    public ModrinthService(AppServices s) => _s = s;

    /* ---------------------------------------------------------- поиск */

    /// <summary>modrinth.search → { hits:[…], total, offline }.</summary>
    public async Task<object> SearchAsync(JsonNode? p, CancellationToken ct)
    {
        var query = (p?.Str("query") ?? "").Trim();
        var type = (p?.Str("type") ?? "").Trim();
        var loaders = p?.StrList("loaders") ?? new List<string>();
        var gameVersions = p?.StrList("gameVersions") ?? new List<string>();
        var categories = p?.StrList("categories") ?? new List<string>();
        var licenses = p?.StrList("license") ?? new List<string>();
        if (licenses.Count == 0) licenses = p?.StrList("licenses") ?? new List<string>();

        var limitParam = p?.Int("limit", 0) ?? 0;
        var limit = limitParam > 0
            ? Math.Clamp(limitParam, 1, 100)
            : Math.Clamp(_s.Settings.Get("modrinthPageSize", 20), 1, 100);
        var offset = Math.Max(0, p?.Int("offset", 0) ?? 0);
        var index = NormalizeIndex(p?.Str("sort") ?? p?.Str("index"));

        // facets Modrinth v2 — массив групп выражений: [["project_type=mod"],["loaders=fabric"]]
        var groups = new List<List<string>>();
        void Group(string field, IEnumerable<string> values)
        {
            var g = values.Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => field + "=" + v.Trim()).ToList();
            if (g.Count > 0) groups.Add(g);
        }
        if (!string.IsNullOrWhiteSpace(type)) Group("project_type", new[] { type });
        Group("loaders", loaders);
        Group("versions", gameVersions);
        Group("categories", categories);
        Group("license", licenses);
        groups.AddRange(ParseRawFacets(p?.Arr("facets")));

        var qs = new List<string>();
        if (query.Length > 0) qs.Add("query=" + Uri.EscapeDataString(query));
        qs.Add("limit=" + limit);
        qs.Add("offset=" + offset);
        qs.Add("index=" + index);
        var facetsJson = groups.Count > 0 ? JsonSerializer.Serialize(groups, IpcRouter.JsonOptions) : "";
        if (facetsJson.Length > 0) qs.Add("facets=" + Uri.EscapeDataString(facetsJson));

        var path = "/search?" + string.Join("&", qs);
        // в ключе держим исходный запрос — так его находит search.global при работе с кэшем
        var cacheKey = $"search:{query}_{ShortHash(string.Join("|", limit, offset, index, facetsJson))}";

        var res = await FetchAsync(path, cacheKey, ct).ConfigureAwait(false);
        var root = res.Node as JsonObject
            ?? throw new LauncherException("Некорректный ответ Modrinth", "ожидался объект поиска");

        var loaderNames = await LoaderNamesAsync(ct).ConfigureAwait(false);
        var hits = new List<object>();
        if (root["hits"] is JsonArray arr)
        {
            foreach (var h in arr)
            {
                if (h is null) continue;
                var cats = Lst(h, "categories");
                var display = Lst(h, "display_categories");
                var shown = display.Count > 0 ? display : cats;
                // загрузчики: либо уже посчитаны (нормализованный кэш), либо пересекаем категории с /tag/loader
                var preset = Lst(h, "loaders");
                var ls = preset.Count > 0 ? preset :
                    cats.Concat(display).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(c => loaderNames.Contains(c)).ToList();
                hits.Add(new
                {
                    projectId = Txt(h, "projectId") ?? Txt(h, "project_id") ?? "",
                    slug = Txt(h, "slug"),
                    title = Txt(h, "title") ?? "",
                    description = Txt(h, "description") ?? "",
                    author = Txt(h, "author"),
                    iconUrl = Txt(h, "iconUrl") ?? Txt(h, "icon_url"),
                    downloads = h.Long("downloads", 0),
                    follows = h.Long("follows", 0),
                    categories = shown,
                    loaders = ls,
                    versions = Lst(h, "versions"),
                    projectType = Txt(h, "projectType") ?? Txt(h, "project_type") ?? "mod",
                    dateModified = Txt(h, "dateModified") ?? Txt(h, "date_modified"),
                });
            }
        }

        var total = (int)root.Long("total_hits", 0);
        if (total == 0 && root["total"] is not null) total = (int)root.Long("total", hits.Count);

        // в кэш кладём нормализованный вид: search.global читает оттуда projectId/projectType
        if (res.FromNetwork)
            WriteCache(cacheKey, JsonSerializer.Serialize(
                new { hits, total, offline = false }, IpcRouter.JsonOptions));

        return new { hits, total, offline = res.Offline };
    }

    /* ---------------------------------------------------------- проект */

    /// <summary>modrinth.project → карточка проекта для детального вида.</summary>
    public async Task<object> ProjectAsync(string id, CancellationToken ct)
    {
        var res = await FetchAsync("/project/" + Uri.EscapeDataString(id), "project:" + id, ct)
            .ConfigureAwait(false);
        var p = res.Node as JsonObject
            ?? throw new LauncherException("Проект не найден", id);

        var author = Txt(p, "author") ?? Txt(p, "owner");
        var team = Txt(p, "team");
        if (author is null && !string.IsNullOrWhiteSpace(team))
            author = await TeamOwnerAsync(team!, ct).ConfigureAwait(false);

        return MapProject(p, author, res.Offline);
    }

    /* ---------------------------------------------------------- версии */

    /// <summary>modrinth.versions → список версий проекта с фильтрами по загрузчику и версии MC.</summary>
    public async Task<object> VersionsAsync(string id, List<string> loaders, List<string> gameVersions, CancellationToken ct)
    {
        var qs = new List<string>();
        if (loaders.Count > 0) qs.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(loaders)));
        if (gameVersions.Count > 0) qs.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(gameVersions)));

        var path = $"/project/{Uri.EscapeDataString(id)}/version" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        var cacheKey = $"versions:{id}:{string.Join(",", loaders)}:{string.Join(",", gameVersions)}";
        var res = await FetchAsync(path, cacheKey, ct, wrapArray: true).ConfigureAwait(false);

        var list = new List<object>();
        if (res.Node is JsonArray arr)
            foreach (var v in arr)
            {
                if (v is null) continue;
                var files = new List<object>();
                if (v["files"] is JsonArray fa)
                    foreach (var f in fa)
                    {
                        if (f is null) continue;
                        files.Add(new
                        {
                            filename = Txt(f, "filename") ?? "",
                            url = Txt(f, "url") ?? "",
                            primary = f.Bool("primary"),
                            size = f.Long("size", 0),
                            hashes = f["hashes"]?.DeepClone(),
                        });
                    }
                var vt = Txt(v, "version_type") ?? "release";
                list.Add(new
                {
                    id = Txt(v, "id") ?? "",
                    projectId = Txt(v, "project_id"),
                    name = Txt(v, "name") ?? Txt(v, "version_number") ?? "",
                    versionNumber = Txt(v, "version_number"),
                    datePublished = Txt(v, "date_published"),
                    gameVersions = Lst(v, "game_versions"),
                    loaders = Lst(v, "loaders"),
                    files,
                    downloads = v.Long("downloads", 0),
                    versionType = vt,
                    channels = new[] { vt },
                    status = Txt(v, "status"),
                    featured = v.Bool("featured"),
                });
            }
        return list;
    }

    /* ---------------------------------------------------------- теги */

    /// <summary>modrinth.tags → { categories, loaders, gameVersions, projectTypes, licenses }.</summary>
    public async Task<object> TagsAsync(CancellationToken ct)
    {
        var cat = await TryFetchAsync("/tag/category", "tag:category", ct).ConfigureAwait(false);
        var ldr = await TryFetchAsync("/tag/loader", "tag:loader", ct).ConfigureAwait(false);
        var gvr = await TryFetchAsync("/tag/game_version", "tag:game_version", ct).ConfigureAwait(false);
        var ptr = await TryFetchAsync("/tag/project_type", "tag:project_type", ct).ConfigureAwait(false);
        var lir = await TryFetchAsync("/tag/license", "tag:license", ct).ConfigureAwait(false);

        if (cat is null && ldr is null && gvr is null && ptr is null && lir is null)
            throw new LauncherException("Modrinth недоступен", "не удалось загрузить списки фильтров");

        var categories = new List<object>();
        foreach (var el in AsArray(cat))
        {
            var n = Txt(el, "name");
            if (!string.IsNullOrWhiteSpace(n)) categories.Add(new { name = n! });
        }

        var loaders = new List<object>();
        foreach (var el in AsArray(ldr))
        {
            var n = Txt(el, "name");
            if (!string.IsNullOrWhiteSpace(n)) loaders.Add(new { name = n! });
        }

        var gameVersions = new List<object>();
        foreach (var el in AsArray(gvr))
        {
            var v = Txt(el, "version");
            if (string.IsNullOrWhiteSpace(v)) continue;
            gameVersions.Add(new { name = v!, versionType = Txt(el, "version_type") ?? "release" });
        }

        var projectTypes = new List<object>();
        foreach (var el in AsArray(ptr))
        {
            var n = el?.GetValueKind() == JsonValueKind.Null ? null : el?.ToString().Trim('"');
            if (!string.IsNullOrWhiteSpace(n)) projectTypes.Add(n!);
        }

        var licenses = new List<object>();
        foreach (var el in AsArray(lir))
        {
            var shortName = Txt(el, "short");
            var title = Txt(el, "name");
            if (string.IsNullOrWhiteSpace(shortName) && string.IsNullOrWhiteSpace(title)) continue;
            licenses.Add(new { name = shortName ?? title!, title = title ?? shortName! });
        }

        return new { categories, loaders, gameVersions, projectTypes, licenses };
    }

    /* ---------------------------------------------------------- установка */

    /// <summary>modrinth.install → скачивает primary-файл версии и записывает строку в таблицу контента.</summary>
    public async Task<object> InstallAsync(string instanceId, string versionId, string kindParam, CancellationToken ct,
        bool installDependencies = true)
    {
        var inst = _s.Instances.Require(instanceId);
        var kind = ContentService.NormalizeKind(kindParam);
        var table = ContentService.TableFor(kind);

        var res = await FetchAsync("/version/" + Uri.EscapeDataString(versionId), "version:" + versionId, ct)
            .ConfigureAwait(false);
        var ver = res.Node as JsonObject
            ?? throw new LauncherException("Версия не найдена", versionId);

        var files = ver["files"] as JsonArray ?? new JsonArray();
        var primary = files.OfType<JsonObject>().FirstOrDefault(f => f.Bool("primary"))
                      ?? files.OfType<JsonObject>().FirstOrDefault()
                      ?? throw new LauncherException("Файл версии не найден", $"{versionId}: файлов нет");

        var warnings = new List<string>();
        if (files.Count > 1)
            warnings.Add($"У версии {files.Count} файла — установлен основной «{Txt(primary, "filename")}».");

        var mvs = Lst(ver, "game_versions");
        if (mvs.Count > 0 && !mvs.Contains(inst.McVersion, StringComparer.OrdinalIgnoreCase))
            warnings.Add($"Версия не заявляет поддержку Minecraft {inst.McVersion}.");

        var vld = Lst(ver, "loaders");
        if (vld.Count > 0 && !string.Equals(inst.Loader, "vanilla", StringComparison.OrdinalIgnoreCase)
            && !vld.Contains(inst.Loader, StringComparer.OrdinalIgnoreCase))
            warnings.Add($"Версия не заявлена для загрузчика {inst.Loader}.");

        var pid = Txt(ver, "project_id");

        // зависимости — перечисляем в предупреждениях, имена подбираем одним запросом
        if (ver["dependencies"] is JsonArray deps && deps.Count > 0)
        {
            var required = new List<string>();
            var optional = new List<string>();
            var incompatible = new List<string>();
            var depIds = new List<string>();
            foreach (var d in deps)
            {
                var depId = Txt(d, "project_id");
                if (!string.IsNullOrWhiteSpace(depId)) depIds.Add(depId!);
            }
            var briefs = await ProjectBriefsAsync(depIds, ct).ConfigureAwait(false);
            foreach (var d in deps)
            {
                var depId = Txt(d, "project_id") ?? Txt(d, "version_id") ?? "?";
                var title = briefs.TryGetValue(depId!, out var b) ? b.Title : depId!;
                switch ((Txt(d, "dependency_type") ?? "required").ToLowerInvariant())
                {
                    case "optional" or "alternative": optional.Add(title); break;
                    case "incompatible": incompatible.Add(title); break;
                    default: required.Add(title); break;
                }
            }
            if (required.Count > 0) warnings.Add("Требуются зависимости: " + string.Join(", ", required.Distinct()));
            if (optional.Count > 0) warnings.Add("Опциональные зависимости: " + string.Join(", ", optional.Distinct()));
            if (incompatible.Count > 0) warnings.Add("Несовместимо с: " + string.Join(", ", incompatible.Distinct()));
        }

        var filename = SanitizeFileName(Txt(primary, "filename"), Txt(primary, "url"))
            ?? throw new LauncherException("Не удалось определить имя файла", versionId);
        var url = Txt(primary, "url")
            ?? throw new LauncherException("Файл версии недоступен", filename);

        var sha1 = Txt(primary["hashes"], "sha1");
        var sha512 = Txt(primary["hashes"], "sha512");
        var hashAlgo = !string.IsNullOrWhiteSpace(sha1) ? "sha1" : !string.IsNullOrWhiteSpace(sha512) ? "sha512" : null;
        var hash = hashAlgo == "sha1" ? sha1 : hashAlgo is null ? null : sha512;

        // имя проекта и автор — для строки в БД (best effort, результат кэшируется)
        string? projectName = null, author = null;
        if (!string.IsNullOrWhiteSpace(pid))
        {
            try
            {
                var pr = await FetchAsync("/project/" + Uri.EscapeDataString(pid!), "project:" + pid!, ct)
                    .ConfigureAwait(false);
                if (pr.Node is JsonObject po)
                {
                    projectName = Txt(po, "title");
                    var team = Txt(po, "team");
                    if (team is not null) author = await TeamOwnerAsync(team, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (LauncherException ex) { Log.Debug($"Метаданные проекта {pid}: {ex.Message}"); }
        }

        Directory.CreateDirectory(inst.ContentDir(kind));
        Directory.CreateDirectory(_s.Paths.TempDir);
        var temp = Path.Combine(_s.Paths.TempDir, $"modrinth-{Guid.NewGuid():N}.tmp");
        try
        {
            await _s.Downloads.DownloadAsync(
                url, temp, $"{projectName ?? filename}: {filename}", kind: "content",
                hashAlgo: hashAlgo, hash: hash, instanceId: inst.Id, ct: ct).ConfigureAwait(false);

            var target = Path.Combine(inst.ContentDir(kind), filename);
            foreach (var old in new[] { target, target + ".disabled" })
                try { if (File.Exists(old)) File.Delete(old); }
                catch (Exception ex) { Log.Warn($"Не удалось удалить {Path.GetFileName(old)}: {ex.Message}"); }

            try { File.Move(temp, target); }
            catch (Exception ex) { throw new LauncherException("Не удалось сохранить файл", $"{filename}: {ex.Message}", ex); }

            var size = new FileInfo(target).Length;
            var id = Sha1File(target);
            // id — хеш файла; если он уже занят чужой строкой, добавляем id сборки, чтобы не ломать PRIMARY KEY
            var taken = Database.DatabaseService.Scalar<string>(
                $"SELECT filename FROM {table} WHERE id = @id AND NOT (instance_id = @iid AND filename = @fn)",
                new { id, iid = inst.Id, fn = filename });
            if (!string.IsNullOrWhiteSpace(taken)) id = id + "-" + inst.Id;

            var now = DateTime.UtcNow.ToString("o");
            try
            {
                Database.DatabaseService.Execute($"""
                    INSERT INTO {table} (id, instance_id, filename, name, version, author, size_bytes, enabled,
                                         project_id, version_id, source_url, source, mc_versions, loaders,
                                         updated_at, last_checked_at)
                    VALUES (@Id, @InstanceId, @Filename, @Name, @Version, @Author, @SizeBytes, 1,
                            @ProjectId, @VersionId, @SourceUrl, 'modrinth', @McVersions, @Loaders, @Now, @Now)
                    ON CONFLICT(instance_id, filename) DO UPDATE SET
                      name = CASE WHEN excluded.name IS NOT NULL AND excluded.name <> '' THEN excluded.name ELSE name END,
                      version = COALESCE(excluded.version, version),
                      author = COALESCE(excluded.author, author),
                      size_bytes = excluded.size_bytes,
                      enabled = 1,
                      project_id = COALESCE(excluded.project_id, project_id),
                      version_id = COALESCE(excluded.version_id, version_id),
                      source_url = COALESCE(excluded.source_url, source_url),
                      source = CASE WHEN excluded.project_id IS NOT NULL THEN excluded.source ELSE source END,
                      mc_versions = COALESCE(excluded.mc_versions, mc_versions),
                      loaders = COALESCE(excluded.loaders, loaders),
                      updated_at = excluded.updated_at,
                      last_checked_at = excluded.last_checked_at
                    """, new
                {
                    Id = id,
                    InstanceId = inst.Id,
                    Filename = filename,
                    Name = string.IsNullOrWhiteSpace(projectName) ? ContentService.DisplayName(filename) : projectName!,
                    Version = Txt(ver, "version_number") ?? Txt(ver, "name"),
                    Author = author,
                    SizeBytes = size,
                    ProjectId = pid,
                    VersionId = versionId,
                    SourceUrl = string.IsNullOrWhiteSpace(pid) ? null : $"https://modrinth.com/project/{pid}",
                    McVersions = ToJson(mvs),
                    Loaders = ToJson(vld),
                    Now = now,
                });
            }
            catch (Exception ex)
            {
                throw new LauncherException("Не удалось сохранить запись", $"{filename}: {ex.Message}", ex);
            }

            ContentService.EmitChanged(_s, inst.Id, kind);
            Log.Info($"Modrinth: {filename} установлен ({projectName ?? pid ?? "?"}, {kind}, {inst.Id})");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch { /* временный файл подчистится позже */ }
        }

        // обязательные зависимости ставим сами (вместе с их зависимостями)
        var depReport = new List<object>();
        if (installDependencies && ver["dependencies"] is JsonArray depsArr && depsArr.Count > 0)
        {
            try
            {
                depReport = await InstallDependenciesAsync(inst, depsArr, ct).ConfigureAwait(false);
                var installedDeps = depReport.Count(x => StatusOf(x) == "installed");
                if (installedDeps > 0) warnings.Add($"Установлено зависимостей: {installedDeps}.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log.Warn($"Зависимости {pid}: {ex.Message}"); }
        }

        return new { installed = new[] { new { filename, enabled = true } }, warnings, dependencies = depReport };
    }

    /// <summary>Статус из анонимного объекта отчёта о зависимостях.</summary>
    private static string StatusOf(object o)
        => o.GetType().GetProperty("status")?.GetValue(o) as string ?? "";

    /// <summary>Ставит обязательные зависимости версии (опциональные — только отмечает в отчёте).</summary>
    private async Task<List<object>> InstallDependenciesAsync(InstanceRecord inst, JsonArray deps, CancellationToken ct)
    {
        var report = new List<object>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await CollectDependenciesAsync(inst, deps, visited, report, 0, ct).ConfigureAwait(false);
        return report;
    }

    private async Task CollectDependenciesAsync(InstanceRecord inst, JsonArray deps, HashSet<string> visited,
        List<object> report, int depth, CancellationToken ct)
    {
        if (depth >= 4) return;

        foreach (var node in deps)
        {
            ct.ThrowIfCancellationRequested();
            if (node is not JsonObject d) continue;

            var type = (Txt(d, "dependency_type") ?? "required").ToLowerInvariant();
            if (type is "incompatible") continue;

            var depPid = Txt(d, "project_id");
            var depVid = Txt(d, "version_id");

            if (string.IsNullOrWhiteSpace(depPid) && !string.IsNullOrWhiteSpace(depVid))
            {
                // указан только version_id — узнаём проект по версии
                try
                {
                    var vres = await FetchAsync("/version/" + Uri.EscapeDataString(depVid!), "version:" + depVid, ct)
                        .ConfigureAwait(false);
                    depPid = Txt(vres.Node, "project_id");
                }
                catch (Exception ex) { Log.Debug($"Зависимость {depVid}: {ex.Message}"); }
            }
            if (string.IsNullOrWhiteSpace(depPid)) continue;
            if (!visited.Add(depPid!)) continue;

            var kind = await ProjectKindAsync(depPid!, ct).ConfigureAwait(false);
            var table = Content.ContentService.TableFor(kind);

            var name = depPid!;
            try
            {
                var briefs = await ProjectBriefsAsync(new List<string> { depPid! }, ct).ConfigureAwait(false);
                if (briefs.TryGetValue(depPid!, out var brief) && !string.IsNullOrWhiteSpace(brief.Title))
                    name = brief.Title;
            }
            catch (Exception ex) { Log.Debug($"Имя зависимости {depPid}: {ex.Message}"); }

            if (type is "optional" or "alternative")
            {
                report.Add(new { status = "optional", projectId = depPid, name, version = (string?)null, error = (string?)null });
                continue;
            }

            var already = false;
            try
            {
                already = Database.DatabaseService.Scalar<int>(
                    $"SELECT COUNT(*) FROM {table} WHERE instance_id = @iid AND project_id = @pid",
                    new { iid = inst.Id, pid = depPid }) > 0;
            }
            catch (Exception ex) { Log.Debug($"Проверка зависимости {depPid}: {ex.Message}"); }

            if (already)
            {
                report.Add(new { status = "already", projectId = depPid, name, version = (string?)null, error = (string?)null });
                continue;
            }

            try
            {
                Content.ContentService.MrVersion depVersion;
                if (!string.IsNullOrWhiteSpace(depVid))
                {
                    depVersion = await Content.ContentService.FetchVersionAsync(_s, depVid!, ct).ConfigureAwait(false);
                }
                else
                {
                    var list = await Content.ContentService.FetchVersionsAsync(_s, depPid!, ct).ConfigureAwait(false);
                    depVersion = Content.ContentService.SelectUpdateVersion(inst, list, releaseOnly: false);
                }

                await Content.ContentService.InstallVersionAsync(_s, inst, kind, table, null, depVersion, depPid,
                    enabled: true, ct: ct).ConfigureAwait(false);

                var shown = depVersion.VersionNumber ?? depVersion.Name ?? depVersion.Id;
                report.Add(new { status = "installed", projectId = depPid, name, version = shown, error = (string?)null });
                Log.Info($"Modrinth: зависимость «{name}» {shown} установлена ({kind}, {inst.Id})");

                // зависимости самой зависимости
                var vres2 = await FetchAsync("/version/" + Uri.EscapeDataString(depVersion.Id),
                    "version:" + depVersion.Id, ct).ConfigureAwait(false);
                if ((vres2.Node as JsonObject)?["dependencies"] is JsonArray nested && nested.Count > 0)
                    await CollectDependenciesAsync(inst, nested, visited, report, depth + 1, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"Зависимость {name}: {ex.Message}");
                report.Add(new { status = "error", projectId = depPid, name, version = (string?)null, error = ex.Message });
            }
        }
    }

    /// <summary>Тип контента проекта Modrinth → каталог сборки.</summary>
    private async Task<string> ProjectKindAsync(string projectId, CancellationToken ct)
    {
        try
        {
            var res = await FetchAsync("/project/" + Uri.EscapeDataString(projectId), "project:" + projectId, ct)
                .ConfigureAwait(false);
            return (Txt(res.Node, "project_type") ?? "").ToLowerInvariant() switch
            {
                "resourcepack" => "resourcepacks",
                "shader" => "shaderpacks",
                "datapack" => "datapacks",
                _ => "mods",
            };
        }
        catch (Exception ex)
        {
            Log.Debug($"Тип проекта {projectId}: {ex.Message}");
            return "mods";
        }
    }

    /* ---------------------------------------------------------- обновления */

    /// <summary>modrinth.checkUpdates → файлы, для которых на Modrinth есть более новая release-версия.</summary>
    public async Task<object> CheckUpdatesAsync(string instanceId, CancellationToken ct)
    {
        var inst = _s.Instances.Require(instanceId);
        var result = new List<object>();
        var marked = new List<string>();
        var versionsByProject = new Dictionary<string, List<JsonObject>?>(StringComparer.OrdinalIgnoreCase);

        foreach (var kind in ContentService.AllKinds)
        {
            ct.ThrowIfCancellationRequested();
            var table = ContentService.TableFor(kind);
            List<UpdateRow> rows;
            try
            {
                rows = Database.DatabaseService.Query<UpdateRow>($"""
                    SELECT id AS Id, filename AS Filename, name AS Name, version AS Version,
                           project_id AS ProjectId, version_id AS VersionId
                    FROM {table}
                    WHERE instance_id = @iid AND project_id IS NOT NULL AND project_id <> ''
                    """, new { iid = instanceId });
            }
            catch (Exception ex)
            {
                Log.Warn($"Не удалось прочитать {table}: {ex.Message}");
                continue;
            }

            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var pid = row.ProjectId ?? "";
                if (pid.Length == 0) continue;

                List<JsonObject>? versions;
                if (!versionsByProject.TryGetValue(pid, out versions))
                {
                    try { versions = await FetchVersionObjectsAsync(pid, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        // ошибка одного проекта не должна валять всю проверку
                        Log.Warn($"Обновления {pid}: {ex.Message}");
                        versions = null;
                    }
                    versionsByProject[pid] = versions;
                }
                if (versions is null || versions.Count == 0) continue;

                var best = SelectLatest(versions, inst);
                if (best is null) continue;

                var latestId = Txt(best, "id") ?? "";
                if (latestId.Length == 0 || string.Equals(latestId, row.VersionId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var latest = Txt(best, "version_number") ?? Txt(best, "name") ?? latestId;
                marked.Add(row.Id);
                result.Add(new
                {
                    modId = row.Id,
                    filename = row.Filename,
                    current = row.Version ?? "",
                    latest,
                    latestVersionId = latestId,
                    projectId = pid,
                    projectName = string.IsNullOrWhiteSpace(row.Name) ? Txt(best, "name") ?? pid : row.Name!,
                });
            }
        }
        // запоминаем, что реально можно обновить: плашка в списке контента ставится только по этим файлам
        ContentService.RememberUpdates(instanceId, marked);
        return result;
    }

    /* ---------------------------------------------------------- поиск по хешу */

    /// <summary>modrinth.projectByFile → сопоставление локальных sha1/sha512 с проектами Modrinth.</summary>
    public async Task<object> ProjectByFileAsync(JsonNode? p, CancellationToken ct)
    {
        var algorithm = string.Equals(p?.Str("algorithm"), "sha512", StringComparison.OrdinalIgnoreCase)
            ? "sha512" : "sha1";
        var hashes = p?.StrList("hashes") ?? new List<string>();
        if (hashes.Count == 0)
        {
            var single = p?.Str("hash");
            if (!string.IsNullOrWhiteSpace(single)) hashes.Add(single!);
        }

        var clean = hashes
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h!.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (clean.Count == 0)
            throw new LauncherException("Не переданы хеши файлов", "параметр 'hashes' пуст");
        if (clean.Count > 200)
            throw new LauncherException("Слишком много файлов",
                $"за раз можно проверить не более 200 хешей (получено {clean.Count})");

        var matches = new List<object>();
        var projectIds = new List<string>();
        var found = new List<(string Hash, JsonObject Version, JsonObject? File)>();

        foreach (var hash in clean)
        {
            ct.ThrowIfCancellationRequested();
            var res = await FetchAsync(
                $"/version_file/{Uri.EscapeDataString(hash)}?algorithm={algorithm}",
                $"file:{algorithm}:{hash}", ct).ConfigureAwait(false);
            if (res.NotFound || res.Node is not JsonObject v) continue;

            var file = v["files"] is JsonArray fa
                ? fa.OfType<JsonObject>().FirstOrDefault(f => HashOf(f, algorithm) == hash)
                  ?? fa.OfType<JsonObject>().FirstOrDefault(f => f.Bool("primary"))
                  ?? fa.OfType<JsonObject>().FirstOrDefault()
                : null;
            found.Add((hash, v, file));
            var pid = Txt(v, "project_id");
            if (!string.IsNullOrWhiteSpace(pid)) projectIds.Add(pid!);
        }

        var briefs = await ProjectBriefsAsync(projectIds, ct).ConfigureAwait(false);
        foreach (var (hash, v, file) in found)
        {
            var pid = Txt(v, "project_id") ?? "";
            briefs.TryGetValue(pid, out var brief);
            matches.Add(new
            {
                hash,
                versionId = Txt(v, "id") ?? "",
                projectId = pid,
                projectName = brief?.Title,
                slug = brief?.Slug,
                projectType = brief?.ProjectType,
                iconUrl = brief?.IconUrl,
                filename = file is null ? null : Txt(file, "filename"),
                url = file is null ? null : Txt(file, "url"),
                size = file?.Long("size", 0) ?? 0,
                primary = file?.Bool("primary") ?? false,
                sha1 = Txt(file?["hashes"], "sha1"),
                sha512 = Txt(file?["hashes"], "sha512"),
                versionNumber = Txt(v, "version_number"),
                name = Txt(v, "name"),
                versionType = Txt(v, "version_type") ?? "release",
                datePublished = Txt(v, "date_published"),
                gameVersions = Lst(v, "game_versions"),
                loaders = Lst(v, "loaders"),
                downloads = v.Long("downloads", 0),
            });
        }
        return matches;
    }

    /* ================================================== HTTP + кэш */

    private async Task<string> GetRawAsync(string path, CancellationToken ct)
    {
        var url = ApiBase + path;
        await GateAsync(ct).ConfigureAwait(false);
        var http = HttpFactory.Get(_s);   // общий клиент — не освобождаем
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            return await ReadAsync(resp, url, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // общий клиент мог быть освобождён другим модулем — используем собственный
            using var own = CreateClient();
            using var resp = await own.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            return await ReadAsync(resp, url, ct).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadAsync(HttpResponseMessage resp, string url, CancellationToken ct)
    {
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new NotFoundException(url);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} от {new Uri(url).Host}", null, resp.StatusCode);
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private HttpClient CreateClient()
    {
        var timeout = Math.Max(10, _s.Settings.Get("networkTimeoutSec", 60));
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Max(5, timeout / 3)),
        })
        { Timeout = TimeSpan.FromSeconds(timeout) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(HttpFactory.UserAgent);
        return client;
    }

    /// <summary>Пауза между запросами — держим темп ниже лимита 300 req/min.</summary>
    private static async Task GateAsync(CancellationToken ct)
    {
        while (true)
        {
            var last = Interlocked.Read(ref _lastRequestTicks);
            var now = Environment.TickCount64;
            var wait = MinIntervalMs - (now - last);
            if (wait <= 0)
            {
                if (Interlocked.CompareExchange(ref _lastRequestTicks, now, last) == last) return;
                continue;
            }
            await Task.Delay((int)wait, ct).ConfigureAwait(false);
        }
    }

    private void SetOffline(bool offline)
    {
        if (_offlineKnown && _offlineState == offline) return;
        _offlineKnown = true;
        _offlineState = offline;
        try { _s.Emit("modrinth.status", new { offline }); }
        catch (Exception ex) { Log.Debug($"modrinth.status: {ex.Message}"); }
    }

    /* ---------------------------------------------------------- кэш api_cache */

    private sealed class CacheEntry
    {
        public string Value { get; set; } = "";
        public string ExpiresAt { get; set; } = "";
    }

    private sealed class FetchResult
    {
        public JsonNode? Node { get; set; }
        public bool Offline { get; set; }
        public bool NotFound { get; set; }

        /// <summary>true — ответ пришёл из сети (можно переписать кэш нормализованным видом).</summary>
        public bool FromNetwork { get; set; }
    }

    private sealed class NotFoundException : Exception
    {
        public NotFoundException(string url) : base(url) { }
    }

    private int CacheMinutes => Math.Clamp(_s.Settings.Get("apiCacheMinutes", 30), 1, 24 * 60);

    private CacheEntry? ReadCache(string key)
    {
        try
        {
            return Database.DatabaseService.QueryFirstOrDefault<CacheEntry>(
                "SELECT value AS Value, expires_at AS ExpiresAt FROM api_cache WHERE key = @key",
                new { key = CachePrefix + key });
        }
        catch (Exception ex)
        {
            Log.Debug($"Кэш Modrinth ({key}) не прочитан: {ex.Message}");
            return null;
        }
    }

    private void WriteCache(string key, string value)
    {
        try
        {
            var expires = DateTime.UtcNow.AddMinutes(CacheMinutes).ToString("o");
            Database.DatabaseService.Execute(
                "INSERT INTO api_cache(key, value, expires_at) VALUES(@k, @v, @e) " +
                "ON CONFLICT(key) DO UPDATE SET value = @v, expires_at = @e",
                new { k = CachePrefix + key, v = value, e = expires });
        }
        catch (Exception ex) { Log.Debug($"Кэш Modrinth ({key}) не записан: {ex.Message}"); }
    }

    private static bool IsFresh(CacheEntry e)
        => DateTime.TryParse(e.ExpiresAt, null, DateTimeStyles.RoundtripKind, out var exp)
           && exp > DateTime.UtcNow;

    /// <summary>
    /// Основной GET: свежий кэш → сразу, иначе сеть; при сбое — устаревший кэш (offline)
    /// либо дружелюбная LauncherException.
    /// </summary>
    private async Task<FetchResult> FetchAsync(string path, string cacheKey, CancellationToken ct, bool wrapArray = false)
    {
        var entry = ReadCache(cacheKey);
        var cached = entry is null || string.IsNullOrEmpty(entry.Value) ? null : ParseSilent(entry.Value);

        if (cached is not null && entry is not null && IsFresh(entry))
        {
            if (IsNotFound(cached)) return new FetchResult { NotFound = true };
            return new FetchResult { Node = Unwrap(cached, wrapArray) };
        }

        try
        {
            var body = await GetRawAsync(path, ct).ConfigureAwait(false);
            var node = ParseOrThrow(body);   // сначала проверяем, что ответ валиден — в кэш кладём только его
            WriteCache(cacheKey, Store(body, wrapArray));
            SetOffline(false);
            return new FetchResult { Node = node, FromNetwork = true };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (NotFoundException)
        {
            WriteCache(cacheKey, NotFoundValue);
            return new FetchResult { NotFound = true };
        }
        catch (Exception ex)
        {
            if (cached is not null && _s.Settings.Get("modrinthOfflineCache", true))
            {
                SetOffline(true);
                if (IsNotFound(cached)) return new FetchResult { NotFound = true };
                Log.Debug($"Modrinth недоступен ({cacheKey}) — отдаю кэш: {ex.Message}");
                return new FetchResult { Node = Unwrap(cached, wrapArray), Offline = true };
            }
            throw new LauncherException("Modrinth недоступен", Describe(ex), ex);
        }
    }

    private async Task<JsonNode?> TryFetchAsync(string path, string cacheKey, CancellationToken ct)
    {
        try { return (await FetchAsync(path, cacheKey, ct).ConfigureAwait(false)).Node; }
        catch (OperationCanceledException) { throw; }
        catch (LauncherException ex)
        {
            Log.Warn($"Modrinth {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Массив ответа: в кэше лежит как {"items":[…]}, чтобы не мешать поиску по кэшу.</summary>
    private static string Store(string body, bool wrapArray)
        => wrapArray && body.Length > 0 && body[0] == '[' ? "{\"items\":" + body + "}" : body;

    private static JsonNode? Unwrap(JsonNode? node, bool wrapArray)
        => wrapArray && node is JsonObject o && o["items"] is JsonArray a ? a : node;

    private static JsonNode? ParseSilent(string json)
    {
        try { return JsonNode.Parse(json); }
        catch { return null; }
    }

    private static JsonNode ParseOrThrow(string json)
    {
        try { return JsonNode.Parse(json) ?? throw new FormatException("пустой ответ"); }
        catch (Exception ex) { throw new LauncherException("Некорректный ответ Modrinth", ex.Message, ex); }
    }

    private static bool IsNotFound(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) && s == "__not_found__";

    private static string Describe(Exception ex) => ex switch
    {
        LauncherException le => le.Detail ?? le.UserMessage,
        HttpRequestException he => "сетевая ошибка: " + he.Message,
        TaskCanceledException => "запрос превысил время ожидания",
        _ => ex.Message,
    };

    /* ================================================== справочники */

    private async Task<HashSet<string>> LoaderNamesAsync(CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var node = await FetchAsync("/tag/loader", "tag:loader", ct).ConfigureAwait(false);
            foreach (var el in AsArray(node.Node))
            {
                var n = Txt(el, "name");
                if (!string.IsNullOrWhiteSpace(n)) set.Add(n!);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (LauncherException ex) { Log.Debug($"Список загрузчиков недоступен: {ex.Message}"); }
        return set;
    }

    private async Task<string?> TeamOwnerAsync(string teamId, CancellationToken ct)
    {
        try
        {
            var res = await FetchAsync(
                $"/team/{Uri.EscapeDataString(teamId)}/members", "team:" + teamId, ct, wrapArray: true)
                .ConfigureAwait(false);
            if (res.Node is not JsonArray arr) return null;
            string? fallback = null;
            foreach (var el in arr)
            {
                var name = Txt(el, "name") ?? Txt(el, "username");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (string.Equals(Txt(el, "role"), "Owner", StringComparison.OrdinalIgnoreCase)) return name;
                fallback ??= name;
            }
            return fallback;
        }
        catch (OperationCanceledException) { throw; }
        catch (LauncherException ex)
        {
            Log.Debug($"Команда {teamId}: {ex.Message}");
            return null;
        }
    }

    private sealed class ProjectBrief
    {
        public string Title { get; set; } = "";
        public string? Slug { get; set; }
        public string? ProjectType { get; set; }
        public string? IconUrl { get; set; }
    }

    /// <summary>Короткие карточки проектов одним запросом GET /projects?ids=[…] (ответ кэшируется).</summary>
    private async Task<Dictionary<string, ProjectBrief>> ProjectBriefsAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        var map = new Dictionary<string, ProjectBrief>(StringComparer.OrdinalIgnoreCase);
        var unique = ids.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unique.Count == 0) return map;

        foreach (var chunk in unique.Chunk(50))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var key = "projects:" + ShortHash(string.Join(",", chunk));
                var res = await FetchAsync(
                    "/projects?ids=" + Uri.EscapeDataString(JsonSerializer.Serialize(chunk, IpcRouter.JsonOptions)),
                    key, ct).ConfigureAwait(false);
                if (res.Node is not JsonArray arr) continue;

                var lite = new List<object>();
                foreach (var el in arr)
                {
                    if (el is null) continue;
                    var pid = Txt(el, "id");
                    if (string.IsNullOrWhiteSpace(pid)) continue;
                    lite.Add(MapProjectLite(el));
                    map[pid!] = new ProjectBrief
                    {
                        Title = Txt(el, "title") ?? "",
                        Slug = Txt(el, "slug"),
                        ProjectType = Txt(el, "projectType") ?? Txt(el, "project_type"),
                        IconUrl = Txt(el, "iconUrl") ?? Txt(el, "icon_url"),
                    };
                }

                // полный ответ Modrinth тяжёлый (body, gallery) — в кэш кладём лёгкую карточку
                if (res.FromNetwork && lite.Count > 0)
                    WriteCache(key, JsonSerializer.Serialize(lite, IpcRouter.JsonOptions));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log.Debug($"Проекты Modrinth: {ex.Message}"); }
        }
        return map;
    }

    private async Task<List<JsonObject>> FetchVersionObjectsAsync(string projectId, CancellationToken ct)
    {
        var res = await FetchAsync(
            $"/project/{Uri.EscapeDataString(projectId)}/version", "pvers:" + projectId, ct, wrapArray: true)
            .ConfigureAwait(false);
        return AsArray(res.Node).OfType<JsonObject>().ToList();
    }

    /// <summary>Свежайшая release-версия, подходящая сборке (MC + загрузчик, для vanilla — любой).</summary>
    private static JsonObject? SelectLatest(List<JsonObject> versions, InstanceRecord inst)
    {
        foreach (var v in versions)
        {
            if (!string.Equals(Txt(v, "version_type") ?? "release", "release", StringComparison.OrdinalIgnoreCase))
                continue;
            var mvs = Lst(v, "game_versions");
            if (mvs.Count > 0 && !mvs.Contains(inst.McVersion, StringComparer.OrdinalIgnoreCase)) continue;
            var lds = Lst(v, "loaders");
            if (lds.Count > 0 && !string.Equals(inst.Loader, "vanilla", StringComparison.OrdinalIgnoreCase)
                && !lds.Contains(inst.Loader, StringComparer.OrdinalIgnoreCase)) continue;
            return v;
        }
        return null;
    }

    /* ================================================== маппинг проекта */

    /// <summary>Проект в форме, которую ждёт discover.html (camelCase после сериализации).</summary>
    private static object MapProject(JsonNode p, string? author, bool offline)
    {
        var license = p["license"];
        var links = new Dictionary<string, string>();
        void Link(string key, string? url) { if (!string.IsNullOrWhiteSpace(url)) links[key] = url!; }
        Link("source", Txt(p, "source_url"));
        Link("homepage", Txt(p, "homepage_url") ?? Txt(p, "website_url"));
        Link("wiki", Txt(p, "wiki_url"));
        Link("discord", Txt(p, "discord_url"));
        Link("support", Txt(p, "issues_url"));
        Link("donation", p["donation_urls"] is JsonArray da && da.Count > 0 ? Txt(da[0], "url") : null);

        var gallery = new List<object>();
        if (p["gallery"] is JsonArray ga)
            foreach (var g in ga)
            {
                if (g is null) continue;
                var url = g is JsonValue ? g.ToString().Trim('"') : Txt(g, "url");
                if (string.IsNullOrWhiteSpace(url)) continue;
                gallery.Add(new
                {
                    url = url!,
                    title = g is JsonValue ? null : Txt(g, "title"),
                    description = g is JsonValue ? null : Txt(g, "description"),
                    featured = g is JsonValue ? false : g.Bool("featured"),
                });
            }

        return new
        {
            id = Txt(p, "id") ?? "",
            slug = Txt(p, "slug"),
            title = Txt(p, "title") ?? "",
            description = Txt(p, "description") ?? "",
            body = Txt(p, "body") ?? "",
            projectType = Txt(p, "project_type") ?? "mod",
            iconUrl = Txt(p, "icon_url"),
            author,
            downloads = p.Long("downloads", 0),
            follows = p.Long("followers", 0),
            datePublished = Txt(p, "published"),
            dateModified = Txt(p, "updated"),
            categories = Lst(p, "categories"),
            additionalCategories = Lst(p, "additional_categories"),
            loaders = Lst(p, "loaders"),
            gameVersions = Lst(p, "game_versions"),
            license = license is null ? null : new { name = Txt(license, "name") ?? Txt(license, "id"), url = Txt(license, "url") },
            links,
            gallery,
            status = Txt(p, "status"),
            clientSide = Txt(p, "client_side"),
            serverSide = Txt(p, "server_side"),
            offline,
        };
    }

    /// <summary>
    /// Лёгкая карточка проекта для кэша GET /projects?ids: её же читает search.global
    /// (нужны projectId / projectType / downloads), поэтому ключи — camelCase.
    /// </summary>
    private static object MapProjectLite(JsonNode p) => new
    {
        id = Txt(p, "id") ?? "",
        slug = Txt(p, "slug"),
        title = Txt(p, "title") ?? "",
        description = Txt(p, "description") ?? "",
        projectType = Txt(p, "projectType") ?? Txt(p, "project_type") ?? "mod",
        iconUrl = Txt(p, "iconUrl") ?? Txt(p, "icon_url"),
        author = Txt(p, "author"),
        downloads = p.Long("downloads", 0),
        follows = p.Long("follows", 0),
        datePublished = Txt(p, "datePublished") ?? Txt(p, "published"),
        dateModified = Txt(p, "dateModified") ?? Txt(p, "updated"),
    };

    /* ================================================== мелочи */

    private sealed class UpdateRow
    {
        public string Id { get; set; } = "";
        public string Filename { get; set; } = "";
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? ProjectId { get; set; }
        public string? VersionId { get; set; }
    }

    private static List<JsonNode?> AsArray(JsonNode? node)
    {
        var list = new List<JsonNode?>();
        if (node is JsonArray arr) foreach (var el in arr) list.Add(el);
        return list;
    }

    /// <summary>Строка из JSON без «null» и без объектов/массивов.</summary>
    private static string? Txt(JsonNode? n, string key)
    {
        var v = n?[key];
        if (v is null || v.GetValueKind() == JsonValueKind.Null) return null;
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return string.IsNullOrWhiteSpace(s) ? null : s;
            if (jv.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
        }
        if (v is JsonArray or JsonObject) return null;
        var raw = v.ToJsonString().Trim('"');
        return string.IsNullOrWhiteSpace(raw) || raw == "null" ? null : raw;
    }

    private static List<string> Lst(JsonNode? n, string key)
    {
        var list = new List<string>();
        if (n?[key] is JsonArray arr)
            foreach (var el in arr)
            {
                if (el is null || el.GetValueKind() == JsonValueKind.Null) continue;
                var s = el.ToString().Trim('"');
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        return list;
    }

    private static string? ToJson(List<string> values)
        => values.Count > 0 ? JsonSerializer.Serialize(values, IpcRouter.JsonOptions) : null;

    private static string NormalizeIndex(string? s)
    {
        var v = (s ?? "").Trim().ToLowerInvariant();
        return v is "relevance" or "downloads" or "follows" or "newest" or "updated" ? v : "relevance";
    }

    /// <summary>Произвольные фасеты из UI: [[..]] нового и старого вида, либо объект {поле:значение}.</summary>
    private static List<List<string>> ParseRawFacets(JsonNode? node)
    {
        var groups = new List<List<string>>();
        if (node is JsonObject o)
        {
            foreach (var (key, value) in o)
            {
                var list = value is JsonArray ? Lst(o, key) : new List<string> { Txt(o, key) ?? "" };
                var g = list.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => key + "=" + v).ToList();
                if (g.Count > 0) groups.Add(g);
            }
            return groups;
        }
        if (node is not JsonArray arr) return groups;

        foreach (var el in arr)
        {
            if (el is JsonValue ev && ev.TryGetValue<string>(out var expr))
            {
                if (expr.Contains('=') && !string.IsNullOrWhiteSpace(expr)) groups.Add(new List<string> { expr });
                continue;
            }
            if (el is JsonObject eo)
            {
                foreach (var (key, value) in eo)
                {
                    var list = value is JsonArray ? Lst(eo, key) : new List<string> { Txt(eo, key) ?? "" };
                    var g = list.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => key + "=" + v).ToList();
                    if (g.Count > 0) groups.Add(g);
                }
                continue;
            }
            if (el is not JsonArray inner) continue;
            var items = inner.Select(x => x?.ToString().Trim('"'))
                .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
            if (items.Count == 0) continue;
            if (items.All(s => s.Contains('='))) { groups.Add(items); continue; }
            // старый вид ["categories","fabric"]
            if (items.Count >= 2 && !items[0].Contains('=') && !items[1].Contains('='))
            {
                groups.Add(new List<string> { items[0] + "=" + items[1] });
                var rest = items.Skip(2).Where(s => s.Contains('=')).ToList();
                if (rest.Count > 0) groups.Add(rest);
            }
        }
        return groups;
    }

    private static string? SanitizeFileName(string? filename, string? url)
    {
        var name = string.IsNullOrWhiteSpace(filename) ? "" : Path.GetFileName(filename!.Trim());
        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
        {
            try { name = Path.GetFileName(new Uri(url!).LocalPath); }
            catch { /* некорректный URL */ }
        }
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private static string HashOf(JsonObject file, string algorithm)
    {
        var algo = algorithm == "sha512" ? "sha512" : "sha1";
        return Txt(file["hashes"], algo) ?? "";
    }

    private static string Sha1File(string path)
    {
        using var sha = SHA1.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static string ShortHash(string s)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant()[..16];
}
