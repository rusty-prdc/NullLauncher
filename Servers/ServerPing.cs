using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NullLauncher.Servers;

/// <summary>Статус-пинг Minecraft-сервера (list status): онлайн, максимум, версия, latency.
/// Лёгкий TCP-запрос без библиотек; таймаут короткий, чтобы вкладка не подвисала.</summary>
public static class ServerPing
{
    public sealed record Result(bool Online, int OnlinePlayers, int MaxPlayers, string Version, int PingMs, string? Error);

    private const int TimeoutMs = 3500;

    /// <summary>Пингует сервер по host:port. Всегда возвращает результат (никогда не бросает).</summary>
    public static async Task<Result> PingAsync(string host, int port, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeoutMs);

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            using var stream = tcp.GetStream();

            await WriteHandshakeAsync(stream, host, port, timeout.Token).ConfigureAwait(false);
            await stream.WriteAsync(new byte[] { 0x00 }, timeout.Token).ConfigureAwait(false); // status request

            var json = await ReadStatusJsonAsync(stream, timeout.Token).ConfigureAwait(false);
            sw.Stop();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var online = root.TryGetProperty("players", out var pl) && pl.TryGetProperty("online", out var on) ? on.GetInt32() : 0;
            var max = root.TryGetProperty("players", out pl) && pl.TryGetProperty("max", out var mx) ? mx.GetInt32() : 0;
            var version = root.TryGetProperty("version", out var v) && v.TryGetProperty("name", out var vn) ? vn.GetString() ?? "" : "";

            return new Result(true, online, max, version, (int)sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException)
        {
            return new Result(false, 0, 0, "", 0, "таймаут");
        }
        catch (SocketException ex)
        {
            return new Result(false, 0, 0, "", 0, ex.Message);
        }
        catch (Exception ex)
        {
            return new Result(false, 0, 0, "", (int)sw.ElapsedMilliseconds, ex.Message);
        }
    }

    private static async Task WriteHandshakeAsync(NetworkStream s, string host, int port, CancellationToken ct)
    {
        var hostBytes = Encoding.UTF8.GetBytes(host);
        using var ms = new MemoryStream();
        WriteVarInt(ms, 0x00);            // packet id
        WriteVarInt(ms, -1);              // protocol version: -1 = status-only
        WriteVarInt(ms, hostBytes.Length);
        ms.Write(hostBytes);
        ms.Write(BitConverter.GetBytes((ushort)port).Reverse().ToArray()); // big-endian
        WriteVarInt(ms, 1);               // next state: 1 = status
        var body = ms.ToArray();

        var len = new MemoryStream();
        WriteVarInt(len, body.Length);
        var prefix = len.ToArray();

        await s.WriteAsync(prefix, ct).ConfigureAwait(false);
        await s.WriteAsync(body, ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadStatusJsonAsync(NetworkStream s, CancellationToken ct)
    {
        var len = await ReadVarIntAsync(s, ct).ConfigureAwait(false);
        if (len <= 0 || len > 4_000_000) throw new IOException("некорректный ответ сервера");

        var buffer = new byte[len];
        var read = 0;
        while (read < len)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read, len - read), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("соединение закрыто");
            read += n;
        }

        using var ms = new MemoryStream(buffer);
        var packetId = ReadVarInt(ms);
        if (packetId != 0x00) throw new IOException("неожиданный пакет " + packetId);
        var jsonLen = ReadVarInt(ms);
        if (jsonLen <= 0 || jsonLen > ms.Length - ms.Position) throw new IOException("некорректная длина JSON");
        var json = Encoding.UTF8.GetString(ms.ToArray(), (int)ms.Position, jsonLen);
        return json;
    }

    private static void WriteVarInt(Stream s, int value)
    {
        var u = (uint)value;
        while (true)
        {
            if ((u & ~0x7Fu) == 0) { s.WriteByte((byte)u); return; }
            s.WriteByte((byte)(u & 0x7F | 0x80));
            u >>= 7;
        }
    }

    private static int ReadVarInt(Stream s)
    {
        var value = 0; var shift = 0; int b;
        do
        {
            b = s.ReadByte();
            if (b < 0) throw new IOException("обрыв потока");
            value |= (b & 0x7F) << shift;
            shift += 7;
            if (shift > 35) throw new IOException("слишком длинный varint");
        } while ((b & 0x80) != 0);
        return value;
    }

    private static async Task<int> ReadVarIntAsync(NetworkStream s, CancellationToken ct)
    {
        var value = 0; var shift = 0;
        var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("обрыв потока");
            var b = one[0];
            value |= (b & 0x7F) << shift;
            shift += 7;
            if (shift > 35) throw new IOException("слишком длинный varint");
            if ((b & 0x80) == 0) return value;
        }
    }
}
