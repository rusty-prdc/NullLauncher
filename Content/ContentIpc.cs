using System.Text.Json.Nodes;
using NullLauncher.Core;
using NullLauncher.Instances;

namespace NullLauncher.Content;

/// <summary>
/// IPC-методы контента сборки: список, включение/выключение, удаление, импорт,
/// обновление с Modrinth, проверка целостности и пресеты.
/// Тяжёлая работа уходит в пул потоков, чтобы не блокировать UI.
/// </summary>
public static class ContentIpc
{
    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("content.list", (p, ct) => Guard("Не удалось загрузить содержимое", () =>
            Task.Run(() => (object?)ContentService.List(s, InstanceId(p), Kind(p), ct), ct)));

        r.Register("content.toggle", (p, _) => Guard("Не удалось изменить состояние файла", () =>
            Run(() => ContentService.Toggle(s, InstanceId(p), Kind(p), Req(p, "id"), p?.Bool("enabled") ?? false))));

        r.Register("content.remove", (p, _) => Guard("Не удалось удалить файл", () =>
            Run(() => ContentService.Remove(s, InstanceId(p), Kind(p), Req(p, "id")))));

        r.Register("content.update", (p, ct) => Guard("Не удалось обновить файл", async () =>
        {
            await ContentService.UpdateAsync(s, InstanceId(p), Kind(p), Req(p, "id"), p?.Str("versionId"), ct)
                .ConfigureAwait(false);
            return (object?)new { ok = true };
        }));

        r.Register("content.updateAll", (p, ct) => Guard("Не удалось обновить контент", async () =>
        {
            var (updated, failed) = await ContentService.UpdateAllAsync(s, InstanceId(p), Kind(p), ct)
                .ConfigureAwait(false);
            return (object?)new { updated, failed };
        }));

        r.Register("content.import", (p, ct) => Guard("Не удалось импортировать файлы", () =>
            Task.Run(() =>
            {
                var (imported, skipped) = ContentService.Import(
                    s, InstanceId(p), Kind(p), p?.StrList("paths") ?? new List<string>(), ct);
                return (object?)new { imported, skipped };
            }, ct)));

        r.Register("content.openFolder", (p, _) => Guard("Не удалось открыть папку", () =>
            Run(() =>
            {
                var (inst, kind) = Resolve(s, p);
                var dir = inst.ContentDir(kind);
                Directory.CreateDirectory(dir);
                // тот же механизм, что у system.openPath: проводник через ShellExecute
                var app = System.Windows.Application.Current;
                if (app is not null) app.Dispatcher.Invoke(() => MainWindow.OpenPath(dir));
                else MainWindow.OpenPath(dir);
            })));

        r.Register("content.checkIntegrity", (p, ct) => Guard("Не удалось проверить целостность", () =>
            Task.Run(() => (object?)ContentService.CheckIntegrity(s, InstanceId(p), Kind(p)), ct)));

        r.Register("content.presets", (p, ct) => Guard("Не удалось загрузить пресеты", () =>
            Task.Run(() => (object?)ContentService.ListPresets(s, InstanceId(p)), ct)));

        r.Register("content.savePreset", (p, ct) => Guard("Не удалось сохранить пресет", () =>
            Task.Run(() => (object?)ContentService.SavePreset(s, InstanceId(p), p?.Str("name") ?? ""), ct)));

        r.Register("content.applyPreset", (p, ct) => Guard("Не удалось применить пресет", async () =>
            (object?)await ContentService.ApplyPresetAsync(s, InstanceId(p), Req(p, "presetId"), ct)
                .ConfigureAwait(false)));

        r.Register("content.deletePreset", (p, _) => Guard("Не удалось удалить пресет", () =>
            Run(() => ContentService.DeletePreset(s, InstanceId(p), Req(p, "presetId")))));
    }

    /* ---------------------------------------------------------- помощники */

    /// <summary>Ошибка с дружелюбным заголовком; LauncherException проходит наружу без изменений.</summary>
    private static async Task<object?> Guard(string title, Func<Task<object?>> body)
    {
        try { return await body().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (LauncherException) { throw; }
        catch (Exception ex) { throw new LauncherException(title, ex.Message, ex); }
    }

    /// <summary>Синхронную операцию выполняем в пуке потоков и отвечаем { ok:true }.</summary>
    private static Task<object?> Run(Action body)
        => Task.Run<object?>(() =>
        {
            body();
            return (object?)new { ok = true };
        });

    private static string InstanceId(JsonNode? p)
    {
        var id = p?.Str("instanceId") ?? "";
        if (string.IsNullOrWhiteSpace(id))
            throw new LauncherException("Не указана сборка", "не передан параметр 'instanceId'");
        return id;
    }

    private static string Kind(JsonNode? p) => ContentService.NormalizeKind(p?.Str("kind"));

    private static string Req(JsonNode? p, string key)
    {
        var v = p?.Str(key) ?? "";
        if (string.IsNullOrWhiteSpace(v))
            throw new LauncherException("Некорректный запрос", $"не передан параметр '{key}'");
        return v;
    }

    private static (InstanceRecord inst, string kind) Resolve(AppServices s, JsonNode? p)
        => (s.Instances.Require(InstanceId(p)), Kind(p));
}
