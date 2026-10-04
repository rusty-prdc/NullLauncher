using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NullLauncher.Accounts;

namespace NullLauncher.Minecraft;

public sealed class CompatIssue
{
    public string Severity { get; set; } = "info"; // info | warn | error
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Fixable { get; set; }
    public string? FixAction { get; set; }
    public bool Fixed { get; set; }
}

public sealed class CompatResult
{
    public bool Ok { get; set; }
    public List<CompatIssue> Issues { get; set; } = new();
    public int FixedCount { get; set; }
}

public sealed class LaunchStatus
{
    public string State { get; set; } = "idle"; // idle|preparing|running|exited|error
    public int? ExitCode { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? Error { get; set; }
    public string? VersionId { get; set; }
}

public sealed class LaunchRequest
{
    public string InstanceId { get; set; } = "";
    public bool SafeMode { get; set; }
    public List<string>? OnlyMods { get; set; }
    public string? ServerIp { get; set; }
    public int? ServerPort { get; set; }
}

/// <summary>
/// Движок запуска Minecraft: проверка совместимости, подготовка файлов, сборка командной строки,
/// запуск процесса, поток логов в UI, обновление статистики инстанса и история запусков.
/// </summary>
public sealed class LaunchService
{
    private readonly Core.AppServices _s;
    private readonly FileProvisioner _provisioner;
    private readonly Dictionary<string, Process> _running = new();
    /// <summary>Запуски, которые ещё готовятся (скачивают файлы) — их тоже можно отменить.</summary>
    private readonly Dictionary<string, CancellationTokenSource> _pending = new();
    private readonly Dictionary<string, LaunchStatus> _status = new();
    private readonly object _sync = new();
    private readonly List<string> _safeModeMoved = new();
    private string? _safeModeInstanceId;

    public LaunchService(Core.AppServices s, FileProvisioner provisioner)
    {
        _s = s;
        _provisioner = provisioner;
        _provisioner.Progress += (stage, step, total, message) =>
            EmitProgress(_currentInstanceId, stage, step, total, message);
    }

    private string _currentInstanceId = "";

    public LaunchStatus Status(string instanceId)
    {
        lock (_sync) return _status.TryGetValue(instanceId, out var st) ? st : new LaunchStatus();
    }

    public bool IsRunning(string instanceId)
    {
        // «идёт подготовка» тоже считается запуском: иначе UI показывал «не запущено»,
        // пока качались файлы, и можно было стартовать второй экземпляр
        lock (_sync) return _running.ContainsKey(instanceId) || _pending.ContainsKey(instanceId);
    }

    /// <summary>Сборки, которые сейчас запускаются или работают (для показа состояния во всём UI).</summary>
    public List<object> RunningList()
    {
        var list = new List<object>();
        lock (_sync)
        {
            var ids = _running.Keys.Concat(_pending.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                var inst = _s.Instances.Get(id);
                _status.TryGetValue(id, out var st);
                list.Add(new
                {
                    instanceId = id,
                    name = inst?.Name ?? id,
                    mcVersion = inst?.McVersion,
                    loader = inst?.Loader,
                    preparing = _pending.ContainsKey(id) && !_running.ContainsKey(id),
                    state = _running.ContainsKey(id) ? "running" : (st?.State ?? "preparing"),
                    startedAt = st?.StartedAt?.ToString("o"),
                    pid = _running.TryGetValue(id, out var p) ? SafePid(p) : (int?)null,
                });
            }
        }
        return list;
    }

    private static int? SafePid(Process p)
    {
        try { return p.HasExited ? null : p.Id; } catch { return null; }
    }

    /// <summary>Сколько сборок можно держать запущенными одновременно (по умолчанию 1).</summary>
    public int MaxRunning => Math.Max(1, _s.Settings.Get("maxRunningInstances", 1));

    /* ------------------------------------------------------------ совместимость */

