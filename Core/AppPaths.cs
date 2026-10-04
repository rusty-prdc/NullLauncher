using System.IO;

namespace NullLauncher.Core;

/// <summary>
/// Все пути приложения. Настройки всегда лежат в %APPDATA%\NullLauncher\config,
/// игровые данные — в настраиваемой корневой папке (по умолчанию та же).
/// </summary>
public sealed class AppPaths
{
    public const string AppFolderName = "NullLauncher";

    /// <summary>%APPDATA%\NullLauncher — здесь всегда живут настройки лаунчера.</summary>
    public string ConfigRoot { get; }

    /// <summary>Корень пользовательских данных (instances, java, cache…), настраивается.</summary>
    public string DataRoot { get; private set; }

    public AppPaths(string? dataRootOverride = null)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        ConfigRoot = Path.Combine(appData, AppFolderName);
        DataRoot = string.IsNullOrWhiteSpace(dataRootOverride) ? ConfigRoot : dataRootOverride!;
    }

    public void SetDataRoot(string path) => DataRoot = string.IsNullOrWhiteSpace(path) ? ConfigRoot : path;

    /* --- разделы --- */
    public string ConfigDir => Path.Combine(ConfigRoot, "config");
    public string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public string LauncherStateFile => Path.Combine(ConfigDir, "launcher.json");
    public string UiStateFile => Path.Combine(ConfigDir, "ui.json");

    public string InstancesDir => Path.Combine(DataRoot, "instances");
    public string GroupsDir => Path.Combine(DataRoot, "groups");
    public string ProfilesDir => Path.Combine(DataRoot, "profiles");
    public string DownloadsDir => Path.Combine(DataRoot, "downloads");
    public string CacheDir => Path.Combine(DataRoot, "cache");
    public string MetadataDir => Path.Combine(DataRoot, "metadata");
    public string JavaDir => Path.Combine(DataRoot, "java");
    public string LogsDir => Path.Combine(DataRoot, "logs");
    public string CrashReportsDir => Path.Combine(DataRoot, "crash-reports");
    public string DatabaseDir => Path.Combine(DataRoot, "database");
    public string BackupsDir => Path.Combine(DataRoot, "backups");
    public string AssetsDir => Path.Combine(DataRoot, "assets");
    public string VersionsDir => Path.Combine(DataRoot, "versions");
    public string LibrariesDir => Path.Combine(DataRoot, "libraries");
    public string ResourcesDir => Path.Combine(DataRoot, "resources");
    public string PluginsDir => Path.Combine(ConfigRoot, "plugins");
    public string TempDir => Path.Combine(CacheDir, "tmp");
    public string DatabaseFile => Path.Combine(DatabaseDir, "launcher.db");

    public string InstanceDir(string id) => Path.Combine(InstancesDir, id);
    public string InstanceGameDir(string id) => Path.Combine(InstanceDir(id), ".minecraft");

    public string UiDir => Path.Combine(AppContext.BaseDirectory, "UI");

    /// <summary>Создаёт все каталоги при первом запуске. Ничего не удаляет.</summary>
    public void EnsureCreated()
    {
        string[] dirs =
        {
            ConfigDir, InstancesDir, GroupsDir, ProfilesDir, DownloadsDir, CacheDir, MetadataDir,
            JavaDir, LogsDir, CrashReportsDir, DatabaseDir, BackupsDir, AssetsDir, VersionsDir,
            LibrariesDir, ResourcesDir, PluginsDir, TempDir,
            Path.Combine(CacheDir, "webview2"), Path.Combine(CacheDir, "images"), Path.Combine(CacheDir, "modrinth"),
        };
        foreach (var d in dirs)
        {
            try { Directory.CreateDirectory(d); }
            catch (Exception ex) { Log.Warn($"Не удалось создать каталог {d}: {ex.Message}"); }
        }
    }

    public static long DirectorySize(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f =>
            {
                try { return new FileInfo(f).Length; } catch { return 0L; }
            });
        }
        catch { return 0L; }
    }
}
