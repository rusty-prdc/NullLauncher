using System.Text.Json.Nodes;

namespace NullLauncher.Skins;

/// <summary>IPC-методы гардероба скинов. Хранение — SkinService, применение — через аккаунты.</summary>
public static class SkinsIpc
{
    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("skins.list", (_, _) => Task.FromResult<object?>(s.Skins.List()));

        r.Register("skins.add", async (p, ct) =>
        {
            var url = p?.Str("url");
            var path = p?.Str("path");
            var name = p?.Str("name");
            var variant = p?.Str("variant");

            if (!string.IsNullOrWhiteSpace(url))
                return (object?)await s.Skins.AddFromUrlAsync(url!, name, variant, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(path))
                throw new LauncherException("Не указан файл скина", "Нужен путь к PNG или ссылка url");
            return (object?)s.Skins.AddFromFile(path!, name, variant);
        });

        r.Register("skins.rename", (p, _) =>
        {
            var id = p?.Str("id") ?? throw new LauncherException("Не указан скин");
            s.Skins.Rename(id, p?.Str("name") ?? "");
            return Task.FromResult<object?>(new { ok = true });
        });

        r.Register("skins.remove", (p, _) =>
        {
            var id = p?.Str("id") ?? throw new LauncherException("Не указан скин");
            s.Skins.Remove(id);
            return Task.FromResult<object?>(new { ok = true });
        });

        r.Register("skins.apply", async (p, ct) =>
        {
            var id = p?.Str("id") ?? throw new LauncherException("Не указан скин");
            var acc = await s.Skins.ApplyAsync(id, ct).ConfigureAwait(false);
            s.Emit("accounts.changed", new { action = "skin", uuid = acc.Uuid });
            return (object?)acc;
        });

        // data URL превью одного скина
        r.Register("skins.image", (p, _) =>
        {
            var id = p?.Str("id") ?? throw new LauncherException("Не указан скин");
            return Task.FromResult<object?>(DataUrl(s.Skins.FilePath(id)));
        });

        // открыть исходный PNG в системном просмотрщике
        r.Register("skins.open", (p, _) =>
        {
            var id = p?.Str("id") ?? throw new LauncherException("Не указан скин");
            var file = s.Skins.FilePath(id);
            System.Windows.Application.Current.Dispatcher.Invoke(() => MainWindow.OpenPath(file, select: false));
            return Task.FromResult<object?>(new { ok = true });
        });
    }

    private static string? DataUrl(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var fi = new FileInfo(path);
            if (fi.Length > 2 * 1024 * 1024) return null;
            return "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
        }
        catch { return null; }
    }
}