    public async Task<CompatResult> CompatCheckAsync(string instanceId, bool autoFix, CancellationToken ct)
    {
        var inst = _s.Instances.Require(instanceId);
        var result = new CompatResult { Ok = true };

        void issue(string severity, string title, string message, bool fixable = false, string? action = null)
            => result.Issues.Add(new CompatIssue { Severity = severity, Title = title, Message = message, Fixable = fixable, FixAction = action });

        // Java: точное требование берём из version json (для 26.x это Java 25, а не 21)
        var requiredJava = _s.Versions.RequiredJavaFor(inst.McVersion);
        Java.JavaService.Recommendation? rec = null;
        try { rec = _s.Java.Recommend(inst.McVersion, inst.JavaMode, inst.JavaMajor, inst.JavaPath, requiredJava); }
        catch (Exception ex) { issue("error", "Java не найдена", ex.Message, true, "java.install"); }

        if (rec is not null && rec.Path is null)
            issue("error", $"Не найдена Java {requiredJava}",
                $"{rec.Reason}. Установите Java {requiredJava} в Настройки → Java.",
                true, "java.install");

        else if (rec is not null && rec.Major < requiredJava)
            issue("error", $"Нужна Java {requiredJava}",
                $"Minecraft {inst.McVersion} требует Java {requiredJava}, а найдена только Java {rec.Major}." +
                (inst.JavaMode == "auto"
                    ? " Лаунчер скачает нужную версию автоматически при запуске."
                    : " Смените Java в настройках сборки."),
                true, "java.install");

        // Память
        var (totalMb, freeMb) = Modules.SystemIpc.Memory();
        if (inst.RamMaxMb > totalMb)
            issue("warn", "Слишком большой объём RAM",
                $"Выделено {inst.RamMaxMb} МБ, а всего в системе {totalMb} МБ. Игра может не запуститься.");
        else if (inst.RamMaxMb > freeMb + 512)
            issue("info", "Мало свободной памяти",
                $"Свободно {freeMb} МБ из выделенных {inst.RamMaxMb} МБ. Закройте тяжёлые программы при необходимости.");
        if (inst.RamMaxMb < 1024)
            issue("warn", "Мало памяти для игры", "Рекомендуется не менее 1024 МБ, а для модов — 4096 МБ.");

        // Диск
        var freeDisk = Modules.SystemIpc.FreeDisk(_s.Paths.DataRoot) / 1024.0 / 1024 / 1024;
        if (freeDisk < 5)
            issue("error", "Мало места на диске",
                $"Свободно {freeDisk:0.0} ГБ. Для Minecraft требуется не менее 5 ГБ.");

        // Файлы версии
        var missing = await CountMissingFilesAsync(inst, ct).ConfigureAwait(false);
        if (missing.libs > 0 || missing.versionJson == false)
            issue("warn", "Не все файлы игры скачаны",
                $"Отсутствует библиотек: {missing.libs}, индекс ассетов: {(missing.versionJson ? "есть" : "нет")}",
                true, "repair");

        // Моды против версии
        var modIssues = await ScanModCompatibilityAsync(inst, ct).ConfigureAwait(false);
        foreach (var m in modIssues) issue(m.Severity, m.Title, m.Message);

        if (inst.Loader != "vanilla" && string.IsNullOrEmpty(inst.LoaderVersion) && missing.versionJson)
            issue("info", "Версия загрузчика не выбрана", "Загрузчик будет установлен автоматически при запуске.");

        if (autoFix)
        {
            // если нужной Java нет — скачиваем Temurin автоматически (Требуется Java 25 для 26.x)
            var javaIssues = result.Issues.Where(x => x.FixAction == "java.install" && x.Severity == "error").ToList();
            if (javaIssues.Count > 0 && inst.JavaMode == "auto" && _s.Settings.Get("javaAuto", true))
            {
                try
                {
                    await EnsureJavaAsync(requiredJava, ct).ConfigureAwait(false);
                    foreach (var i in javaIssues)
                    {
                        i.Fixed = true;
                        i.Severity = "info";
                        i.Message = $"Java {requiredJava} установлена автоматически";
                    }
                    result.FixedCount++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Автоматическая установка Java {requiredJava}: {ex.Message}");
                    foreach (var i in javaIssues)
                        i.Message += $"\nАвтоматическая установка не удалась: {ex.Message}";
                }
            }

            foreach (var i in result.Issues.Where(x => x.Fixable && !x.Fixed).ToList())
            {
                try
                {
                    if (i.FixAction == "repair")
                    {
                        await PrepareFilesAsync(inst, ct).ConfigureAwait(false);
                        i.Fixed = true;
                        result.FixedCount++;
                    }
                }
                catch (Exception ex) { i.Message += $"\nНе удалось исправить: {ex.Message}"; }
            }
        }

        result.Ok = !result.Issues.Any(i => i.Severity == "error");
        return result;
    }

    /// <summary>Гарантирует, что в системе есть Java нужной версии: при отсутствии качает Temurin.</summary>
    private async Task EnsureJavaAsync(int major, CancellationToken ct)
    {
        if (_s.Java.Detect().Any(j => j.Valid && j.Major >= major)) return;
        Log.Info($"Java {major} не найдена — скачиваю Temurin через Adoptium");
        _s.Emit("java.install.progress", new { major, progress = 0.0 });
        var progress = new Progress<double>(v => _s.Emit("java.install.progress", new { major, progress = v }));
        var res = await _s.Java.InstallAsync(major, progress, ct).ConfigureAwait(false);
        _s.Emit("java.install.progress", new { major, progress = 1.0, done = true });
        Log.Info($"Установлена Java {res.Major} ({res.Version}) → {res.Path}");
    }

    private async Task PrepareFilesAsync(Instances.InstanceRecord inst, CancellationToken ct)
    {
        await _provisioner.PrepareAsync(inst.McVersion, inst.Loader, inst.LoaderVersion, inst.Id, ct)
            .ConfigureAwait(false);
    }

