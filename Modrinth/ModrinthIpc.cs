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

    public static void Register(IpcRouter r, AppServices s)
    {
        var svc = _service = new ModrinthService(s);

        // { query?, type?, loaders?, gameVersions?, categories?, license?, sort?, limit?, offset? }
        r.Register("modrinth.search", (p, ct) => Guard("Не удалось найти в Modrinth",
            () => svc.SearchAsync(p, ct)));

        // { id } — slug или внутренний id проекта
        r.Register("modrinth.project", (p, ct) => Guard("Не удалось загрузить проект",
            () => svc.ProjectAsync(Req(p, "id"), ct)));

        // { id, loaders?, gameVersions? }
        r.Register("modrinth.versions", (p, ct) => Guard("Не удалось загрузить версии",
            () => svc.VersionsAsync(Req(p, "id"),
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

    private static async Task<object?> Guard(string title, Func<Task<object>> body)
    {
        try { return await body().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (LauncherException) { throw; }
        catch (Exception ex) { throw new LauncherException(title, ex.Message, ex); }
    }
}
