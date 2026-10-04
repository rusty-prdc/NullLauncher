using System.IO;

namespace NullLauncher.Core;

public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

/// <summary>
/// Простой файловый логгер с ежедневными файлами launcher-yyyy-MM-dd.log и ротацией.
/// Полные stack traces пишутся сюда, пользователю они никогда не показываются.
/// </summary>
public static class Log
{
    private static readonly object Sync = new();
    private static string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppPaths.AppFolderName, "logs");

    private static int _minLevel = (int)LogLevel.Info;
    private static bool _mirrorConsole = true;
    private static int _retentionDays = 14;

    public static void Configure(string dir, int minLevel = 0, bool mirrorConsole = true, int retentionDays = 14)
    {
        _dir = dir;
        _minLevel = minLevel;
        _mirrorConsole = mirrorConsole;
        _retentionDays = retentionDays;
        try { Directory.CreateDirectory(_dir); } catch { /* логирование не должно ронять приложение */ }
        Cleanup();
    }

    public static string CurrentFile => Path.Combine(_dir, $"launcher-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Debug(string msg) => Write(LogLevel.Debug, msg);
    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);
    public static void Error(string msg, Exception? ex = null)
        => Write(LogLevel.Error, ex is null ? msg : $"{msg}\n{ex}");

    /// <summary>Полный отчёт об ошибке в файл (stack trace), пользователю не выводится.</summary>
    public static void Crash(string msg, Exception ex)
    {
        Write(LogLevel.Error, $"{msg}\n{ex}");
    }

    private static void Write(LogLevel level, string msg)
    {
        if ((int)level < _minLevel) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()}] {msg}";
        lock (Sync)
        {
            try
            {
                File.AppendAllText(CurrentFile, line + Environment.NewLine);
            }
            catch { /* диск недоступен — игнорируем, чтобы не уронить UI */ }
            if (_mirrorConsole) Console.WriteLine(line);
        }
    }

    /// <summary>Удаляет логи старше заданного срока, чтобы каталог не рёл бесконечно.</summary>
    public static void Cleanup(int? retentionDays = null)
    {
        var days = retentionDays ?? _retentionDays;
        if (days <= 0) return;
        try
        {
            var cutoff = DateTime.Now.AddDays(-days);
            foreach (var f in Directory.EnumerateFiles(_dir, "launcher-*.log"))
                if (File.GetLastWriteTime(f) < cutoff)
                    try { File.Delete(f); } catch { /* ignore */ }
        }
        catch { /* ignore */ }
    }

    public static IReadOnlyList<string> ListFiles()
    {
        try
        {
            return Directory.EnumerateFiles(_dir, "launcher-*.log")
                .OrderByDescending(f => f)
                .Select(Path.GetFileName)
                .Where(n => n != null)
                .Select(n => n!)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }
}