    private async Task<(int libs, bool versionJson)> CountMissingFilesAsync(Instances.InstanceRecord inst, CancellationToken ct)
    {
        try
        {
            var path = _s.Versions.VersionJsonPath(inst.McVersion);
            if (!File.Exists(path)) return (0, false);
            var node = VersionNode.Load(path);
            var chain = new List<VersionNode> { node };
            var cur = node;
            while (!string.IsNullOrEmpty(cur.InheritsFrom))
            {
                var p = _s.Versions.VersionJsonPath(cur.InheritsFrom);
                if (!File.Exists(p)) break;
                cur = VersionNode.Load(p);
                chain.Add(cur);
            }
            var missing = 0;
            foreach (var n in chain)
                foreach (var lib in n.ApplicableLibraries())
                {
                    var rel = lib.RelativePath;
                    if (string.IsNullOrEmpty(rel)) continue;
                    var f = Path.Combine(_s.Paths.LibrariesDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(f)) missing++;
                }
            return (missing, true);
        }
        catch { return (0, false); }
    }

    private async Task<List<CompatIssue>> ScanModCompatibilityAsync(Instances.InstanceRecord inst, CancellationToken ct)
    {
        var issues = new List<CompatIssue>();
        try
        {
            if (!Directory.Exists(inst.ModsDir)) return issues;
            foreach (var file in Directory.EnumerateFiles(inst.ModsDir)
                         .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).Take(80))
            {
                ct.ThrowIfCancellationRequested();
                var declared = await ReadDeclaredMcAsync(file).ConfigureAwait(false);
                if (declared is null) continue;
                if (!MatchesMc(declared, inst.McVersion))
                    issues.Add(new CompatIssue
                    {
                        Severity = "warn",
                        Title = $"{Path.GetFileName(file)} может не подходить",
                        Message = $"Мод рассчитан на {declared}, инстанс использует {inst.McVersion}",
                    });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* парсинг манифестов модов не должен валить запуск */ }
        return issues;
    }

    /// <summary>Версия Minecraft, заявленная в манифесте мода (fabric.mod.json / mods.toml).</summary>
    public static async Task<string?> ReadDeclaredMcAsync(string jarPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(jarPath);
            var fabric = zip.GetEntry("fabric.mod.json");
            if (fabric is not null)
            {
                using var s = fabric.Open();
                using var sr = new StreamReader(s);
                var json = JsonNode.Parse(await sr.ReadToEndAsync().ConfigureAwait(false));
                var mc = json?["depends"]?["minecraft"] ?? json?["minecraft"];
                var v = mc?.GetValue<string>();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
            var forge = zip.GetEntry("META-INF/mods.toml");
            if (forge is not null)
            {
                using var s = forge.Open();
                using var sr = new StreamReader(s);
                var text = await sr.ReadToEndAsync().ConfigureAwait(false);
                var m = System.Text.RegularExpressions.Regex.Match(text,
                    @"minecraftVersion\s*=\s*""(?<v>[^""]+)""");
                if (m.Success) return m.Groups["v"].Value;
            }
        }
        catch { /* не jar или повреждён */ }
        return null;
    }

