using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NullLauncher.Core;

namespace NullLauncher.Auth;

/// <summary>
/// Аккаунт NullLauncher: собственная регистрация/вход против api.nulllaucnher.xprodc.site.
/// Пароли никогда не хранятся — только токен, зашифрованный DPAPI (CurrentUser).
/// Здесь же: баланс NullCoin, ежедневный бонус и синхронизация часов игры (дельтами).
/// </summary>
public sealed class AuthService
{
    private readonly AppServices _s;
    private readonly object _lock = new();

    public AuthService(AppServices s) { _s = s; }

    /// <summary>Базовый адрес API. Пока DNS/SSL не настроен — можно переключить kv accountApi на IP.</summary>
    private string BaseUrl => _s.Settings.Get("accountApi", "https://api.xprodc.site").TrimEnd('/');

    private string? Token
    {
        get
        {
            lock (_lock)
            {
                var v = _s.Settings.Get("authToken", "");
                if (string.IsNullOrEmpty(v)) return null;
                try { return Unprotect(v); }
                catch { return null; } /* токен с другой машины/пользователя — считаем отсутствующим */
            }
        }
    }

    private void SaveToken(string? token)
    {
        lock (_lock)
            _s.Settings.Set("authToken", token is null ? "" : Protect(token), raise: false);
    }

