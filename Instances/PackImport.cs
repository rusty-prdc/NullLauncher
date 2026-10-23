using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NullLauncher.Instances;

/// <summary>
/// Универсальный импорт архивов: сборка NullLauncher (.nlpkg), модпак Modrinth (.mrpack и зипы
/// с modrinth.index.json), модпак CurseForge (manifest.json + overrides) и готовая папка игры
/// (папка .minecraft или папка с mods/config/versions). Формат определяется по содержимому
/// архива, а не по расширению — любой разумный зип принимается.
/// </summary>
public static class PackImport
{
    private enum Kind { Unknown, Instance, Mrpack, CurseForge, GameFolder, MultiMC }

    /// <summary>
    /// Роутит архив по его содержимому в нужный импорт и возвращает готовую сборку.
    /// Если вместо локального файла передан sourceUrl — архив скачивается во временную папку,
    /// а дальше определяется формат по содержимому (.mrpack или модпак CurseForge).
    /// </summary>
    public static async Task<object?> ImportAsync(
        AppServices s, string? sourcePath, string? sourceUrl, string? instanceName, CancellationToken ct)
    {
        string? downloaded = null;
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) && !string.IsNullOrWhiteSpace(sourceUrl))
            {
                Directory.CreateDirectory(s.Paths.TempDir);
                var name = "pack-" + Guid.NewGuid().ToString("N")[..8] + GuessExt(sourceUrl!);
                downloaded = Path.Combine(s.Paths.TempDir, name);
                await s.Downloads.DownloadAsync(
                    sourceUrl!, downloaded, "Скачивание модпака", kind: "pack", ct: ct).ConfigureAwait(false);
                sourcePath = downloaded;
            }
            if (string.IsNullOrWhiteSpace(sourcePath) || (!File.Exists(sourcePath) && !Directory.Exists(sourcePath)))
                throw new Core.LauncherException("Файл не найден", sourcePath ?? sourceUrl ?? "");

            // папка: инстанс MultiMC/Prism (mmc-pack.json) либо готовая папка игры
            if (Directory.Exists(sourcePath))
                return await ImportFolderAsync(s, sourcePath, instanceName, ct).ConfigureAwait(false);

            return await ImportFileAsync(s, sourcePath, instanceName, ct).ConfigureAwait(false);
        }
        finally
        {
            if (downloaded is not null)
                try { if (File.Exists(downloaded)) File.Delete(downloaded); }
                catch { /* временный файл подчистится позже */ }
        }
    }

    /// <summary>Расширение из URL (нужно, чтобы быстрая ветка .mrpack сработала).</summary>
    private static string GuessExt(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var ext = Path.GetExtension(path);
            return ext.Length is > 1 and < 8 ? ext : ".zip";
        }
        catch { return ".zip"; }
    }

    private static async Task<object?> ImportFileAsync(
        AppServices s, string sourcePath, string? instanceName, CancellationToken ct)
    {
        if (sourcePath.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            return await InstanceExtrasIpc.ImportMrpackAsync(s, MrpackParams(sourcePath, instanceName), ct)
                .ConfigureAwait(false);

        var kind = Detect(sourcePath, out var detail);
        switch (kind)
        {
            case Kind.Instance:
                return s.Instances.Import(sourcePath);

            case Kind.Mrpack:
                return await InstanceExtrasIpc.ImportMrpackAsync(s, MrpackParams(sourcePath, instanceName), ct)
                    .ConfigureAwait(false);

            case Kind.CurseForge:
                return await ImportCurseForgeAsync(s, sourcePath, instanceName, ct).ConfigureAwait(false);

            case Kind.GameFolder:
                return await ImportGameFolderAsync(s, sourcePath, instanceName, ct).ConfigureAwait(false);

            default:
                throw new Core.LauncherException("Не удалось распознать архив", detail);
        }
    }

    private static JsonObject MrpackParams(string sourcePath, string? instanceName)
    {
        var p = new JsonObject { ["sourcePath"] = sourcePath };
        if (!string.IsNullOrWhiteSpace(instanceName)) p["instanceName"] = instanceName;
        return p;
    }

    /* ------------------------------------------------------------ определение формата */

    private static Kind Detect(string path, out string detail)
    {
        detail = "Ожидался файл сборки (.nlpkg), модпак Modrinth/CurseForge или архив с папкой игры";
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var names = zip.Entries
                .Where(e => e.FullName.Length > 0)
                .Select(e => e.FullName.Replace('\\', '/'))
                .ToList();
            if (names.Count == 0) { detail = "Архив пуст"; return Kind.Unknown; }

            // приоритет однозначных маркеров
            if (names.Any(n => IsMarker(n, "instance.json"))) return Kind.Instance;
            if (names.Any(n => IsMarker(n, "modrinth.index.json"))) return Kind.Mrpack;

            // CurseForge: manifest.json с minecraft+files (любой другой manifest.json не считаем)
            foreach (var entry in zip.Entries.Where(e => IsMarker(e.FullName.Replace('\\', '/'), "manifest.json")))
            {
                string text;
                using (var st = entry.Open())
                using (var sr = new StreamReader(st))
                    text = sr.ReadToEnd();
                if (text.Contains("\"minecraft\"") && text.Contains("\"files\"")) return Kind.CurseForge;
            }

            if (LooksLikeGameFolder(names)) return Kind.GameFolder;

            // «зип, в котором лежит зип с майнкрафтом»: берём один вложенный архив с корня
            var nested = names.FirstOrDefault(n =>
                n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                IsMarker(n, Path.GetFileName(n)) && !IsInsideContentDir(n));
            if (nested is not null)
            {
                var inner = ExtractNested(zip, nested);
                if (inner is not null)
                {
                    var k = Detect(inner, out detail);
                    if (k != Kind.Unknown) return k;
                }
            }

            detail = "В архиве нет instance.json, modrinth.index.json, manifest.json или папки игры (mods/versions)";
            return Kind.Unknown;
        }
        catch (InvalidDataException)
        {
            detail = "Это не ZIP-архив";
            return Kind.Unknown;
        }
        catch (Exception ex) when (ex is not Core.LauncherException)
        {
            detail = ex.Message;
            return Kind.Unknown;
        }
    }

    /// <summary>entry — файл с указанным именем на глубине не более двух папок (корень/обёртка).</summary>
    private static bool IsMarker(string entryPath, string fileName)
    {
        var idx = entryPath.LastIndexOf('/');
        var dir = idx < 0 ? "" : entryPath[..idx];
        var depth = dir.Length == 0 ? 0 : dir.Count(c => c == '/') + 1;
        return depth <= 2 && string.Equals(entryPath[(idx + 1)..], fileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Похоже ли на распакованную папку игры: .minecraft, mods+config, versions.</summary>
    private static bool LooksLikeGameFolder(List<string> names)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            var idx = -1;
            while ((idx = n.IndexOf('/', idx + 1)) > 0)
                dirs.Add(n[..idx]);
        }

        bool HasDir(string name, int maxDepth) => dirs.Any(d =>
        {
            if (!string.Equals(d[(d.LastIndexOf('/') + 1)..], name, StringComparison.OrdinalIgnoreCase))
                return false;
            var depth = d.Count(c => c == '/') + 1;
            return depth <= maxDepth;
        });

        if (HasDir(".minecraft", 2) || HasDir("minecraft", 2)) return true;
        if (HasDir("versions", 2)) return true;
        // Latte-подобные папки: mods лежит рядом с config/options.txt, versions может не быть
        if (HasDir("mods", 2) && (HasDir("config", 2) || names.Any(n =>
                n.Equals("options.txt", StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("/options.txt", StringComparison.OrdinalIgnoreCase))))
            return true;
        return false;
    }

    /// <summary>Вложенные зипы, лежащие в корне/обёртке (не datapack/resourcepack из контента).</summary>
    private static bool IsInsideContentDir(string entryPath)
    {
        var parts = entryPath.Split('/');
        for (var i = 0; i < parts.Length - 1; i++)
            if (parts[i] is "resourcepacks" or "shaderpacks" or "config" or "overrides" or "datapacks" or "mods" or "saves")
                return true;
        return false;
    }

    private static string? ExtractNested(ZipArchive zip, string entryName)
    {
        try
        {
            var entry = zip.GetEntry(entryName);
            if (entry is null) return null;
            var tmp = Path.Combine(AppServices.Current!.Paths.TempDir,
                "nested-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
            entry.ExtractToFile(tmp, true);
            return tmp;
        }
        catch (Exception ex)
        {
            Log.Debug($"Не удалось извлечь вложенный архив {entryName}: {ex.Message}");
            return null;
        }
    }

    /* ------------------------------------------------------------ импорт готовой папки игры */

    private static async Task<object?> ImportGameFolderAsync(
        AppServices s, string sourcePath, string? instanceName, CancellationToken ct)
    {
        var tmp = Path.Combine(s.Paths.TempDir, "gameimport-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            try { ZipFile.ExtractToDirectory(sourcePath, tmp, true); }
            catch (Exception ex) { throw new Core.LauncherException("Не удалось распаковать архив", ex.Message, ex); }

            var gameDir = FindGameDir(tmp)
                ?? throw new Core.LauncherException("Это не папка игры",
                    "В архиве нет .minecraft, mods/, config/ или versions/");
            ct.ThrowIfCancellationRequested();

            var ver = DetectGameVersion(gameDir)
                ?? throw new Core.LauncherException("Не удалось определить версию Minecraft",
                    "В архиве нет папки versions/, а по именам модов версию и загрузчик определить не удалось.\n" +
                    "Импортируйте сборку в формате .nlpkg/.mrpack либо папку с versions/.");

            var name = (instanceName ?? "").Trim();
            if (name.Length == 0) name = Path.GetFileNameWithoutExtension(sourcePath);
            if (name.Length == 0) name = "Импортированная сборка";

            var rec = s.Instances.Create(new CreateInstanceRequest(
                Name: name,
                Description: $"Импортировано из {Path.GetFileName(sourcePath)}",
                IconPath: FindImage(tmp, "icon.png") ?? FindImage(gameDir, "icon.png"),
                CoverPath: null, Color: null,
                McVersion: ver.mc, Loader: ver.loader, LoaderVersion: ver.lv,
                JavaMode: null, JavaMajor: null, JavaPath: null,
                RamMinMb: 512, RamMaxMb: 4096,
                Width: null, Height: null, Fullscreen: null, GroupIds: null));

            // содержимое папки игры → .minecraft сборки; общую папку versions берёт сам лаунчер
            MergeMove(gameDir, rec.GameDir, skipTopDir: "versions");
            ct.ThrowIfCancellationRequested();

            Log.Info($"Импорт папки игры → '{rec.Name}' ({ver.mc}/{ver.loader}" +
                     (ver.lv is null ? "" : $" {ver.lv}") + ")");
            s.Emit("instance.changed", new { id = rec.Id });
            return s.Instances.Get(rec.Id) ?? rec;
        }
        catch (Core.LauncherException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new Core.LauncherException("Не удалось импортировать архив", ex.Message, ex); }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
            catch (Exception ex) { Log.Debug($"Не удалось удалить {tmp}: {ex.Message}"); }
        }
    }

    /* ------------------------------------------------------------ импорт папки (MultiMC/Prism, папка игры) */

    /// <summary>
    /// Импорт из папки: инстанс MultiMC/Prism/ATLauncher (есть mmc-pack.json) либо обычная
    /// папка игры (.minecraft / mods+config). Аккуратно — папка не распаковывается, а читается напрямую.
    /// </summary>
    private static async Task<object?> ImportFolderAsync(
        AppServices s, string dir, string? instanceName, CancellationToken ct)
    {
        if (File.Exists(Path.Combine(dir, "mmc-pack.json")))
            return await ImportMultiMCAsync(s, dir, instanceName, ct).ConfigureAwait(false);

        var gameDir = FindGameDir(dir) ?? (HasGameContent(dir) ? dir : null)
            ?? throw new Core.LauncherException("Это не папка игры",
                "Внутри нет .minecraft, mods/, config/ или versions/, а также нет mmc-pack.json (MultiMC/Prism).");

        var ver = DetectGameVersion(gameDir)
            ?? throw new Core.LauncherException("Не удалось определить версию Minecraft",
                "В папке нет versions/, а по именам модов версию определить не удалось.");

        var name = (instanceName ?? "").Trim();
        if (name.Length == 0) name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
        if (name.Length == 0) name = "Импортированная сборка";

        var rec = s.Instances.Create(new CreateInstanceRequest(
            Name: name,
            Description: $"Импортировано из папки {Path.GetFileName(dir)}",
            IconPath: FindImage(dir, "icon.png") ?? FindImage(gameDir, "icon.png"),
            CoverPath: null, Color: null,
            McVersion: ver.mc, Loader: ver.loader, LoaderVersion: ver.lv,
            JavaMode: null, JavaMajor: null, JavaPath: null,
            RamMinMb: 512, RamMaxMb: 4096,
            Width: null, Height: null, Fullscreen: null, GroupIds: null));

        MergeMove(gameDir, rec.GameDir, skipTopDir: "versions");
        ct.ThrowIfCancellationRequested();

        Log.Info($"Импорт папки игры → '{rec.Name}' ({ver.mc}/{ver.loader})");
        s.Emit("instance.changed", new { id = rec.Id });
        return s.Instances.Get(rec.Id) ?? rec;
    }

    /// <summary>Импорт инстанса MultiMC/Prism: mmc-pack.json (версия/загрузчик) + instance.cfg (имя, RAM) + папка minecraft/.</summary>
    private static async Task<object?> ImportMultiMCAsync(
        AppServices s, string instDir, string? instanceName, CancellationToken ct)
    {
        var pack = ParseMmcPack(Path.Combine(instDir, "mmc-pack.json"))
            ?? throw new Core.LauncherException("Не удалось прочитать mmc-pack.json",
                "В папке инстанса нет компонентов (net.minecraft и загрузчик).");

        var cfg = ParseInstanceCfg(Path.Combine(instDir, "instance.cfg"));

        var gameDir =
            Directory.Exists(Path.Combine(instDir, "minecraft")) ? Path.Combine(instDir, "minecraft") :
            Directory.Exists(Path.Combine(instDir, ".minecraft")) ? Path.Combine(instDir, ".minecraft") :
            throw new Core.LauncherException("В инстансе нет папки minecraft", instDir);

        var name = (instanceName ?? cfg.Name ?? "").Trim();
        if (name.Length == 0) name = Path.GetFileName(instDir.TrimEnd(Path.DirectorySeparatorChar));
        if (name.Length == 0) name = "Импортированный инстанс";

        var rec = s.Instances.Create(new CreateInstanceRequest(
            Name: name,
            Description: "Импортировано из MultiMC / Prism",
            IconPath: FindImage(instDir, "icon.png"),
            CoverPath: null, Color: null,
            McVersion: pack.mc, Loader: pack.loader, LoaderVersion: pack.lv,
            JavaMode: null, JavaMajor: null, JavaPath: null,
            RamMinMb: cfg.MinMem ?? 512, RamMaxMb: cfg.MaxMem ?? 4096,
            Width: null, Height: null, Fullscreen: null, GroupIds: null));

        MergeMove(gameDir, rec.GameDir, skipTopDir: "versions");
        ct.ThrowIfCancellationRequested();

        Log.Info($"Импорт MultiMC/Prism → '{rec.Name}' ({pack.mc}/{pack.loader}" +
                 (pack.lv is null ? "" : $" {pack.lv}") + ")");
        s.Emit("instance.changed", new { id = rec.Id });
        return s.Instances.Get(rec.Id) ?? rec;
    }

    /// <summary>Читает mmc-pack.json: возвращает (mc, loader, loaderVersion).</summary>
    private static (string mc, string loader, string? lv)? ParseMmcPack(string path)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            var comps = node?["components"]?.AsArray();
            if (comps is null || comps.Count == 0) return null;

            string? mc = null, loader = null, lv = null;
            foreach (var c in comps)
            {
                var uid = c?["uid"]?.GetValue<string>() ?? "";
                var ver = c?["version"]?.GetValue<string>();
                if (uid == "net.minecraft") { mc = ver; continue; }
                if (uid is "net.minecraftforge") { loader = "forge"; lv = ver; }
                else if (uid is "net.fabricmc.fabric-loader") { loader = "fabric"; lv = ver; }
                else if (uid is "org.quiltmc.quilt-loader") { loader = "quilt"; lv = ver; }
                else if (uid is "net.neoforged") { loader = "neoforge"; lv = ver; }
            }
            if (string.IsNullOrWhiteSpace(mc)) return null;
            return (mc!, loader ?? "vanilla", lv);
        }
        catch (Exception ex)
        {
            Log.Debug($"mmc-pack.json: {ex.Message}");
            return null;
        }
    }

    /// <summary>Читает instance.cfg (формат key=value): имя и выделенную память.</summary>
    private static (string? Name, int? MinMem, int? MaxMem) ParseInstanceCfg(string path)
    {
        string? name = null;
        int? min = null, max = null;
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line[..eq].Trim();
                var v = line[(eq + 1)..].Trim();
                if (k == "name") name = v;
                else if (k == "MinMemAlloc" && int.TryParse(v, out var a)) min = a;
                else if (k == "MaxMemAlloc" && int.TryParse(v, out var b)) max = b;
            }
        }
        catch (Exception ex) { Log.Debug($"instance.cfg: {ex.Message}"); }
        return (name, min, max);
    }

    /// <summary>Ищет папку игры: .minecraft внутри (в т.ч. в обёртке) либо папка с mods+config/versions.</summary>
    private static string? FindGameDir(string root)
    {
        var queue = new Queue<(string dir, int depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            var mc = Path.Combine(dir, ".minecraft");
            if (Directory.Exists(mc)) return mc;

            if (HasGameContent(dir)) return dir;
            if (depth >= 3) continue;
            foreach (var sub in Directory.GetDirectories(dir))
                queue.Enqueue((sub, depth + 1));
        }
        return null;
    }

    private static bool HasGameContent(string dir)
        => Directory.Exists(Path.Combine(dir, "versions")) ||
           (Directory.Exists(Path.Combine(dir, "mods")) &&
            (Directory.Exists(Path.Combine(dir, "config")) || File.Exists(Path.Combine(dir, "options.txt"))));

    /// <summary>
    /// Версия/загрузчик папки игры: сначала versions/*/&lt;id&gt;.json (id, inheritsFrom, mainClass),
    /// затем — голосование по именам мод-джарников. Загрузчик без версии оставляем пустым:
    /// лаунчер подберёт последнюю совместимую с этой версией Minecraft.
    /// </summary>
    private static (string mc, string loader, string? lv)? DetectGameVersion(string gameDir)
    {
        var versionsDir = Path.Combine(gameDir, "versions");
        if (Directory.Exists(versionsDir))
        {
            var best = DetectFromVersions(versionsDir);
            if (best is not null) return best;
        }
        return DetectFromModNames(gameDir);
    }

    private static (string mc, string loader, string? lv)? DetectFromVersions(string versionsDir)
    {
        var candidates = new List<(string mc, string loader, string? lv, bool hasLoader)>();
        foreach (var vdir in Directory.GetDirectories(versionsDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(vdir);
            var jsonFile = Path.Combine(vdir, id + ".json");
            if (!File.Exists(jsonFile))
                jsonFile = Directory.GetFiles(vdir, "*.json").FirstOrDefault() ?? "";
            string? inherits = null, mainClass = null;
            if (jsonFile.Length > 0)
            {
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(jsonFile));
                    inherits = node?["inheritsFrom"]?.GetValue<string>();
                    mainClass = node?["mainClass"]?.GetValue<string>();
                }
                catch { /* битый json версии — решаем по имени папки */ }
            }

            var loader = LoaderFromId(id) ?? LoaderFromMainClass(mainClass);
            var mc = inherits ?? ParseMcFromId(id);
            if (mc is null) continue;
            var lv = LoaderVersionFromId(id, mc);
            candidates.Add((mc, loader ?? "vanilla", lv, loader is not null));
        }

        // предпочтение — профилю загрузчика (он и есть запускаемый), затем просто версии
        var pick = candidates.FirstOrDefault(c => c.hasLoader);
        if (pick.mc is null && candidates.Count > 0) pick = candidates[0];
        return pick.mc is null ? null : (pick.mc, pick.loader, pick.lv);
    }

    private static string? LoaderFromId(string id)
    {
        var i = id.ToLowerInvariant();
        if (i.Contains("fabric-loader") || i.StartsWith("fabric-")) return "fabric";
        if (i.Contains("quilt-loader") || i.StartsWith("quilt-")) return "quilt";
        if (i.Contains("neoforge")) return "neoforge";
        if (i.Contains("forge")) return "forge";
        if (i.Contains("liteloader")) return "vanilla";
        return null;
    }

    private static string? LoaderFromMainClass(string? mainClass)
    {
        if (string.IsNullOrEmpty(mainClass)) return null;
        if (mainClass.Contains("net.fabricmc", StringComparison.OrdinalIgnoreCase)) return "fabric";
        if (mainClass.Contains("org.quiltmc", StringComparison.OrdinalIgnoreCase)) return "quilt";
        if (mainClass.Contains("net.neoforged", StringComparison.OrdinalIgnoreCase)) return "neoforge";
        if (mainClass.Contains("net.minecraftforge", StringComparison.OrdinalIgnoreCase) ||
            mainClass.Contains("cpw.mods.fml", StringComparison.OrdinalIgnoreCase)) return "forge";
        return null;
    }

    /// <summary>Версия Minecraft из id профиля: «1.21.1», «1.20.1-forge-47.4.10», «fabric-loader-…-1.21.1».</summary>
    private static string? ParseMcFromId(string id)
    {
        var m = Regex.Match(id, @"^(?<v>\d+\.\d+(?:\.\d+)?)(?:-|$)");
        if (m.Success) return m.Groups["v"].Value;
        m = Regex.Match(id, @"-(?<v>\d+\.\d+(?:\.\d+)?)$");
        return m.Success ? m.Groups["v"].Value : null;
    }

    private static string? LoaderVersionFromId(string id, string mc)
    {
        var i = id;
        string? Strip(string prefix, string suffix)
        {
            if (!i.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var v = i[prefix.Length..];
            if (suffix.Length > 0 && v.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                v = v[..^suffix.Length];
            return v.Length > 0 ? v : null;
        }

        var fromFabric = Strip("fabric-loader-", "-" + mc);
        if (fromFabric is not null) return fromFabric;
        var fromQuilt = Strip("quilt-loader-", "-" + mc);
        if (fromQuilt is not null) return fromQuilt;
        var fromNeo = Strip("neoforge-", "");
        if (fromNeo is not null) return fromNeo;

        var m = Regex.Match(id, @"-forge-(?<v>[\w.\-]+)$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["v"].Value : null;
    }

    /// <summary>Голосование по именам модов: «…-1.21.1-neoforge-….jar» → 1.21.1 / neoforge.</summary>
    private static (string mc, string loader, string? lv)? DetectFromModNames(string gameDir)
    {
        var modsDir = Path.Combine(gameDir, "mods");
        if (!Directory.Exists(modsDir)) return null;
        var jars = Directory.GetFiles(modsDir, "*.jar").Select(Path.GetFileName).ToList();
        if (jars.Count == 0) return null;

        var mcVotes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var jar in jars)
            foreach (Match m in Regex.Matches(jar ?? "", @"(?<![\d.])1\.\d{1,2}(?:\.\d{1,2})?(?![\d.])"))
            {
                var parts = m.Value.Split('.');
                if (parts.Length < 2) continue;
                if (!int.TryParse(parts[1], out var minor) || minor < 5) continue; // мусорные совпадения вида 1.1.13
                mcVotes[m.Value] = mcVotes.TryGetValue(m.Value, out var c) ? c + 1 : 1;
            }
        if (mcVotes.Count == 0) return null;
        var mc = mcVotes.OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => kv.Key.Length)
            .First().Key;

        int neo = jars.Count(j => j!.Contains("neoforge", StringComparison.OrdinalIgnoreCase));
        int fabric = jars.Count(j => j!.Contains("fabric", StringComparison.OrdinalIgnoreCase));
        int quilt = jars.Count(j => j!.Contains("quilt", StringComparison.OrdinalIgnoreCase));
        int forge = jars.Count(j => j!.Contains("forge", StringComparison.OrdinalIgnoreCase)) - neo;
        var votes = new[] { ("neoforge", neo), ("fabric", fabric), ("quilt", quilt), ("forge", forge) };
        var loader = votes.OrderByDescending(v => v.Item2).First();
        return loader.Item2 > 0 ? (mc, loader.Item1, (string?)null) : (mc, "vanilla", (string?)null);
    }

    private static string? FindImage(string dir, string fileName)
    {
        try
        {
            var p = Path.Combine(dir, fileName);
            if (File.Exists(p)) return p;
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>Переносит содержимое src в dst; существующие каталоги dst сливаются рекурсивно.</summary>
    private static void MergeMove(string src, string dst, string? skipTopDir = null)
    {
        Directory.CreateDirectory(dst);
        foreach (var sub in Directory.GetDirectories(src))
        {
            var name = Path.GetFileName(sub);
            if (skipTopDir is not null && name.Equals(skipTopDir, StringComparison.OrdinalIgnoreCase))
                continue;
            var target = Path.Combine(dst, name);
            if (Directory.Exists(target)) MergeMove(sub, target);
            else Directory.Move(sub, target);
        }
        foreach (var file in Directory.GetFiles(src))
            File.Move(file, Path.Combine(dst, Path.GetFileName(file)), true);
    }

    /* ------------------------------------------------------------ импорт CurseForge */

    private static async Task<object?> ImportCurseForgeAsync(
        AppServices s, string sourcePath, string? instanceName, CancellationToken ct)
    {
        var tmp = Path.Combine(s.Paths.TempDir, "cfimport-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            try { ZipFile.ExtractToDirectory(sourcePath, tmp, true); }
            catch (Exception ex) { throw new Core.LauncherException("Не удалось распаковать архив", ex.Message, ex); }

            var manifestFile = InstanceExtrasIpc.FindMarkerFile(tmp, "manifest.json")
                ?? throw new Core.LauncherException("Это не модпак CurseForge", "В архиве нет manifest.json");
            var root = Path.GetDirectoryName(manifestFile)!;

            JsonObject manifest;
            try
            {
                manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestFile, ct).ConfigureAwait(false))
                           as JsonObject ?? throw new Core.LauncherException("Повреждённый manifest.json");
            }
            catch (Core.LauncherException) { throw; }
            catch (Exception ex) { throw new Core.LauncherException("Повреждённый manifest.json", ex.Message, ex); }

            var mc = manifest["minecraft"]?["version"]?.GetValue<string>() ?? "";
            if (mc.Length == 0)
                throw new Core.LauncherException("В манифесте не указана версию Minecraft", manifestFile);

            var loaderId = manifest["minecraft"]?["modLoaders"]?[0]?["id"]?.GetValue<string>() ?? "";
            var (loader, loaderVersion) = ParseCfLoader(loaderId);

            var name = (instanceName ?? "").Trim();
            if (name.Length == 0) name = manifest["name"]?.GetValue<string>()?.Trim() ?? "";
            if (name.Length == 0) name = Path.GetFileNameWithoutExtension(sourcePath);

            var rec = s.Instances.Create(new CreateInstanceRequest(
                Name: name,
                Description: manifest["summary"]?.GetValue<string>() ?? "",
                IconPath: null, CoverPath: null, Color: null,
                McVersion: mc, Loader: loader, LoaderVersion: loaderVersion,
                JavaMode: null, JavaMajor: null, JavaPath: null,
                RamMinMb: 512, RamMaxMb: 4096,
                Width: null, Height: null, Fullscreen: null, GroupIds: null));
            ct.ThrowIfCancellationRequested();

            // overrides поверх .minecraft
            var overridesName = manifest["overrides"]?.GetValue<string>() ?? "overrides";
            var overridesDir = Path.Combine(root, overridesName);
            if (Directory.Exists(overridesDir))
                CopyTree(overridesDir, rec.GameDir);

            // файлы манифеста: имя через cfwidget (кэш на проект), скачивание через forgecdn
            var files = new List<(long project, long file, bool required)>();
            if (manifest["files"] is JsonArray arr)
                foreach (var node in arr)
                {
                    var pid = node?.Int("projectID", 0) ?? 0;
                    var fid = node?.Int("fileID", 0) ?? 0;
                    if (pid > 0 && fid > 0)
                        files.Add((pid, fid, node?["required"]?.GetValueKind() != System.Text.Json.JsonValueKind.False));
                }

            var names = await ResolveCfFileNamesAsync(s, files, ct).ConfigureAwait(false);

            var modsDir = Path.Combine(rec.GameDir, "mods");
            Directory.CreateDirectory(modsDir);
            int downloaded = 0, failed = 0;
            foreach (var (pid, fid, _) in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!names.TryGetValue((pid, fid), out var fileName) || fileName.Length == 0)
                {
                    failed++;
                    continue;
                }
                var url = $"https://edge.forgecdn.net/files/{fid / 1000}/{fid % 1000}/{Uri.EscapeDataString(fileName)}";
                try
                {
                    await s.Downloads.DownloadAsync(url, Path.Combine(modsDir, fileName), fileName,
                        kind: "mod", instanceId: rec.Id, ct: ct).ConfigureAwait(false);
                    downloaded++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    Log.Debug($"CurseForge-файл {fileName} (project {pid}): {ex.Message}");
                }
            }

            Log.Info($"Импорт CurseForge → '{rec.Name}' ({mc}/{loader}" +
                     (loaderVersion is null ? "" : $" {loaderVersion}") +
                     $"): скачано {downloaded}, ошибок {failed}");
            if (failed > 0)
                s.NotifyUser("warn", "Модпак импортирован частично",
                    $"Скачано {downloaded} файл(ов), не удалось {failed}. " +
                    "Недостающие моды можно доустановить из вкладки Mods.");

            s.Emit("instance.changed", new { id = rec.Id });
            return s.Instances.Get(rec.Id) ?? rec;
        }
        catch (Core.LauncherException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new Core.LauncherException("Не удалось импортировать модпак", ex.Message, ex); }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
            catch (Exception ex) { Log.Debug($"Не удалось удалить {tmp}: {ex.Message}"); }
        }
    }

    /// <summary>id лоадера CurseForge: «neoforge-21.1.250», «forge-47.4.10», «fabric-0.16.9».</summary>
    private static (string loader, string? lv) ParseCfLoader(string loaderId)
    {
        var i = loaderId.Trim().ToLowerInvariant();
        if (i.Length == 0) return ("vanilla", null);
        var idx = i.IndexOf('-');
        var head = idx < 0 ? i : i[..idx];
        var rest = idx < 0 ? "" : loaderId[(idx + 1)..];
        return head switch
        {
            "neoforge" => ("neoforge", rest),
            "forge" => ("forge", rest),
            "fabric" => ("fabric", rest),
            "quilt" or "quilt-loader" => ("quilt", rest),
            _ => ("vanilla", null),
        };
    }

    /// <summary>Имена файлов для projectID+fileID: один запрос cfwidget на проект, параллельно ≤6.</summary>
    private static async Task<Dictionary<(long project, long file), string>> ResolveCfFileNamesAsync(
        AppServices s, List<(long project, long file, bool required)> files, CancellationToken ct)
    {
        var cache = new ConcurrentDictionary<long, ConcurrentDictionary<long, string>>();
        var result = new Dictionary<(long, long), string>();
        var gate = new SemaphoreSlim(6);
        // свой клиент: общий HttpClient живёт в HttpFactory и рассчитан на короткие запросы,
        // а cfwidget может тянуться; распад на десятках параллельных проектов не должен ронять импорт
        var timeout = Math.Max(30, s.Settings.Get("networkTimeoutSec", 60));
        using var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        { Timeout = TimeSpan.FromSeconds(timeout) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpFactory.UserAgent);
        try
        {
            await Task.WhenAll(files.Select(async f =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var map = cache.GetOrAdd(f.project, _ => new ConcurrentDictionary<long, string>());
                    if (map.IsEmpty)
                    {
                        try
                        {
                            using var req = new HttpRequestMessage(HttpMethod.Get,
                                $"https://api.cfwidget.com/minecraft/mc-mods/{f.project}");
                            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                            if (resp.IsSuccessStatusCode)
                            {
                                var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                                foreach (var node in json?["files"]?.AsArray() ?? new JsonArray())
                                {
                                    var id = node?.Int("id", 0) ?? 0;
                                    var name = node?.Str("name");
                                    if (id > 0 && !string.IsNullOrWhiteSpace(name)) map[id] = name;
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            // сбой одного проекта (в том числе тайм-аут) — не отмена пользователя
                            Log.Debug($"cfwidget project {f.project}: {ex.Message}");
                        }
                    }
                    if (map.TryGetValue(f.file, out var fileName))
                        lock (result) result[(f.project, f.file)] = fileName;
                }
                finally { gate.Release(); }
            })).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        return result;
    }

    private static void CopyTree(string src, string dst)
    {
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            if (rel.StartsWith(".nulllauncher-meta.json", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
}
