using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace NullLauncher.Core;

/// <summary>
/// Discord Rich Presence без сторонних библиотек: прямой IPC к локальному клиенту Discord
/// через именованные каналы discord-ipc-0..9, протокол v1 (handshake + SET_ACTIVITY).
/// Показывает, что лаунчер запущен, а при старте игры — какую сборку и версию человек играет.
/// Discord сам добавляет строку «Играет в NullLauncher» по Application ID.
/// </summary>
public sealed class DiscordPresenceService : IDisposable
{
    // Application ID приложения Discord из Developers Portal (discord.com/developers/applications).
    // Это ID приложения «NullLauncher» — его видно друзьям в статусе. Можно своим в Настройки → Конфиденциальность.
    private const string DefaultClientId = "1562930391499673773";

    private readonly AppServices _s;
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string? _clientId;
    private string? _details;
    private string? _state;
    private bool _gameActive;

    public DiscordPresenceService(AppServices s)
    {
        _s = s;
        _s.Settings.Changed += (_, _) => ApplySettings();
    }

    /// <summary>Стартует фоновый цикл: пытается держать присутствие живым, пока лаунчер открыт.</summary>
    public void Start() => ApplySettings();

    private void ApplySettings()
    {
        string clientId;
        // пустое поле в настройках — не ошибка, а возврат к встроенному ID лаунчера
        lock (_sync)
        {
            clientId = _s.Settings.Get("discordClientId", DefaultClientId);
            if (string.IsNullOrWhiteSpace(clientId)) clientId = DefaultClientId;
        }
        if (!Enabled || string.IsNullOrWhiteSpace(clientId))
        {
            if (Enabled && string.IsNullOrWhiteSpace(clientId))
                Log.Debug("Discord Rich Presence: не задан Application ID (Настройки → Конфиденциальность) — статус выключен");
            lock (_sync) { _cts?.Cancel(); _loop = null; }
            try { ClearActivity(); } catch { /* Discord мог уже закрыться */ }
            return;
        }
        lock (_sync) _clientId = clientId;
        lock (_sync)
        {
            if (_loop != null) return;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }
        SetLauncherPresence();
    }

    private bool Enabled => _s.Settings.Get("discordPresence", true);

    /// <summary>Лаунчер открыт — в статусе видно «В лаунчере NullLauncher».</summary>
    public void SetLauncherPresence()
    {
        Update("NullLauncher", "В лаунчере", game: false);
    }

    /// <summary>Игра стартовала — показываем сборку, версию и загрузчик.</summary>
    public void SetGamePresence(string instanceName, string mcVersion, string? loader)
    {
        var ver = string.IsNullOrWhiteSpace(mcVersion) ? "Minecraft" : "Minecraft " + mcVersion;
        if (!string.IsNullOrWhiteSpace(loader)) ver += " · " + loader;
        Update(ver, instanceName, game: true);
    }

    /// <summary>Игра закрылась — возвращаемся к статусу «В лаунчере».</summary>
    public void ClearGamePresence() => SetLauncherPresence();

    private void Update(string details, string state, bool game)
    {
        lock (_sync)
        {
            _details = details;
            _state = state;
            _gameActive = game;
        }
        if (!Enabled) return;
        string clientId;
        lock (_sync) clientId = _clientId ?? "";
        if (string.IsNullOrWhiteSpace(clientId)) return; // ID не задан — цикл не поднимаем
        lock (_sync)
        {
            if (_loop != null) return;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }
        Wake();
    }

    private readonly SemaphoreSlim _wake = new(0, 1);
    private void Wake() { try { _wake.Release(); } catch { /* уже сигнализировано */ } }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = await ConnectAsync(ct).ConfigureAwait(false);
                if (pipe == null) { await Task.Delay(5000, ct).ConfigureAwait(false); continue; }
                await HandshakeAsync(pipe, ct).ConfigureAwait(false);
                // живём, пока Discord на связи: переподключаемся после обрыва или закрытия клиента
                while (!ct.IsCancellationRequested)
                {
                    SendActivity(pipe);
                    await _wake.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                try { await Task.Delay(5000, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private static async Task<NamedPipeClientStream?> ConnectAsync(CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", "discord-ipc-" + i,
                    PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2000, ct).ConfigureAwait(false);
                return pipe;
            }
            catch { /* следующий сокет */ }
        }
        return null; // Discord не запущен — это нормально, повторим через 5 секунд
    }

