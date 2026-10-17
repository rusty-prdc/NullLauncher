using System.Text.Json.Nodes;
using NullLauncher.Core;

namespace NullLauncher.Modrinth;

/// <summary>
/// IPC-методы Modrinth: поиск, карточка проекта, версии, фильтры, установка файла,
/// проверка обновлений и сопоставление локальных файлов по хешу.
/// Здесь только разбор параметров и дружелюбные ошибки; вся работа (сеть, кэш, БД) —
/// в <see cref="ModrinthService"/>, который создаётся при регистрации и живёт в статике модуля.
/// </summary>
public static class ModrinthIpc
{
    private static ModrinthService? _service;
    private static CurseForge.CurseForgeService? _cf;

    public static void Register(IpcRouter r, AppServices s)
    {
        var svc = _service = new ModrinthService(s);
        var cf = _cf = new CurseForge.CurseForgeService(s);

        // { query?, type?, loaders?, gameVersions?, categories?, license?, sort?, limit?, offset? }
        // раздел «Модпаки» (type=modpack) обслуживает CurseForge, остальное — Modrinth
        r.Register("modrinth.search", (p, ct) => Guard("Не удалось найти в каталоге",
            () => IsModpackType(p) ? cf.SearchAsync(p, ct) : svc.SearchAsync(p, ct)));

        // { id } — slug или внутренний id проекта; «cf:NNN» — проект CurseForge
        r.Register("modrinth.project", (p, ct) => Guard("Не удалось загрузить проект",
            () => IsCfId(Req(p, "id")) ? cf.ProjectAsync(Req(p, "id"), ct) : svc.ProjectAsync(Req(p, "id"), ct)));

        // { id, loaders?, gameVersions? }
        r.Register("modrinth.versions", (p, ct) => Guard("Не удалось загрузить версии",
            () => IsCfId(Req(p, "id"))
                ? cf.VersionsAsync(Req(p, "id"),
                    p?.StrList("loaders") ?? new List<string>(),
                    p?.StrList("gameVersions") ?? new List<string>(), ct)
                : svc.VersionsAsync(Req(p, "id"),
                    p?.StrList("loaders") ?? new List<string>(),
                    p?.StrList("gameVersions") ?? new List<string>(), ct)));

        // без параметров
        r.Register("modrinth.tags", (_, ct) => Guard("Не удалось загрузить фильтры",
            () => svc.TagsAsync(ct)));

        // { instanceId, versionId, kind?, installDependencies? } → { installed, warnings, dependencies }
        r.Register("modrinth.install", (p, ct) => Guard("Не удалось установить файл",
            () => svc.InstallAsync(Req(p, "instanceId"), Req(p, "versionId"),
                p?.Str("kind") ?? "mods", ct,
                p?.Bool("installDependencies", true) ?? true)));

        // { instanceId } → [{ modId, filename, current, latest, latestVersionId, projectId, projectName }]
        r.Register("modrinth.checkUpdates", (p, ct) => Guard("Не удалось проверить обновления",
            () => svc.CheckUpdatesAsync(Req(p, "instanceId"), ct)));

        // { hashes:[…], algorithm? } → [{ hash, versionId, projectId, projectName, … }]
        r.Register("modrinth.projectByFile", (p, ct) => Guard("Не удалось сопоставить файлы",
            () => svc.ProjectByFileAsync(p, ct)));
    }

    private static string Req(JsonNode? p, string key)
        => p.Str(key) is { Length: > 0 } v ? v
           : throw new LauncherException("Не передан обязательный параметр", key);

    /// <summary>Раздел «Модпаки» каталога обслуживает CurseForge.</summary>
    private static bool IsModpackType(JsonNode? p)
        => string.Equals((p?.Str("type") ?? "").Trim(), "modpack", StringComparison.OrdinalIgnoreCase);

    /// <summary>id вида «cf:NNN» — проект CurseForge.</summary>
    private static bool IsCfId(string id)
        => id.StartsWith(CurseForge.CurseForgeService.IdPrefix, StringComparison.OrdinalIgnoreCase);

    private static async Task<object?> Guard(string title, Func<Task<object>> body)
    {
        try { return await body().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (LauncherException) { throw; }
        catch (Exception ex) { throw new LauncherException(title, ex.Message, ex); }
    }
}
