using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NullLauncher.Minecraft;

/// <summary>
/// Установка модлоадеров. Fabric и Quilt ставятся через официальные meta-профили (готовый version JSON),
/// Forge и NeoForge — запуском их официальных инсталляторов. Файлы складываются в общий каталог
/// версий/библиотек, инстансы их только используют.
/// </summary>
public static class LoaderInstaller
{
    public static async Task<string> EnsureAsync(
        Core.AppPaths paths, Versions.VersionService versions, Downloads.DownloadService downloads,
        string mc, string loader, string? loaderVersion, CancellationToken ct)
    {
        loader = loader.ToLowerInvariant();
        switch (loader)
        {
            case "vanilla":
                return mc;
            case "fabric":
                return await EnsureFabricAsync(paths, versions, downloads, mc, loaderVersion, ct).ConfigureAwait(false);
            case "quilt":
                return await EnsureQuiltAsync(paths, versions, downloads, mc, loaderVersion, ct).ConfigureAwait(false);
            case "forge":
                return await EnsureForgeAsync(paths, versions, downloads, mc, loaderVersion, ct).ConfigureAwait(false);
            case "neoforge":
                return await EnsureNeoForgeAsync(paths, versions, downloads, mc, loaderVersion, ct).ConfigureAwait(false);
            default:
                throw new LauncherException("Неизвестный загрузчик", loader);
        }
    }

    /* ------------------------------------------------------------ Fabric */