    private async Task HandshakeAsync(Stream pipe, CancellationToken ct)
    {
        string clientId;
        lock (_sync) clientId = _clientId ?? DefaultClientId;
        var payload = JsonSerializer.Serialize(new { v = 1, client_id = clientId });
        await WriteFrameAsync(pipe, 0, payload, ct).ConfigureAwait(false);
        _ = await ReadFrameAsync(pipe, ct).ConfigureAwait(false); // READY
    }

    private void SendActivity(Stream pipe)
    {
        string details, state;
        bool game;
        lock (_sync) { details = _details ?? "NullLauncher"; state = _state ?? "В лаунчере"; game = _gameActive; }

        object activity;
        if (game)
        {
            // у игры есть timestamps: видно, сколько уже играют
            activity = new
            {
                details,
                state,
                timestamps = new { start = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                assets = new { large_image = "logo", large_text = "NullLauncher" },
            };
        }
        else
        {
            activity = new
            {
                details,
                state,
                assets = new { large_image = "logo", large_text = "NullLauncher — лаунчер Minecraft" },
            };
        }

        var body = JsonSerializer.Serialize(new
        {
            cmd = "SET_ACTIVITY",
            args = new { pid = Environment.ProcessId, activity },
            nonce = Guid.NewGuid().ToString(),
        });
        WriteFrame(pipe, 1, body);
    }

    private void ClearActivity()
    {
        try
        {
            using var pipe = ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (pipe == null) return;
            var body = JsonSerializer.Serialize(new
            {
                cmd = "SET_ACTIVITY",
                args = new { pid = Environment.ProcessId, activity = (object?)null },
                nonce = Guid.NewGuid().ToString(),
            });
            WriteFrame(pipe, 1, body);
        }
        catch { /* noop */ }
    }

    private static async Task WriteFrameAsync(Stream pipe, int op, string json, CancellationToken ct)
    {
        var data = Encoding.UTF8.GetBytes(json);
        var head = new byte[8];
        BitConverter.TryWriteBytes(head.AsSpan(0, 4), op);
        BitConverter.TryWriteBytes(head.AsSpan(4, 4), data.Length);
        await pipe.WriteAsync(head, ct).ConfigureAwait(false);
        await pipe.WriteAsync(data, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    // повторная отправка при обновлении: синхронная, но канал локальный и мгновенный
    private void WriteFrame(Stream pipe, int op, string json)
    {
        var data = Encoding.UTF8.GetBytes(json);
        var head = new byte[8];
        BitConverter.TryWriteBytes(head.AsSpan(0, 4), op);
        BitConverter.TryWriteBytes(head.AsSpan(4, 4), data.Length);
        pipe.Write(head);
        pipe.Write(data);
        pipe.Flush();
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream pipe, CancellationToken ct)
    {
        var head = new byte[8];
        if (await ReadExactAsync(pipe, head, ct).ConfigureAwait(false) == false) return null;
        var len = BitConverter.ToInt32(head, 4);
        if (len <= 0 || len > 1 << 20) return null;
        var data = new byte[len];
        return await ReadExactAsync(pipe, data, ct).ConfigureAwait(false) == false ? null : data;
    }

    private static async Task<bool> ReadExactAsync(Stream pipe, byte[] buffer, CancellationToken ct)
    {
        var off = 0;
        while (off < buffer.Length)
        {
            var n = await pipe.ReadAsync(buffer.AsMemory(off), ct).ConfigureAwait(false);
            if (n == 0) return false;
            off += n;
        }
        return true;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* noop */ }
        try { ClearActivity(); } catch { /* noop */ }
        _wake.Dispose();
    }
}
