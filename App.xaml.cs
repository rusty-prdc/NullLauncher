using System.IO;
using System.Windows;
using System.Windows.Threading;
using NullLauncher.Core;

namespace NullLauncher;

public partial class App : System.Windows.Application
{
    private AppServices? _services;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
