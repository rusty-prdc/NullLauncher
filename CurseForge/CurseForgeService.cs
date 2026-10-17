using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NullLauncher.CurseForge;

/// <summary>
/// Клиент CurseForge API v1 для каталога модпаков (игрок = Minecraft, gameId 432,
/// класс «Модпак» = 4471). Заменяет Modrinth только в разделе модпаков: поиск, карточка
/// проекта и список версий отдаются в той же форме, что и ModrinthService, поэтому
/// discover.html не различает источники.
/// Особенности CurseForge:
///   * ключ обязателен (шапка x-api-key) — без ключа сервер отдаёт 403;
///   * downloadUrl у файлов часто null — ссылка собирается вручную по edge.forgecdn.net;
///   * сортировка — sortField + sortOrder (asc/desc, «descending» невалиден);
///   * список файлов проекта — максимум 50 на страницу, поэтому листаем до 150.
/// Ответы кэшируются в api_cache с префиксом «cf:», свежий кэш отдаётся сразу,
/// при недоступности сети — устаревший (offline=true).
/// </summary>
internal sealed class CurseForgeService
{
    private const string ApiBase = "https://api.curseforge.com/v1";
    private const string CachePrefix = "cf:";
    private const string NotFoundValue = "\"__not_found__\"";
    private const long GameId = 432;          // Minecraft: Java Edition
    private const long ModpackClassId = 4471; // раздел «Модпаки»

    /// <summary>Ключ по умолчанию; можно переопределить в настройках (curseForgeApiKey).</summary>
    private const string DefaultApiKey = "$2a$10$rN6w5ChjsVM/N2rZDEr0oOeyiVl5x/9mijROM.0o2xt7Pujc5C.ia";

    public const string IdPrefix = "cf:";

    private const int FilesPageSize = 50;   // предел CurseForge на страницу
    private const int MaxFilePages = 3;     // 150 файлов — достаточно для выбора версии
    private const long MinIntervalMs = 200; // консервативный темп: 300 запросов/мин в запасе

    private static long _lastRequestTicks;

    private readonly AppServices _s;

    public CurseForgeService(AppServices s) => _s = s;

    private string ApiKey => _s.Settings.Get("curseForgeApiKey", DefaultApiKey);

    /* ---------------------------------------------------------- поиск */

    /// <summary>Каталог модпаков: { hits:[…], total, offline } — та же форма, что у modrinth.search.</summary>
    public async Task<object> SearchAsync(JsonNode? p, CancellationToken ct)
    {
        var query = (p?.Str("query") ?? "").Trim();
        var loaders = p?.StrList("loaders") ?? new List<string>();
        var gameVersions = p?.StrList("gameVersions") ?? new List<string>();

        var limitParam = p?.Int("limit", 0) ?? 0;
        var limit = limitParam > 0
            ? Math.Clamp(limitParam, 1, 100)
            : Math.Clamp(_s.Settings.Get("modrinthPageSize", 20), 1, 100);
        var offset = Math.Max(0, p?.Int("offset", 0) ?? 0);
        var (sortField, sortOrder) = MapSort(p?.Str("sort"));

        var qs = new List<string>
        {
            "gameId=" + GameId,
            "classId=" + ModpackClassId,
            "pageSize=" + limit,
            "index=" + offset,
            "sortField=" + sortField,
            "sortOrder=" + sortOrder,
        };
        if (query.Length > 0) qs.Add("searchFilter=" + Uri.EscapeDataString(query));

        // фильтр по загрузчику: CurseForge принимает ровно один тип
        var loaderType = MapModLoaderType(loaders.FirstOrDefault());
        if (loaderType > 0) qs.Add("modLoaderType=" + loaderType);

        // версия Minecraft: тоже одна за раз (как и у Modrinth UI передаёт список, берём первую)
        if (gameVersions.Count > 0) qs.Add("gameVersion=" + Uri.EscapeDataString(gameVersions[0]));
        // категории CurseForge не пересекаются с тегами Modrinth — фильтр по ним не передаём

        var path = "/mods/search?" + string.Join("&", qs);
        var cacheKey = "search:" + query + "_" + ShortHash(string.Join("|", qs));

        var res = await FetchAsync(path, cacheKey, ct).ConfigureAwait(false);
        var root = res.Node as JsonObject
            ?? throw new LauncherException("Некорректный ответ CurseForge", "ожидался объект поиска");

        var hits = new List<object>();
        foreach (var mod in Items(root["data"]))
            hits.Add(MapHit(mod));

        var total = (int)((root["pagination"]?["totalCount"] as JsonValue)?.GetValue<long>() ?? hits.Count);

        // нормализованный вид кладём в отдельный ключ (его читает search.global);
        // основной ключ кэша остаётся с сырым ответом API — иначе следующий запрос
        // прочитает нормализованный вид как сырой и не найдёт в нём «data»
        if (res.FromNetwork)
            WriteCache("gsearch:" + cacheKey[7..], JsonSerializer.Serialize(
                new { hits, total, offline = false }, IpcRouter.JsonOptions));

        return new { hits, total, offline = res.Offline };
    }

