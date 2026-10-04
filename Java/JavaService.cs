using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NullLauncher.Java;

public sealed class JavaInstallation
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Version { get; set; } = "";
    public int Major { get; set; }
    public string Arch { get; set; } = "x64";
    public string Vendor { get; set; } = "";
    public string Source { get; set; } = "system";
    public bool Valid { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public bool Is64 { get; set; } = true;
}

/// <summary>
/// Менеджер Java: обнаружение установленных JDK/JRE, проверка работоспособности,
/// подбор версии под Minecraft и автоматическая установка Temurin (Adoptium API).
/// </summary>
public sealed class JavaService
{
    private readonly Core.AppPaths _paths;
    private readonly Core.SettingsService _settings;
    private readonly List<JavaInstallation> _cache = new();
    private DateTime _cacheTime = DateTime.MinValue;
    private readonly object _sync = new();

    public JavaService(Core.AppPaths paths, Core.SettingsService settings)
    {
        _paths = paths;
        _settings = settings;
    }

    /* ------------------------------------------------------------ поиск */
    private static readonly string[] KnownRoots =
    {
        @"%ProgramFiles%\Java",
        @"%ProgramFiles%\Eclipse Adoptium",
        @"%ProgramFiles%\Eclipse Foundation",
        @"%ProgramFiles%\Microsoft",
        @"%ProgramFiles%\Amazon Corretto",
        @"%ProgramFiles%\Zulu",
        @"%ProgramFiles%\BellSoft",
        @"%ProgramFiles%\SapMachine",
        @"%ProgramFiles%\Red Hat",
        @"%ProgramFiles(x86)%\Java",
        @"%LocalAppData%\Programs\Eclipse Adoptium",
        @"%USERPROFILE%\.jdks",
    };

    public static (string path, string version)? FindAnyJava(Core.AppPaths paths)
    {
        foreach (var candidate in EnumerateCandidates(paths))
        {
            var info = Probe(candidate);
            if (info is { Valid: true }) return (info.Path, info.Version);
        }
        return null;
    }

    private static IEnumerable<string> EnumerateCandidates(Core.AppPaths paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void Add(string? exe)
        {
            if (string.IsNullOrWhiteSpace(exe)) return;
            if (!exe.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase))
                exe = Path.Combine(exe, "bin", "java.exe");
            try
            {
                if (File.Exists(exe))
                {
                    var full = Path.GetFullPath(exe);
                    if (seen.Add(full)) result.Add(full);
                }
            }
            catch { /* невалидный путь — пропускаем */ }
        }

        // JAVA_HOME
        Add(Path.Combine(Environment.GetEnvironmentVariable("JAVA_HOME") ?? "", "bin", "java.exe"));

        // PATH
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            Add(Path.Combine(dir.Trim(), "java.exe"));

        // стандартные каталоги
        foreach (var rootPattern in KnownRoots)
        {
            var root = Environment.ExpandEnvironmentVariables(rootPattern);
            if (!Directory.Exists(root)) continue;
            Add(Path.Combine(root, "bin", "java.exe"));
            foreach (var sub in SafeEnumDirs(root))
                Add(Path.Combine(sub, "bin", "java.exe"));
        }

        // установленные нами: архив Temurin распаковывается с вложенной папкой
        // (java\temurin-25\jdk-25.0.4.1+1-jre\bin\java.exe), поэтому ищем и на уровень глубже
        if (Directory.Exists(paths.JavaDir))
        {
            foreach (var sub in SafeEnumDirs(paths.JavaDir))
                Add(Path.Combine(sub, "bin", "java.exe"));
            foreach (var exe in SafeEnumFiles(paths.JavaDir, "java.exe"))
                Add(exe);
        }

