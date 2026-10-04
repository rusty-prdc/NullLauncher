namespace NullLauncher.Minecraft;

public static class LaunchIpc
{
    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("launch.compatCheck", async (p, ct) =>
        {
            var id = p?.Str("instanceId") ?? throw new LauncherException("Не указана сборка");
            var fix = p?.Bool("fix", false);
            var res = await s.Launch.CompatCheckAsync(id, fix ?? false, ct).ConfigureAwait(false);
            return new
            {
                ok = res.Ok,
                issues = res.Issues.Select(i => new
                {
                    severity = i.Severity, title = i.Title, message = i.Message,
                    fixable = i.Fixable, @fixed = i.Fixed, fixAction = i.FixAction,
                }).ToList(),
                fixedCount = res.FixedCount,
            };
        });

        r.Register("launch.start", async (p, ct) =>
        {
            var id = p?.Str("instanceId") ?? throw new LauncherException("Не указана сборка");
            var server = p?["server"];
            int? port = null;
            if (server is not null) port = server.Int("port", 25565);
            else if (p?.Int("serverPort", 0) is int sp && sp > 0) port = sp;

            var req = new LaunchRequest
            {
                InstanceId = id,
                SafeMode = p?.Bool("safeMode", false) ?? false,
                ServerIp = server?.Str("ip") ?? p?.Str("serverIp"),
                ServerPort = port,
                OnlyMods = p?.StrList("onlyMods"),
            };

            // отвечаем, когда процесс игры реально стартовал; дальше события идут событиями
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = s.Launch.StartAsync(req, CancellationToken.None, started);
            await Task.WhenAny(task, started.Task).ConfigureAwait(false);

            // сначала наблюдаем фоновую задачу — иначе её исключение «всплывёт» от финализатора
            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Log.Warn($"Фоновый запуск {id} завершился ошибкой: {t.Exception?.GetBaseException().Message}");
            }, TaskScheduler.Default);

            if (started.Task.IsFaulted)
                await started.Task.ConfigureAwait(false); // бросит исключение до старта
            if (task.IsFaulted)
                await task.ConfigureAwait(false);         // ошибка до момента старта

            return (object?)new { ok = true };
        });

        r.Register("launch.stop", (p, _) =>
        {
            var id = p?.Str("instanceId") ?? throw new LauncherException("Не указана сборка");
            var res = s.Launch.Stop(id);
            // «ничего не запущено» — не ошибка: UI просто обновит состояние кнопки
            return Task.FromResult<object?>(new
            {
                ok = true,
                stopped = res.stopped,
                wasPreparing = res.wasPreparing,
                wasRunning = res.wasRunning,
            });
        });

        // без параметров → [{ instanceId, name, state, preparing, startedAt, pid }]
        r.Register("launch.running", (_, _) =>
            Task.FromResult<object?>(new { limit = s.Launch.MaxRunning, items = s.Launch.RunningList() }));

        r.Register("launch.lastStatus", (p, _) =>
        {
            var id = p?.Str("instanceId") ?? throw new LauncherException("Не указана сборка");
            var st = s.Launch.Status(id);
            return Task.FromResult<object?>(new
            {
                state = st.State,
                exitCode = st.ExitCode,
                startedAt = st.StartedAt?.ToString("o"),
                endedAt = st.EndedAt?.ToString("o"),
                error = st.Error,
                versionId = st.VersionId,
                running = s.Launch.IsRunning(id),
                preparing = string.Equals(st.State, "preparing", StringComparison.OrdinalIgnoreCase),
            });
        });
    }
}
