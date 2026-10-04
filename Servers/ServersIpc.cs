using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NullLauncher.Core;
using NullLauncher.Instances;
using NullLauncher.Minecraft;

namespace NullLauncher.Servers;

/// <summary>IPC-методы серверов сборки: список, добавление, изменение, удаление и запуск с подключением.</summary>
public static class ServersIpc
{
    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("servers.list", (p, _) => Task.FromResult<object?>(ListServers(s, p)));
        r.Register("servers.add", (p, _) => Task.FromResult<object?>(AddServer(s, p)));
        r.Register("servers.update", (p, _) => Task.FromResult<object?>(UpdateServer(s, p)));
        r.Register("servers.remove", (p, _) => Task.FromResult<object?>(RemoveServer(s, p)));
        r.Register("servers.connect", (p, _) => Connect(s, p));
    }

    /* ------------------------------------------------------------ общее */
    private sealed class ServerRow
    {
        public string InstanceId { get; set; } = "";
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Ip { get; set; } = "";
        public int Port { get; set; } = 25565;
        public string? Icon { get; set; }
    }

    private const string SelectSql =
        "SELECT id, instance_id AS InstanceId, name, ip, port, icon FROM servers WHERE instance_id=@i ORDER BY name COLLATE NOCASE";

    private const string UpsertSql = """
        INSERT INTO servers (instance_id, id, name, ip, port, icon)
        VALUES (@InstanceId, @Id, @Name, @Ip, @Port, @Icon)
        ON CONFLICT(instance_id, id) DO UPDATE SET
          name=excluded.name, ip=excluded.ip, port=excluded.port, icon=excluded.icon
        """;

    private static InstanceRecord RequireInstance(AppServices s, JsonNode? p)
    {
        var id = p?.Str("instanceId");
        if (string.IsNullOrWhiteSpace(id)) throw new LauncherException("Не указана сборка");
        return s.Instances.Require(id);
    }

    private static string RequireServerId(JsonNode? p)
    {
        var serverId = p?.Str("serverId");
        if (string.IsNullOrWhiteSpace(serverId)) throw new LauncherException("Не указан сервер");
        return serverId;
    }

    /// <summary>Разбирает "host", "host:port" или "ip:port"; порт из параметра — как значение по умолчанию.</summary>
    private static (string Host, int Port) ParseAddress(string? raw, int? fallbackPort)
    {
        var value = (raw ?? "").Trim();
        if (value.Length == 0) throw new LauncherException("Укажите адрес сервера");

        if (value.Contains("://"))
            value = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var slash = value.IndexOf('/');
        if (slash >= 0) value = value[..slash];
        value = value.Trim();
        if (value.Length == 0) throw new LauncherException("Укажите адрес сервера");

        var port = fallbackPort ?? 25565;

        if (value.StartsWith('[')) // IPv6 в скобках: [::1]:25565
        {
            var close = value.IndexOf(']');
            if (close > 0)
            {
                var rest = value[(close + 1)..];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out var p6) && p6 is > 0 and <= 65535) port = p6;
                value = value[..(close + 1)];
            }
        }
        else
        {
            var colon = value.IndexOf(':');
            if (colon > 0 && value.LastIndexOf(':') == colon)
            {
                var tail = value[(colon + 1)..];
                if (int.TryParse(tail, out var parsed) && parsed is > 0 and <= 65535)
                {
                    value = value[..colon];
                    port = parsed;
                }
            }
        }

        value = value.Trim();
        if (value.Length == 0) throw new LauncherException("Некорректный адрес сервера", raw ?? "");
        if (!Regex.IsMatch(value, @"^[A-Za-z0-9._\-:\[\]]+$"))
            throw new LauncherException("Некорректный адрес сервера", raw ?? "");
        if (port is <= 0 or > 65535) throw new LauncherException("Некорректный порт сервера", port.ToString());
        return (value, port);
    }

    private static object ToDto(string id, string name, string ip, int port, string? icon)
        => new { id, name, ip, port, icon };

    /* ------------------------------------------------------------ список */
    private static object ListServers(AppServices s, JsonNode? p)
    {
        var inst = RequireInstance(s, p);
        var rows = Database.DatabaseService.Query<ServerRow>(SelectSql, new { i = inst.Id });
        return rows.Select(x => ToDto(x.Id, x.Name, x.Ip, x.Port, x.Icon)).ToList();
    }

    /* ------------------------------------------------------------ добавление */
    private static object AddServer(AppServices s, JsonNode? p)
    {
        var inst = RequireInstance(s, p);
        var name = (p?.Str("name") ?? "").Trim();
        if (name.Length == 0) throw new LauncherException("Укажите название сервера");

        var (host, port) = ParseAddress(p?.Str("ip"), p?["port"] is null ? null : p!.Int("port", 25565));
        var id = Guid.NewGuid().ToString("N");
        var icon = string.IsNullOrWhiteSpace(p?.Str("icon")) ? null : p!.Str("icon");

        Database.DatabaseService.Execute(UpsertSql, new
        {
            InstanceId = inst.Id,
            Id = id,
            Name = name,
            Ip = host,
            Port = port,
            Icon = icon,
        });

        Log.Info($"Добавлен сервер '{name}' ({host}:{port}) для '{inst.Name}'");
        return ToDto(id, name, host, port, icon);
    }

    /* ------------------------------------------------------------ изменение */
    private static object UpdateServer(AppServices s, JsonNode? p)
    {
        var inst = RequireInstance(s, p);
        var serverId = RequireServerId(p);
        var name = (p?.Str("name") ?? "").Trim();
        if (name.Length == 0) throw new LauncherException("Укажите название сервера");

        var (host, port) = ParseAddress(p?.Str("ip"), p?["port"] is null ? null : p!.Int("port", 25565));
        var icon = string.IsNullOrWhiteSpace(p?.Str("icon")) ? null : p!.Str("icon");

        var affected = Database.DatabaseService.Execute("""
            UPDATE servers SET name=@Name, ip=@Ip, port=@Port, icon=COALESCE(@Icon, icon)
            WHERE instance_id=@i AND id=@sid
            """, new { Name = name, Ip = host, Port = port, Icon = icon, i = inst.Id, sid = serverId });
        if (affected == 0) throw new LauncherException("Сервер не найден", serverId);

        Log.Info($"Сервер '{serverId}' обновлён: {name} ({host}:{port})");
        return ToDto(serverId, name, host, port, icon);
    }

    /* ------------------------------------------------------------ удаление */
    private static object RemoveServer(AppServices s, JsonNode? p)
    {
        var inst = RequireInstance(s, p);
        var serverId = RequireServerId(p);
        var affected = Database.DatabaseService.Execute(
            "DELETE FROM servers WHERE instance_id=@i AND id=@sid", new { i = inst.Id, sid = serverId });
        if (affected == 0) throw new LauncherException("Сервер не найден", serverId);

        Log.Info($"Сервер '{serverId}' удалён из '{inst.Name}'");
        return new { ok = true };
    }

    /* ------------------------------------------------------------ подключение */
    private static Task<object?> Connect(AppServices s, JsonNode? p)
    {
        var inst = RequireInstance(s, p);
        var serverId = RequireServerId(p);
        var srv = Database.DatabaseService.QueryFirstOrDefault<ServerRow>(
            "SELECT id, instance_id AS InstanceId, name, ip, port, icon FROM servers WHERE instance_id=@i AND id=@sid",
            new { i = inst.Id, sid = serverId }) ?? throw new LauncherException("Сервер не найден", serverId);

        if (s.Launch.IsRunning(inst.Id))
            throw new LauncherException("Сборка уже запущена", "Закройте игру, чтобы подключиться к серверу");

        // адрес сохраняем у сборки — следующие запуски пойдут на этот же сервер
        inst.ServerIp = srv.Ip;
        inst.ServerPort = srv.Port;
        s.Instances.SaveMetadata(inst);
        Instances.InstanceRepository.Upsert(inst);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = s.Launch.StartAsync(new LaunchRequest
        {
            InstanceId = inst.Id,
            ServerIp = srv.Ip,
            ServerPort = srv.Port,
        }, CancellationToken.None, started);

        _ = task.ContinueWith(t =>
        {
            if (!t.IsFaulted) return;
            var msg = t.Exception?.GetBaseException().Message ?? "неизвестная ошибка";
            Log.Warn($"Подключение к {srv.Ip}:{srv.Port} ('{srv.Name}') не удалось: {msg}");
            // ошибка до внутреннего try в StartAsync — тогда своё уведомление там не показано
            if (started.Task.Status == TaskStatus.Created)
                s.NotifyUser("error", "Не удалось запустить Minecraft", msg);
        }, TaskScheduler.Default);

        Log.Info($"Подключение к серверу '{srv.Name}' ({srv.Ip}:{srv.Port}) через '{inst.Name}'");
        return Task.FromResult<object?>(new { ok = true });
    }
}
