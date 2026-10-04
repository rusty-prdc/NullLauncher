using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace NullLauncher.Downloads;

public enum DownloadState
{
    Queued, Running, Paused, Done, Error, Cancelled,
}

public sealed class DownloadItem
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "file";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Target { get; set; } = "";
    public string? InstanceId { get; set; }
    public long TotalBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public double Progress { get; set; }
    public DownloadState State { get; set; } = DownloadState.Queued;
    public double SpeedBps { get; set; }
    public double EtaS { get; set; }
    public string? Error { get; set; }
    public int RetryCount { get; set; }
    public string? HashAlgo { get; set; }
    public string? Hash { get; set; }
    public bool Hidden { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [System.Text.Json.Serialization.JsonIgnore]
    public CancellationTokenSource? Cts;
    [System.Text.Json.Serialization.JsonIgnore]
    public TaskCompletionSource<DownloadItem>? Tcs;
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime StartedAt;
    [System.Text.Json.Serialization.JsonIgnore]
    public long LastBytes;
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime LastTick;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool PauseRequested;
}

/// <summary>
/// Очередь загрузок с прогрессом, паузой, возобновлением, проверкой хешей.
/// Событие downloads.progress шлётся пачками (не чаще 4 раз в секунду), чтобы не тормозить UI.
/// </summary>
public sealed class DownloadService : IDisposable
{
    private readonly Core.AppServices _s;
    private readonly ConcurrentDictionary<string, DownloadItem> _items = new();
    /// <summary>Активная загрузка на конкретный путь — чтобы один файл не качали дважды.</summary>
    private readonly ConcurrentDictionary<string, DownloadItem> _byTarget = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Блокировки на путь: повторная попытка не должна открывать тот же .part.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _targetLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workers = new();
    private readonly object _emitSync = new();
    private bool _emitScheduled;

    public DownloadService(Core.AppServices s)
    {
        _s = s;
        LoadHistory();
        for (var i = 0; i < 4; i++)
            _workers.Add(Task.Run(() => WorkerLoop(_cts.Token)));
    }

    public int Active => _items.Values.Count(i => i.State == DownloadState.Running || i.State == DownloadState.Queued);

    /// <summary>
    /// Сводка по активным загрузкам: сколько байт получено/всего, суммарная скорость и оставшееся время.
    /// Нужна для прогресса запуска (полоса, проценты, время, скорость).
    /// </summary>
    public (long received, long total, double speedBps, double etaS, int active) Stats()
    {
        long received = 0, total = 0;
        double speed = 0, eta = 0;
        var active = 0;
        foreach (var i in _items.Values)
        {
            if (i.State is not (DownloadState.Running or DownloadState.Queued)) continue;
            active++;
            received += i.ReceivedBytes;
            total += i.TotalBytes;
            if (i.State == DownloadState.Running) speed += i.SpeedBps;
        }
        if (speed > 0) eta = Math.Max(0, total - received) / speed;
        return (received, total, speed, eta, active);
    }

    /* --------------------------------------------------------- API */

