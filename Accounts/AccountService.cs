using System.IO;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NullLauncher.Accounts;

/// <summary>Сессия для запуска игры: имя, UUID, токен доступа Minecraft Services.</summary>
public sealed class AccountSession
{
    public string Name { get; set; } = "Player";
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string Type { get; set; } = "microsoft"; // microsoft | offline
    public string? Xuid { get; set; }
    public bool GameOwned { get; set; } = true;
    public string? SkinUrl { get; set; }
    /// <summary>Срок жизни токена Minecraft Services (UTC).</summary>
    public DateTime? ExpiresAtUtc { get; set; }
}

public sealed class AccountDto
{
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "microsoft";
    public bool Default { get; set; }
    public string? SkinUrl { get; set; }
    public string? ExpiresAt { get; set; }
    public bool Expired { get; set; }
    public string CreatedAt { get; set; } = "";
}

/// <summary>
/// Аккаунты: Microsoft (device code, честный OAuth через login.live.com + Xbox Live + Minecraft Services)
/// и офлайн «гость». Токены шифруются DPAPI Windows (CurrentUser) — на диске лежит только шифротекст.
/// </summary>
public sealed class AccountService
{
    // Публичный MSA client id, который Prism Launcher регистрирует в Microsoft Identity Platform.
    // Можно переопределить настройкой msClientId (advanced).
    private const string DefaultClientId = "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb";
    private const string DeviceScope = "XboxLive.Signin XboxLive.offline_access";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NullLauncher.msa.v1");

    private readonly Core.AppServices _s;
    private readonly Dictionary<string, DeviceFlow> _flows = new();
    private readonly object _sync = new();

    public AccountService(Core.AppServices s) => _s = s;

    private string ClientId => _s.Settings.Get("msClientId", DefaultClientId);

    /* ------------------------------------------------------------ список */

    public List<AccountDto> List()
    {
        var rows = Database.DatabaseService.Query<AccountRow>(
            "SELECT uuid, name, type, is_default AS IsDefault, skin_url AS SkinUrl, " +
            "expires_at AS ExpiresAt, created_at AS CreatedAt FROM accounts ORDER BY is_default DESC, created_at");
        return rows.Select(r => new AccountDto
        {
            Uuid = r.Uuid,
            Name = r.Name,
            Type = r.Type,
            Default = r.IsDefault != 0,
            SkinUrl = r.SkinUrl,
            ExpiresAt = r.ExpiresAt,
            Expired = r.ExpiresAt is not null && DateTime.TryParse(r.ExpiresAt, out var e) &&
                      e.ToUniversalTime() < DateTime.UtcNow,
            CreatedAt = r.CreatedAt,
        }).ToList();
    }

    private sealed class AccountRow
    {
        public string Uuid { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "microsoft";
        public int IsDefault { get; set; }
        public string? SkinUrl { get; set; }
        public string? ExpiresAt { get; set; }
        public string CreatedAt { get; set; } = "";
    }

    /* ------------------------------------------------------------ офлайн / гость */

    public AccountDto AddOffline(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 3 or > 16)
            throw new LauncherException("Некорректный никнейм", "Имя должно быть от 3 до 16 символов (латиница, цифры, _)");
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$"))
            throw new LauncherException("Некорректный никнейм", "Допустимы только латинские буквы, цифры и знак подчёркивания");

        var uuid = OfflineUuid(name);
        Database.DatabaseService.Execute("""
            INSERT INTO accounts (uuid, name, type, is_default, created_at)
            VALUES (@Uuid, @Name, 'offline', 0, @CreatedAt)
            ON CONFLICT(uuid) DO UPDATE SET name=excluded.name, type='offline'
            """, new { Uuid = uuid, Name = name, CreatedAt = DateTime.UtcNow.ToString("o") });

        EnsureDefault();
        Log.Info($"Добавлен офлайн-аккаунт {name} ({uuid})");
        return List().First(a => a.Uuid == uuid);
    }

