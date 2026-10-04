using System.IO;
using NullLauncher.Core;
using System.IO;

namespace NullLauncher.Modules;

/// <summary>Обзор и очистка хранилища/кэша. Никогда не трогает сборки и Java.</summary>
public static class StorageIpc
{
    private static readonly HashSet<string> ClearableCache = new() { "modrinth", "images", "metadata", "tmp", "webview2" };

    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("storage.overview", (_, _) => Task.FromResult<object?>(SystemIpc.StorageForIpc(s)));
        r.Register("storage.analyze", (_, _) => Task.FromResult<object?>(Analyze(s)));
        r.Register("cache.stats", (_, _) => Task.FromResult<object?>(CacheStats(s)));
        r.Register("cache.clear", (p, _) => Task.FromResult<object?>(ClearCache(s, p)));
    }

    private static object Analyze(AppServices s)
    {
        var perInstance = new List<(string id, string name, long bytes, long savesBytes, int mods, string path)>();
        foreach (var inst in s.Instances.List())
        {
            var bytes = AppPaths.DirectorySize(inst.Dir);
            var mods = Directory.Exists(inst.ModsDir) ? Directory.EnumerateFiles(inst.ModsDir).Count() : 0;
            var saves = AppPaths.DirectorySize(inst.SavesDir);
            perInstance.Add((inst.Id, inst.Name, bytes, saves, mods, inst.Dir));
        }
        var categories = new[]
        {
            new { key = "instances", label = "Сборки", bytes = AppPaths.DirectorySize(s.Paths.InstancesDir) },
            new { key = "java", label = "Java", bytes = AppPaths.DirectorySize(s.Paths.JavaDir) },
            new { key = "backups", label = "Резервные копии", bytes = AppPaths.DirectorySize(s.Paths.BackupsDir) },
            new { key = "cache", label = "Кэш", bytes = AppPaths.DirectorySize(s.Paths.CacheDir) },
            new { key = "downloads", label = "Загрузки", bytes = AppPaths.DirectorySize(s.Paths.DownloadsDir) },
            new { key = "game", label = "Библиотеки Minecraft", bytes = AppPaths.DirectorySize(s.Paths.VersionsDir) + AppPaths.DirectorySize(s.Paths.LibrariesDir) + AppPaths.DirectorySize(s.Paths.AssetsDir) },
        };
        return new
        {
            categories,
            instances = perInstance.OrderByDescending(x => x.bytes)
                .Select(x => new { x.id, x.name, x.bytes, x.savesBytes, x.mods, x.path })
                .ToList(),
            freeBytes = SystemIpc.FreeDisk(s.Paths.DataRoot),
        };
    }

    private static object CacheStats(AppServices s)
    {
        var rows = new (string key, string label, long bytes)[]
        {
            ("modrinth", "Ответы Modrinth", AppPaths.DirectorySize(Path.Combine(s.Paths.CacheDir, "modrinth")) + ApiCacheBytes()),
            ("images", "Рзображения", AppPaths.DirectorySize(Path.Combine(s.Paths.CacheDir, "images"))),
            ("metadata", "Метаданные", AppPaths.DirectorySize(s.Paths.MetadataDir)),
            ("tmp", "Временные файлы", AppPaths.DirectorySize(s.Paths.TempDir)),
        };
        return rows.Select(x => new { key = x.key, label = x.label, bytes = x.bytes }).ToList();
    }

    private static long ApiCacheBytes()
    {
        try { return Database.DatabaseService.Scalar<long>("SELECT COALESCE(SUM(LENGTH(value)),0) FROM api_cache"); }
        catch { return 0; }
    }

    private static object ClearCache(AppServices s, System.Text.Json.Nodes.JsonNode? p)
    {
        var keys = p?.StrList("keys");
        if (keys is null || keys.Count == 0) keys = new List<string> { "modrinth", "images", "metadata", "tmp" };
        foreach (var k in keys)
            if (!ClearableCache.Contains(k))
                throw new LauncherException("Этот кэш очищать нельзя", $"Ключ '{k}' не входит в список безопасных. Сборки, Java и БД не удаляются.");

        long freed = 0;
        foreach (var k in keys)
        {
            switch (k)
            {
                case "metadata":
                    freed += Database.DatabaseService.Scalar<long>("SELECT COALESCE(SUM(LENGTH(value)),0) FROM api_cache");
                    Database.DatabaseService.Execute("DELETE FROM api_cache");
                    freed += AppPaths.DirectorySize(s.Paths.MetadataDir);
                    ClearDir(s.Paths.MetadataDir);
                    break;
                case "modrinth":
                    ClearDir(Path.Combine(s.Paths.CacheDir, "modrinth"));
                    freed += ApiCacheBytes();
                    Database.DatabaseService.Execute("DELETE FROM api_cache WHERE key LIKE 'modrinth:%'");
                    break;
                case "images":
                    ClearDir(Path.Combine(s.Paths.CacheDir, "images"));
                    break;
                case "tmp":
                    ClearDir(s.Paths.TempDir);
                    Directory.CreateDirectory(s.Paths.TempDir);
                    break;
                case "webview2":
                    // намеренно ничего не делаем: WebView2 хранит там свои данные
                    break;
            }
        }
        Log.Info($"Очистка кэша [{string.Join(",", keys)}]: {freed} байт");
        return new { freed = freed, keys };
    }

    private static void ClearDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { File.Delete(f); } catch { /* ignore */ }
            foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
                try { Directory.Delete(d, false); } catch { /* ignore */ }
        }
        catch (Exception ex) { Log.Warn($"Не удалось очистить {dir}: {ex.Message}"); }
    }
}
