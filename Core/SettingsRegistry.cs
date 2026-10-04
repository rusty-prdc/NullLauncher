namespace NullLauncher.Core;

public sealed record SettingInfo(string Key, string Title, string SectionId, string Section, string Hint);

/// <summary>
/// Единый реестр настроек: значения по умолчанию + человекочитаемые описания.
/// Используется для поиска по настройкам, подсказок и восстановления после сброса.
/// </summary>
public static class SettingsRegistry
{
    public static readonly Dictionary<string, object?> Defaults = new()
    {
        // внешний вид
        ["theme"] = "dark",
        ["accent"] = "#3ecf8e",
        ["density"] = "normal",
        ["uiScale"] = 100,
        ["fontScale"] = 1.0,
        ["animations"] = true,
        ["rounding"] = "normal",
        ["opacity"] = 100,
        ["highContrast"] = false,
        ["cardSize"] = "normal",
        ["startPage"] = "home",
        ["sidebarOrder"] = new[] { "home", "instances", "discover", "downloads", "settings" },
        ["showSidebarLabels"] = true,

        // общие
        ["language"] = "ru",
        ["confirmDelete"] = true,
        ["closeToTray"] = false,
        ["checkUpdatesOnStart"] = true,
        ["onboardingDone"] = false,
        ["expertMode"] = false,
        ["currentProfile"] = "default",

        // данные
        ["dataDir"] = "",
        ["instanceSort"] = "lastPlayed",
        ["instanceView"] = "grid",
        ["showFavoritesFirst"] = true,
        ["showSnapshots"] = false,
        ["showOldVersions"] = false,
        ["autoBackup"] = true,
        ["backupBeforeUpdate"] = true,
        ["backupRetention"] = 5,

        // java / ram
        ["javaAuto"] = true,
        ["javaMajor"] = 21,
        ["javaPath"] = "",
        ["ramMinMb"] = 512,
        ["ramMaxMb"] = 4096,
        ["ramRecommend"] = true,
        ["jvmArgsDefault"] = "",

        // запуск
        ["windowMode"] = "windowed",
        ["windowWidth"] = 1920,
        ["windowHeight"] = 1080,
        ["fullscreen"] = false,
        ["vsync"] = true,
        ["fpsLimit"] = 120,
        ["closeLauncherOnLaunch"] = false,
        ["safeModeByDefault"] = false,

        // загрузка
        ["downloadThreads"] = 8,
        ["maxConcurrentDownloads"] = 6,
        ["verifyHashes"] = true,
        ["reuseCache"] = true,
        ["networkTimeoutSec"] = 60,
        ["retryCount"] = 3,
        ["speedLimitKbs"] = 0,
        ["resumeInterrupted"] = true,

        // сеть
        ["proxyType"] = "none",
        ["proxyHost"] = "",
        ["proxyPort"] = 8080,
        ["proxyUser"] = "",
        ["proxyPass"] = "",
        ["proxySystem"] = true,

        // modrinth
        ["modrinthPageSize"] = 20,
        ["modrinthOfflineCache"] = true,
        ["modrinthAutoUpdateCheck"] = true,

        // уведомления
        ["notifyToasts"] = true,
        ["notifyDownloadDone"] = true,
        ["notifyLaunchDone"] = true,
        ["notifyModUpdates"] = true,
        ["notifySound"] = false,

        // логи / диагностика
        ["logLevel"] = "info",
        ["logRetentionDays"] = 14,
        ["debugMode"] = false,
        ["crashAutoAnalyze"] = true,

        // конфиденциальность
        ["telemetry"] = false,
        ["saveTokenSecure"] = true,

        // производительность
        ["lazyImages"] = true,
        ["apiCacheMinutes"] = 15,
        ["lowResourceMode"] = false,

        // hotkeys
        ["hotkeys"] = new Dictionary<string, string>
        {
            ["search"] = "Ctrl+K", ["refresh"] = "Ctrl+R", ["newInstance"] = "Ctrl+N",
            ["settings"] = "Ctrl+,", ["logs"] = "Ctrl+Shift+L", ["play"] = "Ctrl+Enter",
        },

        // аккаунт / профиль
        ["accountUuid"] = "",
        ["activeInstance"] = "",

        // experimental
        ["expNewUi"] = false,
        ["expParallelDownloads"] = true,
        ["expAdvancedModScanner"] = true,
        ["expAutoCrashAnalysis"] = true,
        ["expInstanceSync"] = false,
    };

