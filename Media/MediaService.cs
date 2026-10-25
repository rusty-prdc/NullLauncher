using Windows.Media.Control;
using Windows.Storage.Streams;

namespace NullLauncher.Media;

/// <summary>
/// Плашка «сейчас играет» в титульной панели.
/// Читает системные сессии медиа Windows (SMTC): Spotify и любой другой плеер, который показывает
/// трек в медиа-панели системы. Никаких API-ключей и OAuth — только то, что Windows уже знает.
/// Состояние уходит в UI событием media.changed; опрос (5 с) — подстраховка для приложений,
/// которые не шлют события при смене трека.
/// </summary>
public sealed class MediaService : IDisposable
{
    public sealed record MediaState(
        bool Active, string? Title, string? Artist, string? Album, string? App,
        bool Playing, string? Art, bool CanPlayPause, bool CanNext, bool CanPrev,
        double PositionMs, double DurationMs);

    private static readonly MediaState Idle = new(false, null, null, null, null, false, null, false, false, false, 0, 0);


    private readonly Dictionary<string, string> _artCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;
    private System.Threading.Timer? _poll;
    private System.Threading.Timer? _debounce;
    private string _lastSig = "";
    private volatile bool _disposed;

    public MediaService()
    {
        _debounce = new System.Threading.Timer(_ => _ = SyncAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed) return;
            _mgr = mgr;
            mgr.SessionsChanged += (_, _) =>
            {
                try { Attach(mgr.GetCurrentSession()); }
                catch { /* менеджер уже закрыт */ }
            };
            Attach(mgr.GetCurrentSession());
            Log.Debug("SMTC: системные медиа-сессии доступны");
            _poll = new System.Threading.Timer(_ =>
            {
                try { Attach(_mgr?.GetCurrentSession()); }
                catch { /* таймер не должен падать */ }
                ScheduleSync(0);
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) { Log.Debug($"SMTC недоступен: {ex.Message}"); }
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? next)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_session, next)) return;
            if (_session is not null)
            {
                try { _session.MediaPropertiesChanged -= OnMediaProps; } catch { /* сессия умерла */ }
                try { _session.PlaybackInfoChanged -= OnPlaybackInfo; } catch { }
                try { _session.TimelinePropertiesChanged -= OnTimeline; } catch { }
            }
            _session = next;
            if (next is not null)
            {
                try { next.MediaPropertiesChanged += OnMediaProps; } catch { }
                try { next.PlaybackInfoChanged += OnPlaybackInfo; } catch { }
                try { next.TimelinePropertiesChanged += OnTimeline; } catch { }
            }
        }
        ScheduleSync(0);
    }

    private void OnMediaProps(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => ScheduleSync(200);

    private void OnPlaybackInfo(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => ScheduleSync(200);

    private void OnTimeline(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        => ScheduleSync(400);

    private void ScheduleSync(int delayMs)
    {
        if (_disposed) return;
        try { _debounce?.Change(delayMs, Timeout.Infinite); }
        catch { /* таймер остановлен */ }
    }

    private async Task SyncAsync()
    {
        try { Publish(await BuildAsync()); }
        catch (Exception ex) { Log.Debug($"SMTC sync: {ex.Message}"); }
    }

    private void Publish(MediaState state)
    {
        var sig = string.Join("|", new object?[]
        {
            state.Active, state.Title, state.Artist, state.Playing,
            state.Art is not null, state.CanNext, state.CanPrev,
            (long)(state.PositionMs / 5000)
        });
        if (sig == _lastSig) return;
        _lastSig = sig;
        Log.Debug($"media: playing={state.Playing} app={state.App} title=[{state.Title}] pos={state.PositionMs:F0}/{state.DurationMs:F0}");
        AppServices.Current?.Emit("media.changed", state);
    }

    private GlobalSystemMediaTransportControlsSession? Current()
    {
        try { return _session ?? _mgr?.GetCurrentSession(); }
        catch { return null; }
    }

    private async Task<MediaState> BuildAsync()
    {
        var s = Current();
        if (s is null) return Idle;

        GlobalSystemMediaTransportControlsSessionMediaProperties? p = null;
        try { p = await s.TryGetMediaPropertiesAsync(); }
        catch { /* приложение не отдало свойства */ }

        var status = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        var canPlayPause = false;
        var canNext = false;
        var canPrev = false;
        try
        {
            var pb = s.GetPlaybackInfo();
            status = pb.PlaybackStatus;
            canPlayPause = pb.Controls.IsPlayEnabled || pb.Controls.IsPauseEnabled;
            canNext = pb.Controls.IsNextEnabled;
            canPrev = pb.Controls.IsPreviousEnabled;
        }
        catch { /* сессия закрылась во время чтения */ }

        double posMs = 0, durMs = 0;
        try
        {
            var tl = s.GetTimelineProperties();
            posMs = tl.Position.TotalMilliseconds;
            durMs = tl.EndTime > TimeSpan.Zero ? tl.EndTime.TotalMilliseconds : tl.MaxSeekTime.TotalMilliseconds;
        }
        catch { /* таймлайн не обязателен */ }

        if (status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed
            or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped)
            return Idle;

        var playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var title = string.IsNullOrWhiteSpace(p?.Title) ? null : p!.Title;
        if (!playing && title is null) return Idle;

        string app;
        try { app = FriendlyApp(s.SourceAppUserModelId); }
        catch { app = ""; }

        var art = await ResolveArtAsync(p?.Thumbnail, $"{app}|{title}|{p?.Artist}");

        return new MediaState(true, title, EmptyToNull(p?.Artist), EmptyToNull(p?.AlbumTitle),
            app, playing, art, canPlayPause, canNext, canPrev, posMs, durMs);
    }

    /// <summary>Состояние для UI (IPC media.state): трек, обложка, кнопки.</summary>
    public async Task<object> StateAsync()
    {
        try
        {
            var state = await BuildAsync();
            Publish(state);
            return state;
        }
        catch (Exception ex)
        {
            Log.Debug($"SMTC state: {ex.Message}");
            return Idle;
        }
    }

    /// <summary>Кнопки плашки: пауза/продолжить, следующий, предыдущий, перемотка.</summary>
    public async Task<object> ControlAsync(string? action, double positionMs)
    {
        var s = Current();
        if (s is null) return new { ok = false, reason = "no-session" };

        bool ok;
        try
        {
            ok = action switch
            {
                "playPause" => await s.TryTogglePlayPauseAsync(),
                "next" => await s.TrySkipNextAsync(),
                "prev" => await s.TrySkipPreviousAsync(),
                "seek" => await s.TryChangePlaybackPositionAsync(
                    (long)(Math.Clamp(positionMs, 0, 86_400_000) * 10_000)),
                _ => false,
            };
        }
        catch (Exception ex)
        {
            Log.Debug($"SMTC control: {ex.Message}");
            ok = false;
        }

        foreach (var delay in new[] { 400, 1000, 2000, 3500 })
        {
            var d = delay;
            _ = Task.Run(async () => { await Task.Delay(d); ScheduleSync(0); });
        }
        return new { ok };
    }

    /// <summary>Обложка трека: Thumbnail — ссылка WinRT (http(s)- или локальная), читается через OpenReadAsync.</summary>
    private async Task<string?> ResolveArtAsync(IRandomAccessStreamReference? thumb, string cacheKey)
    {
        if (thumb is null) return null;

        lock (_artCache)
            if (_artCache.TryGetValue(cacheKey, out var hit)) return hit;

        try
        {
            using var stream = await thumb.OpenReadAsync();
            var size = stream.Size;
            if (size is <= 0 or > 4_000_000) return null;

            var reader = new DataReader(stream);
            byte[] bytes;
            try
            {
                await reader.LoadAsync((uint)size);
                bytes = new byte[(int)size];
                reader.ReadBytes(bytes);
            }
            finally { reader.Dispose(); }

            var dataUrl = $"data:image/{GuessMime(bytes, cacheKey)};base64,{Convert.ToBase64String(bytes)}";
            lock (_artCache)
            {
                if (_artCache.Count > 16) _artCache.Clear();
                _artCache[cacheKey] = dataUrl;
            }
            return dataUrl;
        }
        catch (Exception ex)
        {
            Log.Debug($"Обложка не загрузилась: {ex.Message}");
            return null;
        }
    }

    private static string? EmptyToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string GuessMime(byte[] bytes, string key)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "jpeg";
        if (bytes.Length >= 4 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F') return "webp";
        return key.Contains(".png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpeg";
    }

    private static string FriendlyApp(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return "";
        if (aumid.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (aumid.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (aumid.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (aumid.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (aumid.Contains("vlc", StringComparison.OrdinalIgnoreCase)) return "VLC";

        var name = aumid;
        var bang = name.IndexOf('!');
        if (bang > 0 && bang + 1 < name.Length) name = name[(bang + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var comma = name.IndexOf(',');
        if (comma > 0) name = name[..comma];
        return name.Length > 24 ? name[..24] : name;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _poll?.Dispose(); } catch { }
        try { _debounce?.Dispose(); } catch { }
        lock (_gate)
        {
            if (_session is not null)
            {
                try { _session.MediaPropertiesChanged -= OnMediaProps; } catch { }
                try { _session.PlaybackInfoChanged -= OnPlaybackInfo; } catch { }
                _session = null;
            }
        }

    }
}