    /// <summary>Ставит загрузку в очередь. await вернёт завершённый элемент (или бросит исключение).</summary>
    public Task<DownloadItem> EnqueueAsync(DownloadRequest req, CancellationToken ct = default)
    {
        var item = new DownloadItem
        {
            Id = string.IsNullOrEmpty(req.Id) ? Guid.NewGuid().ToString("N")[..12] : req.Id,
            Kind = req.Kind,
            Title = req.Title,
            Url = req.Url,
            Target = req.Target,
            InstanceId = req.InstanceId,
            HashAlgo = req.HashAlgo,
            Hash = req.Hash,
            Hidden = req.Hidden,
            Tcs = new TaskCompletionSource<DownloadItem>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        if (Directory.Exists(req.Target)) item.Target = Path.Combine(req.Target, GuessName(req.Url));

        if (_items.TryGetValue(item.Id, out var old) && old.State is DownloadState.Running or DownloadState.Queued)
            return old.Tcs!.Task;

        // тот же файл уже качается (или стоит в очереди) — не запускаем вторую загрузку
        if (_byTarget.TryGetValue(item.Target, out var sameTarget) &&
            sameTarget.State is DownloadState.Running or DownloadState.Queued)
            return sameTarget.Tcs!.Task;

        _items[item.Id] = item;
        _byTarget[item.Target] = item;
        Persist(item);
        _signal.Release();
        Emit(force: true);

        if (ct.CanBeCanceled)
        {
            ct.Register(() =>
            {
                if (item.State is DownloadState.Queued or DownloadState.Running)
                {
                    item.Cts?.Cancel();
                    item.State = DownloadState.Cancelled;
                    item.Tcs?.TrySetCanceled();
                    Persist(item);
                    Emit(force: true);
                }
            });
        }
        return item.Tcs!.Task;
    }

    public List<DownloadItem> List(bool includeHidden = false)
        => _items.Values
            .Where(i => includeHidden || !i.Hidden)
            .OrderByDescending(i => i.CreatedAt)
            .ToList();

    public DownloadItem? Get(string id) => _items.TryGetValue(id, out var i) ? i : null;

    /// <summary>Снимает связь «путь → активная загрузка», если она принадлежит этому элементу.</summary>
    private void ReleaseTarget(DownloadItem item)
    {
        if (_byTarget.TryGetValue(item.Target, out var current) && ReferenceEquals(current, item))
            _byTarget.TryRemove(item.Target, out _);
    }

    public void Cancel(string id)
    {
        if (!_items.TryGetValue(id, out var item)) return;
        if (item.State == DownloadState.Done) return;
        item.Cts?.Cancel();
        item.State = DownloadState.Cancelled;
        item.Tcs?.TrySetCanceled();
        ReleaseTarget(item);
        Persist(item);
        Emit(force: true);
    }

    public void Pause(string id)
    {
        if (!_items.TryGetValue(id, out var item)) return;
        if (item.State is not (DownloadState.Running or DownloadState.Queued)) return;
        item.PauseRequested = true;
        item.Cts?.Cancel();
        item.State = DownloadState.Paused;
        Persist(item);
        Emit(force: true);
    }

    public void Resume(string id)
    {
        if (!_items.TryGetValue(id, out var item)) return;
        if (item.State is not (DownloadState.Paused or DownloadState.Cancelled or DownloadState.Error)) return;
        item.Error = null;
        item.State = DownloadState.Queued;
        item.Tcs = new TaskCompletionSource<DownloadItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        _byTarget[item.Target] = item;
        Persist(item);
        _signal.Release();
        Emit(force: true);
    }

    public void Retry(string id)
    {
        if (!_items.TryGetValue(id, out var item)) return;
        if (item.State is DownloadState.Done) return;
        item.RetryCount++;
        item.PauseRequested = false;
        Resume(id);
    }

    public int ClearFinished()
    {
        var removed = 0;
        foreach (var i in _items.Values.Where(x => x.State is DownloadState.Done or DownloadState.Cancelled or DownloadState.Error && !x.Hidden).ToList())
        {
            _items.TryRemove(i.Id, out _);
            removed++;
        }
        if (removed > 0)
        {
            Database.DatabaseService.Execute(
                "DELETE FROM downloads WHERE state IN ('done','error','cancelled') AND @hidden = 0",
                new { hidden = 0 });
            Emit(force: true);
        }
        return removed;
    }

    /// <summary>Скачать файл и дождаться результата (используется движком запуска и контентом).</summary>
    public Task<DownloadItem> DownloadAsync(string url, string target, string title, string kind = "file",
        string? hashAlgo = null, string? hash = null, string? instanceId = null, CancellationToken ct = default)
        => EnqueueAsync(new DownloadRequest(
            Id: Guid.NewGuid().ToString("N")[..12],
            Kind: kind, Title: title, Url: url, Target: target,
            InstanceId: instanceId, HashAlgo: hashAlgo, Hash: hash), ct);

    /* --------------------------------------------------------- воркеры */

    private async Task WorkerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var max = Math.Clamp(_s.Settings.Get("maxConcurrentDownloads", 6), 1, 16);
            var running = _items.Values.Count(i => i.State == DownloadState.Running);
            if (running >= max)
            {
                _signal.Release();
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            var next = _items.Values
                .Where(i => i.State == DownloadState.Queued)
                .OrderBy(i => i.CreatedAt)
                .FirstOrDefault();
            if (next is null) continue;

            next.State = DownloadState.Running;
            next.StartedAt = DateTime.UtcNow;
            next.LastTick = DateTime.UtcNow;
            next.LastBytes = 0;
            next.Cts = new CancellationTokenSource();
            if (ct.CanBeCanceled) ct.Register(() => next.Cts?.Cancel());
            Emit(force: true);
            _ = Task.Run(() => RunAsync(next, next.Cts.Token), CancellationToken.None);
            _signal.Release();
        }
    }

