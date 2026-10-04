using System.IO;
using System.Windows;

namespace NullLauncher.Core;

/// <summary>
/// Контейнер сервисов. Один экземпляр на процесс, создаётся при старте.
/// Каждый сервис сам подключает свои IPC-методы через RegisterIpc — модуль добавляется в одном месте.
/// </summary>
public sealed class AppServices
{
    public static AppServices? Current { get; private set; }

    public AppPaths Paths { get; }
    public SettingsService Settings { get; }
    public Database.DatabaseService Db { get; }
    public IpcRouter Router { get; } = new();
    public Instances.InstanceService Instances { get; }
    public Groups.GroupService Groups { get; }
    public Java.JavaService Java { get; }
    public Versions.VersionService Versions { get; }
    public Downloads.DownloadService Downloads { get; }
    public Minecraft.FileProvisioner Provisioner { get; }
    public Minecraft.LaunchService Launch { get; }
    public Accounts.AccountService Accounts { get; }

    public event Action<string, object?>? Notify;

    public AppServices()
    {
        Current = this;
        Paths = new AppPaths();
        Settings = new SettingsService(Paths);

        // Пользовательская папка данных применяется до того, как сервисы начнут работать с файлами
        var dataDir = Settings.Get("dataDir", "");
        if (!string.IsNullOrWhiteSpace(dataDir) && Directory.Exists(dataDir))
        {
            if (!string.Equals(dataDir, Paths.DataRoot, StringComparison.OrdinalIgnoreCase))
                Log.Info($"Папка данных: {Paths.DataRoot} → {dataDir}");
            Paths.SetDataRoot(dataDir);
        }

        Paths.EnsureCreated();
        Log.Configure(Paths.LogsDir,
            Settings.Get("debugMode", false) ? 0 : (int)ParseLevel(Settings.Get("logLevel", "info")),
            mirrorConsole: true,
            retentionDays: Settings.Get("logRetentionDays", 14));

        Db = new Database.DatabaseService(Paths);
        Instances = new Instances.InstanceService(Paths);
        Groups = new Groups.GroupService(Paths);
        Java = new Java.JavaService(Paths, Settings);
        NullLauncher.Java.JavaService.Bind(this);

        Versions = new Versions.VersionService(Paths, Settings);
        NullLauncher.Versions.VersionService.Bind(this);
        Downloads = new Downloads.DownloadService(this);
        Provisioner = new Minecraft.FileProvisioner(Paths, Versions, Downloads);
        Launch = new Minecraft.LaunchService(this, Provisioner);
        Accounts = new Accounts.AccountService(this);

        RegisterAll();
        Settings.Changed += (_, e) => Notify?.Invoke("settings.changed", e.Snapshot);
    }

    private static LogLevel ParseLevel(string s) => s.ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "warn" or "warning" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Info,
    };

    private void RegisterAll()
    {
        Modules.SystemIpc.Register(Router, this);
        Modules.SettingsIpc.Register(Router, this);
        NullLauncher.Instances.InstanceIpc.Register(Router, this);
        NullLauncher.Groups.GroupIpc.Register(Router, this);
        Modules.StorageIpc.Register(Router, this);
        NullLauncher.Java.JavaIpc.Register(Router, this);
        NullLauncher.Versions.VersionsIpc.Register(Router, this);
        NullLauncher.Downloads.DownloadsIpc.Register(Router, this);
        NullLauncher.Minecraft.LaunchIpc.Register(Router, this);
        NullLauncher.Accounts.AccountsIpc.Register(Router, this);
        NullLauncher.Content.ContentIpc.Register(Router, this);
        NullLauncher.Modrinth.ModrinthIpc.Register(Router, this);
        NullLauncher.Backups.BackupIpc.Register(Router, this);
        NullLauncher.Modules.ExtrasIpc.Register(Router, this);
        NullLauncher.Instances.InstanceExtrasIpc.Register(Router, this);
        NullLauncher.Worlds.WorldsIpc.Register(Router, this);
        NullLauncher.Servers.ServersIpc.Register(Router, this);
        NullLauncher.Logs.LogsIpc.Register(Router, this);
        NullLauncher.News.NewsIpc.Register(Router, this);
    }

    /// <summary>Всплывающее уведомление в UI.</summary>
    public void NotifyUser(string type, string title, string? message = null, object? actions = null)
        => Notify?.Invoke("notify", new { type, title, message, actions });

    public void Emit(string @event, object? data) => Notify?.Invoke(@event, data);
}
