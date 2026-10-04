using System.Text.Json.Nodes;
using NullLauncher.Core;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace NullLauncher.Modules;

/// <summary>Системные IPC-методы: инфо, диалоги файлов, открытие путей, диагностика, хранилище.</summary>
public static class SystemIpc
{
    [DllImport("kernel32.dll")] private static extern bool GetPhysicallyInstalledSystemMemory(out long totalKb);
    [StructLayout(LayoutKind.Sequential)] private class MemoryStatusEx { public uint Length; public uint MemoryLoad; public ulong TotalPhys, TotalPageFile, TotalVirtual, AvailVirtual, AvailPageFile, AvailPhys; }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx([In] MemoryStatusEx lpBuffer);

    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("system.info", (_, _) => Task.FromResult<object?>(Info(s)));
        r.Register("system.window", (p, _) => { System.Windows.Application.Current.Dispatcher.Invoke(() => (System.Windows.Application.Current.MainWindow as MainWindow)?.HandleWindowAction(p.Str("action") ?? "")); return Task.FromResult<object?>(new { ok = true }); });
        r.Register("system.pickDirectory", (p, _) => Task.FromResult<object?>(PickDirectory(p)));
        r.Register("system.pickFiles", (p, _) => Task.FromResult<object?>(PickFiles(p)));
        r.Register("system.openPath", (p, _) =>
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() => MainWindow.OpenPath(p.Str("path") ?? "", p.Bool("select")));
            return Task.FromResult<object?>(new { ok = true });
        });
        r.Register("system.openUrl", (p, _) =>
        {
            var url = p.Str("url") ?? "";
            MainWindow.OpenUrl(url);
            return Task.FromResult<object?>(new { ok = true });
        });
        r.Register("system.clipboard", (p, _) =>
        {
            var text = p.Str("text") ?? "";
            System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Clipboard.SetText(text));
            return Task.FromResult<object?>(new { ok = true });
        });
        r.Register("system.diagnose", (_, ct) => Task.FromResult<object?>(Diagnose(s, ct)));
        r.Register("system.storage", (_, _) => Task.FromResult<object?>(Storage(s)));
        r.Register("system.clearStorage", (p, _) => Task.FromResult<object?>(ClearStorage(s, p)));
        r.Register("system.checkUpdate", async (_, ct) => (object?)await CheckUpdateAsync(s, ct).ConfigureAwait(false));
        r.Register("system.firstRun", (_, _) => Task.FromResult<object?>(new
        {
            needed = !s.Settings.Get("onboardingDone", false),
            steps = new[] { "folder", "java", "account", "ram", "theme" },
        }));
        r.Register("system.completeFirstRun", (p, _) =>
        {
            if (p is not null)
            {
                var patch = new Dictionary<string, object?>();
                if (p["folder"] is not null && p.Str("folder") is string f && f.Length > 0) patch["dataDir"] = f;
                if (p["ram"] is not null) patch["ramMaxMb"] = p.Int("ramMaxMb", 4096);
                if (p["theme"] is not null) patch["theme"] = p.Str("theme");
                if (p["accent"] is not null) patch["accent"] = p.Str("accent");
                if (p["javaMajor"] is not null) patch["javaMajor"] = p.Int("javaMajor", 21);
                if (patch.Count > 0) s.Settings.SetMany(patch);
            }
            s.Settings.Set("onboardingDone", true);
            return Task.FromResult<object?>(new { ok = true });
        });
    }

    /* ---------------------------------------------------------- info */
    private static object Info(AppServices s)
    {
        var os = Environment.OSVersion.VersionString;
        var ver = Environment.OSVersion.Version;
        if (ver.Major >= 10) os = ver.Build >= 22000 ? "Windows 11" : "Windows 10";

        var (total, free) = Memory();
        return new
        {
            appVersion = typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            os,
            osVersion = Environment.OSVersion.Version.ToString(),
            dataDir = s.Paths.DataRoot,
            configDir = s.Paths.ConfigRoot,
            ramTotalMb = total,
            ramFreeMb = free,
            cpu = CpuName(),
            cpuCores = Environment.ProcessorCount,
            gpu = GpuName(),
            screen = new { width = (int)SystemParameters.PrimaryScreenWidth, height = (int)SystemParameters.PrimaryScreenHeight },
            isFirstRun = !s.Settings.Get("onboardingDone", false),
            expertMode = s.Settings.Get("expertMode", false),
            locale = "ru",
            exePath = Environment.ProcessPath ?? "",
            freeDiskGb = FreeDisk(s.Paths.DataRoot),
        };
    }

    public static (long totalMb, long freeMb) Memory()
    {
        var st = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(st))
            return ((long)(st.TotalPhys / 1024 / 1024), (long)(st.AvailPhys / 1024 / 1024));
        return (0, 0);
    }

    private static string CpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Hardware\Description\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString") as string;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch { /* ignore */ }
        return $"{Environment.ProcessorCount} потоков";
    }

    private static string GpuName()
    {
        try
        {
            var gpus = new List<string>();
            using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                foreach (var o in searcher.Get())
                {
                    var n = o["Name"] as string;
                    if (!string.IsNullOrWhiteSpace(n)) gpus.Add(n.Trim());
                }
            if (gpus.Count > 0) return string.Join(", ", gpus.Distinct());
        }
        catch (Exception ex) { Log.Debug($"WMI GPU: {ex.Message}"); }
        return "";
    }

    public static long FreeDisk(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return 0;
            var di = new DriveInfo(root);
            return di.AvailableFreeSpace;
        }
        catch { return 0; }
    }

    /* ---------------------------------------------------------- диалоги */
    private static object? PickDirectory(JsonNode? p)
    {
        string? result = null;
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = p?.Str("title") ?? "Выберите папку",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                SelectedPath = p?.Str("initial") ?? "",
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                result = dlg.SelectedPath;
        });
        return result is null ? null : new { path = result };
    }

    private static object? PickFiles(JsonNode? p)
    {
        var filter = (p?.Str("filter") ?? "all").ToLowerInvariant();
        var (title, spec) = filter switch
        {
            "image" => ("Рзображение", "Рзображения|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|Все файлы|*.*"),
            "jar" => ("Выберите мод", "Java-моды (*.jar)|*.jar|Все файлы|*.*"),
            "zip" => ("Выберите архив", "Архивы (*.zip)|*.zip|Все файлы|*.*"),
            "mrpack" => ("Модпак Modrinth", "Modrinth (*.mrpack)|*.mrpack|Все файлы|*.*"),
            "instance" => ("Файл сборки", "Сборка NullLauncher (*.nlpkg;*.zip)|*.nlpkg;*.zip|Все файлы|*.*"),
            "world" => ("Мир Minecraft", "Архив мира (*.zip)|*.zip|Все файлы|*.*"),
            "json" => ("JSON", "JSON (*.json)|*.json|Все файлы|*.*"),
            "exe" => ("Рсполняемый файл", "Программы (*.exe)|*.exe|Все файлы|*.*"),
            "log" => ("Лог", "Логи (*.log;*.txt)|*.log;*.txt|Все файлы|*.*"),
            _ => ("Выберите файл", "Все файлы|*.*"),
        };

        string[]? files = null;
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = p?.Str("title") ?? title,
                Filter = spec,
                Multiselect = p?.Bool("multiple", true) ?? true,
            };
            if (dlg.ShowDialog() == true) files = dlg.FileNames;
        });
        return files;
    }

    /* ---------------------------------------------------------- диагностика */
    private static object Diagnose(AppServices s, CancellationToken ct)
    {
        var checks = new List<object>();
        void add(string id, string title, string status, string message) => checks.Add(new { id, title, status, message });

        try
        {
            var writable = true;
            var probe = Path.Combine(s.Paths.DataRoot, ".write-test");
            File.WriteAllText(probe, "ok"); File.Delete(probe);
            add("data", "Папка данных", writable ? "ok" : "error", s.Paths.DataRoot);
        }
        catch (Exception ex) { add("data", "Папка данных", "error", "Нет прав на запись: " + ex.Message); }

        try
        {
            var free = FreeDisk(s.Paths.DataRoot);
            var freeGb = free / 1024.0 / 1024 / 1024;
            add("disk", "Свободное место", freeGb < 5 ? "error" : freeGb < 15 ? "warn" : "ok",
                $"{freeGb:0.0} ГБ свободно (рекомендуется от 15 ГБ)");
        }
        catch { add("disk", "Свободное место", "warn", "Не удалось определить"); }

        try
        {
            var conn = s.Db.Open(); conn.Close();
            add("db", "База данных", "ok", s.Paths.DatabaseFile);
        }
        catch (Exception ex) { add("db", "База данных", "error", ex.Message); }

        try
        {
            var java = Java.JavaService.FindAnyJava(s.Paths);
            add("java", "Java", java is null ? "warn" : "ok", java is null ? "Ни одной установки Java не найдено" : $"{java.Value.version} ({java.Value.path})");
        }
        catch (Exception ex) { add("java", "Java", "warn", ex.Message); }

        try
        {
            using var http = HttpFactory.Create(s);
            using var cts = new CancellationTokenSource(8000);
            using var resp = http.GetAsync("https://api.modrinth.com/v2/status", cts.Token).GetAwaiter().GetResult();
            add("modrinth", "Modrinth API", resp.IsSuccessStatusCode ? "ok" : "warn", $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { add("modrinth", "Modrinth API", "warn", "Нет ответа: " + ex.Message); }

        try
        {
            using var http = HttpFactory.Create(s);
            using var cts = new CancellationTokenSource(8000);
            using var resp = http.GetAsync("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json", cts.Token).GetAwaiter().GetResult();
            add("mojang", "Mojang manifest", resp.IsSuccessStatusCode ? "ok" : "warn", $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { add("mojang", "Mojang manifest", "warn", "Нет ответа: " + ex.Message); }

        add("webview2", "WebView2", "ok", "Установлен (приложение запущено)");
        {
            var (t, f) = Memory();
            add("mem", "Оперативная память", f < 1024 ? "warn" : "ok", $"Всего {t} МБ, свободно {f} МБ");
        }

        return new { checks };
    }

    /* ---------------------------------------------------------- хранилище */
    public static object StorageForIpc(AppServices s) => Storage(s);

    private static object Storage(AppServices s)
    {
        var p = s.Paths;
        long sizeOf(params string[] dirs) => dirs.Sum(AppPaths.DirectorySize);

        var rows = new (string key, string label, long bytes, string path)[]
        {
            ("instances", "Сборки", AppPaths.DirectorySize(p.InstancesDir), p.InstancesDir),
            ("java", "Java", AppPaths.DirectorySize(p.JavaDir), p.JavaDir),
            ("backups", "Резервные копии", AppPaths.DirectorySize(p.BackupsDir), p.BackupsDir),
            ("cache", "Кэш", AppPaths.DirectorySize(p.CacheDir), p.CacheDir),
            ("downloads", "Загрузки", AppPaths.DirectorySize(p.DownloadsDir), p.DownloadsDir),
            ("logs", "Логи и отчёты", AppPaths.DirectorySize(p.LogsDir) + AppPaths.DirectorySize(p.CrashReportsDir), p.LogsDir),
            ("game", "Библиотеки Minecraft", sizeOf(p.VersionsDir, p.LibrariesDir, p.AssetsDir, p.ResourcesDir), p.VersionsDir),
            ("database", "База данных", AppPaths.DirectorySize(p.DatabaseDir), p.DatabaseDir),
        };

        return new
        {
            items = rows.Select(r => new { key = r.key, label = r.label, bytes = r.bytes, path = r.path }).ToList(),
            totalBytes = rows.Sum(r => r.bytes),
            freeBytes = FreeDisk(p.DataRoot),
        };
    }

    private static object ClearStorage(AppServices s, JsonNode? p)
    {
        var key = p?.Str("key") ?? "";
        var allowed = new HashSet<string> { "cache", "downloads", "logs", "backups" };
        if (!allowed.Contains(key))
            throw new LauncherException("Эту категорию нельзя очищать автоматически",
                "Сборки, Java и база данных не удаляются через очистку хранилища");

        var dir = key switch
        {
            "cache" => s.Paths.CacheDir,
            "downloads" => s.Paths.DownloadsDir,
            "logs" => s.Paths.LogsDir,
            "backups" => s.Paths.BackupsDir,
            _ => throw new LauncherException("Неизвестная категория", key),
        };
        if (key == "backups")
            Database.DatabaseService.Execute("DELETE FROM backups");

        long freed = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { var fi = new FileInfo(f); freed += fi.Length; fi.Delete(); } catch { /* ignore */ }
        }
        // WebView2 живёт в кэше — не трогаем его
        AppPathsNoCreate(s.Paths, dir);
        Log.Info($"Очистка '{key}': освобождено {freed} байт");
        return new { freedBytes = freed };
    }

    private static void AppPathsNoCreate(AppPaths p, string dir)
    {
        foreach (var d in new[] { p.TempDir, Path.Combine(p.CacheDir, "webview2"), Path.Combine(p.CacheDir, "images"), Path.Combine(p.CacheDir, "modrinth") })
            if (d.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                try { Directory.CreateDirectory(d); } catch { /* ignore */ }
    }

    /* ---------------------------------------------------------- обновления */
    public sealed record UpdateInfo(string Current, string? Latest, string? Url, string? Notes, bool UpdateAvailable, bool SourceConfigured);

    public static Task<UpdateInfo> CheckForUpdates(AppServices s) => CheckUpdateAsync(s, CancellationToken.None);

    private static async Task<UpdateInfo> CheckUpdateAsync(AppServices s, CancellationToken ct)
    {
        var url = s.Settings.Get("updateManifestUrl", "");
        var current = typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        if (string.IsNullOrWhiteSpace(url))
            return new UpdateInfo(current, null, null, null, false, false);

        try
        {
            using var http = HttpFactory.Create(s);
            var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
            var node = JsonNode.Parse(json);
            var latest = node?["version"]?.GetValue<string>() ?? "";
            var notes = node?["notes"]?.GetValue<string>();
            var download = node?["url"]?.GetValue<string>();
            var available = IsNewer(latest, current);
            return new UpdateInfo(current, latest, download, notes, available, true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Проверка обновлений не удалась: {ex.Message}");
            return new UpdateInfo(current, null, null, null, false, true);
        }
    }

    private static bool IsNewer(string remote, string local)
    {
        static int[] parse(string v) => v.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(x =>
        {
            var d = new string(x.TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(d, out var n) ? n : 0;
        }).ToArray();
        var a = parse(remote); var b = parse(local);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0; var y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }
}
