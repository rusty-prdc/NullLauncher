# NullLauncher

Полноценный лаунчер Minecraft для Windows: настоящие версии Mojang, моды с Modrinth, загрузчики Fabric/Quilt/Forge/NeoForge, автоматический подбор Java, бэкапы и запуск игры.

**Стек:** C# / .NET 8 · WPF + WebView2 · HTML/CSS/JS · SQLite
**Интерфейс:** русский, тёмная тема с изумрудным акцентом.

---

## Возможности

- **Сборки (инстансы)** — каждая сборка изолирована: свои версии, моды, миры, конфиги, журналы.
- **Версии Mojang** — релизы, снапшоты, бета/альфа из официального piston-meta, с проверкой sha1 и офлайн-кэшем.
- **Загрузчики** — Fabric и Quilt из официальных meta-профилей, Forge и NeoForge официальными инсталляторами.
- **Modrinth** — поиск модов, модпаков, ресурспаков и шейдеров, установка в сборку, проверка обновлений.
- **Java** — автоопределение установленных JRE, автоматический подбор версии (8/17/21) под требования Minecraft, докачка Temurin при нехватке.
- **Аккаунты** — вход через Microsoft (device code, OAuth без пароля), оффлайн-аккаунты и гость; токены шифруются DPAPI.
- **Запуск** — проверка целостности файлов, разбор правил аргументов, нативники, класспасс, реальный прогресс и логи процесса.
- **Загрузки** — единая очередь с паузой, возобновлением, повторами, sha1-контролем и историей.
- **Миры и серверы** — список, переименование, экспорт/импорт zip, бэкапы, быстрый запуск с конкретным миром или сервером.
- **Журналы и сбои** — чтение логов лаунчера и игры, анализ crash-report с реальными причинами и советами.
- **Бэкапы** — архивы сборок и миров по запросу и перед опасными операциями.
- **Настройки** — тема, акцент, память, Java, прокси, экспертный режим, экспорт/сброс, диагностика.

---

## Установка

Готовые сборки — в [Releases](../../releases):

- **`NullLauncher-<версия>-Setup.exe`** — инсталлятор (ярлыки в меню Пуск и на рабочем столе, деинсталлятор).
- **`NullLauncher-<версия>-win-x64.zip`** — архив для переноса без установки.

Самодостаточная сборка: .NET ставить не нужно, достаточно Windows 10/11 x64.
Для запуска игры потребуется установленный **Microsoft WebView2 Runtime** (встроен в Windows 11 и почти во все сборки Windows 10).

---

## Сборка из исходников

Требования: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
# отладочная сборка
.\build.ps1

# сборка и запуск
.\build.ps1 -Run

# релиз
.\build.ps1 -Configuration Release
```

Результат: `bin\Debug\net8.0-windows\NullLauncher.exe`.

### Самодостаточный релиз (win-x64)

```powershell
.\publish.ps1                # self-contained, несколько файлов
.\publish.ps1 -SingleFile    # один exe
.\publish.ps1 -FrameworkDependent   # меньше размер, нужен .NET 8 на ПК
```

Результат: `publish\NullLauncher\NullLauncher.exe` — работает на чистой Windows 10/11 без установленного .NET.

### Дистрибутив (ZIP + инсталлятор)

```powershell
.\installer\make-installer.ps1            # publish → ZIP → Setup.exe
.\installer\make-installer.ps1 -SkipPublish   # использовать готовый publish\
.\installer\make-installer.ps1 -NoZip     # только Setup.exe
```

Результат в `dist\`. Инсталлятор собирается [Inno Setup 6](https://jrsoftware.org/isinfo.php).

---

## Данные

Всё пользовательское хранится в `%APPDATA%\NullLauncher\` (рядом с exe ничего не создаётся):

| Путь | Содержимое |
|---|---|
| `config\settings.json` | настройки лаунчера |
| `instances\` | сборки: mods, config, saves, logs, options |
| `versions\`, `libraries\`, `assets\` | общие файлы Minecraft |
| `java\` | скачанные JRE |
| `downloads\`, `cache\`, `metadata\` | кэш и очередь загрузок |
| `backups\` | резервные копии |
| `logs\` | журналы лаунчера |
| `NullLauncher.db` | SQLite: аккаунты (DPAPI), группы, содержимое, бэкапы |

Корень данных можно сменить в **Настройки → Хранилище**; настройки всегда остаются в `%APPDATA%`.

---

## Структура проекта

```
Core/          контейнер сервисов, IPC-маршрутизатор, настройки, HTTP, логи, пути
Database/      схема и доступ к SQLite
Instances/     сборки: создание, копирование, импорт/экспорт, метаданные
Versions/      манифест Mojang, список версий, Fabric/Quilt/Forge/NeoForge
Minecraft/     разбор version.json, подготовка файлов, запуск, совместимость
Downloads/     очередь загрузок с паузой и повторами
Java/          поиск, установка и выбор JRE
Accounts/      Microsoft OAuth (device code), офлайн, гость
Modrinth/      API Modrinth v2: поиск, версии, установка
Content/       моды/ресурспаки/шейдеры/датапаки внутри сборки
Worlds/ Servers/ Logs/ Backups/   миры, серверы, журналы, копии
Modules/       системные IPC: инфо, настройки, хранилище, диагностика
UI/            index.html, app.js, styles.css, pages/*.html, assets/
docs/IPC.md    контракт фронт-бэкенд обмена
```

---

## IPC

Весь UI общается с бэкендом одной командой:

```js
await api("versions.list", { type: "release", limit: 50 })
```

Маршруты, формы запросов/ответов и события — в [docs/IPC.md](docs/IPC.md).
Ошибки всегда дружелюбные: `{ code: "Заголовок", message: "детали" }` без сырых исключений.

---

## Лицензия

[MIT](LICENSE) — свободное использование, изменение и распространение с сохранением упоминания автора.

Не связано с Mojang Studios и Microsoft. Minecraft — товарный знак Mojang Studios.
Использование официальных API (piston-meta, Modrinth, Microsoft OAuth) не нарушает их условий.
