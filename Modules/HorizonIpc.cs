using System.Diagnostics;
using System.IO;
using NullLauncher.Core;

namespace NullLauncher.Modules;

/// <summary>
/// Horizon (playhorizon.dev) — неофициальная реализация Minecraft: Java Edition 26.3 на Rust.
/// Своей копии игры у Horizon нет: клиент собирает ресурсы из вашей Java Edition и требует
/// аккаунт Microsoft с лицензией. Поэтому лаунчер не «пересобирает» сборку на Rust, а
/// находит Horizon, открывает его и помогает выгрузить сборку в .mrpack для импорта.
/// </summary>
public static class HorizonIpc
{
    private const string SiteUrl = "https://playhorizon.dev/docs/install";

    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("horizon.status", (_, _) => Task.FromResult<object?>(Status()));

        r.Register("horizon.open", (_, _) =>
        {
            var st = Status();
            if (!st.Installed || st.Path is null)
                return Task.FromResult<object?>(new { ok = false, installed = false });

            try
            {
                Process.Start(new ProcessStartInfo(st.Path)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(st.Path) ?? "",
                });
                Log.Info($"Horizon: открыт {st.Path}");
                return Task.FromResult<object?>(new { ok = true, installed = true, path = st.Path });
            }
            catch (Exception ex)
            {
                Log.Warn($"Horizon: не удалось запустить {st.Path}: {ex.Message}");
                return Task.FromResult<object?>(new { ok = false, installed = true, path = st.Path, error = ex.Message });
            }
        });

        r.Register("horizon.site", (_, _) =>
        {
            MainWindow.OpenUrl(SiteUrl);
            return Task.FromResult<object?>(new { ok = true, url = SiteUrl });
        });
    }

    private sealed record HorizonStatus(bool Installed, string? Path);

    private static HorizonStatus Status()
    {
        foreach (var dir in CandidateDirs())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var found = Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories)
                    .Take(400)
                    .FirstOrDefault(exe =>
                    {
                        var name = System.IO.Path.GetFileNameWithoutExtension(exe);
                        return name.Contains("horizon", StringComparison.OrdinalIgnoreCase)
                               && !name.Contains("unins", StringComparison.OrdinalIgnoreCase);
                    });
                if (found is not null) return new HorizonStatus(true, found);
            }
            catch (Exception ex)
            {
                Log.Debug($"Horizon: поиск в {dir} — {ex.Message}");
            }
        }
        return new HorizonStatus(false, null);
    }

    private static IEnumerable<string> CandidateDirs()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        yield return Path.Combine(local, "Programs", "Horizon");
        yield return Path.Combine(local, "Programs", "horizon");
        yield return Path.Combine(local, "Horizon");
        yield return Path.Combine(local, "horizon");
        yield return Path.Combine(roaming, "Horizon");
        yield return Path.Combine(pf, "Horizon");
        yield return Path.Combine(pf86, "Horizon");
    }
}