    /// <summary>Обёртка с блокировкой на конкретный файл: одна попытка загрузки на путь.</summary>
    private async Task RunAsync(DownloadItem item, CancellationToken ct)
    {
        var gate = _targetLocks.GetOrAdd(item.Target, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await RunAttemptAsync(item, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
            if (gate.CurrentCount == 1) _targetLocks.TryRemove(item.Target, out _);
            if (item.State is DownloadState.Done or DownloadState.Cancelled or DownloadState.Error)
                ReleaseTarget(item);
        }
    }

    private async Task RunAttemptAsync(DownloadItem item, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
            var part = item.Target + ".part";

            // файл уже на месте и размер совпадает — считаем выполненным
            if (!item.Hash.HasValue() && File.Exists(item.Target) && item.TotalBytes > 0 &&
                new FileInfo(item.Target).Length == item.TotalBytes)
            {
                Finish(item, part);
                return;
            }

            var resumeFrom = File.Exists(part) ? new FileInfo(part).Length : 0;
            var speedLimit = _s.Settings.Get("speedLimitKbs", 0);

            using var http = HttpFactory.Get(_s);
            using var req = new HttpRequestMessage(HttpMethod.Get, item.Url);
            if (resumeFrom > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && resumeFrom > 0)
            {
                // докачка с середины не поддерживается — удаляем огрызок и повторяем через очередь
                File.Delete(part);
                throw new LauncherException("Докачка невозможна", "Файл будет скачан заново");
            }
            resp.EnsureSuccessStatusCode();

            item.TotalBytes = resp.Content.Headers.ContentLength is long l ? l + (resumeFrom > 0 ? resumeFrom : 0)
                : resumeFrom;
            if (resp.Content.Headers.ContentRange?.Length is long full) item.TotalBytes = full;

            await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(part, resumeFrom > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

            var buffer = new byte[1 << 16];
            var window = new Queue<(DateTime t, long bytes)>();
            int n;
            var lastEmit = DateTime.UtcNow;

            while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (item.PauseRequested) throw new OperationCanceledException();
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);

                item.ReceivedBytes += n;
                if (item.TotalBytes > 0) item.Progress = Math.Min(1.0, (double)item.ReceivedBytes / item.TotalBytes);

                // скорость по скользящему окну 3 секунд
                var now = DateTime.UtcNow;
                window.Enqueue((now, n));
                while (window.Count > 0 && (now - window.Peek().t).TotalSeconds > 3) window.Dequeue();
                var span = (now - window.Peek().t).TotalSeconds;
                if (span > 0.2)
                {
                    item.SpeedBps = window.Sum(w => w.bytes) / span;
                    item.EtaS = item.TotalBytes > 0 && item.SpeedBps > 0
                        ? Math.Max(0, (item.TotalBytes - item.ReceivedBytes) / item.SpeedBps) : 0;
                }

                if (speedLimit > 0 && item.SpeedBps > speedLimit * 1024)
                {
                    var throttleMs = (int)Math.Clamp(n / (double)(speedLimit * 1024) * 1000, 1, 500);
                    await Task.Delay(throttleMs, ct).ConfigureAwait(false);
                }

                if ((now - lastEmit).TotalMilliseconds > 250)
                {
                    lastEmit = now;
                    Emit();
                }
            }

            output.Close();

            if (_s.Settings.Get("verifyHashes", true) && item.Hash.HasValue())
            {
                var ok = await VerifyAsync(part, item.HashAlgo!, item.Hash!, ct).ConfigureAwait(false);
                if (!ok)
                    throw new LauncherException("Контрольная сумма не совпала",
                        $"{Path.GetFileName(item.Target)}: ожидался {item.HashAlgo}:{item.Hash}");
            }

            Finish(item, part);
        }
        catch (OperationCanceledException)
        {
            if (item.PauseRequested)
            {
                item.State = DownloadState.Paused;
                item.Error = null;
                item.Tcs?.TrySetResult(item);
                item.Tcs = new TaskCompletionSource<DownloadItem>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                item.State = DownloadState.Cancelled;
                item.Tcs?.TrySetCanceled();
            }
            item.SpeedBps = 0;
            Persist(item);
            Emit(force: true);
        }
        catch (Exception ex)
        {
            item.Error = ex is LauncherException le ? le.UserMessage : ex.Message;
            item.State = DownloadState.Error;
            item.SpeedBps = 0;
            item.Tcs?.TrySetException(ex is LauncherException ? ex
                : new LauncherException("Не удалось скачать файл", $"{item.Url}\n{ex.Message}", ex));
            Persist(item);
            Emit(force: true);
            Log.Warn($"Загрузка {item.Title}: {item.Error}");

            if (item.RetryCount < 3)
            {
                item.RetryCount++;
                await Task.Delay(1000 * item.RetryCount).ConfigureAwait(false);
                item.Error = null;
                item.State = DownloadState.Queued;
                item.Tcs = new TaskCompletionSource<DownloadItem>(TaskCreationOptions.RunContinuationsAsynchronously);
                Persist(item);
                _signal.Release();
            }
        }
    }

    private void Finish(DownloadItem item, string part)
    {
        try
        {
            if (File.Exists(part))
            {
                if (File.Exists(item.Target)) File.Delete(item.Target);
                File.Move(part, item.Target);
            }
            item.Progress = 1;
            item.State = DownloadState.Done;
            item.Error = null;
            item.EtaS = 0;
            item.SpeedBps = 0;
            item.ReceivedBytes = Math.Max(item.ReceivedBytes, item.TotalBytes);
            item.Tcs?.TrySetResult(item);
        }
        catch (Exception ex)
        {
            item.State = DownloadState.Error;
            item.Error = ex.Message;
            item.Tcs?.TrySetException(new LauncherException("Не удалось сохранить файл", ex.Message, ex));
        }
        Persist(item);
        Emit(force: true);
    }

    private static async Task<bool> VerifyAsync(string file, string algo, string expected, CancellationToken ct)
    {
        await using var fs = File.OpenRead(file);
        byte[] hash = algo.ToLowerInvariant() switch
        {
            "sha1" => await SHA1.HashDataAsync(fs, ct).ConfigureAwait(false),
            "sha256" => await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false),
            "sha512" => await SHA512.HashDataAsync(fs, ct).ConfigureAwait(false),
            "md5" => await MD5.HashDataAsync(fs, ct).ConfigureAwait(false),
            _ => Array.Empty<byte>(),
        };
        if (hash.Length == 0) return true;
        return string.Equals(Convert.ToHexString(hash), expected.Replace("-", ""), StringComparison.OrdinalIgnoreCase);
    }

    private static string GuessName(string url)
    {
        try
        {
            var name = Path.GetFileName(new Uri(url).LocalPath);
            return string.IsNullOrEmpty(name) ? "download" : name;
        }
        catch { return "download"; }
    }

    /* --------------------------------------------------------- события */

    public void Emit(bool force = false)
    {
        lock (_emitSync)
        {
            if (_emitScheduled && !force) return;
            if (!force && (DateTime.UtcNow - _lastEmit).TotalMilliseconds < 250) return;
            _emitScheduled = true;
        }
        _lastEmit = DateTime.UtcNow;
        var snapshot = List().Select(i => new
        {
            i.Id, i.Kind, i.Title, i.Url, i.Target, i.InstanceId, i.TotalBytes, i.ReceivedBytes,
            progress = Math.Round(i.Progress, 4), state = i.State.ToString().ToLowerInvariant(),
            speedBps = (long)i.SpeedBps, etaS = (int)i.EtaS, i.Error, i.RetryCount,
        }).ToList();
        _s.Emit("downloads.progress", snapshot);
        lock (_emitSync) _emitScheduled = false;
    }

    private DateTime _lastEmit = DateTime.MinValue;

    /* --------------------------------------------------------- персистентность */

    private void Persist(DownloadItem i)
    {
        if (i.Hidden) return;
        try
        {
            Database.DatabaseService.Execute("""
                INSERT INTO downloads (id, kind, title, url, target, instance_id, total_bytes, received_bytes,
                  progress, state, hash_algo, hash_value, error, retry_count, created_at, updated_at)
                VALUES (@Id, @Kind, @Title, @Url, @Target, @InstanceId, @TotalBytes, @ReceivedBytes,
                  @Progress, @State, @HashAlgo, @Hash, @Error, @RetryCount, @CreatedAt, @UpdatedAt)
                ON CONFLICT(id) DO UPDATE SET
                  total_bytes=excluded.total_bytes, received_bytes=excluded.received_bytes,
                  progress=excluded.progress, state=excluded.state, error=excluded.error,
                  retry_count=excluded.retry_count, updated_at=excluded.updated_at
                """, new
            {
                i.Id, i.Kind, i.Title, i.Url, i.Target, i.InstanceId, i.TotalBytes, i.ReceivedBytes,
                i.Progress,
                State = i.State.ToString().ToLowerInvariant(),
                i.HashAlgo, i.Hash, i.Error, i.RetryCount,
                CreatedAt = i.CreatedAt.ToString("o"),
                UpdatedAt = DateTime.UtcNow.ToString("o"),
            });
        }
        catch (Exception ex) { Log.Debug($"Не удалось сохранить состояние загрузки: {ex.Message}"); }
    }

    private void LoadHistory()
    {
        try
        {
            var rows = Database.DatabaseService.Query<DownloadRow>(
                "SELECT id, kind, title, url, target, instance_id AS InstanceId, total_bytes AS TotalBytes, " +
                "received_bytes AS ReceivedBytes, progress, state, error, retry_count AS RetryCount, " +
                "created_at AS CreatedAt, updated_at AS UpdatedAt FROM downloads ORDER BY created_at DESC LIMIT 300");
            foreach (var r in rows)
            {
                var state = r.State switch
                {
                    "running" => DownloadState.Error,     // прерванная прошлой сессией
                    "queued" => DownloadState.Error,
                    "paused" => DownloadState.Paused,
                    "done" => DownloadState.Done,
                    "cancelled" => DownloadState.Cancelled,
                    _ => DownloadState.Error,
                };
                _items[r.Id] = new DownloadItem
                {
                    Id = r.Id, Kind = r.Kind, Title = r.Title, Url = r.Url, Target = r.Target,
                    InstanceId = r.InstanceId, TotalBytes = r.TotalBytes, ReceivedBytes = r.ReceivedBytes,
                    Progress = r.Progress, State = state, Error = r.Error, RetryCount = r.RetryCount,
                    CreatedAt = DateTime.TryParse(r.CreatedAt, out var c) ? c : DateTime.UtcNow,
                };
            }
        }
        catch (Exception ex) { Log.Warn($"История загрузок: {ex.Message}"); }
    }

    private sealed class DownloadRow
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string Target { get; set; } = "";
        public string? InstanceId { get; set; }
        public long TotalBytes { get; set; }
        public long ReceivedBytes { get; set; }
        public double Progress { get; set; }
        public string State { get; set; } = "";
        public string? Error { get; set; }
        public int RetryCount { get; set; }
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var i in _items.Values) i.Cts?.Cancel();
        _cts.Dispose();
    }
}

public sealed record DownloadRequest(
    string Id, string Kind, string Title, string Url, string Target,
    string? InstanceId = null, string? HashAlgo = null, string? Hash = null, bool Hidden = false);

public static class StringHashExt
{
    public static bool HasValue(this string? s) => !string.IsNullOrWhiteSpace(s);
}