    private static string Protect(string plain)
        => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string b64)
        => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser));

    /* ================= HTTP ================= */

    private async Task<JsonNode> ReqAsync(string method, string path, object? body, bool auth = true)
    {
        var baseUrl = BaseUrl;
        using var http = HttpFactory.Get(_s);
        using var req = new HttpRequestMessage(new HttpMethod(method), baseUrl + path);

        // дев-режим по IP: имя домена уезжает в Host-заголовок (nginx роутит по нему)
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) && System.Net.IPAddress.TryParse(b.Host, out _))
            req.Headers.Host = "api.xprodc.site";

        var token = auth ? Token : null;
        if (auth && token is null)
            throw new LauncherException("Войдите в аккаунт NullLauncher", "Токен не найден");
        if (token is not null)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, IpcRouter.JsonOptions), Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req); }
        catch (Exception ex) { throw new LauncherException("Сервис аккаунтов недоступен — проверьте подключение", ex.Message, ex); }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync();
            JsonNode? node = null;
            try { node = JsonNode.Parse(text); } catch { /* не JSON */ }
            if (!resp.IsSuccessStatusCode)
            {
                var msg = Str(node, "message");
                if (string.IsNullOrEmpty(msg)) msg = $"Сервис аккаунтов ответил ошибкой ({(int)resp.StatusCode})";
                throw new LauncherException(msg, text.Length > 400 ? text[..400] : text);
            }
            if (node is null)
                throw new LauncherException("Сервис аккаунтов вернул пустой ответ", $"HTTP {(int)resp.StatusCode}");
            return node;
        }
    }

    /* ================= профиль ================= */

    public async Task<object> StateAsync()
    {
        if (Token is null) return new { authed = false };
        try { return await MeCoreAsync(emit: false); }
        catch
        {
            var cached = _s.Settings.Get("authUser", "");
            if (!string.IsNullOrEmpty(cached))
            {
                try { return Dto(JsonNode.Parse(cached)!, offline: true); }
                catch { /* битый кэш */ }
            }
            return new { authed = false, offline = true };
        }
    }

    public async Task<object> MeAsync()
    {
        var dto = await MeCoreAsync(emit: true);
        return dto;
    }

    private async Task<object> MeCoreAsync(bool emit)
    {
        var node = await ReqAsync("GET", "/api/me", null);
        _s.Settings.Set("authUser", node.ToJsonString(), raise: false);
        var dto = Dto(node);
        if (emit) _s.Emit("auth.changed", dto);
        return dto;
    }

    public async Task<object> RegisterAsync(string name, string email, string password)
        => await AuthFlowAsync("/api/register", new { name, email, password });

    public async Task<object> LoginAsync(string email, string password)
        => await AuthFlowAsync("/api/login", new { email, password });

    private async Task<object> AuthFlowAsync(string path, object body)
    {
        var node = await ReqAsync("POST", path, body, auth: false);
        var token = Str(node, "token");
        if (string.IsNullOrEmpty(token))
            throw new LauncherException("Сервер не выдал токен входа", node.ToJsonString());
        SaveToken(token);

        var user = node["user"] ?? throw new LauncherException("Сервер не вернул профиль", node.ToJsonString());
        _s.Settings.Set("authUser", user.ToJsonString(), raise: false);
        var dto = Dto(user);
        Log.Info($"Аккаунт: выполнен вход ({Str(user, "name")})");
        _s.Emit("auth.changed", dto);
        return dto;
    }

    public object Logout()
    {
        SaveToken(null);
        _s.Settings.Set("authUser", "", raise: false);
        Log.Info("Аккаунт: выход");
        _s.Emit("auth.changed", new { authed = false });
        return new { ok = true };
    }

    /* ================= вход через Ely.by ================= */

    /// <summary>Открывает страницу входа Ely.by в системном браузере
    /// (сервер выдаёт URL со своим state; возврат идёт по deep-link nulllauncher://auth?token=…).</summary>
    public async Task<object> ElyStartAsync()
    {
        var node = await ReqAsync("GET", "/api/ely/start?redirect=launcher", null, auth: false);
        var url = Str(node, "url");
        if (string.IsNullOrEmpty(url))
            throw new LauncherException(Str(node, "message") is { Length: > 0 } m ? m : "Сервис Ely.by недоступен");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new LauncherException("Не удалось открыть браузер", ex.Message, ex);
        }
        Log.Info("Аккаунт: открыт вход через Ely.by");
        return new { ok = true, opened = true };
    }

    /// <summary>Приём токена из deep-link: сохраняет его и подтягивает профиль (событие auth.changed).</summary>
    public async Task<object> ApplyExternalTokenAsync(string token)
    {
        token = (token ?? "").Trim();
        if (token.Length < 10) throw new LauncherException("Пустой токен входа Ely.by");
        SaveToken(token);
        Log.Info("Аккаунт: вход через Ely.by (токен из deep-link)");
        try
        {
            var dto = await MeCoreAsync(emit: true);
            _s.Emit("auth.elyDone", dto);
            return dto;
        }
        catch (Exception ex)
        {
            // сеть может подвести сразу после входа — токен уже сохранён, профиль подтянется при следующем запросе
            Log.Warn("Ely.by: профиль не загрузился сразу: " + ex.Message);
            var cached = _s.Settings.Get("authUser", "");
            if (!string.IsNullOrEmpty(cached))
            {
                try
                {
                    var off = Dto(JsonNode.Parse(cached)!);
                    _s.Emit("auth.elyDone", off);
                    return off;
                }
                catch { /* битый кэш */ }
            }
            _s.Emit("auth.changed", new { authed = true });
            _s.Emit("auth.elyDone", new { authed = true });
            return new { authed = true };
        }
    }

    public async Task<object> SetBioAsync(string bio)
    {
        var node = await ReqAsync("PATCH", "/api/me", new { bio });
        _s.Settings.Set("authUser", node.ToJsonString(), raise: false);
        var dto = Dto(node);
        _s.Emit("auth.changed", dto);
        return dto;
    }

    /* ================= NullCoin ================= */

    public async Task<object> DailyAsync()
    {
        var node = await ReqAsync("POST", "/api/coins/daily", new { });
        var claimed = Ln(node["claimed"]);
        var coins = Ln(node["coins"]);

        var cached = _s.Settings.Get("authUser", "");
        JsonNode? user = null;
        try { user = JsonNode.Parse(cached); } catch { /* кэша нет */ }
        if (user is null)
            return new { claimed, coins };

        user["coins"] = coins;
        user["dailyAvailable"] = false;
        _s.Settings.Set("authUser", user.ToJsonString(), raise: false);
        var dto = Dto(user);
        _s.Emit("auth.changed", dto);
        return new { claimed, coins, user = dto };
    }

    /* ================= часы игры ================= */

    /// <summary>Тотальное локальное время игры → дельта на сервер (не больше суток за вызов).</summary>
    public async Task<object> SyncPlaytimeAsync(long totalMs)
    {
        var synced = _s.Settings.Get("playtimeSyncedMs", 0L);
        var delta = Math.Max(0, totalMs - synced);
        if (delta == 0) return new { pushed = false, syncedMs = synced };

        var push = Math.Min(delta, 86_400_000L);
        var node = await ReqAsync("POST", "/api/me/playtime", new { addMs = push });
        _s.Settings.Set("playtimeSyncedMs", synced + push, raise: false);
        Log.Debug($"Аккаунт: синхронизировано {push} мс часов игры");
        return new { pushed = true, addMs = push, playtimeMs = Ln(node["playtimeMs"]) };
    }

    /* ================= друзья (в разработке) ================= */

    public async Task<object> FriendsAsync()
    {
        try { return await ReqAsync("GET", "/api/friends", null, auth: false); }
        catch (Exception ex)
        {
            return new { status = "unavailable", message = ex.Message, friends = Array.Empty<object>() };
        }
    }

    /* ================= ежедневная награда ================= */

    /// <summary>Состояние серии: день, награда, получен ли бонус сегодня.</summary>
    public async Task<object> DailyStateAsync()
        => await ReqAsync("GET", "/api/coins/daily/state", null);

    /* ================= обложка главной (меняется на сервере) ================= */

    /// <summary>URL обложки с сервера; кэш в kv — без сети берём прошлое значение.</summary>
    public async Task<object> CoverAsync()
    {
        try
        {
            var node = await ReqAsync("GET", "/api/cover", null, auth: false);
            var url = Str(node, "url");
            _s.Settings.Set("coverUrl", url, raise: false);
            return new { url };
        }
        catch
        {
            return new { url = _s.Settings.Get("coverUrl", "") };
        }
    }

    /* ================= DTO ================= */

    public static long Ln(JsonNode? n)
    {
        if (n is null) return 0;
        try { return n.GetValue<long>(); }
        catch { try { return (long)n.GetValue<double>(); } catch { return 0; } }
    }

    public static string Str(JsonNode? n, string k)
    {
        try { return n?[k]?.GetValue<string>() ?? ""; }
        catch { return ""; }
    }

    public static bool Bn(JsonNode? n, string k)
    {
        try { return n?[k]?.GetValue<bool>() ?? false; }
        catch { return false; }
    }

    private static object Dto(JsonNode n, bool offline = false) => new
    {
        authed = true,
        id = Ln(n["id"]),
        name = Str(n, "name"),
        email = Str(n, "email"),
        coins = Ln(n["coins"]),
        playtimeMs = Ln(n["playtimeMs"]),
        bio = Str(n, "bio"),
        dailyAvailable = Bn(n, "dailyAvailable"),
        offline,
    };
}