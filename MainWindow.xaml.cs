using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace NullLauncher;

public partial class MainWindow : Window
{
    private readonly Core.IpcRouter _router;
    private readonly Core.AppPaths _paths;

    public MainWindow(Core.IpcRouter router, Core.AppPaths paths)
    {
        _router = router;
        _paths = paths;
        InitializeComponent();
        SourceInitialized += (_, _) => RegisterWindowChrome();
        StateChanged += (_, _) => PostEvent("window.maximized", WindowState == WindowState.Maximized);
        Loaded += async (_, _) => await InitWebViewAsync();
        Closed += (_, _) => WebViewClosed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? WebViewClosed;
    public event Func<string, JsonNode?, CancellationToken, Task<object?>>? MessageReceived;
    public event Action<string, object?>? EventRequested;

    /* ---------------------------------------------------------
       WebView2
       --------------------------------------------------------- */
    private async Task InitWebViewAsync()
    {
        try
        {
            var userData = Path.Combine(_paths.CacheDir, "webview2");
            Directory.CreateDirectory(userData);
            var env = await CoreWebView2Environment.CreateAsync(null, userData, null);
            await Web.EnsureCoreWebView2Async(env);

            var cw = Web.CoreWebView2;
            cw.Settings.AreDefaultContextMenusEnabled = false;
            cw.Settings.IsStatusBarEnabled = false;
            cw.Settings.IsSwipeNavigationEnabled = false;
            cw.Settings.AreBrowserAcceleratorKeysEnabled = true; // Ctrl+C/T/R/F оставляем как в браузере
            cw.Settings.IsGeneralAutofillEnabled = false;
            cw.Settings.IsPasswordAutosaveEnabled = false;

            // UI обслуживается с виртуального хоста — https://app.nulllauncher.local/index.html
            cw.SetVirtualHostNameToFolderMapping("app.nulllauncher.local", _paths.UiDir, CoreWebView2HostResourceAccessKind.Allow);

            cw.WebMessageReceived += OnWebMessage;
            cw.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
                    OpenUrl(u.ToString());
            };

            cw.NavigationCompleted += (_, e) => Log.Info($"UI загружено: {Web.Source} (успех={e.IsSuccess})");

            // Ссылки на modrinth.com и прочие внешние ресурсы из описаний проектов
            cw.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Allow;

            Web.Source = new Uri("https://app.nulllauncher.local/index.html");
            Log.Info("WebView2 инициализирован");
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось инициализировать WebView2", ex);
            System.Windows.MessageBox.Show(
                "Не удалось запустить компонент WebView2.\n\n" +
                "Установите WebView2 Runtime:\nhttps://developer.microsoft.com/microsoft-edge/webview2/\n\n" + ex.Message,
                "NullLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(e.WebMessageAsJson); }
        catch (Exception ex) { Log.Warn($"Некорректное сообщение из UI: {ex.Message}"); return; }
        if (node is null) return;

        var id = node["id"]?.GetValue<int?>();
        var method = node["method"]?.GetValue<string>();
        if (method is null) return;

        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource();
            try
            {
                var result = await (MessageReceived?.Invoke(method, node["params"], cts.Token)
                    ?? Task.FromResult<object?>(null));
                if (id is int rid) Send(new { id = rid, result });
            }
            catch (Exception ex)
            {
                var le = Core.LauncherException.Wrap(ex, $"Не удалось выполнить «{method}»");
                Log.Error($"IPC {method}: {le.UserMessage}", ex);
                if (id is int rid) Send(new { id = rid, error = le.ToDto() });
            }
        }, CancellationToken.None);
    }

    /// <summary>Отправка события в UI (notify, launch.progress, downloads.progress…).</summary>
    public void PostEvent(string eventName, object? data)
    {
        try
        {
            if (Web?.CoreWebView2 is null) return;
            var json = Core.IpcRouter.Serialize(new { @event = eventName, data });
            Web.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex) { Log.Debug($"Не удалось отправить событие {eventName}: {ex.Message}"); }
    }

    private void Send(object payload)
    {
        try
        {
            var json = Core.IpcRouter.Serialize(payload);
            Web.Dispatcher.Invoke(() => Web.CoreWebView2?.PostWebMessageAsJson(json));
        }
        catch (Exception ex) { Log.Warn($"Не удалось отправить ответ UI: {ex.Message}"); }
    }

    /* ---------------------------------------------------------
       Системные действия из UI
       --------------------------------------------------------- */
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WmNCLButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;

    private void RegisterWindowChrome() { /* окно без рамки, hit-test настраивается через drag-сообщение */ }

    public void HandleWindowAction(string action)
    {
        switch (action)
        {
            case "minimize":
                WindowState = WindowState.Minimized;
                break;
            case "maximize":
                WindowState = WindowState.Maximized;
                break;
            case "restore":
                WindowState = WindowState.Normal;
                break;
            case "toggleMax":
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                break;
            case "close":
                Close();
                break;
            case "drag":
                if (WindowState == WindowState.Maximized)
                {
                    // перетаскивание развёрнутого окна возвращает его в нормальный размер
                    var ratio = Mouse.GetPosition(this).X / Math.Max(1, ActualWidth);
                    WindowState = WindowState.Normal;
                    Left += ratio * ActualWidth - Width * ratio;
                }
                ReleaseCapture();
                SendMessage(new System.Windows.Interop.WindowInteropHelper(this).Handle, WmNCLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
                break;
        }
    }

    /* ---------------------------------------------------------
       Drag & drop файлов
       --------------------------------------------------------- */
    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) e.Effects = System.Windows.DragDropEffects.Copy;
        else e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            Log.Info($"Перетащено файлов: {files.Length}");
            EventRequested?.Invoke("files.dropped", new { files, count = files.Length });
        }
        e.Handled = true;
    }

    /* --------------------------------------------------------- */
    public static void OpenUrl(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return;
            if (u.Scheme is not ("http" or "https")) return;
            Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn($"Не удалось открыть ссылку {url}: {ex.Message}"); }
    }

    public static void OpenPath(string path, bool select = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (Directory.Exists(path))
            {
                if (select) Process.Start("explorer.exe", $"/select,\"{path}\"");
                else Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex) { Log.Warn($"Не удалось открыть путь {path}: {ex.Message}"); }
    }
}