    /* ---------------------------------------------------------- проект */

    /// <summary>Карточка проекта в форме MapProject (discover.html не различает источники).</summary>
    public async Task<object> ProjectAsync(string rawId, CancellationToken ct)
    {
        var id = NumericId(rawId);
        var res = await FetchAsync("/mods/" + id, "project:" + id, ct).ConfigureAwait(false);
        var p = (res.Node as JsonObject)?["data"] as JsonObject
            ?? throw new LauncherException("Проект не найден", rawId);

        var links = new Dictionary<string, string>();
        void Link(string key, string? url) { if (!string.IsNullOrWhiteSpace(url)) links[key] = url!; }
        Link("homepage", Txt(p["links"], "websiteUrl"));
        Link("wiki", Txt(p["links"], "wikiUrl"));
        Link("source", Txt(p["links"], "sourceUrl"));
        Link("support", Txt(p["links"], "issuesUrl"));

        var gallery = new List<object>();
        if (p["screenshots"] is JsonArray sa)
            foreach (var g in sa)
            {
                var url = Txt(g, "url") ?? Txt(g, "thumbnailUrl");
                if (string.IsNullOrWhiteSpace(url)) continue;
                gallery.Add(new { url = url!, title = Txt(g, "title"), description = Txt(g, "description"), featured = false });
            }

        var (loaders, mcVersions) = ProjectLoadersAndVersions(p);

        return new
        {
            id = IdPrefix + id,
            slug = Txt(p, "slug"),
            title = Txt(p, "name") ?? "",
            description = Txt(p, "summary") ?? "",
            body = "",   // CurseForge не отдаёт длинное описание в API —.summary показывается в шапке
            projectType = "modpack",
            iconUrl = Txt(p["logo"], "url") ?? Txt(p["logo"], "thumbnailUrl"),
            author = AuthorOf(p),
            downloads = p.Long("downloadCount", 0),
            follows = p.Long("thumbsUpCount", 0),
            datePublished = Txt(p, "dateCreated"),
            dateModified = Txt(p, "dateModified"),
            categories = Names(p["categories"]),
            additionalCategories = new List<string>(),
            loaders,
            gameVersions = mcVersions,
            license = (object?)null,
            links,
            gallery,
            status = "listed",
            clientSide = (string?)null,
            serverSide = (string?)null,
            offline = res.Offline,
        };
    }

    /* ---------------------------------------------------------- версии */