        return result;
    }

    private static IEnumerable<string> SafeEnumFiles(string root, string name)
    {
        try { return Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeEnumDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    /* ------------------------------------------------------------ проверка */
    public static JavaInstallation? Probe(string javaExe)
    {
        if (!File.Exists(javaExe)) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "-version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var stderr = p.StandardError.ReadToEnd();
            var stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return null; }
            var text = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;

            var (version, major) = ParseVersion(text);
            if (major == 0) return null;

            return new JavaInstallation
            {
                Id = HashId(javaExe),
                Path = javaExe,
                Version = version,
                Major = major,
                Arch = ReadArch(javaExe),
                Vendor = DetectVendor(javaExe, text),
                Source = DetectSource(javaExe),
                Valid = true,
                Is64 = ReadArch(javaExe) != "x86",
            };
        }
        catch (Exception ex)
        {
            Log.Debug($"Java probe {javaExe}: {ex.Message}");
            return null;
        }
    }

    public static (string version, int major) ParseVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ("", 0);
        // openjdk 21.0.4  /  java version "17.0.1"  /  java version "1.8.0_391"
        var m = Regex.Match(text, @"version\s+""?(?<v>[0-9]+(?:\.[0-9]+)+(?:_[0-9]+)?)""?", RegexOptions.IgnoreCase);
        if (!m.Success) return ("", 0);
        var v = m.Groups["v"].Value;
        var head = v.Split('.', '_', '-')[0];
        var major = int.TryParse(head, out var mi) ? mi : 0;
        if (major == 1)
        {
            var second = v.Split('.')[1];
            major = int.TryParse(second, out var s) ? s : 8;
        }
        return (v, major);
    }

    private static string ReadArch(string exe)
    {
        try
        {
            using var fs = File.OpenRead(exe);
            var br = new BinaryReader(fs);
            fs.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = br.ReadInt32();
            fs.Seek(peOffset + 4, SeekOrigin.Begin);
            var machine = br.ReadUInt16();
            return machine switch
            {
                0x8664 => "x64",
                0xAA64 => "ARM64",
                0x014C => "x86",
                _ => "unknown",
            };
        }
        catch { return "unknown"; }
    }

    private static string DetectVendor(string exe, string text)
    {
        var dir = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(exe)!) ?? "").ToLowerInvariant();
        if (dir.Contains("adoptium") || dir.Contains("temurin")) return "Eclipse Temurin";
        if (dir.Contains("microsoft")) return "Microsoft";
        if (dir.Contains("corretto")) return "Amazon Corretto";
        if (dir.Contains("zulu")) return "Azul Zulu";
        if (dir.Contains("bellsoft") || dir.Contains("liberica")) return "BellSoft Liberica";
        if (dir.Contains("sapmachine")) return "SapMachine";
        if (dir.Contains("red hat") || dir.Contains("openjdk")) return "Red Hat";
        if (dir.Contains(@"\java\")) return "Oracle/legacy";
        if (text.Contains("OpenJDK", StringComparison.OrdinalIgnoreCase)) return "OpenJDK";
        return "Java";
    }

    private static string DetectSource(string exe)
    {
        var full = exe.ToLowerInvariant();
        if (full.Contains(@"\nulllauncher\") && full.Contains(@"\java\")) return "launcher";
        if (full.Contains("adoptium") || full.Contains("temurin")) return "adoptium";
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        return pathVar.Split(Path.PathSeparator).Any(d => full.StartsWith(Path.Combine(d.Trim(), "").ToLowerInvariant())) ? "path" : "system";
    }

    public static string HashId(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..16].ToLowerInvariant();

    /* ------------------------------------------------------------ список */
    public List<JavaInstallation> Detect(bool force = false)
    {
        lock (_sync)
        {
            if (!force && _cache.Count > 0 && (DateTime.UtcNow - _cacheTime).TotalMinutes < 5)
                return _cache.OrderBy(j => j.Major).ThenBy(j => j.Source).ToList();
        }

        var found = new List<JavaInstallation>();
        foreach (var exe in EnumerateCandidates(_paths).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var inst = Probe(exe);
            if (inst is { Valid: true } && found.All(f => f.Path != inst.Path)) found.Add(inst);
        }

        // синхронизация с БД
        try
        {
            Database.DatabaseService.Execute("DELETE FROM java_installations");
            foreach (var j in found)
                Database.DatabaseService.Execute("""
                    INSERT OR REPLACE INTO java_installations (id, path, version, major, arch, vendor, source, valid, last_seen)
                    VALUES (@Id, @Path, @Version, @Major, @Arch, @Vendor, @Source, @Valid, @LastSeen)
                    """, j);
        }
        catch (Exception ex) { Log.Warn($"Не удалось обновить таблицу java: {ex.Message}"); }

        lock (_sync)
        {
            _cache.Clear();
            _cache.AddRange(found);
            _cacheTime = DateTime.UtcNow;
        }
        Log.Info($"Найдено Java: {found.Count} ({string.Join(", ", found.Select(f => f.Major).Distinct())})");
        return found.OrderBy(j => j.Major).ThenBy(j => j.Source).ToList();
    }

    public JavaInstallation? Validate(string path) => Probe(path);

    /* ------------------------------------------------------------ подбор */
    /// <summary>Требуемая версия Java по версии Minecraft (данные Mojang).</summary>
    public static int RequiredMajor(string mcVersion)
    {
        if (string.IsNullOrWhiteSpace(mcVersion)) return 21;
        var m = Regex.Match(mcVersion.Trim(), @"^(\d+)(?:\.(\d+))?");
        // alpha/beta/rd/inf — старые версии, им нужна Java 8
        if (!m.Success) return 8;
        if (!int.TryParse(m.Groups[1].Value, out var major)) return 21;
        var minor = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var mi) ? mi : 0;

        if (major == 1)
        {
            if (minor <= 16) return 8;
            if (minor == 17) return 16;
            if (minor <= 20) return 17;
            return 21; // 1.20.5 … 1.21.x
        }

        // Новая схема нумерации Mojang (26.1, 26.3, 26.4-snapshot-2): начиная с неё нужна Java 25.
        // Раньше здесь всегда возвращалось 21, из-за чего 26.3 запускался на Java 21 и падал.
        return 25;
    }

    public sealed record Recommendation(int Major, string? Path, string Reason, List<JavaInstallation> Candidates);

    public Recommendation Recommend(string mcVersion, string? javaMode, int? javaMajor, string? javaPath,
        int? requiredOverride = null)
    {
        var all = Detect();
        var required = requiredOverride is int ro && ro > 0 ? ro : RequiredMajor(mcVersion);

        if (javaMode == "path" && !string.IsNullOrWhiteSpace(javaPath))
        {
            var probe = Probe(javaPath);
            if (probe is null)
                throw new Core.LauncherException("Указанная Java не работает", javaPath);
            return new Recommendation(probe.Major, javaPath, "Выбран пользовательский путь", all);
        }
        if (javaMode == "major" && javaMajor is int mm)
        {
            var exact = all.FirstOrDefault(j => j.Major == mm && j.Valid);
            return new Recommendation(mm, exact?.Path, exact is null
                ? $"Java {mm} выбрана вручную, но не найдена в системе"
                : $"Java {mm} выбрана вручную", all);
        }

        // auto: точное совпадение → ближайшая новее → самая новая из найденных (с пометкой, что она старее нужной)
        var candidates = all.Where(j => j.Valid).OrderBy(j => j.Major).ToList();
        var exactRequired = candidates.FirstOrDefault(j => j.Major == required);
        if (exactRequired is not null)
            return new Recommendation(required, exactRequired.Path, $"Minecraft {mcVersion} требует Java {required}", all);

        var newer = candidates.FirstOrDefault(j => j.Major > required);
        if (newer is not null)
            return new Recommendation(newer.Major, newer.Path,
                $"Подходит Java {newer.Major} (для Minecraft {mcVersion} рекомендована {required})", all);

        var any = candidates.LastOrDefault();
        return new Recommendation(any?.Major ?? required, any?.Path,
            any is null
                ? $"Java {required} не найдена — нужна установка"
                : $"Для Minecraft {mcVersion} нужна Java {required}, а самая новая в системе — Java {any.Major}",
            all);
    }

    /* ------------------------------------------------------------ установка */
    public sealed record InstallResult(string Path, string Version, int Major);

    /// <summary>Скачивает Temurin JRE нужной major-версии через Adoptium API и ставит в java/.</summary>
    public async Task<InstallResult> InstallAsync(int major, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (major is not (8 or 11 or 16 or 17 or 18 or 21 or 22 or 23 or 25))
            throw new Core.LauncherException("Неподдерживаемая версия Java", major.ToString());

        progress?.Report(0.01);
        using var http = HttpFactory.Get(_services
            ?? throw new Core.LauncherException("Java-сервис не инициализирован"));
        var apiUrl = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?os=windows&architecture=x64&image_type=jre";

        string json;
        try { json = await http.GetStringAsync(apiUrl, ct).ConfigureAwait(false); }
        catch (Exception ex) { throw Core.LauncherException.Wrap(ex, "Adoptium API недоступен"); }

        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            throw new Core.LauncherException("Adoptium не нашёл сборку Java", $"major={major}");
        var first = doc.RootElement[0];
        var bin = first.GetProperty("binary");
        var pkg = bin.GetProperty("package");
        var link = pkg.GetProperty("link").GetString()!;
        var version = pkg.GetProperty("name").GetString() ?? $"temurin-{major}";
        var checksum = pkg.TryGetProperty("checksum", out var cs) ? cs.GetString() : null;

        var tmpZip = Path.Combine(_paths.TempDir, $"java-{major}-{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(_paths.TempDir);
        var targetRoot = Path.Combine(_paths.JavaDir, $"temurin-{major}");

        try
        {
            // скачивание с прогрессом
            progress?.Report(0.05);
            using var resp = await http.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fs = File.Create(tmpZip);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0) progress?.Report(0.05 + 0.75 * read / total);
            }
            fs.Close();

            if (!string.IsNullOrEmpty(checksum) && _settings.Get("verifyHashes", true))
            {
                var bytes = await File.ReadAllBytesAsync(tmpZip, ct).ConfigureAwait(false);
                var actual = Convert.ToHexString(
                    await System.Security.Cryptography.SHA256.HashDataAsync(
                        new MemoryStream(bytes), ct).ConfigureAwait(false)).ToLowerInvariant();
                if (!string.Equals(actual, checksum, StringComparison.OrdinalIgnoreCase))
                    throw new Core.LauncherException("Контрольная сумма Java не совпала", $"{actual} != {checksum}");
            }

            progress?.Report(0.85);
            if (Directory.Exists(targetRoot)) Directory.Delete(targetRoot, true);
            Directory.CreateDirectory(targetRoot);
            ZipFile.ExtractToDirectory(tmpZip, targetRoot, true);

            var javaExe = Directory.EnumerateFiles(targetRoot, "java.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (javaExe is null) throw new Core.LauncherException("В архиве Java нет java.exe", link);

            // верхний уровень: bin/java.exe
            var probe = Probe(javaExe);
            if (probe is null) throw new Core.LauncherException("Скачанная Java не запускается", javaExe);

            progress?.Report(1);
            Log.Info($"Установлена Java {probe.Version} → {probe.Path}");
            Detect(force: true);
            return new InstallResult(probe.Path, probe.Version, probe.Major);
        }
        catch (Exception ex) when (ex is not Core.LauncherException)
        {
            throw Core.LauncherException.Wrap(ex, "Не удалось установить Java");
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { /* ignore */ }
        }
    }

    public void Remove(string id)
    {
        var all = Detect();
        var j = all.FirstOrDefault(x => x.Id == id)
                ?? throw new Core.LauncherException("Установка Java не найдена", id);
        if (j.Source != "launcher")
            throw new Core.LauncherException("Можно удалять только Java, установленную NullLauncher",
                $"{j.Path} ({j.Source})");
        var dir = Path.GetDirectoryName(Path.GetDirectoryName(j.Path)!);
        if (dir is not null && dir.StartsWith(_paths.JavaDir, StringComparison.OrdinalIgnoreCase))
            Directory.Delete(dir, true);
        Detect(force: true);
        Log.Info($"Java удалена: {dir}");
    }

    /// <summary>Сервис используется установкой Java — связывается с контейнером.</summary>
    private static Core.AppServices? _services;
    public static void Bind(Core.AppServices services) => _services = services;
}
