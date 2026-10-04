using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace NullLauncher;

/// <summary>
/// Общий фабрика HTTP-клиентов лаунчера: User-Agent, таймауты и прокси из настроек.
/// Один клиент на процесс (SocketsHttpHandler), повторное создание — только при смене прокси.
/// </summary>
public static class HttpFactory
{
    private static SocketsHttpHandler? _handler;
    private static string? _signature;

    public static HttpClient Create(Core.AppServices s) => Get(s);

    /// <summary>
    /// Возвращает НОВЫЙ HttpClient, разделяющий общий SocketsHttpHandler (пул соединений, прокси, таймауты).
    /// Клиента можно безопасно оборачивать в using — dispose не уничтожает общий handler.
    /// (Раньше здесь отдавался один общий клиент, и `using var http = HttpFactory.Get(...)` убивал
    ///  его для всех последующих вызовов — ObjectDisposedException.)
    /// </summary>
    public static HttpClient Get(Core.AppServices s)
    {
        var sig = string.Join("|",
            s.Settings.Get("proxyType", "none"),
            s.Settings.Get("proxyHost", ""),
            s.Settings.Get("proxyPort", 8080),
            s.Settings.Get("proxyUser", ""),
            s.Settings.Get("proxyPass", ""),
            s.Settings.Get("networkTimeoutSec", 60));

        if (_handler is null || _signature != sig)
        {
            _handler?.Dispose();
            _handler = BuildHandler(s);
            _signature = sig;
        }

        var client = new HttpClient(_handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(10, s.Settings.Get("networkTimeoutSec", 60))),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private static SocketsHttpHandler BuildHandler(Core.AppServices s)
    {
        var connectTimeout = TimeSpan.FromSeconds(Math.Max(5, s.Settings.Get("networkTimeoutSec", 60) / 3));
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = connectTimeout,
            MaxConnectionsPerServer = Math.Clamp(s.Settings.Get("downloadThreads", 8), 1, 32),
            // Устойчивое подключение: часть IP (например, зеркала Cloudflare) может «висеть» —
            // тогда пробываем остальные адреса по очереди, не дожидаясь общего таймаута.
            ConnectCallback = (ctx, ct) => ConnectAsync(ctx, connectTimeout, ct),
        };

        var proxy = BuildProxy(s);
        if (proxy is not null)
        {
            handler.UseProxy = true;
            handler.Proxy = proxy;
        }
        else handler.UseProxy = false;
        return handler;
    }

    public const string UserAgent = "NullLauncher/1.0 (+https://nulllauncher.local; minecraft launcher)";

    /// <summary>
    /// Подключается к хосту, последовательно перебирая его IP-адреса (сначала IPv4).
    /// Нужно потому, что один «мертвый» адрес из DNS-ответа иначе съедает весь ConnectTimeout,
    /// хотя соединение с другим адресом того же хоста устанавливается за миллисекунды.
    /// </summary>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, TimeSpan connectTimeout, CancellationToken ct)
    {
        var ep = ctx.DnsEndPoint;
        var addresses = await Dns.GetHostAddressesAsync(ep.Host, ct).ConfigureAwait(false);
        if (addresses.Length == 0)
            throw new HttpRequestException($"Не удалось разрешить имя хоста {ep.Host}");

        // один адрес — нечего перебирать, отдаём стандартной логике
        if (addresses.Length == 1)
            return await DefaultConnectAsync(ctx, ct).ConfigureAwait(false);

        var ordered = addresses
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();

        var perAddress = TimeSpan.FromSeconds(Math.Clamp(
            connectTimeout.TotalSeconds / ordered.Length, 3, 15));
        Exception? last = null;

        foreach (var addr in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(perAddress);
                await socket.ConnectAsync(addr, ep.Port, cts.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                socket.Dispose();
                throw; // отменил вызывающий код — не свои таймауты
            }
            catch (Exception ex)
            {
                last = ex is OperationCanceledException
                    ? new HttpRequestException($"Таймаут подключения к {addr}:{ep.Port}")
                    : ex;
                socket.Dispose();
            }
        }

        throw new HttpRequestException($"Не удалось подключиться к {ep.Host} ни по одному адресу", last);
    }

    private static async ValueTask<Stream> DefaultConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(ctx.DnsEndPoint, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static IWebProxy? BuildProxy(Core.AppServices s)
    {
        var type = s.Settings.Get("proxyType", "none").ToLowerInvariant();
        switch (type)
        {
            case "none":
                return null;
            case "system":
                return WebRequest.DefaultWebProxy;
            case "http":
            case "https":
            case "socks5":
            {
                var host = s.Settings.Get("proxyHost", "");
                var port = s.Settings.Get("proxyPort", 8080);
                if (string.IsNullOrWhiteSpace(host)) return null;
                var user = s.Settings.Get("proxyUser", "");
                var pass = s.Settings.Get("proxyPass", "");
                var creds = string.IsNullOrEmpty(user) ? null : new NetworkCredential(user, pass);
                // .NET умеет socks5 через URI-схему
                var uri = new Uri($"{(type == "socks5" ? "socks5" : type)}://{host}:{port}");
                return new WebProxy(uri) { Credentials = creds };
            }
            default:
                return null;
        }
    }
}
