using System.IO;
using NullLauncher.Core;
using System.IO;

namespace NullLauncher.Groups;

public sealed class GroupRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#3ecf8e";
    public string Icon { get; set; } = "cube";
    public int SortOrder { get; set; }
    public List<string> InstanceIds { get; set; } = new();
}

public sealed class GroupService
{
    public GroupService(Core.AppPaths paths) { _ = paths; }

    public List<GroupRecord> List()
    {
        var groups = Database.DatabaseService.Query<GroupRecord>(
            "SELECT id, name, color, icon, sort_order AS SortOrder FROM groups ORDER BY sort_order, name");
        var links = Database.DatabaseService.Query<(string group_id, string instance_id)>(
            "SELECT group_id, instance_id FROM group_instances ORDER BY sort_order");
        foreach (var g in groups)
            g.InstanceIds = links.Where(l => l.group_id == g.Id).Select(l => l.instance_id).ToList();
        return groups;
    }

    public GroupRecord Create(string name, string? color, string? icon)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new LauncherException("Укажите название группы");
        var id = Guid.NewGuid().ToString("N")[..8];
        var order = Database.DatabaseService.Scalar<int?>("SELECT COALESCE(MAX(sort_order),0)+1 FROM groups") ?? 1;
        Database.DatabaseService.Execute(
            "INSERT INTO groups (id, name, color, icon, sort_order) VALUES (@Id, @Name, @Color, @Icon, @Order)",
            new { Id = id, Name = name.Trim(), Color = color ?? "#3ecf8e", Icon = icon ?? "cube", Order = order });
        Log.Info($"Создана группа '{name}'");
        return List().First(g => g.Id == id);
    }

    public GroupRecord Update(string id, System.Text.Json.Nodes.JsonNode? patch)
    {
        var g = List().FirstOrDefault(x => x.Id == id)
                ?? throw new LauncherException("Группа не найдена", id);
        var name = patch?.Str("name") ?? g.Name;
        var color = patch?.Str("color") ?? g.Color;
        var icon = patch?.Str("icon") ?? g.Icon;
        var order = patch?["sortOrder"] is null ? g.SortOrder : patch!.Int("sortOrder", g.SortOrder);
        Database.DatabaseService.Execute(
            "UPDATE groups SET name=@Name, color=@Color, icon=@Icon, sort_order=@Order WHERE id=@Id",
            new { Name = name.Trim(), Color = color, Icon = icon, Order = order, Id = id });
        return List().First(x => x.Id == id);
    }

    public void Delete(string id)
    {
        Database.DatabaseService.Execute("DELETE FROM group_instances WHERE group_id=@id", new { id });
        Database.DatabaseService.Execute("DELETE FROM groups WHERE id=@id", new { id });
        Log.Info($"Группа {id} удалена");
    }

    public void AddInstance(string groupId, string instanceId)
    {
        Database.DatabaseService.Execute("""
            INSERT OR IGNORE INTO group_instances (group_id, instance_id, sort_order)
            VALUES (@g, @i, (SELECT COALESCE(MAX(sort_order),0)+1 FROM group_instances WHERE group_id=@g))
            """, new { g = groupId, i = instanceId });
    }

    public void RemoveInstance(string groupId, string instanceId)
        => Database.DatabaseService.Execute("DELETE FROM group_instances WHERE group_id=@g AND instance_id=@i",
            new { g = groupId, i = instanceId });

    public void Reorder(List<string> orderedIds)
    {
        for (var i = 0; i < orderedIds.Count; i++)
            Database.DatabaseService.Execute("UPDATE groups SET sort_order=@o WHERE id=@id",
                new { o = i, id = orderedIds[i] });
    }
}
