namespace NullLauncher.Backups;

/// <summary>
/// IPC резервных копий: список, создание, восстановление, удаление и открытие папки.
/// Вся работа с файлами остаётся в BackupService — здесь только параметры, валидация и ответы.
/// </summary>
public static class BackupIpc
{
    private static readonly string[] Kinds = { "full", "worlds", "mods", "settings" };

    public static void Register(IpcRouter r, AppServices s)
    {
        /* ---------------------------------------------------------- список */
        r.Register("backups.list", (p, _) =>
        {
            var instanceId = p?.Str("instanceId");
            if (string.IsNullOrWhiteSpace(instanceId)) instanceId = null;

            var items = BackupService.List(instanceId)
                .Select(b => new
                {
                    id = b.Id,
                    name = b.Name,
                    path = b.Path,
                    createdAt = b.CreatedAt,
                    sizeBytes = b.SizeBytes,
                    kind = b.Kind,
                    instanceId = b.InstanceId,
                    automatic = b.Automatic,
                })
                .ToList();
            return Task.FromResult<object?>(items);
        });

        /* ---------------------------------------------------------- создание */
        r.Register("backups.create", (p, _) =>
        {
            var instanceId = p?.Str("instanceId");
            if (string.IsNullOrWhiteSpace(instanceId))
                throw new LauncherException("Укажите сборку", "Бэкап всегда делается от конкретной сборки");

            var kind = (p?.Str("kind") ?? "full").Trim().ToLowerInvariant();
            if (!Kinds.Contains(kind))
                throw new LauncherException("Неизвестный тип резервной копии",
                    "Допустимо: full, worlds, mods или settings");

            var name = p?.Str("name");
            if (string.IsNullOrWhiteSpace(name)) name = null;

            var inst = s.Instances.Require(instanceId);
            // CreateForInstance копирует сборку целиком и пишет название вида "<reason>: <имя сборки>"
            var info = BackupService.CreateForInstance(s.Paths, inst, KindLabel(kind), automatic: false);
            // в БД фиксируем запрошенный тип и имя пользователя (если давали)
            info = info with { Kind = kind, Name = name ?? info.Name };
            Database.DatabaseService.Execute(
                "UPDATE backups SET kind=@Kind, name=@Name WHERE id=@Id",
                new { info.Kind, info.Name, info.Id });

            return Task.FromResult<object?>(new
            {
                id = info.Id,
                name = info.Name,
                path = info.Path,
                createdAt = info.CreatedAt,
                sizeBytes = info.SizeBytes,
                kind = info.Kind,
                instanceId = info.InstanceId,
                automatic = info.Automatic,
            });
        });

        /* ---------------------------------------------------------- восстановление */
        r.Register("backups.restore", (p, _) =>
        {
            var backupId = p?.Str("backupId");
            if (string.IsNullOrWhiteSpace(backupId))
                throw new LauncherException("Не указана резервная копия");
            var instanceId = p?.Str("instanceId");
            if (string.IsNullOrWhiteSpace(instanceId))
                throw new LauncherException("Укажите сборку", "В какую сборку восстанавливать копию");

            BackupService.Restore(backupId, instanceId, p?.Str("what"));
            s.Emit("instance.changed", new { id = instanceId });
            return Task.FromResult<object?>(new { ok = true });
        });

        /* ---------------------------------------------------------- удаление */
        r.Register("backups.delete", (p, _) =>
        {
            var backupId = p?.Str("backupId");
            if (string.IsNullOrWhiteSpace(backupId))
                throw new LauncherException("Не указана резервная копия");
            BackupService.Delete(backupId);
            return Task.FromResult<object?>(new { ok = true });
        });

        /* ---------------------------------------------------------- открыть папку */
        r.Register("backups.openFolder", (p, _) =>
        {
            var instanceId = p?.Str("instanceId");
            var dir = string.IsNullOrWhiteSpace(instanceId)
                ? s.Paths.BackupsDir
                : Path.Combine(s.Paths.BackupsDir, instanceId!);
            Directory.CreateDirectory(dir);

            var app = System.Windows.Application.Current;
            if (app is not null) app.Dispatcher.Invoke(() => MainWindow.OpenPath(dir));
            else MainWindow.OpenPath(dir);

            return Task.FromResult<object?>(new { ok = true });
        });
    }

    /// <summary>Человеческая подпись типа копии — попадает в название, если своё имя не задано.</summary>
    private static string KindLabel(string kind) => kind switch
    {
        "worlds" => "Миры",
        "mods" => "Моды",
        "settings" => "Настройки",
        _ => "Полная копия",
    };
}