    public static IReadOnlyList<SettingInfo> All { get; } = new List<SettingInfo>
    {
        new("theme", "Тема оформления", "appearance", "Внешний вид", "Тёмная, светлая или как в системе Windows."),
        new("accent", "Акцентный цвет", "appearance", "Внешний вид", "Цвет кнопок, активных элементов и выделения. Допускается произвольный HEX."),
        new("density", "Плотность интерфейса", "appearance", "Внешний вид", "Compact — меньше отступы, Large — крупнее элементы."),
        new("uiScale", "Масштаб интерфейса", "appearance", "Внешний вид", "Общий масштаб UI в процентах."),
        new("fontScale", "Размер шрифта", "appearance", "Внешний вид", "Множитель размера текста."),
        new("animations", "Анимации", "appearance", "Внешний вид", "Полностью отключает переходы и появление элементов."),
        new("rounding", "Скругление углов", "appearance", "Внешний вид", "Радиус карточек и панелей."),
        new("opacity", "Прозрачность окна", "appearance", "Внешний вид", "Непрозрачность окна приложения."),
        new("highContrast", "Высокий контраст", "appearance", "Доступность", "Усиливает контраст текста и границ."),
        new("startPage", "Стартовая страница", "general", "Общие", "Какая страница открывается при запуске лаунчера."),
        new("confirmDelete", "Подтверждать удаление", "general", "Общие", "Спрашивать подтверждение перед удалением сборок, модов и миров."),
        new("closeToTray", "Сворачивать в трей", "general", "Общие", "Закрытие окна сворачивает лаунчер вместо выхода."),
        new("checkUpdatesOnStart", "Проверять обновления лаунчера", "general", "Общие", "Запрашивать новую версию NullLauncher при старте."),
        new("expertMode", "Экспертный режим", "advanced", "Расширенные", "Открывает кастомный Java, JVM-аргументы, прокси, потоки загрузки и отладку."),
        new("dataDir", "Папка данных", "storage", "Хранилище", "Корень с сборками, Java, кэшем и бэкапами. Настройки всегда остаются в %APPDATA%."),
        new("instanceSort", "Сортировка сборок", "instances", "Сборки", "Поле сортировки библиотеки."),
        new("instanceView", "Вид библиотеки", "instances", "Сборки", "Сетка карточек или компактный список."),
        new("showSnapshots", "Показывать снапшоты", "instances", "Сборки", "Включать снапшоты в списки выбора версий."),
        new("showOldVersions", "Показывать старые версии", "instances", "Сборки", "Beta/Alpha и древние релизы в списке версий."),
        new("autoBackup", "Автоматические резервные копии", "backup", "Резервные копии", "Создавать копию перед обновлением версии, модов и удалением."),
        new("backupBeforeUpdate", "Бэкап перед обновлением", "backup", "Резервные копии", "Всегда копировать сборку перед сменой версии Minecraft или модпака."),
        new("backupRetention", "Хранить бэкапов", "backup", "Резервные копии", "Старые автоматические копии удаляются сверх этого числа."),
        new("javaAuto", "Автоматический выбор Java", "java", "Java", "Подбирать версию Java по требованиям версии Minecraft."),
        new("javaMajor", "Версия Java по умолчанию", "java", "Java", "Используется, если автоматический выбор выключен."),
        new("javaPath", "Путь к java.exe", "java", "Java", "Пусто — использовать найденные установки."),
        new("ramMinMb", "Минимальная RAM", "ram", "RAM", "Стартовый объём памяти Java (-Xms)."),
        new("ramMaxMb", "Максимальная RAM", "ram", "RAM", "Предел памяти Java (-Xmx). Больше физически доступной — ошибка."),
        new("ramRecommend", "Подсказывать объём памяти", "ram", "RAM", "Показывать рекомендации по объёму RAM для сборки."),
        new("windowMode", "Режим окна", "launch", "Запуск", "Полноэкранный, развёрнутый или оконный режим игры."),
        new("windowWidth", "Ширина окна", "launch", "Запуск", "Ширина окна игры в оконном режиме."),
        new("windowHeight", "Высота окна", "launch", "Запуск", "Высота окна игры в оконном режиме."),
        new("fullscreen", "Полноэкранный режим", "launch", "Запуск", "Запускать игру на весь экран."),
        new("vsync", "VSync", "launch", "Запуск", "Синхронизация кадров с монитором."),
        new("fpsLimit", "Лимит FPS", "launch", "Запуск", "Ограничение частоты кадров, 0 — без ограничения."),
        new("safeModeByDefault", "Безопасный режим по умолчанию", "launch", "Запуск", "Запускать сборки без модов, пока не отключено вручную."),
        new("downloadThreads", "Потоки загрузки", "downloads", "Загрузка", "Количество одновременных соединений на один файл."),
        new("maxConcurrentDownloads", "Параллельных загрузок", "downloads", "Загрузка", "Сколько файлов скачивается одновременно."),
        new("verifyHashes", "Проверять хэши", "downloads", "Загрузка", "Сверять sha1/sha512 после скачивания."),
        new("reuseCache", "Использовать общий кэш", "downloads", "Загрузка", "Не качать повторно файлы, уже имеющиеся в кэше (по хэшу)."),
        new("networkTimeoutSec", "Тайм-аут сети", "network", "Сеть", "Сколько секунд ждать ответа от сервера."),
        new("retryCount", "Повторов при ошибке", "network", "Сеть", "Число автоматических повторов загрузки."),
        new("speedLimitKbs", "Ограничение скорости", "downloads", "Загрузка", "КБ/с, 0 — без ограничения."),
        new("proxyType", "Тип прокси", "network", "Сеть", "None, System, HTTP, HTTPS или SOCKS5."),
        new("proxyHost", "Прокси — хост", "network", "Сеть", "Адрес прокси-сервера."),
        new("proxyPort", "Прокси — порт", "network", "Сеть", "Порт прокси-сервера."),
        new("modrinthPageSize", "Результатов на странице", "modrinth", "Modrinth", "Размер страницы поиска Modrinth."),
        new("modrinthOfflineCache", "Кэш Modrinth при офлайне", "modrinth", "Modrinth", "Показывать сохранённые данные, если API недоступен."),
        new("notifyToasts", "Всплывающие уведомления", "notifications", "Уведомления", "Собственные уведомления лаунчера внутри окна."),
        new("notifyDownloadDone", "Уведомлять о загрузках", "notifications", "Уведомления", "Сообщать об окончании скачивания."),
        new("notifyLaunchDone", "Уведомлять о запуске", "notifications", "Уведомления", "Сообщать о завершении игры."),
        new("logLevel", "Уровень логов", "logs", "Логи", "Debug, Info, Warn или Error."),
        new("logRetentionDays", "Хранить логи (дней)", "logs", "Логи", "Старые файлы логов удаляются автоматически."),
        new("debugMode", "Отладочный режим", "advanced", "Расширенные", "Подробные логи запуска и сети."),
        new("telemetry", "Телеметрия", "privacy", "Конфиденциальность", "Анонимная статистика использования. По умолчанию выключена."),
        new("saveTokenSecure", "Хранить токены безопасно", "privacy", "Конфиденциальность", "Токены аккаунтов шифруются через DPAPI (Windows Credential store)."),
        new("lowResourceMode", "Режим экономии", "performance", "Производительность", "Уменьшает фоновую активность лаунчера."),
        new("apiCacheMinutes", "Кэш API (минут)", "performance", "Производительность", "Время жизни кэша ответов Modrinth и манифестов."),
    };

    public static IEnumerable<SettingInfo> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return All;
        var q = query.Trim().ToLowerInvariant();
        return All.Where(s =>
            s.Key.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            s.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            s.Section.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            s.Hint.Contains(q, StringComparison.OrdinalIgnoreCase));
    }
}
