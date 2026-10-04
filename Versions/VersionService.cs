using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using NullLauncher.Minecraft;

namespace NullLauncher.Versions;

public sealed class VersionInfo
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "release";
    public DateTime ReleaseTime { get; set; }
    public string Url { get; set; } = "";
    public bool Installed { get; set; }
    public int JavaMajor { get; set; }
    public string? Sha1 { get; set; }
}

/// <summary>
/// Реальный манифест версий Mojang (piston-meta). Кэшируется в metadata/versions.json;
/// при падении сети показываются сохранённые данные (offline=true на фронте).
/// </summary>
public sealed class VersionService
{
    public const string ManifestUrl =
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    private readonly Core.AppPaths _paths;
    private readonly Core.SettingsService _settings;
    private List<VersionInfo>? _cache;
    private readonly object _sync = new();
    private static readonly JsonSerializerOptions JsonOpts = Core.IpcRouter.JsonOptions;

    public VersionService(Core.AppPaths paths, Core.SettingsService settings)
    {
        _paths = paths;
        _settings = settings;
    }

    private string CacheFile => Path.Combine(_paths.MetadataDir, "versions.json");

    public async Task<(List<VersionInfo> list, bool offline)> ListAsync(bool forceRefresh, CancellationToken ct = default)
    {
        List<VersionInfo>? cached = null;
        lock (_sync) cached = _cache;
        if (cached is not null && !forceRefresh) return (cached, false);

        // пробуем сохранённый кэш сначала, чтобы UI не пустовал при отсутствии сети
        var fromDisk = LoadDisk(ct);
        try
        {
            using var http = HttpFactory.Get(_services!);
            using var resp = await http.GetAsync(ManifestUrl, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var list = ParseManifest(json);
            SaveDisk(json, ct);
            lock (_sync) _cache = list;
            return (list, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Манифест Mojang недоступен: {ex.Message}");
            if (fromDisk is not null)
            {
                lock (_sync) _cache = fromDisk;
                return (fromDisk, true);
            }
            throw new LauncherException("Не удалось получить список версий Minecraft",
                "Проверьте подключение к интернету и повторите попытку.", ex);
        }
    }

    private static List<VersionInfo> ParseManifest(string json)
    {
        var doc = JsonDocument.Parse(json);
        var result = new List<VersionInfo>();
        foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
        {
            result.Add(new VersionInfo
            {
                Id = v.GetProperty("id").GetString() ?? "",
                Type = v.GetProperty("type").GetString() ?? "release",
                ReleaseTime = v.TryGetProperty("releaseTime", out var rt) && DateTime.TryParse(rt.GetString(), out var d)
                    ? d.ToUniversalTime() : DateTime.MinValue,
                Url = v.GetProperty("url").GetString() ?? "",
                Sha1 = v.TryGetProperty("sha1", out var s) ? s.GetString() : null,
                JavaMajor = Java.JavaService.RequiredMajor(v.GetProperty("id").GetString() ?? ""),
            });
        }
        return result;
    }

    private List<VersionInfo>? LoadDisk(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            var json = File.ReadAllText(CacheFile);
            var list = ParseManifest(json);
            lock (_sync) _cache = list;
            return list;
        }
        catch { return null; }
    }

    private void SaveDisk(string json, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            File.WriteAllText(CacheFile, json);
        }
        catch (Exception ex) { Log.Warn($"Не удалось сохранить манифест: {ex.Message}"); }
    }

    /* ------------------------------------------------------------ фильтрация для UI */