    /// <summary>
    /// Список версий (файлов пака) в форме modrinth.versions. Сервер-паки пропускаются —
    /// установить их как клиентский модпак нельзя.
    /// </summary>
    public async Task<object> VersionsAsync(string rawId, List<string> loaders, List<string> gameVersions, CancellationToken ct)
    {
        var id = NumericId(rawId);
        var qs = new List<string> { "pageSize=" + FilesPageSize, "sort=2" }; // 2 — по дате, новые сверху
        var loaderType = MapModLoaderType(loaders.FirstOrDefault());
        if (loaderType > 0) qs.Add("modLoaderType=" + loaderType);
        if (gameVersions.Count > 0) qs.Add("gameVersion=" + Uri.EscapeDataString(gameVersions[0]));

        var list = new List<object>();
        for (var page = 0; page < MaxFilePages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var path = $"/mods/{id}/files?index={page * FilesPageSize}&" + string.Join("&", qs);
            var res = await FetchAsync(path, $"files:{id}:{ShortHash(string.Join("|", qs, page))}", ct)
                .ConfigureAwait(false);
            var files = Items((res.Node as JsonObject)?["data"]);
            if (files.Count == 0) break;

            foreach (var f in files)
            {
                if (f.Bool("isAvailable", true) == false) continue;
                if (f.Bool("isServerPack")) continue;   // серверные паки — не модпак для игры
                list.Add(MapVersion(id, f));
            }
            if (files.Count < FilesPageSize) break;    // страница неполная — больше файлов нет
        }
        return list;
    }

    /* ---------------------------------------------------------- маппинг */

    private object MapHit(JsonObject mod)
    {
        var (loaders, mcVersions) = LatestFileLoadersAndVersions(mod);
        return new
        {
            projectId = IdPrefix + mod.Long("id", 0),
            slug = Txt(mod, "slug"),
            title = Txt(mod, "name") ?? "",
            description = Txt(mod, "summary") ?? "",
            author = AuthorOf(mod),
            iconUrl = Txt(mod["logo"], "thumbnailUrl") ?? Txt(mod["logo"], "url"),
            downloads = mod.Long("downloadCount", 0),
            follows = mod.Long("thumbsUpCount", 0),
            categories = Names(mod["categories"]),
            loaders,
            versions = mcVersions,
            projectType = "modpack",
            dateModified = Txt(mod, "dateModified"),
        };
    }

    private static object MapVersion(long modId, JsonObject f)
    {
        var fileId = f.Long("id", 0);
        var fileName = Txt(f, "fileName") ?? Txt(f, "displayName") ?? fileId.ToString();

        // downloadUrl почти всегда null — ссылка собирается по id файла (edge.forgecdn)
        var url = Txt(f, "downloadUrl");
        if (string.IsNullOrWhiteSpace(url))
            url = $"https://edge.forgecdn.net/files/{fileId / 1000}/{fileId % 1000}/{Uri.EscapeDataString(fileName)}";

        var mc = new List<string>();
        var lds = new List<string>();
        if (f["sortableGameVersions"] is JsonArray sgv)
            foreach (var e in sgv)
            {
                // у версии игры gameVersion заполнен, у загрузчика — пустой, а имя в gameVersionName
                var ver = Txt(e, "gameVersion");
                if (!string.IsNullOrWhiteSpace(ver)) { mc.Add(ver!); continue; }
                var name = Txt(e, "gameVersionName");
                if (!string.IsNullOrWhiteSpace(name)) lds.Add(name!.ToLowerInvariant());
            }
        if (mc.Count == 0) mc.AddRange(Lst(f, "gameVersions").Where(LooksLikeMcVersion));
        if (lds.Count == 0) lds.AddRange(Lst(f, "gameVersions").Where(LooksLikeLoader).Select(l => l.ToLowerInvariant()));

        var vt = ReleaseType(f);
        return new
        {
            id = fileId.ToString(),
            projectId = modId.ToString(),
            name = Txt(f, "displayName") ?? fileName,
            versionNumber = (string?)null,
            datePublished = Txt(f, "fileDate"),
            gameVersions = mc,
            loaders = lds,
            files = new[]
            {
                new { filename = fileName, url = url!, primary = true, size = f.Long("fileLength", 0), hashes = (JsonNode?)null },
            },
            downloads = f.Long("downloadCount", 0),
            versionType = vt,
            channels = new[] { vt },
            status = "listed",
            featured = vt == "release",
        };
    }