    private static bool MatchesMc(string declared, string actual)
    {
        // поддерживаем простые случаи: "1.21", "1.21.x", ">=1.20.1", "[1.20,1.21)", "1.20.1-1.21"
        declared = declared.Trim();
        if (declared.EndsWith(".x", StringComparison.OrdinalIgnoreCase))
            return actual.StartsWith(declared[..^2], StringComparison.OrdinalIgnoreCase);
        if (declared.Contains('*')) return actual.StartsWith(declared.Replace("*", ""), StringComparison.OrdinalIgnoreCase);
        if (declared.Contains("..") || declared.Contains(",") || declared.StartsWith('[') || declared.StartsWith('('))
        {
            var nums = System.Text.RegularExpressions.Regex.Matches(declared, @"\d+(\.\d+)+")
                .Select(m => m.Value).ToList();
            if (nums.Count == 0) return true;
            var cmp = CompareVersion(actual, nums[0]) >= 0;
            if (nums.Count > 1)
            {
                var upper = CompareVersion(actual, nums[1]);
                cmp = cmp && (declared.Contains(']') || declared.Contains(')') ? upper < 0 : upper <= 0);
            }
            return cmp;
        }
        if (declared.Contains('-'))
        {
            var parts = declared.Split('-');
            return CompareVersion(actual, parts[0]) >= 0 &&
                   CompareVersion(actual, parts[1]) <= 0;
        }
        return actual.Equals(declared, StringComparison.OrdinalIgnoreCase) ||
               actual.StartsWith(declared, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareVersion(string a, string b)
    {
        var pa = a.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var pb = b.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /* ------------------------------------------------------------ запуск */

    public async Task StartAsync(LaunchRequest req, CancellationToken ct, TaskCompletionSource? started = null)
    {
        var inst = _s.Instances.Require(req.InstanceId);
        var launchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_sync)
        {
            if (_running.ContainsKey(req.InstanceId) || _pending.ContainsKey(req.InstanceId))
                throw new LauncherException("Эта сборка уже запускается или запущена", inst.Name);

            // лимит одновременных запусков: по умолчанию только одна сборка
            var limit = MaxRunning;
            var busy = _running.Keys.Concat(_pending.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (busy.Count >= limit)
            {
                var otherId = busy[0];
                var otherName = _s.Instances.Get(otherId)?.Name ?? otherId;
                var what = _running.ContainsKey(otherId) ? "запущена" : "запускается";
                throw new LauncherException("Уже запущена другая сборка",
                    $"Сейчас {what} «{otherName}». Можно держать запущенной только {limit} сборку — остановите её и попробуйте снова.");
            }

            _pending[req.InstanceId] = launchCts;
            _status[req.InstanceId] = new LaunchStatus { State = "preparing", StartedAt = DateTime.UtcNow };
        }
        _currentInstanceId = req.InstanceId;
        ct = launchCts.Token;
        // сразу сообщаем всем страницам, что сборка запускается (иначе UI считал, что ничего не происходит)
        _s.Emit("launch.state", new
        {
            instanceId = req.InstanceId,
            name = inst.Name,
            state = "preparing",
            exitCode = (int?)null,
            error = (string?)null,
        });

        var safeMode = req.SafeMode || inst.SafeMode || _s.Settings.Get("safeModeByDefault", false);
        var state = Status(req.InstanceId);
        var startedWall = DateTime.UtcNow;
        string? error = null;

        try
        {
            EmitProgress(req.InstanceId, "Подготовка", 0, 6, "Проверка совместимости");
            var check = await CompatCheckAsync(req.InstanceId, autoFix: true, ct).ConfigureAwait(false);
            var blocking = check.Issues.Where(i => i.Severity == "error").ToList();
            if (blocking.Count > 0)
                throw new LauncherException(blocking[0].Title, string.Join("\n", blocking.Select(b => b.Message)));

            EmitProgress(req.InstanceId, "Подготовка", 1, 6, "Файлы игры");
            var (versionId, node) = await _provisioner.PrepareAsync(
                inst.McVersion, inst.Loader, inst.LoaderVersion, inst.Id, ct).ConfigureAwait(false);

            var account = await _s.Accounts.ResolveForLaunchAsync(inst.AccountUuid, ct).ConfigureAwait(false);

            EmitProgress(req.InstanceId, "Запуск", 5, 6, "Сборка команды");
            var args = BuildArguments(inst, versionId, node, account, safeMode, req);

            var javaExe = ResolveJavaExe(inst, node);
            var jvmExtra = BuildExtraJvmArgs(inst);

            // рабочая папка должна существовать: иначе Process.Start падает с Win32 error 267
            var workDir = inst.GameDir;
            try { Directory.CreateDirectory(workDir); }
            catch (Exception ex)
            {
                Log.Warn($"Не удалось создать {workDir}: {ex.Message} — запускаю из {inst.Dir}");
                workDir = inst.Dir;
                Directory.CreateDirectory(workDir);
            }

            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var kv in BuildEnv(inst)) psi.Environment[kv.Key] = kv.Value;
            foreach (var a in jvmExtra) psi.ArgumentList.Add(a);

            /* конфиг логирования Mojang: -Dlog4j.configurationFile=${path} */
            var (logArg, logPath) = _provisioner.GetLoggingInfo(versionId);
            if (!string.IsNullOrEmpty(logArg) && !string.IsNullOrEmpty(logPath))
                psi.ArgumentList.Add(logArg.Replace("${path}", logPath));

            foreach (var a in args) psi.ArgumentList.Add(a);

            // полная командная строка в лог: по ней видно, какие аргументы реально ушли в игру
            // (токен доступа в лог не пишем)
            var cmdLine = string.Join(' ', psi.ArgumentList);
            if (!string.IsNullOrEmpty(account.AccessToken))
                cmdLine = cmdLine.Replace(account.AccessToken, "***");
            Log.Info($"Запуск '{inst.Name}' ({inst.McVersion}, {inst.Loader}, Java: {javaExe}):");
            Log.Info($"  {javaExe} {cmdLine}");

            if (safeMode) EnableSafeMode(inst);

            var proc = Process.Start(psi)
                       ?? throw new LauncherException("Не удалось запустить процесс Java", javaExe);

            lock (_sync)
            {
                _running[req.InstanceId] = proc;
                _status[req.InstanceId] = new LaunchStatus
                {
                    State = "running", StartedAt = startedWall, VersionId = versionId,
                };
            }
            EmitProgress(req.InstanceId, "Запуск", 6, 6, "Minecraft запущен");
            _s.Emit("launch.state", new { instanceId = req.InstanceId, state = "running", exitCode = (int?)null });
            started?.TrySetResult();

            var logFile = Path.Combine(inst.LogsDir, $"launcher-{DateTime.Now:yyyyMMdd_HHmmss}.log");
            Directory.CreateDirectory(inst.LogsDir);

            proc.OutputDataReceived += (_, e) => ForwardLog(req.InstanceId, e.Data, "INFO", logFile);
            proc.ErrorDataReceived += (_, e) => ForwardLog(req.InstanceId, e.Data, "ERROR", logFile);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var exit = proc.ExitCode;

            var durationMs = (long)(DateTime.UtcNow - startedWall).TotalMilliseconds;
            lock (_sync)
            {
                _running.Remove(req.InstanceId);
                _status[req.InstanceId] = new LaunchStatus
                {
                    State = exit == 0 ? "exited" : "error",
                    ExitCode = exit, StartedAt = startedWall, EndedAt = DateTime.UtcNow,
                    VersionId = versionId,
                    Error = exit == 0 ? null : $"Код возврата {exit}",
                };
            }

            await UpdateStatsAsync(inst, durationMs, exit, safeMode, account: null).ConfigureAwait(false);
            _s.Emit("launch.state", new
            {
                instanceId = req.InstanceId,
                state = exit == 0 ? "exited" : "error",
                exitCode = exit,
                error = exit == 0 ? null : $"Minecraft завершился с ошибкой (код {exit})",
            });
            Log.Info($"Minecraft завершён: exit={exit}, время {durationMs} мс");
        }
        catch (OperationCanceledException) when (launchCts.IsCancellationRequested)
        {
            // пользователь нажал «Остановить» во время подготовки (скачивание файлов) — это не ошибка
            lock (_sync)
            {
                _running.Remove(req.InstanceId);
                _status[req.InstanceId] = new LaunchStatus
                {
                    State = "idle", StartedAt = startedWall, EndedAt = DateTime.UtcNow,
                };
            }
            _s.Emit("launch.state", new { instanceId = req.InstanceId, state = "idle", exitCode = (int?)null, error = (string?)null });
            Log.Info($"Запуск отменён ({inst.Name})");
            started?.TrySetCanceled();
        }
        catch (Exception ex)
        {
            error = ex is LauncherException le ? le.UserMessage : ex.Message;
            lock (_sync)
            {
                _running.Remove(req.InstanceId);
                _status[req.InstanceId] = new LaunchStatus
                {
                    State = "error", StartedAt = startedWall, EndedAt = DateTime.UtcNow, Error = error,
                };
            }
            _s.Emit("launch.state", new { instanceId = req.InstanceId, state = "error", exitCode = (int?)null, error });
            _s.NotifyUser("error", "Не удалось запустить Minecraft", error);
            Log.Error($"Запуск {inst.Name} не удался", ex);
            started?.TrySetException(ex is LauncherException ? ex
                : new LauncherException("Не удалось запустить Minecraft", ex.Message, ex));
            throw;
        }
        finally
        {
            if (_safeModeInstanceId == inst.Id) DisableSafeMode(inst);
            lock (_sync) _pending.Remove(req.InstanceId);
            launchCts.Dispose();
        }
    }

    private void ForwardLog(string instanceId, string? line, string level, string logFile)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var lvl = level == "ERROR" ? "ERROR"
            : line.Contains("[ERROR]") || line.Contains("Exception") || line.Contains("FATAL") ? "ERROR"
            : line.Contains("[WARN]") || line.Contains("Warning") ? "WARN"
            : line.Contains("[DEBUG]") ? "DEBUG" : "INFO";
        try { File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] [{lvl}] {line}\n"); }
        catch { /* диск может быть занят */ }
        _s.Emit("launch.log", new { instanceId, line, level = lvl });
    }

    /* ------------------------------------------------------------ командная строка */

    private Dictionary<string, string> BuildMap(Instances.InstanceRecord inst, string versionId,
        VersionNode node, AccountSession account, LaunchRequest req, string nativesDir, string classpath)
    {
        var gameDir = inst.GameDir;
        var assetIndex = node.AssetIndexId ?? node.Assets ?? "legacy";
        var quickPlay = req.ServerIp is not null ? $"{req.ServerIp}:{req.ServerPort ?? 25565}" : "";
        return new Dictionary<string, string>
        {
            ["auth_player_name"] = account.Name,
            ["version_name"] = versionId,
            ["game_directory"] = gameDir,
            ["assets_root"] = _s.Paths.AssetsDir,
            ["assets_index_name"] = assetIndex,
            ["auth_uuid"] = account.Uuid,
            ["auth_access_token"] = account.AccessToken,
            ["auth_session"] = account.AccessToken,
            ["user_type"] = account.Type == "microsoft" ? "msa" : "legacy",
            ["user_properties"] = "{}",
            ["version_type"] = node.Type,
            ["natives_directory"] = nativesDir,
            ["launcher_name"] = "nulllauncher",
            ["launcher_version"] = "1.0.0",
            ["classpath"] = classpath,
            ["library_directory"] = _s.Paths.LibrariesDir,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["clientid"] = "",
            ["auth_xuid"] = account.Xuid ?? "",
            ["resolution_width"] = inst.Width.ToString(),
            ["resolution_height"] = inst.Height.ToString(),
            ["game_assets"] = _s.Paths.AssetsDir,
            ["quickPlayMultiplayer"] = quickPlay,
            ["quickPlaySingleplayer"] = "",
            ["quickPlayRealms"] = "",
            ["quickPlayPath"] = Path.Combine(inst.Dir, "quickplay.log"),
            ["quickPlayDemo"] = "",
        };
    }

    /// <summary>
    /// Возможности лаунчера для правил Minecraft. is_demo_user всегда false — иначе игра
    /// уходит в демо-режим с лимитом 100 минут («бесплатная версия на полтора часа»).
    /// </summary>
    private static Dictionary<string, bool> FeatureSet(LaunchRequest req)
    {
        var server = req.ServerIp is not null;
        return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["is_demo_user"] = false,
            ["has_custom_resolution"] = true,
            ["has_quick_plays_support"] = server,
            ["is_quick_play_singleplayer"] = false,
            ["is_quick_play_multiplayer"] = server,
            ["is_quick_play_realms"] = false,
        };
    }