    public async Task<object> ListForUiAsync(string? type, string? query, int limit, int offset, bool forceRefresh, CancellationToken ct)
    {
        var (all, offline) = await ListAsync(forceRefresh, ct).ConfigureAwait(false);

        var showSnapshots = _settings.Get("showSnapshots", false);
        var showOld = _settings.Get("showOldVersions", false);
        var showExp = _settings.Get("showExperimentalVersions", false);

        IEnumerable<VersionInfo> q = all;

        // тип из UI: all|release|snapshot|beta|alpha|old
        var t = (type ?? "all").ToLowerInvariant();
        q = t switch
        {
            "release" => q.Where(v => v.Type == "release"),
            "snapshot" => q.Where(v => v.Type == "snapshot"),
            "beta" => q.Where(v => v.Type == "old_beta"),
            "alpha" => q.Where(v => v.Type == "old_alpha"),
            "old" => q.Where(v => v.Type is "old_beta" or "old_alpha"),
            _ => q,
        };
        if (t == "all")
        {
            q = q.Where(v => v.Type == "release" || (showSnapshots && v.Type == "snapshot"));
            if (showOld) q = q.Where(v => true);
            else q = q.Where(v => v.Type is not ("old_beta" or "old_alpha") || showExp);
        }

        if (!string.IsNullOrWhiteSpace(query))
            q = q.Where(v => v.Id.Contains(query, StringComparison.OrdinalIgnoreCase));

        q = q.OrderByDescending(v => v.ReleaseTime);

        var total = q.Count();
        var page = q.Skip(offset).Take(limit <= 0 ? 100 : limit).ToList();

        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var d in Directory.EnumerateDirectories(_paths.VersionsDir))
                installed.Add(Path.GetFileName(d));
        }
        catch { /* нет каталога — ничего не установлено */ }

        return new
        {
            hits = page.Select(v => new
            {
                v.Id, v.Type,
                releaseTime = v.ReleaseTime.ToString("o"),
                v.Url,
                installed = installed.Contains(v.Id),
                javaMajor = RequiredJava(v.Id, v.ReleaseTime),
                v.Sha1,
            }).ToList(),
            total,
            offline,
            offset,
            limit = limit <= 0 ? 100 : limit,
        };
    }

    /// <summary>Требуемая версия Java (единый источник — JavaService: понимает и 1.x, и 26.x).</summary>
    public static int RequiredJava(string id, DateTime releaseTime)
        => Java.JavaService.RequiredMajor(id);

    /// <summary>
    /// Требуемая Java для версии: точное значение из version json (javaVersion.majorVersion),
    /// если файл уже скачан, иначе — по таблице Mojang.
    /// </summary>
    public int RequiredJavaFor(string mcVersion)
    {
        try
        {
            var path = VersionJsonPath(mcVersion);
            if (File.Exists(path))
            {
                var node = VersionNode.Load(path);
                if (node.JavaMajor > 0) return node.JavaMajor;
            }
        }
        catch (Exception ex) { Log.Debug($"javaVersion для {mcVersion}: {ex.Message}"); }
        return Java.JavaService.RequiredMajor(mcVersion);
    }

    /* ------------------------------------------------------------ детали */

    public async Task<VersionNode> GetDetailAsync(string id, CancellationToken ct)
    {
        var file = VersionJsonPath(id);
        if (!File.Exists(file))
        {
            var (all, _) = await ListAsync(false, ct).ConfigureAwait(false);
            var meta = all.FirstOrDefault(v => v.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                       ?? throw new LauncherException($"Minecraft «{id}» отсутствует в манифесте Mojang",
                           "Обновите список версий (Мои сборки → Обновить) и выберите существующую версию.");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var http = HttpFactory.Get(_services!);
            var json = await http.GetStringAsync(meta.Url, ct).ConfigureAwait(false);
            File.WriteAllText(file, json);
        }
        return VersionNode.Load(file);
    }

    public string VersionJsonPath(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
        return Path.Combine(_paths.VersionsDir, id, id + ".json");
    }

    public async Task EnsureVersionJsonAsync(string id, CancellationToken ct)
    {
        if (File.Exists(VersionJsonPath(id))) return;
        await GetDetailAsync(id, ct).ConfigureAwait(false);
    }

    public int InstalledCount()
    {
        try { return Directory.EnumerateDirectories(_paths.VersionsDir).Count(); }
        catch { return 0; }
    }

    /* ------------------------------------------------------------ загрузчики (Fabric/Quilt/Forge/NeoForge) */

    /// <summary>Реальные версии загрузчиков под указанную версию Minecraft.</summary>
    public async Task<List<object>> LoadersAsync(string mcVersion, CancellationToken ct)
    {
        var result = new List<object>();
        using var http = HttpFactory.Get(_services!);

        // Fabric
        try
        {
            var json = await http.GetStringAsync(
                $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}", ct).ConfigureAwait(false);
            var arr = JsonNode.Parse(json)!.AsArray();
            foreach (var item in arr)
            {
                var loader = item?["loader"]?["version"]?.GetValue<string>();
                var stable = item?["loader"]?["stable"]?.GetValue<bool>() ?? false;
                if (loader is not null)
                    result.Add(new { loader = "fabric", version = loader, stable });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Debug($"Fabric meta: {ex.Message}"); }

        // Quilt
        try
        {
            var json = await http.GetStringAsync(
                $"https://meta.quiltmc.org/v3/versions/loader/{mcVersion}", ct).ConfigureAwait(false);
            var arr = JsonNode.Parse(json)!.AsArray();
            foreach (var item in arr)
            {
                var loader = item?["loader"]?["version"]?.GetValue<string>();
                var stable = item?["loader"]?["stable"]?.GetValue<bool>() ?? false;
                if (loader is not null)
                    result.Add(new { loader = "quilt", version = loader, stable });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Debug($"Quilt meta: {ex.Message}"); }

        // Forge (промо-файл)
        try
        {
            var json = await http.GetStringAsync(
                "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct)
                .ConfigureAwait(false);
            var promos = JsonNode.Parse(json)?["promos"]?.AsObject();
            if (promos is not null)
            {
                foreach (var key in new[] { $"{mcVersion}-recommended", $"{mcVersion}-latest" })
                {
                    if (promos[key] is JsonValue v && v.TryGetValue<string>(out var fv) && !string.IsNullOrEmpty(fv))
                        result.Add(new { loader = "forge", version = fv, stable = key.Contains("recommended") });
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Debug($"Forge promos: {ex.Message}"); }

        // NeoForge: maven-metadata + фильтр по схеме версий NeoForged
        try
        {
            var xml = await http.GetStringAsync(
                "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml", ct)
                .ConfigureAwait(false);
            var versions = System.Text.RegularExpressions.Regex.Matches(xml, @"<version>([^<]+)</version>")
                .Select(m => m.Groups[1].Value)
                .Where(v => v.Contains('.') && !v.Contains('-') && v.Split('.')[0].All(char.IsDigit))
                .ToList();
            var prefix = NeoForgePrefix(mcVersion);
            var matched = versions.Where(v => v.StartsWith(prefix + ".")).ToList();
            if (matched.Count == 0) matched = versions.Where(v => v.StartsWith(prefix)).ToList();
            foreach (var v in matched.Take(5))
                result.Add(new { loader = "neoforge", version = v, stable = true });
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Debug($"NeoForge maven: {ex.Message}"); }

        return result
            .OrderBy(x => (string)x.GetType().GetProperty("loader")!.GetValue(x)!)
            .ThenByDescending(x => (bool)x.GetType().GetProperty("stable")!.GetValue(x)!)
            .ThenByDescending(x => (string)x.GetType().GetProperty("version")!.GetValue(x)!, VersionTextComparer.Instance)
            .ToList();
    }

    /// <summary>
    /// Семантическое сравнение версий: «0.19.5» &gt; «0.10.1+build.209» &gt; «0.1.0.52».
    /// Лексикографический порядок здесь вредный: «0.1.0.48» раньше «0.19.5», из-за чего
    /// в списке наверху оказываются древние сборки загрузчика.
    /// </summary>
    private sealed class VersionTextComparer : IComparer<string>
    {
        public static readonly VersionTextComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var ax = Numbers(x);
            var ay = Numbers(y);
            for (int i = 0; i < Math.Max(ax.Length, ay.Length); i++)
            {
                var vx = i < ax.Length ? ax[i] : 0;
                var vy = i < ay.Length ? ay[i] : 0;
                if (vx != vy) return vx.CompareTo(vy);
            }
            // числа равны: версия без суффикса (release) новее, чем с суффиксом
            return string.CompareOrdinal(y ?? "", x ?? ""); // обратный порядок строк
        }

        /// <summary>
        /// Все числа из строки версии. Раньше здесь было m.Groups[1].Value у регулярки без групп —
        /// .NET возвращает пустую группу, long.Parse("") падал с FormatException и валил весь
        /// список загрузчиков (versions.loaders → «Не удалось выполнить»).
        /// </summary>
        private static long[] Numbers(string? value)
        {
            if (string.IsNullOrEmpty(value)) return Array.Empty<long>();
            var result = new List<long>();
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(value, @"\d+"))
            {
                if (long.TryParse(m.Value, out var n)) result.Add(n);
            }
            return result.ToArray();
        }
    }

    private static string NeoForgePrefix(string mc)
    {
        var parts = mc.Split('.');
        if (parts.Length < 2) return mc;
        // 1.21.1 → "21.1"; старые 1.20.1 → "47.1"-стиль не используется с 20.5
        var major = parts[0] == "1" && parts.Length >= 3 ? parts[1] + "." + parts[2] : mc;
        return major;
    }

    /* ------------------------------------------------------------ удаление установленной версии */

    public object Delete(string id)
    {
        var dir = Path.Combine(_paths.VersionsDir, id);
        if (!Directory.Exists(dir))
            throw new LauncherException("Версия не установлена", id);
        long freed = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { var fi = new FileInfo(f); freed += fi.Length; fi.Delete(); } catch { /* ignore */ }
        }
        try { Directory.Delete(dir, true); } catch { /* ignore */ }
        Log.Info($"Версия {id} удалена, освобождено {freed} байт");
        return new { ok = true, freed };
    }

    private static Core.AppServices? _services;
    public static void Bind(Core.AppServices s) => _services = s;
}
