using System.Text.Json.Nodes;

namespace NullLauncher.Modules;

/// <summary>Настройки лаунчера: чтение, запись, поиск, сброс, экспорт.</summary>
public static class SettingsIpc
{
    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("settings.get", (_, _) => Task.FromResult<object?>(new Dictionary<string, object?>(s.Settings.Full())));
        r.Register("settings.set", (p, _) =>
        {
            if (p is null) throw new LauncherException("Пустой запрос настроек");
            var dict = new Dictionary<string, System.Text.Json.JsonElement>();
            foreach (var (k, v) in p.AsObject())
            {
                if (k is null) continue;
                dict[k] = System.Text.Json.JsonSerializer.SerializeToElement(v, IpcRouter.JsonOptions);
            }
            s.Settings.SetRaw(dict);
            return Task.FromResult<object?>(new { ok = true });
        });
        r.Register("settings.reset", (_, _) =>
        {
            s.Settings.Reset();
            Log.Info("Настройки сброшены (сборки и миры не затронуты)");
            return Task.FromResult<object?>(new { ok = true });
        });
        r.Register("settings.search", (p, _) =>
        {
            var q = p?.Str("query") ?? "";
            var hits = SettingsRegistry.Search(q).Select(x => new
            {
                key = x.Key, title = x.Title, section = x.Section, sectionId = x.SectionId, hint = x.Hint,
            });
            return Task.FromResult<object?>(hits);
        });
        r.Register("settings.export", (p, _) => Task.FromResult<object?>(Export(s, p)));
    }

    private static object Export(AppServices s, JsonNode? p)
    {
        var includeInstances = p?.Bool("includeInstances", false) ?? false;
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmm");
        var defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"NullLauncher-Backup-{stamp}.zip");
        var target = p?.Str("targetPath");
        if (string.IsNullOrWhiteSpace(target)) target = defaultDir;

        var dialogResult = false;
        if (p?.Bool("pickFolder", false) == true)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = Path.GetFileName(target),
                    Filter = "Архив|*.zip",
                    InitialDirectory = Path.GetDirectoryName(target),
                };
                if (dlg.ShowDialog() == true) { target = dlg.FileName; dialogResult = true; }
            });
            if (!dialogResult) return new { path = (string?)null, cancelled = true };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target)) File.Delete(target);

        using (var zip = System.IO.Compression.ZipFile.Open(target, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var f in Directory.EnumerateFiles(s.Paths.ConfigDir, "*", SearchOption.AllDirectories))
                zip.CreateEntryFromFile(f, Path.Combine("config", Path.GetRelativePath(s.Paths.ConfigDir, f)));

            zip.CreateEntryFromFile(s.Paths.DatabaseFile, "database/launcher.db");

            foreach (var d in new[] { s.Paths.ProfilesDir, s.Paths.GroupsDir })
                foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                    zip.CreateEntryFromFile(f, Path.Combine(Path.GetFileName(d), Path.GetRelativePath(d, f)));

            if (includeInstances)
            {
                foreach (var dir in Directory.EnumerateDirectories(s.Paths.InstancesDir))
                    foreach (var f in Directory.EnumerateFiles(dir, "instance.json", SearchOption.AllDirectories))
                        zip.CreateEntryFromFile(f, Path.Combine("instances", Path.GetRelativePath(s.Paths.InstancesDir, f)));
            }
        }

        var size = new FileInfo(target).Length;
        Log.Info($"Экспорт настроек → {target} ({size} байт, instances={includeInstances})");
        return new { path = target, bytes = size };
    }
}