    private List<string> BuildArguments(Instances.InstanceRecord inst, string versionId, VersionNode node,
        AccountSession account, bool safeMode, LaunchRequest req)
    {
        var nativesDir = _provisioner.NativesDir(versionId);
        var classpath = BuildClasspath(inst, versionId, node);
        var map = BuildMap(inst, versionId, node, account, req, nativesDir, classpath);
        var features = FeatureSet(req);
        var result = new List<string>();

        // JVM-аргументы из version json
        if (node.JvmArgs.Count > 0)
        {
            result.AddRange(node.JvmArgsFor(map, features));
        }
        else
        {
            result.Add("-Djava.library.path=" + nativesDir);
            result.Add("-cp");
            result.Add(classpath);
        }

        // main class
        result.Add(node.MainClass);

        // game-аргументы
        if (!string.IsNullOrEmpty(node.MinecraftArguments))
        {
            foreach (var token in node.MinecraftArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var v = token;
                foreach (var (k, val) in map) v = v.Replace("${" + k + "}", val);
                if (!v.Contains("${", StringComparison.Ordinal)) result.Add(v);
            }
        }
        else
        {
            result.AddRange(node.GameArgsFor(map, features));
        }

        // окно: --width/--height уже приходят из version json (has_custom_resolution=true),
        // свои добавляем только если версия их вообще не поддерживает
        var versionHandlesResolution = (node.MinecraftArguments?.Contains("${resolution_width}") ?? false)
            || node.GameArgs.Any(a => a.Value.Contains("${resolution_width}"));
        if (!versionHandlesResolution)
            result.AddRange(new[] { "--width", inst.Width.ToString(), "--height", inst.Height.ToString() });

        // окно
        if (inst.WindowMode == "fullscreen" || inst.Fullscreen)
        {
            if (!result.Contains("--fullscreen")) result.Add("--fullscreen");
        }

        // подключение к серверу для старых версий (новые используют quickPlayMultiplayer)
        if (req.ServerIp is not null &&
            !node.GameArgs.Any(a => a.Value.Contains("${quickPlayMultiplayer}")))
        {
            result.AddRange(new[] { "--server", req.ServerIp, "--port", (req.ServerPort ?? 25565).ToString() });
        }

        // пользовательские аргументы
        if (!string.IsNullOrWhiteSpace(inst.JvmArgs))
        {
            var extra = SplitArgs(inst.JvmArgs);
            // вставляем пользовательские JVM-аргументы перед main class (первый не-флаг)
            var idx = result.FindIndex(a => !a.StartsWith('-'));
            if (idx < 0) idx = result.Count;
            result.InsertRange(idx, extra);
        }

        if (!string.IsNullOrWhiteSpace(inst.GameArgs))
            result.AddRange(SplitArgs(inst.GameArgs));

        // маркер безопасного режима не передаём в игру: неизвестный аргумент может её сломать
        if (safeMode) Log.Info("Безопасный режим: моды отключены на время запуска");

        return result;
    }

