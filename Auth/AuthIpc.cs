using System.Text.Json.Nodes;
using NullLauncher.Core;

namespace NullLauncher.Auth;

/// <summary>IPC аккаунта NullLauncher: вход/регистрация, профиль, NullCoin, часы, друзья.</summary>
public static class AuthIpc
{
    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("auth.state", async (_, _) => await s.Auth.StateAsync());
        r.Register("auth.register", async (p, _) =>
            await s.Auth.RegisterAsync(p.Str("name") ?? "", p.Str("email") ?? "", p.Str("password") ?? ""));
        r.Register("auth.login", async (p, _) =>
            await s.Auth.LoginAsync(p.Str("email") ?? "", p.Str("password") ?? ""));
        r.Register("auth.logout", async (_, _) => s.Auth.Logout());
        r.Register("auth.me", async (_, _) => await s.Auth.MeAsync());
        r.Register("auth.daily", async (_, _) => await s.Auth.DailyAsync());
        r.Register("auth.setBio", async (p, _) => await s.Auth.SetBioAsync(p.Str("bio") ?? ""));
        r.Register("auth.syncPlaytime", async (p, _) => await s.Auth.SyncPlaytimeAsync(Num(p, "totalMs")));
        r.Register("auth.friends", async (_, _) => await s.Auth.FriendsAsync());
        r.Register("auth.dailyState", async (_, _) => await s.Auth.DailyStateAsync());
        r.Register("auth.cover", async (_, _) => await s.Auth.CoverAsync());
    }

    private static long Num(JsonNode? p, string k)
    {
        var n = p?[k];
        if (n is null) return 0;
        try { return n.GetValue<long>(); }
        catch { try { return (long)n.GetValue<double>(); } catch { return 0; } }
    }
}