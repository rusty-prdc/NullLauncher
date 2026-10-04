using System.Text.Json.Nodes;
using NullLauncher.Core;

namespace NullLauncher.Instances;

/// <summary>IPC-методы библиотеки сборок.</summary>
public static class InstanceIpc
{
    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("instances.list", (_, _) => Task.FromResult<object?>(s.Instances.List()));
        r.Register("instances.get", (p, _) => Task.FromResult<object?>(s.Instances.Require(p.Str("id") ?? "")));
        r.Register("instances.create", (p, _) =>
        {
            var req = new CreateInstanceRequest(
                Name: p?.Str("name") ?? "",
                Description: p?.Str("description"),
                IconPath: p?.Str("iconPath") is string ip && ip.Length > 0 ? ip : null,
                CoverPath: p?.Str("coverPath") is string cp && cp.Length > 0 ? cp : null,
                Color: p?.Str("color"),
                McVersion: p?.Str("mcVersion") ?? "",
                Loader: p?.Str("loader") ?? "vanilla",
                LoaderVersion: p?.Str("loaderVersion"),
                JavaMode: p?.Str("javaMode"),
                JavaMajor: p?["javaMajor"] is null ? null : p!.Int("javaMajor", 21),
                JavaPath: p?.Str("javaPath"),
                RamMinMb: p?.Int("ramMinMb", 512) ?? 512,
                RamMaxMb: p?.Int("ramMaxMb", 4096) ?? 4096,
                Width: p?["width"] is null ? null : p!.Int("width", 1920),
                Height: p?["height"] is null ? null : p!.Int("height", 1080),
                Fullscreen: p?["fullscreen"] is null ? null : p!.Bool("fullscreen"),
                GroupIds: p?["groupIds"] is null ? null : p!.StrList("groupIds"));
            var rec = s.Instances.Create(req);
            return Task.FromResult<object?>(rec);
        });
        r.Register("instances.update", (p, _) => Task.FromResult<object?>(s.Instances.Update(p?.Str("id") ?? "", p)));
        r.Register("instances.delete", (p, _) =>
        {
            s.Instances.Delete(p?.Str("id") ?? "", p?.Bool("backup", true) ?? true);
            return Task.FromResult<object?>(new { ok = true });
        });
        r.Register("instances.duplicate", (p, _) => Task.FromResult<object?>(s.Instances.Duplicate(
            p?.Str("id") ?? "", p?.Str("name"), p?.Bool("includeWorlds", true) ?? true, p?.Bool("includeMods", true) ?? true)));
        r.Register("instances.setFavorite", (p, _) =>
        {
            s.Instances.Update(p?.Str("id") ?? "", new JsonObject { ["favorite"] = p?.Bool("favorite") ?? false });
            return Task.FromResult<object?>(new { ok = true });
        });
        r.Register("instances.openFolder", (p, _) =>
        {
            var inst = s.Instances.Require(p?.Str("id") ?? "");
            var sub = p?.Str("sub");
            var dir = string.IsNullOrWhiteSpace(sub) ? inst.Dir : Path.Combine(inst.GameDir, sub);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.Windows.Application.Current.Dispatcher.Invoke(() => MainWindow.OpenPath(dir));
            return Task.FromResult<object?>(new { ok = true });
        });
        r.Register("instances.export", (p, _) =>
        {
            var id = p?.Str("id") ?? "";
            var target = p?.Str("targetPath");
            if (string.IsNullOrWhiteSpace(target))
            {
                var inst = s.Instances.Require(id);
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var dlg = new Microsoft.Win32.SaveFileDialog
                    {
                        FileName = Sanitize(inst.Name) + ".nlpkg",
                        Filter = "Сборка NullLauncher (*.nlpkg;*.zip)|*.nlpkg;*.zip",
                    };
                    if (dlg.ShowDialog() == true) target = dlg.FileName;
                });
                if (string.IsNullOrWhiteSpace(target)) return Task.FromResult<object?>(new { cancelled = true });
            }
            var include = new Dictionary<string, bool>();
            if (p?["include"] is JsonObject obj)
                foreach (var (k, v) in obj)
                    if (k is not null) include[k] = v is not null && v.GetValueKind() == System.Text.Json.JsonValueKind.True;
            if (include.Count == 0)
                foreach (var k in new[] { "mods", "resourcepacks", "shaders", "configs", "worlds", "options", "screenshots" })
                    include[k] = true;
            var (path, bytes) = s.Instances.Export(id, target!, include);
            return Task.FromResult<object?>(new { path, bytes });
        });
        r.Register("instances.import", (p, _) =>
        {
            var src = p?.Str("sourcePath");
            if (string.IsNullOrWhiteSpace(src))
            {
                string? picked = null;
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = "Рмпорт сборки",
                        Filter = "Сборка (*.nlpkg;*.zip)|*.nlpkg;*.zip",
                        Multiselect = false,
                    };
                    if (dlg.ShowDialog() == true) picked = dlg.FileName;
                });
                src = picked;
            }
            if (string.IsNullOrWhiteSpace(src)) return Task.FromResult<object?>(new { cancelled = true });
            return Task.FromResult<object?>(s.Instances.Import(src));
        });
        r.Register("instances.installedVersions", (_, _) =>
        {
            try
            {
                var dirs = Directory.EnumerateDirectories(s.Paths.VersionsDir).Select(Path.GetFileName)
                    .Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList();
                return Task.FromResult<object?>(dirs);
            }
            catch { return Task.FromResult<object?>(new List<string>()); }
        });
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