    /// <summary>Загрузчики и версии MC, собранные по всем клиентским файлам проекта.</summary>
    private static (List<string> loaders, List<string> mcVersions) ProjectLoadersAndVersions(JsonObject p)
    {
        var loaders = new List<string>();
        var mcs = new List<string>();
        foreach (var f in ClientFiles(p["latestFiles"]))
        {
            foreach (var l in Lst(f, "gameVersions").Where(LooksLikeLoader))
            {
                var low = l.ToLowerInvariant();
                if (!loaders.Contains(low)) loaders.Add(low);
            }
            foreach (var v in Lst(f, "gameVersions").Where(LooksLikeMcVersion))
            {
                if (!mcs.Contains(v)) mcs.Add(v);
            }
        }
        return (loaders, mcs);
    }

    /// <summary>Загрузчик/версии из последнего клиентского файла проекта — для карточки поиска.</summary>
    private static (List<string> loaders, List<string> mcVersions) LatestFileLoadersAndVersions(JsonObject mod)
    {
        var mainId = mod.Long("mainFileId", 0);
        var files = ClientFiles(mod["latestFiles"]).ToList();
        var file = files.FirstOrDefault(f => f.Long("id", 0) == mainId) ?? files.FirstOrDefault();
        if (file is null) return (new List<string>(), new List<string>());
        var loaders = Lst(file, "gameVersions").Where(LooksLikeLoader)
            .Select(l => l.ToLowerInvariant()).Distinct().ToList();
        var mcs = Lst(file, "gameVersions").Where(LooksLikeMcVersion).Distinct().ToList();
        return (loaders, mcs);
    }

    private static IEnumerable<JsonObject> ClientFiles(JsonNode? node)
        => Items(node).Where(f => !f.Bool("isServerPack"));

    private static string? AuthorOf(JsonObject p)
        => p["authors"] is JsonArray a && a.Count > 0 ? Txt(a[0], "name") : null;

    private static List<string> Names(JsonNode? node)
    {
        var list = new List<string>();
        foreach (var e in Items(node))
        {
            var n = Txt(e, "name");
            if (!string.IsNullOrWhiteSpace(n)) list.Add(n!);
        }
        return list;
    }

    private static string ReleaseType(JsonObject f) => f.Int("releaseType", 1) switch
    {
        2 => "beta",
        3 => "alpha",
        _ => "release",
    };

    private static bool LooksLikeMcVersion(string s)
        => s.Length >= 3 && char.IsDigit(s[0]) && s[1] == '.' && s.Contains('.');

    private static readonly string[] LoaderNames =
        { "forge", "fabric", "quilt", "neoforge", "liteloader", "rift", "bukkit", "paper", "purpur", "velocity", "bungeecord", "waterfall", "folia", "sponge" };

    private static bool LooksLikeLoader(string s)
        => LoaderNames.Contains(s.ToLowerInvariant());

    /// <summary>Загрузчик UI → числовой modLoaderType CurseForge (0 — любой).</summary>
    private static int MapModLoaderType(string? loader) => (loader ?? "").Trim().ToLowerInvariant() switch
    {
        "forge" => 1,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => 0,
    };

    /// <summary>Сортировка UI → sortField CurseForge. CurseForge не имеет «релевантности» — ближайшее «популярность».</summary>
    private static (int field, string order) MapSort(string? sort) => (sort ?? "").Trim().ToLowerInvariant() switch
    {
        "downloads" => (6, "desc"),      // TotalDownloads
        "newest" or "updated" => (3, "desc"), // LastUpdated
        _ => (2, "desc"),                // Popularity
    };

