using System.Text.Json.Nodes;

namespace NullLauncher.Accounts;

public static class AccountsIpc
{
    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("accounts.list", (_, _) => Task.FromResult<object?>(s.Accounts.List()));

        r.Register("accounts.addMicrosoft", async (_, ct) =>
            await s.Accounts.StartMicrosoftAsync(ct).ConfigureAwait(false));

        r.Register("accounts.pollMicrosoft", async (p, ct) =>
        {
            var code = p?.Str("uuid") ?? p?.Str("deviceCode") ?? p?.Str("device_code")
                       ?? throw new LauncherException("Не передан код устройства");
            var res = await s.Accounts.PollMicrosoftAsync(code, ct).ConfigureAwait(false);
            if (res.Status == "done")
                s.Emit("accounts.changed", new { action = "added" });
            return new { status = res.Status, account = res.Account, message = res.Message };
        });

        r.Register("accounts.remove", (p, _) =>
        {
            var uuid = p?.Str("uuid") ?? throw new LauncherException("Не указан аккаунт");
            s.Accounts.Remove(uuid);
            s.Emit("accounts.changed", new { action = "removed" });
            return Task.FromResult<object?>(new { ok = true });
        });

        r.Register("accounts.setDefault", (p, _) =>
        {
            var uuid = p?.Str("uuid") ?? throw new LauncherException("Не указан аккаунт");
            s.Accounts.SetDefault(uuid);
            s.Emit("accounts.changed", new { action = "setDefault", uuid });
            return Task.FromResult<object?>(new { ok = true });
        });

        // офлайн-вход (гость)
        r.Register("accounts.addOffline", (p, _) =>
        {
            var name = p?.Str("name") ?? "Player";
            var acc = s.Accounts.AddOffline(name);
            s.Emit("accounts.changed", new { action = "added" });
            return Task.FromResult<object?>(acc);
        });
        r.Register("accounts.addGuest", (p, _) =>
        {
            var name = p?.Str("name") ?? "Player";
            var acc = s.Accounts.AddOffline(name);
            s.Emit("accounts.changed", new { action = "added" });
            return Task.FromResult<object?>(acc);
        });

        // смена скина лицензионного аккаунта (PNG 64×64 / 64×32, variant classic|slim)
        r.Register("accounts.changeSkin", async (p, ct) =>
        {
            var acc = await s.Accounts.ChangeSkinAsync(p?.Str("path"), p?.Str("variant") ?? "classic", ct)
                .ConfigureAwait(false);
            s.Emit("accounts.changed", new { action = "skin", uuid = acc.Uuid });
            return (object?)acc;
        });
    }
}
