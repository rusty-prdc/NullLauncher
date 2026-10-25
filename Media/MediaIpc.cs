using NullLauncher.Core;

namespace NullLauncher.Media;

/// <summary>IPC плашки «сейчас играет»: состояние системной медиа-сессии (SMTC) и кнопки управления.</summary>
public static class MediaIpc
{
    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("media.state", async (_, _) => await s.Media.StateAsync());
        r.Register("media.control", async (p, _) =>
        {
            var action = p.Str("action");
            double pos = 0;
            var n = p?["positionMs"];
            if (n is not null)
            {
                try { pos = n.GetValue<double>(); }
                catch { try { pos = n.GetValue<long>(); } catch { pos = 0; } }
            }
            return await s.Media.ControlAsync(action, pos);
        });
    }
}
