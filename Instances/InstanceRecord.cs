using System.Text.Json.Serialization;

namespace NullLauncher.Instances;

/// <summary>Описание сборки. Дублируется в instance.json внутри папки — это позволяет восстановить БД из файлов.</summary>
public sealed class InstanceRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? IconPath { get; set; }
    public string? CoverPath { get; set; }
    public string Color { get; set; } = "#3ecf8e";
    public bool Favorite { get; set; }

    public string McVersion { get; set; } = "1.21.1";
    public string Loader { get; set; } = "vanilla";
    public string? LoaderVersion { get; set; }

    public string JavaMode { get; set; } = "auto"; // auto | major | path
    public int? JavaMajor { get; set; }
    public string? JavaPath { get; set; }

    public int RamMinMb { get; set; } = 512;
    public int RamMaxMb { get; set; } = 4096;
    public string JvmArgs { get; set; } = "";
    public string GameArgs { get; set; } = "";
    public string EnvVars { get; set; } = "";

    public string WindowMode { get; set; } = "windowed"; // windowed | maximized | fullscreen
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public bool Fullscreen { get; set; }
    public bool Vsync { get; set; } = true;
    public int FpsLimit { get; set; } = 120;

    public string Dir { get; set; } = "";
    public string? ServerIp { get; set; }
    public int? ServerPort { get; set; }
    public string? AccountUuid { get; set; }
    public bool SafeMode { get; set; }

    public string? ModpackName { get; set; }
    public string? ModpackVersion { get; set; }
    public string? ModpackProjectId { get; set; }
    public string? ModpackVersionId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastPlayedAt { get; set; }
    public long PlayTimeMs { get; set; }
    public int LaunchCount { get; set; }
    public int SortOrder { get; set; }

    public List<string> GroupIds { get; set; } = new();

    /// <summary>Количество модов. Вычисляется при загрузке списка, в БД не хранится.</summary>
    public int ModCount { get; set; }

    [JsonIgnore] public string GameDir => System.IO.Path.Combine(Dir, ".minecraft");
    [JsonIgnore] public string ModsDir => System.IO.Path.Combine(GameDir, "mods");
    [JsonIgnore] public string ResourcePacksDir => System.IO.Path.Combine(GameDir, "resourcepacks");
    [JsonIgnore] public string ShaderPacksDir => System.IO.Path.Combine(GameDir, "shaderpacks");
    [JsonIgnore] public string DataPacksDir => System.IO.Path.Combine(GameDir, "datapacks");
    [JsonIgnore] public string SavesDir => System.IO.Path.Combine(GameDir, "saves");
    [JsonIgnore] public string ConfigDir => System.IO.Path.Combine(GameDir, "config");
    [JsonIgnore] public string LogsDir => System.IO.Path.Combine(GameDir, "logs");
    [JsonIgnore] public string CrashReportsDir => System.IO.Path.Combine(GameDir, "crash-reports");

    public string ContentDir(string kind) => kind switch
    {
        "resourcepacks" => ResourcePacksDir,
        "shaderpacks" => ShaderPacksDir,
        "datapacks" => DataPacksDir,
        _ => ModsDir,
    };
}
