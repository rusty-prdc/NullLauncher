using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NullLauncher.Core;
using NullLauncher.Instances;

namespace NullLauncher.Logs;

/// <summary>IPC-методы логов лаунчера, логов сборки и отчётов о сбоях (включая анализ причин).</summary>
public static class LogsIpc
{
    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("logs.listLauncher", (_, _) => Task.FromResult<object?>(ListLauncher(s)));
        r.Register("logs.readLauncher", (p, _) => Task.FromResult<object?>(ReadLauncher(s, p)));
        r.Register("logs.instanceFiles", (p, _) => Task.FromResult<object?>(InstanceFiles(s, p)));
        r.Register("logs.readInstance", (p, _) => Task.FromResult<object?>(ReadInstance(s, p)));
        r.Register("crashes.list", (p, _) => Task.FromResult<object?>(CrashList(s, p)));
        r.Register("crashes.read", (p, _) => Task.FromResult<object?>(CrashRead(s, p)));
        r.Register("crashes.analyze", (p, _) => Task.FromResult<object?>(CrashAnalyze(s, p)));
    }

    /* ------------------------------------------------------------ общее */
    private static readonly Regex LauncherLine = new(
        @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d{1,3})?)\s+\[([A-Za-z]+)\]\s?(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex GameLine = new(
        @"^\[(\d{1,2}:\d{2}:\d{2})\]\s*\[(?:[^/\]]*/)?([A-Za-z]+)(?:\s+[^/\]]*)?\]?\s*:?\s?(.*)$",
        RegexOptions.Compiled);

    private static InstanceRecord RequireInstance(AppServices s, JsonNode? p)
    {
        var id = p?.Str("instanceId");
        if (string.IsNullOrWhiteSpace(id)) throw new LauncherException("Не указана сборка");
        return s.Instances.Require(id);
    }

    private static int LevelRank(string? level) => (level ?? "").Trim().ToLowerInvariant() switch
    {
        "error" or "fatal" or "err" => 3,
        "warn" or "warning" => 2,
        "info" => 1,
        "debug" or "trace" or "verbose" => 0,
        _ => -1, // "all", пусто или неизвестно — фильтр не применяется
    };

    private static (string Ts, string Level, string Message) ParseLine(string raw, string prevTs, string prevLevel)
    {
        var m = LauncherLine.Match(raw);
        if (m.Success)
            return (m.Groups[1].Value, m.Groups[2].Value.ToUpperInvariant(), m.Groups[3].Value);
        m = GameLine.Match(raw);
        if (m.Success)
            return (m.Groups[1].Value, m.Groups[2].Value.ToUpperInvariant(), m.Groups[3].Value);
        // stack trace и прочий «хвост» — привязываем к предыдущей строке
        return prevTs.Length > 0 || prevLevel.Length > 0 ? (prevTs, prevLevel, raw) : ("", "INFO", raw);
    }

    private static List<(string Ts, string Level, string Message)> ParseLines(List<string> raws)
    {
        var result = new List<(string Ts, string Level, string Message)>(raws.Count);
        var prevTs = "";
        var prevLevel = "";
        foreach (var raw in raws)
        {
            var parsed = ParseLine(raw, prevTs, prevLevel);
            if (parsed.Ts.Length > 0) prevTs = parsed.Ts;
            if (parsed.Level.Length > 0) prevLevel = parsed.Level;
            result.Add(parsed);
        }
        return result;
    }

    private static List<(string Ts, string Level, string Message)> ApplyFilters(
        List<(string Ts, string Level, string Message)> lines, string? query, string? level)
    {
        var minRank = LevelRank(level);
        var q = query?.Trim();
        var result = new List<(string Ts, string Level, string Message)>();
        foreach (var l in lines)
        {
            if (minRank >= 0 && LevelRank(l.Level) < minRank) continue;
            if (!string.IsNullOrEmpty(q) && !l.Message.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(l);
        }
        return result;
    }

    /// <summary>Читает последние maxLines строк файла (целиком, если файл небольшой).</summary>
    private static (List<string> Lines, bool WholeFile) ReadTailLines(string path, int maxLines)
    {
        const int chunk = 256 * 1024;
        const int maxBytes = 8 * 1024 * 1024;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length == 0) return (new List<string>(), true);

            var reverseChunks = new List<byte[]>();
            var offset = fs.Length;
            var newlines = 0;
            var total = 0L;
            while (offset > 0 && newlines <= maxLines && total < maxBytes)
            {
                var start = Math.Max(0, offset - chunk);
                var len = (int)(offset - start);
                var buf = new byte[len];
                fs.Position = start;
                var read = 0;
                while (read < len)
                {
                    var n = fs.Read(buf, read, len - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != len) Array.Resize(ref buf, read);
                reverseChunks.Add(buf);
                foreach (var b in buf)
                    if (b == (byte)'\n') newlines++;
                total += read;
                offset = start;
            }

            var whole = offset == 0;
            reverseChunks.Reverse();
            using var ms = new MemoryStream();
            foreach (var c in reverseChunks) ms.Write(c, 0, c.Length);
            var text = Encoding.UTF8.GetString(ms.ToArray());
            if (whole && text.StartsWith('\uFEFF')) text = text[1..];

            var parts = text.Split('\n');
            var lines = new List<string>(parts.Length);
            for (var i = 0; i < parts.Length; i++)
            {
                var line = parts[i].TrimEnd('\r');
                if (i == 0 && !whole) continue;      // начало обрезано — строка неполная
                if (i == parts.Length - 1 && line.Length == 0) continue; // пустой хвост после последнего \n
                lines.Add(line);
            }
            if (lines.Count > maxLines)
            {
                lines = lines.Skip(lines.Count - maxLines).ToList();
                whole = false;
            }
            return (lines, whole);
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось прочитать файл лога {path}: {ex.Message}");
            return (new List<string>(), false);
        }
    }

    private static object ToDto(List<(string Ts, string Level, string Message)> lines, int total)
        => lines.Select(l => new { ts = l.Ts, level = l.Level, message = l.Message }).ToList();

    /* ------------------------------------------------------------ логи лаунчера */
    private static object ListLauncher(AppServices s)
    {
        var items = new List<(string Name, long Size, DateTime Created)>();
        foreach (var name in Log.ListFiles())
        {
            try
            {
                var f = Path.Combine(s.Paths.LogsDir, name);
                var fi = new FileInfo(f);
                if (!fi.Exists) continue;
                items.Add((fi.Name, fi.Length, fi.CreationTime));
            }
            catch { /* файл мог исчезнуть между ротациями */ }
        }
        return items
            .OrderByDescending(i => i.Name)
            .Select(i => new { name = i.Name, sizeBytes = i.Size, createdAt = i.Created.ToString("o") })
            .ToList();
    }

    private static object ReadLauncher(AppServices s, JsonNode? p)
    {
        var name = p?.Str("name");
        if (string.IsNullOrWhiteSpace(name)) throw new LauncherException("Файл лога не указан");
        // защита от path traversal: принимается только имя файла без папок
        if (name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name.Contains("..") ||
            Path.IsPathRooted(name) || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
            throw new LauncherException("Некорректное имя файла лога", name);

        var path = Path.Combine(s.Paths.LogsDir, name);
        if (!File.Exists(path)) throw new LauncherException("Файл лога не найден", name);

        var raw = ReadTailLines(path, 8000).Lines;
        var matched = ApplyFilters(ParseLines(raw), p?.Str("query"), p?.Str("level"));
        var total = matched.Count;
        var lines = matched.Count > 2000 ? matched.Skip(matched.Count - 2000).ToList() : matched;
        return new { lines = ToDto(lines, total), total };
    }

    /* ------------------------------------------------------------ логи сборки */
    private static object InstanceFiles(AppServices s, JsonNode? p)
    {
        var inst = RequireInstance(s, p);
        var items = new List<(string Name, string Path, long Size, DateTime Written)>();
        foreach (var dir in new[] { inst.LogsDir, inst.CrashReportsDir })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is not (".log" or ".txt")) continue;
                    var fi = new FileInfo(f);
                    items.Add((fi.Name, fi.FullName, fi.Length, fi.LastWriteTime));
                }
            }
            catch (Exception ex) { Log.Warn($"Не удалось получить файлы логов {dir}: {ex.Message}"); }
        }
        return items
            .OrderByDescending(i => i.Written)
            .Select(i => new
            {
                name = i.Name,
                path = i.Path,
                sizeBytes = i.Size,
                createdAt = i.Written.ToString("o"),
            })
            .ToList();
    }

    private static object ReadInstance(AppServices s, JsonNode? p)
    {
        var inst = RequireInstance(s, p);
        var rawPath = p?.Str("path");
        if (string.IsNullOrWhiteSpace(rawPath)) throw new LauncherException("Файл лога не указан");

        string full;
        try
        {
            full = Path.IsPathRooted(rawPath)
                ? Path.GetFullPath(rawPath)
                : Path.GetFullPath(Path.Combine(inst.LogsDir, rawPath));
        }
        catch (Exception ex) { throw LauncherException.Wrap(ex, "Некорректный путь к файлу лога"); }

        var logsRoot = Path.GetFullPath(inst.LogsDir);
        var crashRoot = Path.GetFullPath(inst.CrashReportsDir);
        if (!IsInside(logsRoot, full) && !IsInside(crashRoot, full))
            throw new LauncherException("Файл вне папки сборки", rawPath);
        if (!File.Exists(full)) throw new LauncherException("Файл лога не найден", Path.GetFileName(full));

        var (raw, whole) = ReadTailLines(full, 4000);
        var matched = ApplyFilters(ParseLines(raw), p?.Str("query"), p?.Str("level"));
        var total = matched.Count;
        var offset = Math.Max(0, p?.Int("offset", 0) ?? 0);
        if (offset > 0 && offset < matched.Count) matched = matched.Skip(offset).ToList();
        var lines = matched.Count > 2000 ? matched.Skip(matched.Count - 2000).ToList() : matched;

        return new
        {
            lines = lines.Select(l => new { ts = l.Ts, level = l.Level, message = l.Message }).ToList(),
            eof = whole,
            total,
        };
    }

    private static bool IsInside(string root, string file)
    {
        var r = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return file.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /* ------------------------------------------------------------ отчёты о сбоях */
    private static List<string> CrashRoots(AppServices s, string? onlyInstanceId)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(onlyInstanceId))
            roots.Add(s.Instances.Require(onlyInstanceId).CrashReportsDir);
        else
        {
            roots.Add(s.Paths.CrashReportsDir);
            foreach (var i in s.Instances.List()) roots.Add(i.CrashReportsDir);
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static object CrashList(AppServices s, JsonNode? p)
    {
        var items = new List<(string Path, long Size, DateTime Written)>();
        foreach (var root in CrashRoots(s, p?.Str("instanceId")))
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var f in Directory.EnumerateFiles(root, "crash-*.txt"))
                {
                    var fi = new FileInfo(f);
                    items.Add((fi.FullName, fi.Length, fi.LastWriteTime));
                }
            }
            catch (Exception ex) { Log.Warn($"Не удалось получить отчёты о сбоях {root}: {ex.Message}"); }
        }
        return items
            .OrderByDescending(i => i.Written)
            .Select(i => new
            {
                id = i.Path,
                path = i.Path,
                createdAt = i.Written.ToString("o"),
                sizeBytes = i.Size,
                analyzed = false,
            })
            .ToList();
    }

    /// <summary>Проверяет, что id — реальный crash-*.txt внутри известной папки отчётов.</summary>
    private static string ResolveCrash(AppServices s, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new LauncherException("Отчёт о сбое не указан");

        string full;
        try { full = Path.GetFullPath(id); }
        catch (Exception ex) { throw LauncherException.Wrap(ex, "Некорректный путь к отчёту"); }

        var name = Path.GetFileName(full);
        if (!name.StartsWith("crash-", StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            throw new LauncherException("Это не отчёт о сбое", name);

        if (!CrashRoots(s, null).Any(r => IsInside(r, full)))
            throw new LauncherException("Файл вне папки отчётов", full);
        if (!File.Exists(full)) throw new LauncherException("Отчёт о сбое не найден", name);
        return full;
    }

    private static string ReadCapped(string path, int capBytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = (int)Math.Min(fs.Length, capBytes);
            if (len <= 0) return "";
            var buf = new byte[len];
            var read = 0;
            while (read < len)
            {
                var n = fs.Read(buf, read, len - read);
                if (n <= 0) break;
                read += n;
            }
            var text = Encoding.UTF8.GetString(buf, 0, read);
            if (read < fs.Length) // обрезали посередине строки — убираем «хвост» без перевода строки
            {
                var nl = text.LastIndexOf('\n');
                if (nl > 0) text = text[..(nl + 1)];
            }
            return text;
        }
        catch (Exception ex)
        {
            throw LauncherException.Wrap(ex, "Не удалось прочитать отчёт о сбое");
        }
    }

    private static object CrashRead(AppServices s, JsonNode? p)
    {
        var file = ResolveCrash(s, p?.Str("id"));
        return new { text = ReadCapped(file, 200 * 1024) };
    }

    private static object CrashAnalyze(AppServices s, JsonNode? p)
    {
        var file = ResolveCrash(s, p?.Str("id"));
        var text = ReadCapped(file, 512 * 1024);
        var (cause, summary, suggestions) = Diagnose(text);
        return new { cause, summary, suggestions };
    }

    /* ------------------------------------------------------------ анализ отчёта */
    private static (string? Cause, string? Summary, List<string> Suggestions) Diagnose(string text)
    {
        string? cause = null;
        string? summary = null;
        var advice = new List<string>();

        bool Has(string token) => text.Contains(token, StringComparison.OrdinalIgnoreCase);

        if (Has("java.lang.OutOfMemoryError") || Has("Java heap space") ||
            Has("GC overhead limit exceeded") || Has("Could not reserve enough space"))
        {
            cause = "Не хватает памяти (OutOfMemoryError)";
            summary = "Игра остановлена из-за нехватки выделенной оперативной памяти: Java-процесс не смог выделить память под мир или моды.";
            advice.Add("Увеличьте память в настройках сборки (4096 МБ и больше)");
            advice.Add("Закройте браузер и тяжёлые программы перед запуском");
        }
        else if (Has("UnsupportedClassVersionError"))
        {
            cause = "Несовместимая версия Java";
            summary = "Классы игры или модов собраны под другую версию Java — установленная Java не подходит для этой сборки.";
            advice.Add("Выберите другую Java в настройках сборки");
            advice.Add("Проверьте рекомендованную версию Java при запуске");
        }
        else if (Has("org.spongepowered.asm.mixin") || Has("Mixin apply failed") ||
                 Has("MixinTargetError") || Has("InvalidMixinException"))
        {
            cause = "Конфликт модов (Mixin)";
            summary = "Один из модов не может быть применён: он конфликтует с другим модом или с текущей версией загрузчика.";
            advice.Add("Отключите недавно добавленные моды");
        }
        else if (Has("NoClassDefFoundError") || Has("ClassNotFoundException") || Has("NoSuchMethodError") ||
                 Has("NoSuchFieldError") || Has("AbstractMethodError") || Has("LinkageError") ||
                 Has("IncompatibleClassChangeError"))
        {
            cause = "Несовместимые моды или загрузчик";
            var missing = Regex.Match(text, @"(NoClassDefFoundError|ClassNotFoundException|NoSuchMethodError)[:\s]+([A-Za-z0-9_.$/]+)");
            summary = "Игра не нашла нужный класс или метод — скорее всего, моды несовместимы с версией Minecraft или загрузчиком."
                      + (missing.Success ? $" Не найдено: {missing.Groups[2].Value.Replace('/', '.')}" : "");
            advice.Add("Обновите или уберите моды, упомянутые в отчёте");
        }
        else if (Has("Mod resolution failed") || Has("Error during mod loading") ||
                 Has("Errors during mod loading") || Has("Circular loading") ||
                 Has("Mod file") && Has("needs language provider") || Has(".fml.ModLoadingException"))
        {
            cause = "Загрузчик модов не смог запустить игру";
            summary = "Ошибка разрешения модов: один или несколько модов не подходят под версию игры или требуют обновления.";
            advice.Add("Обновите загрузчик модов и сами моды");
        }
        else if (Has("UnsatisfiedLinkError") || Has("no lwjgl") || Has("Failed to load the native library"))
        {
            cause = "Не загружаются нативные библиотеки";
            summary = "Игре не удалось подгрузить системные библиотеки (LWJGL) — файлы могли не распаковаться или блокироваться.";
            advice.Add("Запустите лаунчер заново, чтобы повторно распаковать файлы");
        }
        else if (Has("GLFW error") || Has("Couldn't create GL context") || Has("OpenGL") ||
                 Has("PixelFormat") || Has("GL error") || Has("Video driver"))
        {
            cause = "Проблема с видеодрайвером";
            summary = "Игре не удалось создать графический контекст — обычно это устаревшие или повреждённые драйверы видеокарты.";
            advice.Add("Обновите драйверы видеокарты");
            advice.Add("Отключите шейдеры и моды, меняющие графику");
        }
        else if (Has("No space left on device") || Has("There is not enough space") || Has("disk full"))
        {
            cause = "Мало места на диске";
            summary = "Диск, где лежат файлы игры, заполнен — Minecraft не смог записать данные.";
            advice.Add("Освободите место на диске (нужно несколько гигабайт)");
        }
        else if (Has("Access is denied") || Has("The process cannot access the file") ||
                 Has("being used by another process") || Has("Permission denied") ||
                 Has("UnauthorizedAccessException"))
        {
            cause = "Файл занят другим процессом или нет прав на запись";
            summary = "Один из файлов игры недоступен для записи: его держит другой процесс либо не хватает прав на папку.";
            advice.Add("Закройте игру полностью и повторите запуск");
            advice.Add("Убедитесь, что папка игры доступна на запись");
        }
        else if (Has("Invalid session") || Has("session access token") || Has("Failed to authenticate") ||
                 Has("401 Unauthorized") || Has("authentication failed"))
        {
            cause = "Проблема с авторизацией аккаунта";
            summary = "Minecraft не смог подтвердить учётную запись: сессия недействительна или нет доступа к серверам авторизации.";
            advice.Add("Обновите аккаунт во вкладке «Аккаунты»");
        }
        else if (Has("StackOverflowError"))
        {
            cause = "Переполнение стека (конфликт модов)";
            summary = "В коде произошла бесконечная рекурсия — обычно это конфликт модов или ошибка в одном из них.";
            advice.Add("Отключите недавно добавленные моды");
        }

        if (cause is null)
        {
            string? causedBy = null, firstEx = null, description = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("Caused by:", StringComparison.OrdinalIgnoreCase)) causedBy = line; // нижняя строка — первопричина
                if (description is null && line.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
                    description = line["Description:".Length..].Trim().TrimEnd('.');
                if (firstEx is null && line.Length > 0 && !line.StartsWith("at ", StringComparison.Ordinal) &&
                    (line.Contains("Exception") || line.Contains("Error:") ||
                     line.StartsWith("A problem occurred", StringComparison.OrdinalIgnoreCase)))
                    firstEx = line;
            }
            var found = causedBy ?? firstEx;
            if (found is not null)
            {
                cause = Truncate(found, 180);
                summary = (description is not null ? description + ". " : "") +
                          "Minecraft аварийно завершился. Найдите в отчёте блок «Caused by» — нижняя строка обычно указывает на первопричину.";
            }
        }

        advice.Add("Отключите моды и запустите в безопасном режиме");
        advice.Add("Проверьте целостность на вкладке «Файлы»");
        if (cause is null) advice.Add("Откройте полный отчёт кнопкой «Открыть» — там видна цепочка ошибок");

        return (cause, summary, advice.Distinct().ToList());
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
