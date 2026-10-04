using System.Text.Json.Nodes;

namespace NullLauncher.Versions;

public static class VersionsIpc
{
    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("versions.list", async (p, ct) =>
        {
            var res = await s.Versions.ListForUiAsync(
                p?.Str("type"), p?.Str("query"), p?.Int("limit", 100) ?? 100, p?.Int("offset", 0) ?? 0,
                p?.Bool("refresh", false) ?? false, ct).ConfigureAwait(false);
            // UI ожидает плоский массив версий
            return res.GetType().GetProperty("hits")?.GetValue(res);
        });

        r.Register("versions.refresh", async (_, ct) =>
        {
            var (all, offline) = await s.Versions.ListAsync(forceRefresh: true, ct).ConfigureAwait(false);
            s.Emit("versions.updated", new { count = all.Count, offline });
            return new { count = all.Count, offline };
        });

        r.Register("versions.loaders", async (p, ct) =>
        {
            var mc = p?.Str("mcVersion") ?? throw new LauncherException("Укажите версию Minecraft");
            var list = await s.Versions.LoadersAsync(mc, ct).ConfigureAwait(false);
            return list;
        });

        r.Register("versions.detail", async (p, ct) =>
        {
            var id = p?.Str("id") ?? throw new LauncherException("Укажите версию");
            var node = await s.Versions.GetDetailAsync(id, ct).ConfigureAwait(false);
            return new
            {
                id = node.Id,
                type = node.Type,
                releaseTime = node.ReleaseTime,
                javaMajor = node.JavaMajor,
                libraries = node.Libraries.Count,
                assets = node.AssetCount,
                mainClass = node.MainClass,
            };
        });

        r.Register("versions.delete", (p, _) =>
        {
            var id = p?.Str("id") ?? throw new LauncherException("Укажите версию");
            return Task.FromResult<object?>(s.Versions.Delete(id));
        });
    }
}