    private static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        foreach (var c in s)
        {
            if (c == '"') { inQuote = !inQuote; continue; }
            if (char.IsWhiteSpace(c) && !inQuote)
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    private string BuildClasspath(Instances.InstanceRecord inst, string versionId, VersionNode node)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddLib(Library lib)
        {
            if (lib.IsNative) return;
            var rel = lib.RelativePath;
            if (string.IsNullOrEmpty(rel)) return;
            var f = Path.Combine(_s.Paths.LibrariesDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(f) && seen.Add(f)) parts.Add(f);
        }

        // цепочка version json (наследники → база)
        var chain = new List<VersionNode> { node };
        var cur = node;
        while (!string.IsNullOrEmpty(cur.InheritsFrom))
        {
            var p = _s.Versions.VersionJsonPath(cur.InheritsFrom);
            if (!File.Exists(p)) break;
            cur = VersionNode.Load(p);
            chain.Add(cur);
        }

        foreach (var n in chain)
            foreach (var lib in n.ApplicableLibraries())
                AddLib(lib);

        // jar клиента
        foreach (var n in chain)
        {
            var jar = Path.Combine(_s.Paths.VersionsDir, n.Id, n.Id + ".jar");
            if (File.Exists(jar) && seen.Add(jar)) { parts.Add(jar); break; }
        }
        var topJar = Path.Combine(_s.Paths.VersionsDir, versionId, versionId + ".jar");
        if (File.Exists(topJar) && seen.Add(topJar)) parts.Add(topJar);

        if (parts.Count == 0)
            throw new LauncherException("Класспуть пуст — файлы игры не скачаны",
                "Нажмите «Проверить целостность» на вкладке «Файлы»");

        return string.Join(';', parts);
    }

