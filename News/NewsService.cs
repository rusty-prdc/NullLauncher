using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using NullLauncher.Core;
using NullLauncher.Database;

namespace NullLauncher.News;

/// <summary>
/// Новости для главной. Только реальные ленты:
///  1) официальный фид новостей Minecraft-лаунчера (launchercontent.mojang.com/news.json);
///  2) блог Modrinth (blog.modrinth.com/rss.xml).
/// Результат кэшируется в api_cache (30 минут), при недоступности сети отдаётся устаревший кэш.
/// </summary>
public static class NewsService
{
    private const string MojangUrl = "https://launchercontent.mojang.com/news.json";
    private const string ModrinthUrl = "https://blog.modrinth.com/rss.xml";
    private const string MojangImageBase = "https://launchercontent.mojang.com";
    private const string CacheKey = "news:list";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>Возвращает объединённый список новостей (по умолчанию 8 штук, новые сверху).</summary>
    public static async Task<object> ListAsync(AppServices s, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit <= 0 ? 8 : limit, 1, 30);

        var cached = ReadCache();
        if (cached is { Fresh: true })
            return new { items = Trim(cached.Items, limit), fromCache = true };

        var items = new List<NewsItem>();
        Exception? last = null;
        foreach (var src in new[] { FetchMojangAsync, FetchModrinthAsync })
        {
            try { items.AddRange(await src(s, ct).ConfigureAwait(false)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                Log.Debug($"news: {ex.Message}");
            }
        }

        if (items.Count == 0)
        {
            // сеть не ответила вообще — отдаём устаревший кэш, если он есть
            if (cached is { Items.Count: > 0 })
                return new { items = Trim(cached.Items, limit), fromCache = true };
            throw new LauncherException("Новости недоступны",
                "Не удалось загрузить ленту новостей Mojang и Modrinth. Проверьте подключение к интернету." +
                (last is null ? "" : "\n" + last.Message));
        }

        var merged = items
            .GroupBy(i => i.Id)
            .Select(g => g.First())
            .OrderByDescending(i => i.Date)
            .ToList();
        WriteCache(merged);
        return new { items = Trim(merged.Select(ToDto), limit), fromCache = false };
    }

    private static IEnumerable<object> Trim(IEnumerable<object> items, int limit) => items.Take(limit);

    private static object ToDto(NewsItem i) => new
    {
        id = i.Id,
        source = i.Source,
        title = i.Title,
        text = i.Text,
        date = i.Date.ToString("o"),
        image = i.Image,
        url = i.Url,
        category = i.Category,
    };

    /* ---------------------------------------------------------- источники */