    private static async Task<string> EnsureFabricAsync(
        Core.AppPaths paths, Versions.VersionService versions, Downloads.DownloadService downloads,
        string mc, string? loaderVersion, CancellationToken ct)
    {
        loaderVersion ??= await LatestFabricLoaderAsync(versions, mc, ct).ConfigureAwait(false);
        var id = $"fabric-loader-{loaderVersion}-{mc}";
        if (File.Exists(versions.VersionJsonPath(id))) return id;

        var url = $"https://meta.fabricmc.net/v2/versions/loader/{mc}/{loaderVersion}/profile/json";
        using var http = HttpFactory.Get(AppServices.Current!);
        string json;
        try { json = await http.GetStringAsync(url, ct).ConfigureAwait(false); }
        catch (Exception ex) { throw new LauncherException("Не удалось получить профиль Fabric",
            $"meta.fabricmc.net недоступен для {mc}/{loaderVersion}", ex); }

        // id в профиле может отличаться — переписываем на предсказуемый
        var node = JsonNode.Parse(json) as JsonObject
                   ?? throw new LauncherException("Некорректный профиль Fabric", url);
        node["id"] = id;
        node["inheritsFrom"] = mc;

        var path = versions.VersionJsonPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct)
            .ConfigureAwait(false);
        Log.Info($"Fabric {loaderVersion} для {mc} → {id}");
        return id;
    }

    private static async Task<string> LatestFabricLoaderAsync(Versions.VersionService versions, string mc, CancellationToken ct)
    {
        using var http = HttpFactory.Get(AppServices.Current!);
        var json = await http.GetStringAsync($"https://meta.fabricmc.net/v2/versions/loader/{mc}", ct)
            .ConfigureAwait(false);
        var arr = JsonNode.Parse(json)?.AsArray();
        var first = arr?.FirstOrDefault()?["loader"]?["version"]?.GetValue<string>()
                    ?? throw new LauncherException("Fabric не поддерживает эту версию Minecraft", mc);
        return first;
    }

    /* ------------------------------------------------------------ Quilt */

    private static async Task<string> EnsureQuiltAsync(
        Core.AppPaths paths, Versions.VersionService versions, Downloads.DownloadService downloads,
        string mc, string? loaderVersion, CancellationToken ct)
    {
        using var http = HttpFactory.Get(AppServices.Current!);
        if (string.IsNullOrWhiteSpace(loaderVersion))
        {
            var json = await http.GetStringAsync($"https://meta.quiltmc.org/v3/versions/loader/{mc}", ct)
                .ConfigureAwait(false);
            loaderVersion = JsonNode.Parse(json)?.AsArray().FirstOrDefault()?["loader"]?["version"]?.GetValue<string>()
                            ?? throw new LauncherException("Quilt не поддерживает эту версию Minecraft", mc);
        }

        var id = $"quilt-loader-{loaderVersion}-{mc}";
        if (File.Exists(versions.VersionJsonPath(id))) return id;

        var url = $"https://meta.quiltmc.org/v3/versions/loader/{mc}/{loaderVersion}/profile/json";
        string json2;
        try { json2 = await http.GetStringAsync(url, ct).ConfigureAwait(false); }
        catch (Exception ex) { throw new LauncherException("Не удалось получить профиль Quilt",
            $"quiltmeta недоступен для {mc}/{loaderVersion}", ex); }

        var node = JsonNode.Parse(json2) as JsonObject
                   ?? throw new LauncherException("Некорректный профиль Quilt", url);
        node["id"] = id;
        node["inheritsFrom"] = mc;

        var path = versions.VersionJsonPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct)
            .ConfigureAwait(false);
        Log.Info($"Quilt {loaderVersion} для {mc} → {id}");
        return id;
    }

    /* ------------------------------------------------------------ Forge */

    private static async Task<string> EnsureForgeAsync(
        Core.AppPaths paths, Versions.VersionService versions, Downloads.DownloadService downloads,
        string mc, string? forgeVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(forgeVersion))
        {
            var loaders = await versions.LoadersAsync(mc, ct).ConfigureAwait(false);
            forgeVersion = loaders
                .Where(l => (string)l.GetType().GetProperty("loader")!.GetValue(l)! == "forge")
                .Select(l => (string)l.GetType().GetProperty("version")!.GetValue(l)!)
                .FirstOrDefault()
                ?? throw new LauncherException("Для этой версии Minecraft нет Forge", mc);
        }

        var id = $"{mc}-{forgeVersion}";
        if (File.Exists(versions.VersionJsonPath(id))) return id;

        var installerUrl =
            $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mc}-{forgeVersion}/forge-{mc}-{forgeVersion}-installer.jar";
        return await RunOfficialInstallerAsync(paths, versions, downloads,
            installerUrl, id, mc, ct).ConfigureAwait(false);
    }

    /* ------------------------------------------------------------ NeoForge */

    private static async Task<string> EnsureNeoForgeAsync(
        Core.AppPaths paths, Versions.VersionService versions, Downloads.DownloadService downloads,
        string mc, string? neoVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(neoVersion))
        {
            var loaders = await versions.LoadersAsync(mc, ct).ConfigureAwait(false);
            neoVersion = loaders
                .Where(l => (string)l.GetType().GetProperty("loader")!.GetValue(l)! == "neoforge")
                .Select(l => (string)l.GetType().GetProperty("version")!.GetValue(l)!)
                .FirstOrDefault()
                ?? throw new LauncherException("Для этой версии Minecraft нет NeoForge", mc);
        }

        var id = $"neoforge-{neoVersion}";
        if (File.Exists(versions.VersionJsonPath(id))) return id;

        var installerUrl =
            $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{neoVersion}/neoforge-{neoVersion}-installer.jar";
        return await RunOfficialInstallerAsync(paths, versions, downloads,
            installerUrl, id, mc, ct).ConfigureAwait(false);
    }

    /* ------------------------------------------------------------ общий прогон инсталлятора */

    /// <summary>
    /// Скачивает официальный installer.jar и запускает его в временной рабочей папке,
    /// затем забирает созданные version JSON и библиотеки в общие каталоги лаунчера.
    /// </summary>
    private static async Task<string> RunOfficialInstallerAsync(
        Core.AppPaths paths, Versions.VersionService versions, Downloads.DownloadService downloads,
        string installerUrl, string expectedId, string mcVersion, CancellationToken ct)
    {
        var work = Path.Combine(paths.TempDir, "installer-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var installerJar = Path.Combine(work, "installer.jar");
            await downloads.DownloadAsync(installerUrl, installerJar, Path.GetFileName(installerJar),
                kind: "installer", ct: ct).ConfigureAwait(false);

            // официальный инсталлятор Forge/NeoForge требует launcher_profiles.json в целевой папке,
            // иначе падает с «There is no minecraft launcher profile in ... you need to run the launcher first!»
            var profilesFile = Path.Combine(work, "launcher_profiles.json");
            if (!File.Exists(profilesFile))
                await File.WriteAllTextAsync(profilesFile,
                    "{\"profiles\":{},\"settings\":{},\"version\":3}", ct).ConfigureAwait(false);

            // инсталлятор запускаем на Java не ниже требуемой версией Minecraft (иначе Java 8 не поймёт class-файлы)
            var requiredJava = versions.RequiredJavaFor(mcVersion);
            var detected = Core.AppServices.Current?.Java.Detect() ?? new List<Java.JavaInstallation>();
            var java = detected.Where(j => j.Valid && j.Major >= requiredJava).OrderBy(j => j.Major).FirstOrDefault();
            if (java is null)
            {
                var anyValid = detected.FirstOrDefault(j => j.Valid);
                if (anyValid is not null)
                {
                    java = anyValid;
                    if (anyValid.Major < requiredJava)
                        Log.Warn($"Инсталлятор {Path.GetFileName(installerUrl)}: найдена только Java {anyValid.Major}, требуется {requiredJava}");
                }
            }
            if (java is null && Java.JavaService.FindAnyJava(paths) is { } found)
                java = new Java.JavaInstallation { Path = found.path, Version = found.version };
            if (java is null)
                throw new LauncherException("Для установки Forge/NeoForge нужна Java",
                    "Установите Java через Настройки → Java и повторите");

            // инсталлятор работает с «игровой папкой» — используем временную
            var psi = new ProcessStartInfo
            {
                FileName = java.Path,
                Arguments = $"-jar \"{installerJar}\" --installClient \"{work}\"",
                WorkingDirectory = work,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi) ?? throw new LauncherException("Не удалось запустить инсталлятор", java.Path);
            var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            if (!p.WaitForExit(300_000))
            {
                try { p.Kill(); } catch { /* ignore */ }
                throw new LauncherException("Инсталлятор завис", "Превышено время ожидания (5 минут)");
            }
            if (p.ExitCode != 0)
            {
                Log.Warn($"Инсталлятор ({Path.GetFileName(installerUrl)}): код {p.ExitCode}\n{stdout}\n{stderr}");
                throw new LauncherException("Инсталлятор завершился с ошибкой",
                    $"Код {p.ExitCode}\n{stdout}\n{stderr}");
            }

            // подбираем созданный version json: инсталлятор кладёт в целевую папку и базовую версию
            // (versions\1.21.1\1.21.1.json), и профиль загрузчика (versions\neoforge-21.1.5\...)
            var createdJson = FindCreatedVersionJson(work, expectedId, mcVersion);
            if (createdJson is null)
                throw new LauncherException("Инсталлятор не создал version JSON",
                    $"{expectedId}: в {work} нет json загрузчика");

            var realId = Path.GetFileNameWithoutExtension(createdJson);
            if (string.Equals(realId, mcVersion, StringComparison.OrdinalIgnoreCase))
                throw new LauncherException("Инсталлятор не создал профиль загрузчика",
                    $"{expectedId}: найден только базовый {mcVersion}.json");

            // переносим библиотеки в общий каталог
            var libsDir = Path.Combine(work, "libraries");
            if (Directory.Exists(libsDir))
                MergeDirectory(libsDir, paths.LibrariesDir);

            // version json + jar в общий каталог версий
            var targetDir = Path.Combine(paths.VersionsDir, realId);
            Directory.CreateDirectory(targetDir);
            File.Copy(createdJson, Path.Combine(targetDir, realId + ".json"), true);
            foreach (var jar in Directory.GetFiles(Path.GetDirectoryName(createdJson)!, "*.jar"))
                File.Copy(jar, Path.Combine(targetDir, Path.GetFileName(jar).Replace(
                    Path.GetFileNameWithoutExtension(jar), realId)), true);

            // если инсталлятор положил jar с другим именем — ищем в work
            if (!File.Exists(Path.Combine(targetDir, realId + ".jar")))
            {
                var clientJar = Directory.GetFiles(work, "*.jar", SearchOption.AllDirectories)
                    .FirstOrDefault(f => !f.EndsWith("installer.jar"));
                if (clientJar is not null)
                    File.Copy(clientJar, Path.Combine(targetDir, realId + ".jar"), true);
            }

            Log.Info($"Загрузчик установлен: {realId} ({installerUrl})");
            return realId;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Ищет version JSON, созданный официальным инсталлятором (Forge/NeoForge).
    /// Порядок: точное имя → поле id внутри файла → любой json загрузчика → самый свежий,
    /// кроме базовой версии Minecraft (её инсталлятор тоже копирует в целевую папку).
    /// </summary>
    private static string? FindCreatedVersionJson(string work, string expectedId, string mcVersion)
    {
        List<string> json;
        try
        {
            json = Directory.GetFiles(work, "*.json", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).Equals("launcher_profiles.json", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Поиск version json в {work}: {ex.Message}");
            return null;
        }
        if (json.Count == 0) return null;

        var exact = json.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f).Equals(expectedId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        foreach (var f in json)
        {
            try
            {
                var id = JsonNode.Parse(File.ReadAllText(f))?["id"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(id) && id.Equals(expectedId, StringComparison.OrdinalIgnoreCase))
                    return f;
            }
            catch (Exception ex) { Log.Debug($"Чтение {Path.GetFileName(f)}: {ex.Message}"); }
        }

        var loaderName = expectedId.Split('-')[0];
        var byLoader = json.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f).Contains(loaderName, StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileNameWithoutExtension(f).Equals(mcVersion, StringComparison.OrdinalIgnoreCase));
        if (byLoader is not null) return byLoader;

        return json.Where(f => !Path.GetFileNameWithoutExtension(f).Equals(mcVersion, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static void MergeDirectory(string src, string dst)
    {
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
}