    public static string OfflineUuid(string name)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30); // версия 3
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80); // RFC 4122 variant
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    /* ------------------------------------------------------------ Microsoft device code */

    public sealed class DeviceFlow
    {
        public string DeviceCode { get; set; } = "";
        public string UserCode { get; set; } = "";
        public string VerificationUrl { get; set; } = "";
        public int ExpiresInSeconds { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public int Interval { get; set; } = 5;
    }

    /// <summary>Начинает device-code flow и возвращает данные для показа пользователю.</summary>
    public async Task<object> StartMicrosoftAsync(CancellationToken ct)
    {
        using var http = HttpFactory.Get(_s);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://login.live.com/oauth20_connect.srf");
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = DeviceScope,
            ["response_type"] = "device_code",
        };
        req.Content = new FormUrlEncodedContent(form);

        JsonObject json;
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try { json = JsonNode.Parse(body)!.AsObject(); }
        catch (Exception ex) { throw new LauncherException("Microsoft не ответил на запрос авторизации", body[..Math.Min(300, body.Length)], ex); }
        if (json["error"] is not null)
            throw new LauncherException("Не удалось начать вход через Microsoft",
                $"{json["error"]}: {json["error_description"]}");

        var flow = new DeviceFlow
        {
            DeviceCode = json["device_code"]?.GetValue<string>() ?? "",
            UserCode = json["user_code"]?.GetValue<string>() ?? "",
            VerificationUrl = json["verification_uri"]?.GetValue<string>() ?? "https://www.microsoft.com/link",
            ExpiresInSeconds = json["expires_in"]?.GetValue<int>() ?? 900,
            Interval = json["interval"]?.GetValue<int>() ?? 5,
        };
        if (string.IsNullOrEmpty(flow.DeviceCode))
            throw new LauncherException("Microsoft не выдал код устройства", body[..Math.Min(300, body.Length)]);

        lock (_sync)
        {
            foreach (var kv in _flows.Where(f => (DateTime.UtcNow - f.Value.CreatedAt).TotalMinutes > 30).ToList())
                _flows.Remove(kv.Key);
            _flows[flow.DeviceCode] = flow;
        }
        return new
        {
            deviceCode = flow.DeviceCode,
            userCode = flow.UserCode,
            verificationUrl = flow.VerificationUrl,
            expiresInSeconds = flow.ExpiresInSeconds,
            interval = flow.Interval,
            message = $"Откройте {flow.VerificationUrl} и введите код {flow.UserCode}",
        };
    }

    public sealed class PollResult
    {
        public string Status { get; set; } = "pending"; // pending | done | expired | error
        public AccountDto? Account { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>Один опрос device-code flow. Статусы: pending / done / expired / error.</summary>
    public async Task<PollResult> PollMicrosoftAsync(string deviceCode, CancellationToken ct)
    {
        DeviceFlow? flow;
        lock (_sync) _flows.TryGetValue(deviceCode, out flow);

        if (flow is null)
            return new PollResult { Status = "error", Message = "Сессия входа не найдена, начните заново" };
        if ((DateTime.UtcNow - flow.CreatedAt).TotalSeconds > flow.ExpiresInSeconds)
            return new PollResult { Status = "expired", Message = "Код устарел, запросите новый" };

        using var http = HttpFactory.Get(_s);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://login.live.com/oauth20_connect.srf");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = ClientId,
            ["device_code"] = deviceCode,
        });

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonObject json;
        try { json = JsonNode.Parse(body)!.AsObject(); }
        catch { return new PollResult { Status = "error", Message = "Непонятный ответ Microsoft" }; }

        if (json["error"] is JsonValue errVal)
        {
            var code = errVal.GetValue<string>();
            return code switch
            {
                "authorization_pending" => new PollResult { Status = "pending" },
                "slow_down" => new PollResult { Status = "pending" },
                "expired_token" => new PollResult { Status = "expired", Message = "Код устарел, запросите новый" },
                "authorization_declined" => new PollResult { Status = "expired", Message = "Вы отклонили запрос входа" },
                _ => new PollResult
                {
                    Status = "error",
                    Message = $"{code}: {json["error_description"]?.GetValue<string>()}",
                },
            };
        }

        var msaToken = json["access_token"]?.GetValue<string>();
        var refreshToken = json["refresh_token"]?.GetValue<string>();
        if (msaToken is null)
            return new PollResult { Status = "error", Message = "Не удалось получить токен Microsoft" };

        // полный обмен: XBL → XSTS → Minecraft
        var session = await ExchangeToMinecraftAsync(http, msaToken, refreshToken, ct).ConfigureAwait(false);
        var account = Upsert(session.session, session.refreshToken);
        lock (_sync) _flows.Remove(deviceCode);

        return new PollResult { Status = "done", Account = account, Message = $"Вы вошли как {account.Name}" };
    }

    private async Task<(AccountSession session, string msaToken, string refreshToken)> ExchangeToMinecraftAsync(
        HttpClient http, string msaToken, string? refreshToken, CancellationToken ct)
    {
        // 1. Xbox Live token
        var xbl = await PostJsonAsync(http, "https://user.auth.xboxlive.com/user/authenticate", new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = "d=" + msaToken,
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT",
        }, ct).ConfigureAwait(false);

        var xblToken = xbl["Token"]?.GetValue<string>()
                       ?? throw new LauncherException("Xbox Live не выдал токен", xbl.ToJsonString());
        var uhs = xbl["DisplayClaims"]?["xui"]?[0]?["uhs"]?.GetValue<string>()
                  ?? throw new LauncherException("Xbox Live не вернул идентификатор пользователя", null);

        // 2. XSTS
        var xsts = await PostJsonAsync(http, "https://xsts.auth.xboxlive.com/xsts/authorize", new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["SandboxId"] = "RETAIL",
                ["UserTokens"] = new JsonArray(JsonValue.Create(xblToken)),
            },
            ["RelyingParty"] = "rp://api.minecraftservices.com/",
            ["TokenType"] = "JWT",
        }, ct).ConfigureAwait(false);

        if (xsts["XErr"] is JsonValue xerr)
        {
            var code = xerr.GetValue<long>();
            throw new LauncherException("Microsoft отклонил запрос XSTS", code switch
            {
                2148916233 => "Аккаунт Microsoft не привязан к Xbox Live. Завершите настройу на xbox.com.",
                2148916235 => "Аккаунт из региона, не поддерживаемого Xbox Live.",
                2148916236 => "Возрастное ограничение аккаунта не позволяет играть.",
                2148916237 => "Требуется подтверждение возраста на xbox.com.",
                2148916238 => "Детский аккаунт Xbox — требуется вход взрослого.",
                _ => $"Код XErr {code}",
            });
        }
        var xstsToken = xsts["Token"]?.GetValue<string>()
                        ?? throw new LauncherException("XSTS не выдал токен", xsts.ToJsonString());

        // 3. Minecraft Services token
        var mc = await PostJsonAsync(http, "https://api.minecraftservices.com/authentication/login_with_xbox",
            new JsonObject { ["identityToken"] = $"XBL3.0 x={uhs};{xstsToken}" }, ct).ConfigureAwait(false);
        var mcToken = mc["access_token"]?.GetValue<string>()
                      ?? throw new LauncherException("Minecraft Services не выдал токен", mc.ToJsonString());
        var expiresIn = mc["expires_in"]?.GetValue<int>() ?? 86400;

        using var mcHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        mcHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);

        // 4. Проверка владения игрой
        var owned = false;
        try
        {
            using var ent = await mcHttp.GetAsync("https://api.minecraftservices.com/entitlements/mcstore", ct)
                .ConfigureAwait(false);
            if (ent.IsSuccessStatusCode)
            {
                var entJson = JsonNode.Parse(await ent.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                owned = entJson?["items"]?.AsArray()
                            .Any(i => i?["name"]?.GetValue<string>() is "minecraft" or "game_flight" or "game_minecraft")
                        ?? false;
            }
        }
        catch (Exception ex) { Log.Warn($"Entitlements: {ex.Message}"); }
        if (!owned)
            throw new LauncherException("У аккаунта нет доступа к Minecraft",
                "Проверьте, что игра куплена на этом аккаунте (minecraft.net/укажите другую учётную запись).");

        // 5. Профиль
        var name = "Player";
        string? uuid = null, skin = null;
        try
        {
            using var prof = await mcHttp.GetAsync("https://api.minecraftservices.com/minecraft/profile", ct)
                .ConfigureAwait(false);
            if (prof.IsSuccessStatusCode)
            {
                var pj = JsonNode.Parse(await prof.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                uuid = pj?["id"]?.GetValue<string>();
                name = pj?["name"]?.GetValue<string>() ?? name;
                skin = pj?["skins"]?.AsArray()
                    .FirstOrDefault(s => s?["variant"]?.GetValue<string>() == "CLASSIC")?["url"]?.GetValue<string>()
                    ?? pj?["skins"]?.AsArray().FirstOrDefault()?["url"]?.GetValue<string>();
            }
            else
                throw new LauncherException("Не удалось получить профиль Minecraft",
                    $"Статус {(int)prof.StatusCode}. Возможно, у аккаунта нет купленной игры.");
        }
        catch (LauncherException) { throw; }
        catch (Exception ex) { throw new LauncherException("Не удалось получить профиль Minecraft", ex.Message, ex); }

        var xuid = xbl["DisplayClaims"]?["xui"]?[0]?["xuid"]?.GetValue<string>();

        return (new AccountSession
        {
            Name = name,
            Uuid = uuid ?? "",
            AccessToken = mcToken,
            Type = "microsoft",
            Xuid = xuid,
            GameOwned = true,
            SkinUrl = skin,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn),
        }, msaToken, refreshToken ?? "");
    }

    private async Task<JsonObject> PostJsonAsync(HttpClient http, string url, JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonObject json;
        try { json = JsonNode.Parse(text)!.AsObject(); }
        catch { throw new LauncherException("Сервис вернул неожиданный ответ", $"{url}\n{text[..Math.Min(300, text.Length)]}"); }
        if (!resp.IsSuccessStatusCode && json["XErr"] is null)
        {
            var err = json["error"]?.GetValue<string>() ?? ((int)resp.StatusCode).ToString();
            var desc = json["error_description"]?.GetValue<string>() ?? json["Message"]?.GetValue<string>() ?? text;
            throw new LauncherException($"Сервис авторизации ответил с ошибкой", $"{url}: {err} — {desc}");
        }
        return json;
    }

    /* ------------------------------------------------------------ хранилище токенов */

    private AccountDto Upsert(AccountSession session, string refreshToken)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            // токен обновлён, refresh не пришёл — оставляем прежний
            Database.DatabaseService.Execute("""
                INSERT INTO accounts (uuid, name, type, is_default, skin_url, expires_at, token_enc, created_at)
                VALUES (@Uuid, @Name, 'microsoft', 0, @SkinUrl, @ExpiresAt, @Token, @CreatedAt)
                ON CONFLICT(uuid) DO UPDATE SET name=excluded.name, skin_url=excluded.skin_url,
                  expires_at=excluded.expires_at, token_enc=excluded.token_enc
                """, new
            {
                session.Uuid, session.Name, session.SkinUrl,
                ExpiresAt = session.ExpiresAtUtc?.ToString("o"),
                Token = Protect(session.AccessToken),
                CreatedAt = DateTime.UtcNow.ToString("o"),
            });
        }
        else
        {
            Database.DatabaseService.Execute("""
                INSERT INTO accounts (uuid, name, type, is_default, skin_url, expires_at, token_enc, refresh_enc, created_at)
                VALUES (@Uuid, @Name, 'microsoft', 0, @SkinUrl, @ExpiresAt, @Token, @Refresh, @CreatedAt)
                ON CONFLICT(uuid) DO UPDATE SET name=excluded.name, skin_url=excluded.skin_url,
                  expires_at=excluded.expires_at, token_enc=excluded.token_enc, refresh_enc=excluded.refresh_enc
                """, new
            {
                session.Uuid, session.Name, session.SkinUrl,
                ExpiresAt = session.ExpiresAtUtc?.ToString("o"),
                Token = Protect(session.AccessToken),
                Refresh = Protect(refreshToken),
                CreatedAt = DateTime.UtcNow.ToString("o"),
            });
        }
        if (!string.IsNullOrEmpty(session.Xuid))
            Database.DatabaseService.Execute("INSERT OR REPLACE INTO kv (key, value) VALUES (@K, @V)",
                new { K = $"account:{session.Uuid}:xuid", V = session.Xuid });
        EnsureDefault();
        return List().First(a => a.Uuid == session.Uuid);
    }

    public void Remove(string uuid)
    {
        var count = Database.DatabaseService.Scalar<int>("SELECT COUNT(*) FROM accounts");
        Database.DatabaseService.Execute("DELETE FROM accounts WHERE uuid=@Uuid", new { Uuid = uuid });
        Database.DatabaseService.Execute("DELETE FROM kv WHERE key=@K", new { K = $"account:{uuid}:xuid" });
        if (count > 1) EnsureDefault();
        Log.Info($"Аккаунт {uuid} удалён");
    }

    public void SetDefault(string uuid)
    {
        Database.DatabaseService.Execute("UPDATE accounts SET is_default = CASE WHEN uuid=@Uuid THEN 1 ELSE 0 END",
            new { Uuid = uuid });
    }

    private void EnsureDefault()
    {
        var hasDefault = Database.DatabaseService.Scalar<int>("SELECT COUNT(*) FROM accounts WHERE is_default=1");
        if (hasDefault == 0)
        {
            var first = Database.DatabaseService.Scalar<string>("SELECT uuid FROM accounts ORDER BY created_at LIMIT 1");
            if (first is not null) SetDefault(first);
        }
    }

    private static byte[] Protect(string plain)
        => ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);

    private static string? Unprotect(byte[]? data)
    {
        if (data is null || data.Length == 0) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser)); }
        catch { return null; } // другой пользователь Windows или повреждённые данные
    }

    /* ------------------------------------------------------------ использование при запуске */

    /// <summary>Готовая сессия для java-аргументов: при необходимости обновляет токен.</summary>
    public async Task<AccountSession> ResolveForLaunchAsync(string? uuid, CancellationToken ct)
    {
        var row = Database.DatabaseService.QueryFirstOrDefault<AccountFullRow>(
            "SELECT uuid, name, type, expires_at AS ExpiresAt, token_enc AS TokenEnc, refresh_enc AS RefreshEnc FROM accounts WHERE uuid=@U",
            new { U = uuid ?? "" })
            ?? Database.DatabaseService.QueryFirstOrDefault<AccountFullRow>(
                "SELECT uuid, name, type, expires_at AS ExpiresAt, token_enc AS TokenEnc, refresh_enc AS RefreshEnc FROM accounts WHERE is_default=1");

        if (row is null)
            throw new LauncherException("Аккаунт не выбран",
                "Войдите через Microsoft или добавьте офлайн-аккаунт (Настройки → Аккаунты).");

        if (row.Type == "offline")
            return new AccountSession { Name = row.Name, Uuid = row.Uuid, AccessToken = "0", Type = "offline" };

        var mcToken = Unprotect(row.TokenEnc);
        var msaToken = Unprotect(row.RefreshEnc);
        var expires = row.ExpiresAt is not null && DateTime.TryParse(row.ExpiresAt, out var e)
            ? e.ToUniversalTime() : DateTime.MinValue;

        if (mcToken is not null && expires > DateTime.UtcNow.AddMinutes(5))
            return new AccountSession { Name = row.Name, Uuid = row.Uuid, AccessToken = mcToken, Type = "microsoft" };

        if (msaToken is null)
            throw new LauncherException("Сессия Microsoft истекла", "Войдите заново: Настройки → Аккаунты → Войти через Microsoft");

        // обновляем MSA-токен и повторяем обмен
        using var http = HttpFactory.Get(_s);
        var refreshed = await RefreshMsaAsync(http, msaToken, ct).ConfigureAwait(false);
        var session = await ExchangeToMinecraftAsync(http, refreshed.Access, refreshed.Refresh, ct).ConfigureAwait(false);
        Upsert(session.session, session.refreshToken);
        return session.session;
    }

    private sealed class AccountFullRow
    {
        public string Uuid { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "microsoft";
        public string? ExpiresAt { get; set; }
        public byte[]? TokenEnc { get; set; }
        public byte[]? RefreshEnc { get; set; }
    }

    private sealed record Refreshed(string Access, string Refresh);

    private async Task<Refreshed> RefreshMsaAsync(HttpClient http, string refreshToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://login.live.com/oauth20_connect.srf");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = DeviceScope,
        });
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var json = JsonNode.Parse(body)?.AsObject();
        var access = json?["access_token"]?.GetValue<string>();
        if (access is null)
            throw new LauncherException("Не удалось обновить сессию Microsoft",
                "Повторите вход: Настройки → Аккаунты → Войти через Microsoft");
        return new Refreshed(access, json?["refresh_token"]?.GetValue<string>() ?? refreshToken);
    }
}
