using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using NullLauncher.Core;

namespace NullLauncher;

public partial class App : System.Windows.Application
{
    private AppServices? _services;
    private MainWindow? _window;
    private Mutex? _singleInstance;
    private const string PipeName = "NullLauncher.deeplink";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // один экземпляр: второй (например, приход через nulllauncher://auth?token=…)
        // передаёт deep-link работающему окну и завершается
        _singleInstance = new Mutex(true, @"Local\NullLauncherSingle", out var isNew);
        if (!isNew)
        {
            ForwardArgsToRunningInstance(e.Args);
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Необработанная ошибка приложения", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Наблюдаемая ошибка задачи", args.Exception);
            args.SetObserved();
        };

        try
        {
            _services = new AppServices();
            _window = new MainWindow(_services.Router, _services.Paths);

            _services.Router.Register("__ping", (_, _) => Task.FromResult<object?>("pong"));
            _services.Notify += (evt, data) => _window.PostEvent(evt, data);
            _window.MessageReceived += (method, parameters, ct) => _services.Router.DispatchAsync(method, parameters, ct);
            _window.EventRequested += (evt, data) => OnWindowEvent(evt, data);

            MainWindow = _window;
            _window.Show();

            Log.Info($"NullLauncher запущен (данные: {_services.Paths.DataRoot})");
            StartupChecks();
            HandleDeepLink(e.Args);
            _ = Task.Run(ListenDeepLinks);
        }
        catch (Exception ex)
        {
            Log.Error("Критическая ошибка при запуске", ex);
            WriteStartupReport(ex);
            var hint = StartupHint(ex);
            System.Windows.MessageBox.Show(
                "NullLauncher не удалось запустить.\n\n" + ex.Message +
                (hint is null ? "" : "\n\n" + hint) +
                "\n\nПодробности: " + Log.CurrentFile,
                "NullLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Если папка данных недоступна, лог тоже недоступен — дублируем отчёт в несколько мест
    /// (TEMP, папка данных, папка лаунчера) и записываем, куда запись удалась, а куда нет.
    /// </summary>
    private static void WriteStartupReport(Exception ex)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppPaths.AppFolderName);
            const string name = "nulllauncher-startup-error.log";
            var targets = new[]
            {
                Path.Combine(Path.GetTempPath(), name),
                Path.Combine(root, name),
                Path.Combine(AppContext.BaseDirectory, name),
                Path.Combine(root, "logs", name),
            };

            var body = "NullLauncher: сбой запуска " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                       "\r\nexe: " + (Environment.ProcessPath ?? "?") +
                       "\r\nпапка данных: " + root +
                       "\r\nTEMP: " + Path.GetTempPath() +
                       "\r\nпользователь: " + Environment.UserName + ", x64=" + Environment.Is64BitProcess +
                       "\r\n\r\n" + ex + "\r\n";

            var ok = new List<string>();
            var fail = new List<string>();
            foreach (var target in targets)
            {
                try
                {
                    var dir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(target, body);
                    ok.Add(target);
                }
                catch (Exception tex)
                {
                    fail.Add(target + " — " + tex.GetType().Name + ": " + tex.Message);
                }
            }

            var full = body + "\r\n--- результаты записи отчёта ---\r\nOK: " + string.Join("; ", ok) +
                       "\r\nFAIL: " + string.Join("; ", fail) + "\r\n";
            foreach (var target in ok)
            {
                try { File.WriteAllText(target, full); } catch { /* уже записали хотя бы тело */ }
            }
        }
        catch { /* отчёт не критичен */ }
    }

    /// <summary>Понятная подсказка для типовых сбоев запуска (доступ к папке данных, база занята).</summary>
    private static string? StartupHint(Exception ex)
    {
        var access = ex is Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException
                     || ex.InnerException is UnauthorizedAccessException;
        if (!access) return null;

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppPaths.AppFolderName);
        var exe = Environment.ProcessPath ?? "NullLauncher.exe";
        return "Нет доступа на запись в папку данных лаунчера:\n" + root +
               "\n\nТак бывает, если лаунчер запущен из ограниченной среды — например, кнопкой «открыть» " +
               "в окне чата или из другого приложения в песочнице. Закройте его и запустите обычным способом:\n" +
               "Win+R → вставьте путь → Enter:\n" + exe +
               "\n\nЕсли запускаете из Проводника, а ошибка повторяется — её блокирует антивирус или права " +
               "на папку (проверьте, что папка доступна для записи). Также закройте вторую копию лаунчера, " +
               "если она уже открыта.";
    }

    private void OnWindowEvent(string evt, object? data)
    {
        if (evt == "files.dropped" && data is not null)
            _services?.Emit("files.dropped", data);
    }

    /// <summary>Разбирает deep-link вида nulllauncher://join?ip=play.example.com&port=25565,
    /// переданный аргументом командной строки (регистрация протокола nulllauncher в инсталляторе).
    /// После загрузки интерфейса шлёт событие app.deepLink — UI спросит и подключится.</summary>
    private void HandleDeepLink(string[] args)
    {
        try
        {
            var url = args.FirstOrDefault(a => a.StartsWith("nulllauncher://", StringComparison.OrdinalIgnoreCase));
            if (url is null) return;

            var query = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
            var action = query;
            var qIdx = query.IndexOf('?');
            string paramPart = "";
            if (qIdx >= 0) { action = query[..qIdx]; paramPart = query[(qIdx + 1)..]; }
            var slash = action.IndexOf('/');
            if (slash >= 0) action = action[..slash];
            action = action.TrimEnd('/');

            if (action.Equals("auth", StringComparison.OrdinalIgnoreCase))
            {
                // вход через Ely.by: nulllauncher://auth?token=…
                string? authToken = null;
                foreach (var pair in paramPart.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split('=', 2);
                    if (kv.Length == 2 &&
                        Uri.UnescapeDataString(kv[0]).Equals("token", StringComparison.OrdinalIgnoreCase))
                        authToken = Uri.UnescapeDataString(kv[1]);
                }
                if (string.IsNullOrWhiteSpace(authToken))
                {
                    Log.Warn("Deep-link auth без токена — пропуск");
                    return;
                }
                Log.Info("Deep-link: вход через Ely.by — применяю токен");
                var svc = _services;
                _ = Task.Run(async () =>
                {
                    try { if (svc is not null) await svc.Auth.ApplyExternalTokenAsync(authToken); }
                    catch (Exception ex) { Log.Error("Ely.by: не удалось применить токен", ex); }
                });
                return;
            }

            if (!action.Equals("join", StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"Deep-link: неизвестное действие '{action}' — пропуск");
                return;
            }

            string? ip = null; int? port = null;
            foreach (var pair in paramPart.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length != 2) continue;
                var key = Uri.UnescapeDataString(kv[0]).ToLowerInvariant();
                var val = Uri.UnescapeDataString(kv[1]);
                if (key == "ip") ip = val;
                else if (key == "port" && int.TryParse(val, out var pp) && pp is > 0 and <= 65535) port = pp;
            }

            if (string.IsNullOrWhiteSpace(ip))
            {
                Log.Warn("Deep-link join без адреса сервера — пропуск");
                return;
            }

            Log.Info($"Deep-link join: {ip}:{port?.ToString() ?? "25565"}");
            // даём webview загрузиться, чтобы событие дошло до подписчика
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500).ConfigureAwait(false);
                try { _services?.Emit("app.deepLink", new { ip, port }); }
                catch (Exception ex) { Log.Warn($"Deep-link emit: {ex.Message}"); }
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Разбор deep-link: {ex.Message}");
        }
    }

    /// <summary>Вторая копия лаунчера шлёт свои аргументы (deep-link) работающему окну через именованный канал.</summary>
    private static void ForwardArgsToRunningInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1000);
            using var w = new StreamWriter(client);
            w.Write(string.Join("\n", args));
            w.Flush();
        }
        catch (Exception ex) { Log.Warn($"Передача deep-link работающему окну: {ex.Message}"); }
    }

    /// <summary>Слушает именованный канал: сюда приходят deep-link'и от вторых копий (nulllauncher://auth?…).</summary>
    private void ListenDeepLinks()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                server.WaitForConnection();
                using var r = new StreamReader(server);
                var payload = r.ReadToEnd();
                var args = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (args.Length > 0) HandleDeepLink(args);
            }
            catch (Exception ex)
            {
                Log.Warn($"Deep-link канал: {ex.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    /// <summary>Фоновые проверки после запуска: целостность БД, обновления, восстановление метаданных.</summary>
    private async void StartupChecks()
    {
        try
        {
            await Task.Run(() =>
            {
                try
                {
                    var dirs = Directory.EnumerateDirectories(_services!.Paths.InstancesDir).ToList();
                    var recovered = _services.Db.RecoverFromInstanceFolders(dirs);
                    if (recovered > 0)
                        _services.NotifyUser("warn", "Восстановлены метаданные", $"Пересоздано записей: {recovered}");
                }
                catch (Exception ex) { Log.Warn($"Восстановление из папок: {ex.Message}"); }
            });
        }
        catch (Exception ex) { Log.Warn($"StartupChecks: {ex.Message}"); }

        if (_services?.Settings.Get("checkUpdatesOnStart", true) != false)
        {
            try
            {
                var r = await Modules.SystemIpc.CheckForUpdates(_services!);
                if (r.UpdateAvailable) _services!.Emit("app.updateAvailable", r);
            }
            catch (Exception ex) { Log.Debug($"Проверка обновлений: {ex.Message}"); }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // снимаем Rich Presence, чтобы статус «Играет в NullLauncher» не висел после закрытия
        try { _services?.Discord?.Dispose(); } catch { /* noop */ }
        try { _services?.Media?.Dispose(); } catch { /* noop */ }
        Log.Info("NullLauncher завершён");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Ошибка UI", e.Exception);
        e.Handled = true;
        try
        {
            var msg = e.Exception switch
            {
                LauncherException le => le.UserMessage,
                _ => "Непредвиденная ошибка в интерфейсе. Подробности записаны в лог.",
            };
            _services?.NotifyUser("error", "Ошибка", msg);
            if (_services is null)
                System.Windows.MessageBox.Show(msg, "NullLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* вторая ошибка не должна ронять приложение */ }
    }
}
