using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NullLauncher.Minecraft;

/// <summary>
/// Гарантирует наличие всех файлов версии: version JSON, client jar, библиотеки, ассеты, конфиг логирования.
/// Все скачивания идут через DownloadService — прогресс виден в UI, файлы проверяются по sha1.
/// </summary>
public sealed class FileProvisioner
{
    private readonly Core.AppPaths _paths;
    private readonly Versions.VersionService _versions;
    private readonly Downloads.DownloadService _downloads;

    public FileProvisioner(Core.AppPaths paths, Versions.VersionService versions, Downloads.DownloadService downloads)
    {
        _paths = paths;
        _versions = versions;
        _downloads = downloads;
    }

    public event Action<string, int, int, string>? Progress; // stage, step, total, message

    private void Report(string stage, int step, int total, string message)
        => Progress?.Invoke(stage, step, total, message);

    /* ------------------------------------------------------------ цепочка подготовки */

    /// <summary>Возвращает (versionId, node) — идентификатор, который нужно передать в java -cp.</summary>
    public async Task<(string versionId, VersionNode node)> PrepareAsync(
        string baseVersion, string loader, string? loaderVersion,
        string instanceId, CancellationToken ct)
    {
        var mc = baseVersion;

        // 1. базовая версия Minecraft
        Report("Манифест версии", 0, 5, mc);
        await _versions.EnsureVersionJsonAsync(mc, ct).ConfigureAwait(false);
        var baseNode = VersionNode.Load(_versions.VersionJsonPath(mc));

        // 2. загрузчик
        var versionId = mc;
        if (!string.Equals(loader, "vanilla", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(loader))
        {
            Report("Установка загрузчика", 1, 5, $"{loader} {loaderVersion}");
            versionId = await LoaderInstaller.EnsureAsync(_paths, _versions, _downloads,
                mc, loader, loaderVersion, ct).ConfigureAwait(false);
        }

        var nodePath = _versions.VersionJsonPath(versionId);
        if (!File.Exists(nodePath))
            nodePath = _versions.VersionJsonPath(mc);
        var node = VersionNode.Load(nodePath);

        // 3. цепочка inheritsFrom (fabric/forge наследуют json Minecraft)
        var chain = await ResolveChainAsync(node, ct).ConfigureAwait(false);
        // рабочий профиль = базовый Minecraft + загрузчик (аргументы, библиотеки, main class)
        var effective = VersionNode.MergeChain(Enumerable.Reverse(chain).ToList());

        // 4. client jar
        Report("Клиентская сборка", 2, 5, versionId);
        await EnsureClientJarAsync(chain, versionId, ct).ConfigureAwait(false);

        // 5. библиотеки
        Report("Библиотеки", 3, 5, "проверка файлов");
        await EnsureLibrariesAsync(chain, ct).ConfigureAwait(false);

        // 6. ассеты
        Report("Ассеты", 4, 5, "проверка файлов");
        await EnsureAssetsAsync(chain, ct).ConfigureAwait(false);

        // 7. конфиг логирования + нативы
        Report("Подготовка", 5, 5, "нативные библиотеки");
        await EnsureLoggingAsync(chain, ct).ConfigureAwait(false);
        var logCfg = LoggingConfigPath(chain);
        var logArg = chain.Select(n => n.LoggingArgument).FirstOrDefault(a => !string.IsNullOrEmpty(a));
        lock (_syncLogging) _loggingPaths[versionId] = (logArg, logCfg);
        ExtractNatives(versionId, chain, instanceId);

        Report("Готово", 5, 5, versionId);
        return (versionId, effective);
    }

    /* log4j-конфиг, найденный при подготовке (нужен для JVM-аргумента при запуске) */
    private readonly object _syncLogging = new();
    private readonly Dictionary<string, (string? argument, string? path)> _loggingPaths = new(StringComparer.OrdinalIgnoreCase);

    public (string? argument, string? path) GetLoggingInfo(string versionId)
    {
        lock (_syncLogging) return _loggingPaths.TryGetValue(versionId, out var p) ? p : (null, null);
    }

    /* ------------------------------------------------------------ inheritsFrom */

    private async Task<List<VersionNode>> ResolveChainAsync(VersionNode node, CancellationToken ct)
    {
        var chain = new List<VersionNode> { node };
        var current = node;
        var guard = 0;
        while (!string.IsNullOrEmpty(current.InheritsFrom) && guard++ < 5)
        {
            await _versions.EnsureVersionJsonAsync(current.InheritsFrom, ct).ConfigureAwait(false);
            var parent = VersionNode.Load(_versions.VersionJsonPath(current.InheritsFrom));
            chain.Add(parent);
            current = parent;
        }
        return chain;
    }

    /* ------------------------------------------------------------ client jar */

    private async Task EnsureClientJarAsync(List<VersionNode> chain, string versionId, CancellationToken ct)
    {
        // ищем в цепочке узел с downloads.client (обычно базовый Minecraft)
        foreach (var node in chain)
        {
            if (string.IsNullOrEmpty(node.ClientJarUrl)) continue;
            var target = Path.Combine(_paths.VersionsDir, node.Id, node.Id + ".jar");
            if (File.Exists(target) && (node.ClientJarSize == 0 || new FileInfo(target).Length == node.ClientJarSize))
                continue;
            Report("Клиентская сборка", 0, 1, node.Id);
            await _downloads.DownloadAsync(node.ClientJarUrl, target, $"Minecraft {node.Id}.jar",
                kind: "version", hashAlgo: "sha1", hash: node.ClientJarSha1, ct: ct).ConfigureAwait(false);
        }

        // наследник без собственной jar требует jar базовой версии рядом с его id
        var top = chain[0];
        if (chain.Count > 1 && !File.Exists(Path.Combine(_paths.VersionsDir, top.Id, top.Id + ".jar")))
        {
            var baseJar = Path.Combine(_paths.VersionsDir, chain[^1].Id, chain[^1].Id + ".jar");
            if (File.Exists(baseJar))
            {
                Directory.CreateDirectory(Path.Combine(_paths.VersionsDir, top.Id));
                File.Copy(baseJar, Path.Combine(_paths.VersionsDir, top.Id, top.Id + ".jar"), true);
            }
        }
    }

    /* ------------------------------------------------------------ библиотеки */

    private async Task EnsureLibrariesAsync(List<VersionNode> chain, CancellationToken ct)
    {
        var tasks = new List<Task>();
        var pending = new List<(Library lib, string target)>();
        var total = 0;

        foreach (var node in chain)
        {
            foreach (var lib in node.ApplicableLibraries())
            {
                if (string.IsNullOrEmpty(lib.Name)) continue;
                var rel = lib.RelativePath;
                if (string.IsNullOrEmpty(rel)) continue;
                var target = Path.Combine(_paths.LibrariesDir, rel.Replace('/', Path.DirectorySeparatorChar));
                total++;

                var exists = File.Exists(target) &&
                             (lib.Size == 0 || new FileInfo(target).Length == lib.Size) &&
                             (lib.Sha1 is null || !_settingsVerify);
                if (!exists && lib.Url is not null)
                    pending.Add((lib, target));
                else if (!exists && lib.Url is null)
                {
                    // библиотеки без URL (обычно нативные у старых версий) — пробуем стандартный репозиторий
                    pending.Add((lib, target));
                }
            }
        }

        var step = 0;
        foreach (var (lib, target) in pending)
        {
            ct.ThrowIfCancellationRequested();
            step++;
            Report("Библиотеки", step, pending.Count, lib.Name);
            var url = BuildUrl(lib);
            if (url is null) continue;
            await _downloads.DownloadAsync(url, target, lib.Name, kind: "library",
                hashAlgo: "sha1", hash: lib.Sha1, ct: ct).ConfigureAwait(false);
        }
    }

    private bool _settingsVerify => true;

    private string? BuildUrl(Library lib)
    {
        if (!string.IsNullOrEmpty(lib.Url))
        {
            // полный URL уже указан (обычно http://... или https://libraries.minecraft.net/...)
            if (lib.Url.EndsWith('/')) return lib.Url + lib.RelativePath;
            if (lib.Url.Contains(lib.RelativePath)) return lib.Url;
            return lib.Url.TrimEnd('/') + "/" + lib.RelativePath;
        }
        // стандартный репозиторий Mojang
        return "https://libraries.minecraft.net/" + lib.RelativePath;
    }

    /* ------------------------------------------------------------ ассеты */

    /// <summary>
    /// Объекты индекса ассетов: (sha1, относительный путь, размер).
    /// В файле индекса они лежат внутри «objects»; раньше перебирались ключи корня,
    /// поэтому список всегда получался пустым и ассеты (текстуры, звуки) не скачивались.
    /// </summary>
    public static List<(string hash, string path, long size)> ParseAssetIndex(string indexJson)
    {
        var result = new List<(string hash, string path, long size)>();
        var root = JsonNode.Parse(indexJson) as JsonObject;
        var objects = root?["objects"] as JsonObject ?? root;
        if (objects is null) return result;
        foreach (var (_, val) in objects)
        {
            if (val is not JsonObject o) continue;
            var hash = o["hash"]?.GetValue<string>();
            if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;
            var size = o["size"]?.GetValue<long>() ?? 0;
            result.Add((hash, hash[..2] + "/" + hash, size));
        }
        return result;
    }

    private async Task EnsureAssetsAsync(List<VersionNode> chain, CancellationToken ct)
    {
        var node = chain.FirstOrDefault(n => n.AssetIndexUrl is not null);
        if (node is null || node.AssetIndexUrl is null) return;

        var indexId = node.AssetIndexId ?? node.Assets ?? "legacy";
        var indexFile = Path.Combine(_paths.AssetsDir, "indexes", indexId + ".json");
        if (!File.Exists(indexFile))
        {
            Report("Ассеты", 0, 1, "индекс");
            await _downloads.DownloadAsync(node.AssetIndexUrl, indexFile, $"Ассеты {indexId}",
                kind: "asset", hashAlgo: "sha1", hash: node.AssetIndexSha1, ct: ct).ConfigureAwait(false);
        }

        JsonObject index;
        try { index = (JsonNode.Parse(await File.ReadAllTextAsync(indexFile, ct).ConfigureAwait(false)) as JsonObject)!; }
        catch (Exception ex) { throw new LauncherException("Индекс ассетов повреждён", ex.Message, ex); }

        var map = ParseAssetIndex(index.ToJsonString());

        var missing = map.Where(m =>
        {
            var f = Path.Combine(_paths.AssetsDir, "objects", m.path.Replace('/', Path.DirectorySeparatorChar));
            return !File.Exists(f) || (m.size > 0 && new FileInfo(f).Length != m.size);
        }).ToList();

        var step = 0;
        foreach (var m in missing)
        {
            ct.ThrowIfCancellationRequested();
            step++;
            if (step == 1 || step % 25 == 0 || step == missing.Count)
                Report("Ассеты", step, missing.Count, m.path);
            var target = Path.Combine(_paths.AssetsDir, "objects", m.path.Replace('/', Path.DirectorySeparatorChar));
            await _downloads.DownloadAsync(
                    $"https://resources.download.minecraft.net/{m.path}", target,
                    $"Ассет {m.path}", kind: "asset", hashAlgo: "sha1", hash: m.hash, ct: ct)
                .ConfigureAwait(false);
        }
    }

    /* ------------------------------------------------------------ logging */

    private async Task EnsureLoggingAsync(List<VersionNode> chain, CancellationToken ct)
    {
        var node = chain.FirstOrDefault(n => !string.IsNullOrEmpty(n.LoggingUrl));
        if (string.IsNullOrEmpty(node?.LoggingUrl)) return;
        /* URL берётся из logging.client.file.url; защита от мусорных значений */
        if (!Uri.TryCreate(node.LoggingUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
        var name = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrEmpty(name)) name = "client.xml";
        var target = Path.Combine(_paths.AssetsDir, "log_configs", name);
        if (File.Exists(target)) return;
        await _downloads.DownloadAsync(node.LoggingUrl, target, $"Конфиг логирования {name}",
            kind: "config", hashAlgo: "sha1", hash: node.LoggingSha1, ct: ct).ConfigureAwait(false);
    }

    /// <summary>Локальный путь конфига логирования (для -Dlog4j.configurationFile=${path}).</summary>
    public string? LoggingConfigPath(List<VersionNode> chain)
    {
        var node = chain.FirstOrDefault(n => !string.IsNullOrEmpty(n.LoggingUrl));
        if (string.IsNullOrEmpty(node?.LoggingUrl)) return null;
        if (!Uri.TryCreate(node.LoggingUrl, UriKind.Absolute, out var uri)) return null;
        var name = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrEmpty(name)) return null;
        var p = Path.Combine(_paths.AssetsDir, "log_configs", name);
        return File.Exists(p) ? p : null;
    }

    /* ------------------------------------------------------------ нативы */

    public string NativesDir(string versionId)
    {
        var dir = Path.Combine(_paths.CacheDir, "natives", versionId);
        Directory.CreateDirectory(dir);
        // 26.x ожидает нативы и временные папки в подкаталогах:
        // ${natives_directory}/java (java.library.path), /jna, /lwjgl, /netty
        foreach (var sub in NativesSubDirs)
            Directory.CreateDirectory(Path.Combine(dir, sub));
        return dir;
    }

    private static readonly string[] NativesSubDirs = { "java", "jna", "lwjgl", "netty" };

    private void ExtractNatives(string versionId, List<VersionNode> chain, string instanceId)
    {
        var dir = NativesDir(versionId);
        var javaDir = Path.Combine(dir, "java");
        // маркер v2: у старых установок нативы лежали только в корне — распаковываем заново
        var extractedMarker = Path.Combine(dir, ".extracted-v2");
        if (File.Exists(extractedMarker)) return;

        foreach (var node in chain)
        {
            foreach (var lib in node.ApplicableLibraries().Where(l => l.IsNative || l.Natives.Count > 0))
            {
                var rel = lib.RelativePath;
                if (string.IsNullOrEmpty(rel)) continue;
                var jar = Path.Combine(_paths.LibrariesDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(jar)) continue;
                try
                {
                    using var zip = ZipFile.OpenRead(jar);
                    var excludes = lib.Extract.TryGetValue("exclude", out var ex) ? ex.Split(',') : Array.Empty<string>();
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        if (excludes.Any(x => entry.FullName.StartsWith(x.Trim()))) continue;
                        var name = Path.GetFileName(entry.FullName);
                        if (name.EndsWith(".txt") || name.Equals("META-INF", StringComparison.OrdinalIgnoreCase)) continue;
                        entry.ExtractToFile(Path.Combine(dir, name), true);
                        // копия в natives/java — её используют версии 26.x
                        try { entry.ExtractToFile(Path.Combine(javaDir, name), true); }
                        catch (Exception copyEx) { Log.Debug($"Нативы {name} → java/: {copyEx.Message}"); }
                    }
                }
                catch (Exception ex) { Log.Debug($"Не удалось распаковать нативы {rel}: {ex.Message}"); }
            }
        }
        try { File.WriteAllText(extractedMarker, DateTime.UtcNow.ToString("o")); } catch { /* ignore */ }
    }
}
