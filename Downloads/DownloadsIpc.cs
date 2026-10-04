namespace NullLauncher.Downloads;

/// <summary>IPC менеджера загрузок: очередь, пауза, возобновление, отмена.</summary>
public static class DownloadsIpc
{
    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("downloads.list", (p, _) =>
        {
            var all = s.Downloads.List(p?.Bool("all", false) ?? false);
            return Task.FromResult<object?>(new
            {
                active = all.Count(i => i.State is DownloadState.Running or DownloadState.Queued),
                items = all.Select(Ipc).ToList(),
            });
        });

        r.Register("downloads.pause", (p, _) =>
        {
            s.Downloads.Pause(p?.Str("id") ?? throw new LauncherException("Не указана загрузка"));
            return Task.FromResult<object?>(new { ok = true });
        });

        r.Register("downloads.resume", (p, _) =>
        {
            s.Downloads.Resume(p?.Str("id") ?? throw new LauncherException("Не указана загрузка"));
            return Task.FromResult<object?>(new { ok = true });
        });

        r.Register("downloads.cancel", (p, _) =>
        {
            s.Downloads.Cancel(p?.Str("id") ?? throw new LauncherException("Не указана загрузка"));
            return Task.FromResult<object?>(new { ok = true });
        });

        r.Register("downloads.retry", (p, _) =>
        {
            s.Downloads.Retry(p?.Str("id") ?? throw new LauncherException("Не указана загрузка"));
            return Task.FromResult<object?>(new { ok = true });
        });

        r.Register("downloads.clearFinished", (_, _) =>
        {
            var n = s.Downloads.ClearFinished();
            return Task.FromResult<object?>(new { removed = n });
        });
    }

    private static object Ipc(DownloadItem i) => new
    {
        i.Id, i.Kind, i.Title, i.Url, i.Target, i.InstanceId,
        i.TotalBytes, i.ReceivedBytes,
        progress = Math.Round(i.Progress, 4),
        state = i.State.ToString().ToLowerInvariant(),
        speedBps = (long)i.SpeedBps,
        etaS = (int)i.EtaS,
        i.Error,
        i.RetryCount,
        createdAt = i.CreatedAt.ToString("o"),
    };
}