    private string ResolveJavaExe(Instances.InstanceRecord inst, VersionNode node)
    {
        var required = node.JavaMajor > 0 ? node.JavaMajor : _s.Versions.RequiredJavaFor(inst.McVersion);

        if (inst.JavaMode == "path" && !string.IsNullOrWhiteSpace(inst.JavaPath))
        {
            if (!File.Exists(inst.JavaPath))
                throw new LauncherException("Указанная Java не найдена", inst.JavaPath);
            var p = _s.Java.Validate(inst.JavaPath);
            if (p is not null && p.Major < required)
                throw new LauncherException($"Нужна Java {required}",
                    $"Для Minecraft {inst.McVersion} выбрана Java {p.Major} ({inst.JavaPath}). Укажите Java {required} или включите автоматический выбор.");
            return inst.JavaPath;
        }
        if (inst.JavaMode == "major" && inst.JavaMajor is int m)
        {
            var found = _s.Java.Detect().FirstOrDefault(j => j.Major == m && j.Valid);
            if (found is null)
                throw new LauncherException($"Java {m} не найдена", "Установите её в Настройки → Java");
            if (found.Major < required)
                throw new LauncherException($"Нужна Java {required}",
                    $"В сборке вручную выбрана Java {found.Major}, а Minecraft {inst.McVersion} требует Java {required}.");
            return found.Path;
        }

        // auto: точное совпадение, иначе ближайшая более новая
        var all = _s.Java.Detect().Where(j => j.Valid).ToList();
        var best = all.Where(j => j.Major >= required).OrderBy(j => j.Major).FirstOrDefault();
        if (best is not null) return best.Path;

        var newest = all.OrderByDescending(j => j.Major).FirstOrDefault();
        throw new LauncherException($"Не найдена Java {required}",
            newest is null
                ? $"Для Minecraft {inst.McVersion} нужна Java {required}. Установите её в Настройки → Java."
                : $"Для Minecraft {inst.McVersion} нужна Java {required}, а самая новая в системе — Java {newest.Major}. " +
                  $"Установите Java {required} в Настройки → Java.");
    }

    private IEnumerable<string> BuildExtraJvmArgs(Instances.InstanceRecord inst)
    {
        var args = new List<string>
        {
            $"-Xms{inst.RamMinMb}M",
            $"-Xmx{inst.RamMaxMb}M",
            "-XX:+UseG1GC",
            "-XX:+UnlockExperimentalVMOptions",
            "-XX:G1NewSizePercent=20",
            "-XX:G1ReservePercent=20",
            "-XX:MaxGCPauseMillis=50",
            "-XX:G1HeapRegionSize=32M",
        };
        if (inst.RamMaxMb >= 4096)
        {
            args.Add("-XX:+IgnoreUnrecognizedVMOptions");
            args.Add("-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump");
        }
        return args;
    }

    private Dictionary<string, string> BuildEnv(Instances.InstanceRecord inst)
    {
        var env = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(inst.EnvVars))
        {
            foreach (var line in inst.EnvVars.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = line.IndexOf('=');
                if (idx <= 0) continue;
                var k = line[..idx].Trim();
                var v = line[(idx + 1)..].Trim();
                if (k.Length > 0) env[k] = v;
            }
        }
        // JAVA_TOOL_OPTIONS принудительно не выставляем: пустое значение заставляет java писать
        // «Picked up JAVA_TOOL_OPTIONS:» в лог игры и портит вывод ошибок
        return env;
    }

    /* ------------------------------------------------------------ безопасный режим */