    /// <summary>"cf:953274" → 953274 (принимает и «голый» id).</summary>
    private static long NumericId(string rawId)
    {
        var s = rawId.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase) ? rawId[3..] : rawId;
        if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new LauncherException("Некорректный проект CurseForge", rawId);
        return id;
    }

    /* ================================================== HTTP + кэш */

    private async Task<string> GetRawAsync(string path, CancellationToken ct)
    {
        var url = ApiBase + path;
        await GateAsync(ct).ConfigureAwait(false);
        var http = HttpFactory.Get(_s);   // общий клиент — не освобождаем
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("x-api-key", ApiKey);
        try
        {
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            return await ReadAsync(resp, url, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            using var own = CreateClient();
            using var resp = await own.SendAsync(req, ct).ConfigureAwait(false);
            return await ReadAsync(resp, url, ct).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadAsync(HttpResponseMessage resp, string url, CancellationToken ct)
    {
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new NotFoundException(url);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new LauncherException("CurseForge отклонил ключ API",
                $"HTTP {(int)resp.StatusCode}: проверьте настройку «Ключ CurseForge API»");
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

    /// <summary>Пауза между запросами — держим темп ниже лимита.</summary>
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

    private int CacheMinutes => Math.Clamp(_s.Settings.Get("apiCacheMinutes", 30), 1, 24 * 60);

    private sealed class CacheEntry
    {
        public string Value { get; set; } = "";
        public string ExpiresAt { get; set; } = "";
    }

    private sealed class FetchResult
    {
        public JsonNode? Node { get; set; }
        public bool Offline { get; set; }
        public bool FromNetwork { get; set; }
    }

    private sealed class NotFoundException : Exception
    {
        public NotFoundException(string url) : base(url) { }
    }

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
            Log.Debug($"Кэш CurseForge ({key}) не прочитан: {ex.Message}");
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
        catch (Exception ex) { Log.Debug($"Кэш CurseForge ({key}) не записан: {ex.Message}"); }
    }

    /// <summary>GET: свежий кэш → сразу, иначе сеть; при сбое — устаревший кэш либо LauncherException.</summary>
    private async Task<FetchResult> FetchAsync(string path, string cacheKey, CancellationToken ct)
    {
        var entry = ReadCache(cacheKey);
        var cached = entry is null || string.IsNullOrEmpty(entry.Value) ? null : ParseSilent(entry.Value);

        if (cached is not null && entry is not null
            && DateTime.TryParse(entry.ExpiresAt, null, DateTimeStyles.RoundtripKind, out var exp)
            && exp > DateTime.UtcNow)
        {
            if (IsNotFound(cached))
                throw new LauncherException("CurseForge не нашёл запрошенные данные", path);
            return new FetchResult { Node = cached };
        }

        try
        {
            var body = await GetRawAsync(path, ct).ConfigureAwait(false);
            JsonNode node;
            try { node = JsonNode.Parse(body) ?? throw new FormatException("пустой ответ"); }
            catch (Exception ex) { throw new LauncherException("Некорректный ответ CurseForge", ex.Message, ex); }
            WriteCache(cacheKey, body);
            return new FetchResult { Node = node, FromNetwork = true };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (NotFoundException)
        {
            WriteCache(cacheKey, NotFoundValue);
            throw new LauncherException("CurseForge не нашёл запрошенные данные", path);
        }
        catch (LauncherException) { throw; }
        catch (Exception ex)
        {
            if (cached is not null && !IsNotFound(cached)
                && _s.Settings.Get("modrinthOfflineCache", true))
            {
                Log.Debug($"CurseForge недоступен ({cacheKey}) — отдаю кэш: {ex.Message}");
                return new FetchResult { Node = cached, Offline = true };
            }
            throw new LauncherException("CurseForge недоступен", Describe(ex), ex);
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException he => "сетевая ошибка: " + he.Message,
        TaskCanceledException => "запрос превысил время ожидания",
        _ => ex.Message,
    };

    /* ================================================== мелочи */

    private static List<JsonObject> Items(JsonNode? node)
    {
        var list = new List<JsonObject>();
        if (node is JsonArray arr)
            foreach (var el in arr)
                if (el is JsonObject o) list.Add(o);
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

    private static JsonNode? ParseSilent(string json)
    {
        try { return JsonNode.Parse(json); }
        catch { return null; }
    }

    private static bool IsNotFound(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) && s == "__not_found__";

    private static string ShortHash(string s)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes.AsSpan(0, 6)).ToLowerInvariant();
    }
}