    private static async Task<List<NewsItem>> FetchMojangAsync(AppServices s, CancellationToken ct)
    {
        using var http = HttpFactory.Get(s);
        using var resp = await http.GetAsync(MojangUrl, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var doc = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))!;
        var outp = new List<NewsItem>();
        foreach (var e in doc["entries"]?.AsArray() ?? new JsonArray())
        {
            if (e is null) continue;
            var title = e["title"]?.GetValue<string>();
            var dateStr = e["date"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(title) || !DateTime.TryParse(dateStr, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var date)) continue;

            var image = e["newsPageImage"]?["url"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(image))
                image = image.StartsWith("http") ? image : MojangImageBase + image;

            outp.Add(new NewsItem(
                Id: "mojang:" + (e["id"]?.GetValue<string>() ?? date.ToString("yyyyMMdd") + ":" + title),
                Source: "mojang",
                Title: title.Trim(),
                Text: (e["text"]?.GetValue<string>() ?? "").Trim(),
                Date: date,
                Image: string.IsNullOrWhiteSpace(image) ? null : image,
                Url: e["readMoreLink"]?.GetValue<string>(),
                Category: e["category"]?.GetValue<string>() ?? "Minecraft"));
        }
        return outp;
    }

    private static async Task<List<NewsItem>> FetchModrinthAsync(AppServices s, CancellationToken ct)
    {
        using var http = HttpFactory.Get(s);
        using var resp = await http.GetAsync(ModrinthUrl, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var doc = XDocument.Parse(xml);
        var outp = new List<NewsItem>();
        foreach (var item in doc.Descendants("item"))
        {
            var title = (item.Element("title")?.Value ?? "").Trim();
            var link = (item.Element("link")?.Value ?? "").Trim();
            if (title.Length == 0) continue;

            var dateStr = item.Element("pubDate")?.Value;
            if (!DateTimeOffset.TryParse(dateStr, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var dto)) continue;

            var desc = CleanHtml(item.Element("description")?.Value ?? "");
            outp.Add(new NewsItem(
                Id: "modrinth:" + (link.Length > 0 ? link : title),
                Source: "modrinth",
                Title: title,
                Text: desc,
                Date: dto.UtcDateTime,
                Image: null,                      // у блога нет стабильных картинок в RSS
                Url: link.Length > 0 ? link : null,
                Category: "Modrinth"));
        }
        return outp;
    }

    private static string CleanHtml(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var noTags = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", " ");
        return System.Net.WebUtility.HtmlDecode(noTags)
            .Replace(']', ' ').Trim();           // хвост CDATA-объединений
    }

    /* ---------------------------------------------------------- кэш */

    private sealed record NewsItem(string Id, string Source, string Title, string Text,
        DateTime Date, string? Image, string? Url, string Category);

    private sealed record CacheData(List<NewsItem> Items, bool Fresh);

    private sealed class CacheRow { public string value { get; set; } = ""; public string expires_at { get; set; } = ""; }

    private static CacheData? ReadCache()
    {
        try
        {
            var row = DatabaseService.QueryFirstOrDefault<CacheRow>(
                "SELECT value, expires_at FROM api_cache WHERE key = @k", new { k = CacheKey });
            if (row?.value is null) return null;

            var items = new List<NewsItem>();
            foreach (var n in JsonNode.Parse(row.value)!.AsArray())
            {
                if (n is null) continue;
                if (!DateTime.TryParse(n["date"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var date)) continue;
                items.Add(new NewsItem(
                    Id: n["id"]?.GetValue<string>() ?? "",
                    Source: n["source"]?.GetValue<string>() ?? "",
                    Title: n["title"]?.GetValue<string>() ?? "",
                    Text: n["text"]?.GetValue<string>() ?? "",
                    Date: date,
                    Image: n["image"]?.GetValue<string>(),
                    Url: n["url"]?.GetValue<string>(),
                    Category: n["category"]?.GetValue<string>() ?? ""));
            }
            var fresh = DateTime.TryParse(row.expires_at, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var exp) && exp > DateTime.UtcNow;
            return new CacheData(items, fresh);
        }
        catch (Exception ex)
        {
            Log.Debug($"news cache read: {ex.Message}");
            return null;
        }
    }

    private static void WriteCache(List<NewsItem> items)
    {
        try
        {
            var arr = new JsonArray();
            foreach (var i in items)
                arr.Add(new JsonObject
                {
                    ["id"] = i.Id, ["source"] = i.Source, ["title"] = i.Title, ["text"] = i.Text,
                    ["date"] = i.Date.ToString("o"), ["image"] = i.Image, ["url"] = i.Url,
                    ["category"] = i.Category,
                });
            DatabaseService.Execute(
                "INSERT INTO api_cache(key, value, expires_at) VALUES(@k, @v, @e) " +
                "ON CONFLICT(key) DO UPDATE SET value = @v, expires_at = @e",
                new { k = CacheKey, v = arr.ToJsonString(), e = DateTime.UtcNow.Add(CacheTtl).ToString("o") });
        }
        catch (Exception ex) { Log.Debug($"news cache write: {ex.Message}"); }
    }
}
