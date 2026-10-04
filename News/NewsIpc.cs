using NullLauncher.Core;

namespace NullLauncher.News;

/// <summary>IPC-методы новостей.</summary>
public static class NewsIpc
{
    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("news.list", async (p, ct) =>
            (object?)await NewsService.ListAsync(s, p.Int("limit", 8), ct).ConfigureAwait(false));
    }
}
