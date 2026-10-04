using System.Text.Json.Nodes;
using NullLauncher.Core;

namespace NullLauncher.Groups;

public static class GroupIpc
{
    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("groups.list", (_, _) => Task.FromResult<object?>(s.Groups.List()));
        r.Register("groups.create", (p, _) => Task.FromResult<object?>(
            s.Groups.Create(p?.Str("name") ?? "", p?.Str("color"), p?.Str("icon"))));
        r.Register("groups.update", (p, _) => Task.FromResult<object?>(
            s.Groups.Update(p?.Str("id") ?? "", p)));
        r.Register("groups.delete", (p, _) =>
        {
            s.Groups.Delete(p?.Str("id") ?? "");
            return Task.FromResult<object?>(new { ok = true });
        });
        r.Register("groups.addInstance", (p, _) =>
        {
            s.Groups.AddInstance(p?.Str("groupId") ?? "", p?.Str("instanceId") ?? "");
            return Task.FromResult<object?>(new { ok = true });
        });
        r.Register("groups.removeInstance", (p, _) =>
        {
            s.Groups.RemoveInstance(p?.Str("groupId") ?? "", p?.Str("instanceId") ?? "");
            return Task.FromResult<object?>(new { ok = true });
        });
        r.Register("groups.reorder", (p, _) =>
        {
            var ids = p?.StrList("orderedIds") ?? new List<string>();
            if (ids.Count > 0) s.Groups.Reorder(ids);
            return Task.FromResult<object?>(new { ok = true });
        });
    }
}