    private void EnableSafeMode(Instances.InstanceRecord inst)
    {
        try
        {
            _safeModeMoved.Clear();
            if (!Directory.Exists(inst.ModsDir)) return;
            foreach (var f in Directory.EnumerateFiles(inst.ModsDir, "*.jar"))
            {
                var target = f + ".disabled";
                File.Move(f, target, true);
                _safeModeMoved.Add(target);
            }
            _safeModeInstanceId = inst.Id;
            Log.Info($"Безопасный режим: отключено модов {_safeModeMoved.Count}");
        }
        catch (Exception ex) { Log.Warn($"Безопасный режим: {ex.Message}"); }
    }

    private void DisableSafeMode(Instances.InstanceRecord inst)
    {
        try
        {
            foreach (var target in _safeModeMoved)
            {
                if (File.Exists(target))
                    File.Move(target, target[..^9], true);
            }
            _safeModeMoved.Clear();
            _safeModeInstanceId = null;
            Log.Info("Безопасный режим завершён, моды возвращены");
        }
        catch (Exception ex) { Log.Warn($"Возврат модов: {ex.Message}"); }
    }

    /* ------------------------------------------------------------ статистика и история */

    private async Task UpdateStatsAsync(Instances.InstanceRecord inst, long durationMs, int exit, bool safeMode, object? account)
    {
        await Task.Run(() =>
        {
            try
            {
                var rec = _s.Instances.Require(inst.Id);
                rec.LastPlayedAt = DateTime.UtcNow;
                rec.PlayTimeMs += Math.Max(0, durationMs);
                rec.LaunchCount++;
                _s.Instances.SaveMetadata(rec);
                Database.DatabaseService.Execute("""
                    UPDATE instances SET last_played_at=@LastPlayedAt, play_time_ms=@PlayTimeMs, launch_count=@LaunchCount
                    WHERE id=@Id
                    """, new
                {
                    LastPlayedAt = rec.LastPlayedAt.Value.ToString("o"),
                    rec.PlayTimeMs, rec.LaunchCount, rec.Id,
                });
                Database.DatabaseService.Execute("""
                    INSERT INTO launch_history (instance_id, started_at, ended_at, exit_code, duration_ms, safe_mode, error, account_uuid)
                    VALUES (@InstanceId, @StartedAt, @EndedAt, @ExitCode, @DurationMs, @SafeMode, @Error, NULL)
                    """, new
                {
                    InstanceId = rec.Id,
                    StartedAt = DateTime.UtcNow.AddMilliseconds(-durationMs).ToString("o"),
                    EndedAt = DateTime.UtcNow.ToString("o"),
                    ExitCode = exit,
                    DurationMs = durationMs,
                    SafeMode = safeMode ? 1 : 0,
                    Error = exit == 0 ? null : $"exit {exit}",
                });
                _s.Emit("instance.changed", new { id = inst.Id });
            }
            catch (Exception ex) { Log.Warn($"Не удалось обновить статистику: {ex.Message}"); }
        }).ConfigureAwait(false);
    }

    /* ------------------------------------------------------------ остановка */

    /// <summary>
    /// Останавливает игру или отменяет подготовку запуска.
    /// Возвращает, что именно произошло: stopped — удалось, wasPreparing — отменена подготовка,
    /// wasRunning — был остановлен процесс игры.
    /// </summary>
    public (bool stopped, bool wasPreparing, bool wasRunning) Stop(string instanceId)
    {
        Process? proc;
        CancellationTokenSource? preparing;
        lock (_sync)
        {
            _running.TryGetValue(instanceId, out proc);
            _pending.TryGetValue(instanceId, out preparing);
        }

        if (proc is null && preparing is null)
            return (false, false, false);   // ничего не запущено — не ошибка, UI просто обновит состояние

        if (proc is null)
        {
            try { preparing!.Cancel(); } catch { /* уже отменён */ }
            Log.Info($"Подготовка запуска отменена ({instanceId})");
            return (true, true, false);
        }

        try
        {
            if (!proc.CloseMainWindow()) proc.Kill(entireProcessTree: true);
            else if (!proc.WaitForExit(10_000)) proc.Kill(entireProcessTree: true);
            Log.Info($"Процесс Minecraft остановлен ({instanceId})");
            return (true, false, true);
        }
        catch (Exception ex) { throw new LauncherException("Не удалось остановить игру", ex.Message, ex); }
    }

    private void EmitProgress(string instanceId, string stage, int step, int total, string message)
    {
        if (string.IsNullOrEmpty(instanceId)) return;
        var stats = _s.Downloads.Stats();
        _s.Emit("launch.progress", new
        {
            instanceId, stage, step, total, message,
            percent = total > 0 ? (int)(100.0 * step / total) : 0,
            speedBps = (long)stats.speedBps,
            etaS = (int)stats.etaS,
            receivedBytes = stats.received,
            totalBytes = stats.total,
            downloads = stats.active,
        });
    }
}
