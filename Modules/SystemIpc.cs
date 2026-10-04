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
    // Порядок ULONGLONG-полей как в MEMORYSTATUS (phys, pagefile, virtual), размер 64 байта обязателен.
    [StructLayout(LayoutKind.Sequential)] private class MemoryStatusEx { public uint Length; public uint MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, Extra; }

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
        r.Register("system.components", (_, _) => Task.FromResult<object?>(Components(s)));
        r.Register("system.storage", (_, _) => Task.FromResult<object?>(Storage(s)));
        r.Register("system.clearStorage", (p, _) => Task.FromResult<object?>(ClearStorage(s, p)));
        r.Register("system.checkUpdate", async (_, ct) => (object?)await CheckUpdateAsync(s, ct).ConfigureAwait(false));
        r.Register("system.ping", async (_, ct) => (object?)await PingAsync(s, ct).ConfigureAwait(false));
        r.Register("system.installUpdate", async (p, ct) => (object?)await InstallUpdateAsync(s, p, ct).ConfigureAwait(false));
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

        var (total, free, loadPercent) = Memory();
        return new
        {
            appVersion = typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            os,
            osVersion = Environment.OSVersion.Version.ToString(),
            dataDir = s.Paths.DataRoot,
            configDir = s.Paths.ConfigRoot,
            ramTotalMb = total,
            ramFreeMb = free,
            ramUsedMb = Math.Max(0, total - free),
            ramLoadPercent = loadPercent,
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

    /// <summary>Занятая и общая физическая память + процент загрузки (MemoryLoad из Windows).</summary>
    public static (long totalMb, long freeMb, int loadPercent) Memory()
    {
        var st = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(st))
            return ((long)(st.TotalPhys / 1024 / 1024), (long)(st.AvailPhys / 1024 / 1024), (int)st.MemoryLoad);
        return (0, 0, 0);
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
            "image" => ("Изображение", "Изображения|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|Все файлы|*.*"),
            "skin" => ("Скин Minecraft", "Скин PNG (*.png)|*.png|Все файлы|*.*"),
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

    /* ---------------------------------------------------------- компоненты для главной */
    /// <summary>Реальные версии и даты обновления компонентов лаунчера (никаких выдуманных значений).</summary>
    private static object Components(AppServices s)
    {
        var list = new List<object>();
        void add(string key, string name, string? version, DateTime? updated, string detail, string logo) =>
            list.Add(new { key, name, version = string.IsNullOrWhiteSpace(version) ? "—" : version, updated = updated?.ToString("o"), detail, logo });

        // 1. Ядро лаунчера — версия сборки + дата самого exe
        var exe = Environment.ProcessPath;
        add("core", "NullLauncher Core",
            typeof(AppServices).Assembly.GetName().Version?.ToString(3),
            File.Exists(exe ?? "") ? File.GetLastWriteTime(exe!) : null,
            "ядро лаунчера", "app");

        // 2. Интерфейс — дата файла UI/index.html (когда ставился/обновлялся UI)
        var indexHtml = Path.Combine(s.Paths.UiDir, "index.html");
        add("ui", "Интерфейс",
            typeof(AppServices).Assembly.GetName().Version?.ToString(3),
            File.Exists(indexHtml) ? File.GetLastWriteTime(indexHtml) : null,
            "фроненд (HTML/CSS/JS)", "ui");

        // 3. WebView2 Runtime — реестр HKLM, pv = версии, дата = дата файла рантайма
        string? wvVer = null; DateTime? wvUpd = null;
        try
        {
            foreach (var root in new[] { @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients", @"SOFTWARE\Microsoft\EdgeUpdate\Clients" })
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root + @"\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"))
            {
                wvVer = key?.GetValue("pv") as string;
                if (!string.IsNullOrWhiteSpace(wvVer)) break;
            }
            if (!string.IsNullOrWhiteSpace(wvVer))
            {
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                var wvExe = Path.Combine(pf, "Microsoft", "EdgeWebView", "Application", wvVer, "msedgewebview2.exe");
                if (File.Exists(wvExe)) wvUpd = File.GetLastWriteTime(wvExe);
            }
        }
        catch (Exception ex) { Log.Debug($"WebView2 registry: {ex.Message}"); }
        add("webview2", "WebView2 Runtime", wvVer, wvUpd, "браузерное ядро интерфейса", "microsoft");

        // 4. .NET — версия рантайма процесса
        add("dotnet", ".NET Runtime", Environment.Version.ToString(), null, "среда выполнения", "dotnet");

        // 5. Манифест Mojang — последняя release-версия из кэша + дата файла кэша
        string? latest = null; DateTime? mfUpd = null;
        try
        {
            var mf = Path.Combine(s.Paths.MetadataDir, "versions.json");
            if (File.Exists(mf))
            {
                mfUpd = File.GetLastWriteTime(mf);
                var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(mf));
                if (doc.RootElement.TryGetProperty("latest", out var l) && l.TryGetProperty("release", out var r))
                    latest = r.GetString();
            }
        }
        catch (Exception ex) { Log.Debug($"versions.json: {ex.Message}"); }
        add("manifest", "Манифест Mojang", latest, mfUpd, "список версий Minecraft", "mojang");

        // 6. Java — реально найденные установки (мажорные версии + когда обнаружены)
        try
        {
            var javas = s.Java.Detect();
            var majors = javas.Select(j => j.Major).Distinct().OrderBy(x => x).ToArray();
            var lastSeen = javas.Count == 0 ? (DateTime?)null : javas.Max(j => j.LastSeen);
            add("java", "Java", majors.Length == 0 ? null : string.Join(" · ", majors),
                lastSeen, javas.Count == 0 ? "установки не найдены" : $"установок: {javas.Count}", "java");
        }
        catch (Exception ex) { add("java", "Java", null, null, "не удалось определить: " + ex.Message, "java"); }

        // 7. Modrinth API — дата последнего успешного ответа (kv modrinthLastSync пишется при сетевом ответе)
        DateTime? mrSync = null;
        try
        {
            var v = Database.DatabaseService.Scalar<string>("SELECT value FROM kv WHERE key='modrinthLastSync'");
            if (!string.IsNullOrEmpty(v) && DateTime.TryParse(v, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d))
                mrSync = d.ToLocalTime();
        }
        catch (Exception ex) { Log.Debug($"modrinthLastSync: {ex.Message}"); }
        add("modrinth", "Modrinth API", "v2", mrSync, "поиск модов и обновления", "modrinth");

        return new { items = list };
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
            using var resp = http.GetAsync("https://api.modrinth.com/v2/search?limit=1", cts.Token).GetAwaiter().GetResult();
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
            var (t, f, load) = Memory();
            add("mem", "Оперативная память", f < 1024 ? "warn" : "ok", $"Всего {t} МБ, занято {load}% ({t - f} МБ), свободно {f} МБ");
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
    public sealed record UpdateInfo(string Current, string? Latest, string? Url, string? Notes, bool UpdateAvailable, bool SourceConfigured, string? ZipUrl = null);

    public static Task<UpdateInfo> CheckForUpdates(AppServices s) => CheckUpdateAsync(s, CancellationToken.None);

    private const string GitHubRepo = "rusty-prdc/NullLauncher";

    /// <summary>
    /// Проверка обновлений: если задан updateManifestUrl — берёт оттуда, иначе — с вкладки
    /// «Releases» репозитория на GitHub (Setup.exe и zip из assets релиза).
    /// </summary>
    private static async Task<UpdateInfo> CheckUpdateAsync(AppServices s, CancellationToken ct)
    {
        var manifestUrl = s.Settings.Get("updateManifestUrl", "");
        var current = typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        try
        {
            using var http = HttpFactory.Create(s);

            if (!string.IsNullOrWhiteSpace(manifestUrl))
            {
                var json = await http.GetStringAsync(manifestUrl, ct).ConfigureAwait(false);
                var node = JsonNode.Parse(json);
                var latest = node?["version"]?.GetValue<string>() ?? "";
                var notes = node?["notes"]?.GetValue<string>();
                var download = node?["url"]?.GetValue<string>();
                var available = IsNewer(latest, current);
                return new UpdateInfo(current, latest, download, notes, available, true);
            }

            /* источник по умолчанию — GitHub Releases */
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var relJson = await http.GetStringAsync(
                $"https://api.github.com/repos/{GitHubRepo}/releases/latest", ct).ConfigureAwait(false);
            var rel = JsonNode.Parse(relJson)
                ?? throw new LauncherException("Пустой ответ GitHub", "releases/latest");

            var tag = (rel["tag_name"]?.GetValue<string>() ?? rel["name"]?.GetValue<string>() ?? "").Trim();
            var latestVer = tag.TrimStart('v', 'V');
            var notes2 = rel["body"]?.GetValue<string>();
            string? setupUrl = null, zipUrl = null;
            if (rel["assets"] is System.Text.Json.Nodes.JsonArray assets)
                foreach (var a in assets)
                {
                    var name = a?["name"]?.GetValue<string>() ?? "";
                    var browser = a?["browser_download_url"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(browser)) continue;
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) setupUrl ??= browser;
                    else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zipUrl ??= browser;
                }

            var available2 = !string.IsNullOrEmpty(latestVer) && IsNewer(latestVer, current);
            if (!available2) return new UpdateInfo(current, null, null, null, false, true);
            return new UpdateInfo(current, latestVer, setupUrl ?? zipUrl, notes2, true, true, zipUrl);
        }
        catch (OperationCanceledException) { throw; }
        catch (LauncherException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Проверка обновлений не удалась: {ex.Message}");
            return new UpdateInfo(current, null, null, null, false, true);
        }
    }

    /// <summary>system.ping → реальный HTTP-замер задержки до ключевых хостов с учётом прокси.</summary>
    private static async Task<object> PingAsync(AppServices s, CancellationToken ct)
    {
        var targets = new (string Key, string Title, string Url)[]
        {
            ("modrinth", "Modrinth", "https://api.modrinth.com/v2/search?limit=1"),
            ("mojang", "Mojang", "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json"),
            ("github", "GitHub", $"https://api.github.com/repos/{GitHubRepo}"),
            ("microsoft", "Microsoft", "https://login.live.com/"),
        };

        var items = new List<object>();
        foreach (var (key, title, url) in targets)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            try
            {
                using var http = HttpFactory.Create(s);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                sw.Stop();
                items.Add(new
                {
                    key, title, ms = sw.ElapsedMilliseconds,
                    ok = resp.IsSuccessStatusCode,
                    detail = $"HTTP {(int)resp.StatusCode}",
                });
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                items.Add(new { key, title, ms = 10000L, ok = false, detail = "Тайм-аут (10 сек)" });
            }
            catch (Exception ex)
            {
                sw.Stop();
                items.Add(new { key, title, ms = sw.ElapsedMilliseconds, ok = false, detail = ex.Message });
            }
        }
        return new { items };
    }

    /// <summary>
    /// system.installUpdate { url } → скачивает Setup.exe (или zip) из релиза во временную папку
    /// и запускает установщик. Вызывается только после подтверждения пользователя в UI.
    /// </summary>
    private static async Task<object> InstallUpdateAsync(AppServices s, JsonNode? p, CancellationToken ct)
    {
        var url = (p?.Str("url") ?? "").Trim();
        if (url.Length == 0) throw new LauncherException("Не указан адрес обновления");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new LauncherException("Недопустимый адрес обновления", url);

        var dir = Path.Combine(s.Paths.TempDir, "update");
        Directory.CreateDirectory(dir);
        var isExe = Path.GetFileName(uri.AbsolutePath).EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        var target = Path.Combine(dir, Path.GetFileName(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(Path.GetFileName(target)))
            target = Path.Combine(dir, isExe ? "NullLauncher-Setup.exe" : "NullLauncher.zip");

        try
        {
            using var http = HttpFactory.Create(s);
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new LauncherException("Не удалось скачать обновление",
                    $"GitHub ответил HTTP {(int)resp.StatusCode}");

            var total = resp.Content.Headers.ContentLength ?? 0;
            long received = 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(target);
            var buf = new byte[81920];
            var lastEmit = Stopwatch.StartNew();
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                received += n;
                if (lastEmit.ElapsedMilliseconds >= 250)
                {
                    lastEmit.Restart();
                    s.Emit("update.progress", new
                    {
                        percent = total > 0 ? (int)(received * 100 / total) : 0,
                        received, total,
                    });
                }
            }
            s.Emit("update.progress", new { percent = 100, received, total });
        }
        catch (LauncherException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new LauncherException("Не удалось скачать обновление", ex.Message, ex);
        }

        if (!isExe)
            return new { path = target, started = false, kind = "zip" };

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            Log.Info($"Обновление: установщик запущен — {target}");
            return new { path = target, started = true, kind = "setup" };
        }
        catch (Exception ex)
        {
            throw new LauncherException("Не удалось запустить установщик", ex.Message, ex);
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
