using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NullLauncher.Core;

public delegate Task<object?> IpcHandler(JsonNode? parameters, CancellationToken ct);

/// <summary>
/// Реестр IPC-методов. Фронтенд шлёт {id, method, params}, хост выполняет и отвечает {id, result|error}.
/// Каждый сервис сам регистрирует свои методы — поэтому маршрутизация не превращается в одну гигантскую функцию.
/// </summary>
public sealed class IpcRouter
{
    private readonly Dictionary<string, IpcHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false,
    };

    public void Register(string method, IpcHandler handler)
    {
        lock (_sync)
        {
            if (_handlers.ContainsKey(method))
                Log.Warn($"IPC-метод '{method}' перерегистрирован");
            _handlers[method] = handler;
        }
    }

    public bool Has(string method) { lock (_sync) return _handlers.ContainsKey(method); }

    public IReadOnlyCollection<string> Methods { get { lock (_sync) return _handlers.Keys.ToArray(); } }

    public async Task<object?> DispatchAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        IpcHandler? handler;
        lock (_sync) _handlers.TryGetValue(method, out handler);
        if (handler is null)
            throw new LauncherException("Функция не поддерживается", $"Метод '{method}' не зарегистрирован в хосте");
        var result = await handler(parameters, ct).ConfigureAwait(false);
        return result;
    }

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, JsonOptions);

    public static JsonNode? ParseParams(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); }
        catch { return null; }
    }
}

/// <summary>Удобные геттеры из параметров IPC-вызова.</summary>
public static class JsonNodeExtensions
{
    public static string? Str(this JsonNode? n, string key)
    {
        var v = n?[key];
        if (v is null) return null;
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s;
            if (jv.TryGetValue<double>(out var d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return v.ToJsonString().Trim('"');
        }
        return v.ToJsonString().Trim('"');
    }

    public static string Str(this JsonNode? n, string key, string def) => n.Str(key) ?? def;

    public static int Int(this JsonNode? n, string key, int def)
    {
        var v = n?[key];
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<int>(out var i)) return i;
            if (jv.TryGetValue<double>(out var d)) return (int)d;
            if (jv.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
        }
        return def;
    }

    public static long Long(this JsonNode? n, string key, long def)
    {
        var v = n?[key];
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<long>(out var l)) return l;
            if (jv.TryGetValue<double>(out var d)) return (long)d;
            if (jv.TryGetValue<string>(out var s) && long.TryParse(s, out var p)) return p;
        }
        return def;
    }

    public static bool Bool(this JsonNode? n, string key, bool def = false)
    {
        var v = n?[key];
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<bool>(out var b)) return b;
            if (jv.TryGetValue<string>(out var s))
            {
                if (bool.TryParse(s, out var pb)) return pb;
                if (s == "1") return true;
                if (s == "0") return false;
            }
        }
        return def;
    }

    public static T? Obj<T>(this JsonNode? n, string key)
    {
        var v = n?[key];
        if (v is null) return default;
        return v.Deserialize<T>(IpcRouter.JsonOptions);
    }

    public static List<string> StrList(this JsonNode? n, string key)
    {
        var v = n?[key] as JsonArray;
        if (v is null) return new List<string>();
        return v.Select(x => x?.ToString().Trim('"')).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
    }

    public static JsonNode? Arr(this JsonNode? n, string key) => n?[key];
}
