using System.Text.Json.Nodes;
using NullLauncher.Core;

namespace NullLauncher.Java;

public static class JavaIpc
{
    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("java.list", (_, _) => Task.FromResult<object?>(s.Java.Detect()));
        r.Register("java.detect", (_, _) => Task.FromResult<object?>(s.Java.Detect(force: true)));
        r.Register("java.validate", (p, _) =>
        {
            var path = p?.Str("path") ?? "";
            var probe = s.Java.Validate(path);
            if (probe is null)
                return Task.FromResult<object?>(new { valid = false, version = "", major = 0, arch = "", path });
            return Task.FromResult<object?>(new
            {
                valid = true, version = probe.Version, major = probe.Major, arch = probe.Arch, path = probe.Path,
                vendor = probe.Vendor,
            });
        });
        r.Register("java.recommend", (p, _) =>
        {
            var inst = s.Instances.Require(p?.Str("instanceId") ?? "");
            // требуемая Java берётся из version json (для 26.x это 25, а не 21)
            var required = s.Versions.RequiredJavaFor(inst.McVersion);
            var rec = s.Java.Recommend(inst.McVersion, inst.JavaMode, inst.JavaMajor, inst.JavaPath, required);
            return Task.FromResult<object?>(new
            {
                major = rec.Major,
                path = rec.Path,
                reason = rec.Reason,
                found = rec.Path is not null && rec.Major >= required,
                candidates = rec.Candidates,
                required,
            });
        });
        r.Register("java.install", async (p, ct) =>
        {
            var major = p?.Int("major", 0) ?? 0;
            if (major == 0) throw new LauncherException("Укажите версию Java", "major");
            try
            {
                var progress = new Progress<double>(v => s.Emit("java.install.progress", new { major, progress = v }));
                var result = await s.Java.InstallAsync(major, progress, ct).ConfigureAwait(false);
                s.Emit("java.install.progress", new { major, progress = 1.0, done = true });
                s.NotifyUser("success", $"Java {result.Major} установлена", result.Path);
                return new { ok = true, path = result.Path, version = result.Version, major = result.Major };
            }
            catch (Exception ex)
            {
                var le = LauncherException.Wrap(ex, "Не удалось установить Java");
                s.NotifyUser("error", "Установка Java не удалась", le.UserMessage);
                throw le;
            }
        });
        r.Register("java.remove", (p, _) =>
        {
            s.Java.Remove(p?.Str("id") ?? "");
            return Task.FromResult<object?>(new { ok = true });
        });
        r.Register("java.setInstance", (p, _) =>
        {
            var id = p?.Str("instanceId") ?? "";
            var mode = p?.Str("mode") ?? "auto";
            var patch = new JsonObject { ["javaMode"] = mode };
            if (mode == "major") patch["javaMajor"] = p?.Int("value", 21) ?? 21;
            if (mode == "path") patch["javaPath"] = p?.Str("value");
            s.Instances.Update(id, patch);
            return Task.FromResult<object?>(new { ok = true });
        });
    }
}
