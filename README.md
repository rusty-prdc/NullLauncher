# NullLauncher

A Minecraft launcher for Windows 10/11, built with .NET 8, WPF, WebView2, HTML/CSS/JavaScript and SQLite.

**Current release:** [1.0.4](https://github.com/rusty-prdc/NullLauncher/releases/tag/v1.0.4) · **License:** MIT

## Features

- **Isolated instances** with separate game versions, mods, worlds, configs and logs.
- **Official Minecraft version metadata** with releases, snapshots, beta/alpha versions, SHA-1 validation and an offline metadata cache.
- **Mod loaders:** Fabric and Quilt profiles; Forge and NeoForge installers.
- **Modrinth catalog** for mods, modpacks, resource packs and shaders, with project search and installation into an instance.
- **CurseForge modpacks:** catalog browsing, version selection and installation.
- **Archive import:** NullLauncher archives, Modrinth `.mrpack`, CurseForge modpacks and existing `.minecraft` folders.
- **Java runtime management** with automatic Java version selection and Temurin download when required.
- **Microsoft device-code sign-in**, offline profiles and guest mode. Microsoft tokens are protected with Windows DPAPI.
- **Game launch preparation** with library validation, native files, classpath and process logs.
- **Download queue** with pause, resume, retry, SHA-1 validation and history.
- **Worlds and backups:** import/export, backup and restore workflows.
- **Settings and diagnostics:** theme, memory, Java, proxy, storage, logs and crash reports.

## Download

Get signed Windows builds from [GitHub Releases](https://github.com/rusty-prdc/NullLauncher/releases):

- `NullLauncher-<version>-Setup.exe` installs the app and creates shortcuts.
- `NullLauncher-<version>-win-x64.zip` is a portable archive.

Builds target Windows 10/11 x64 and are self-contained; the WebView2 Runtime is required and is included with most Windows 11 and current Windows 10 installations.

## Build from source

Requirements: .NET 8 SDK and Windows 10/11 x64.

```powershell
# Build
.\build.ps1

# Build and run
.\build.ps1 -Run

# Release configuration
.\build.ps1 -Configuration Release
```

Create a self-contained distribution:

```powershell
.\publish.ps1
.\publish.ps1 -SingleFile
```

Create a ZIP archive and installer with Inno Setup 6:

```powershell
.\installer\make-installer.ps1
```

## User data

By default, user data is stored in `%APPDATA%\NullLauncher\`:

| Path | Contents |
|---|---|
| `config\settings.json` | Launcher settings |
| `instances\` | Instances, mods, worlds and configuration |
| `versions\`, `libraries\`, `assets\` | Shared Minecraft files |
| `java\` | Downloaded Java runtimes |
| `downloads\`, `cache\`, `metadata\` | Download queue and caches |
| `backups\` | Backups |
| `logs\` | Launcher logs |
| `NullLauncher.db` | SQLite data, including protected account tokens |

The data root can be changed under **Settings → Storage**.

## Project layout

`Core/` contains settings, HTTP, paths and logging; `Instances/`, `Versions/`, `Minecraft/`, `Java/` and `Accounts/` contain the corresponding services; `UI/` contains the WebView interface; `docs/IPC.md` documents the UI/backend IPC contract.

## Support and legal

- Support: [xprodc@yandex.ru](mailto:xprodc@yandex.ru)
- Source and issue tracker: [github.com/rusty-prdc/NullLauncher](https://github.com/rusty-prdc/NullLauncher)
- NullLauncher is an independent project and is not affiliated with Mojang Studios or Microsoft. Minecraft is a trademark of Mojang Studios.

---

# Русская версия

Лаунчер Minecraft для Windows 10/11 на .NET 8, WPF, WebView2, HTML/CSS/JavaScript и SQLite.

**Текущий релиз:** [1.0.4](https://github.com/rusty-prdc/NullLauncher/releases/tag/v1.0.4) · **Лицензия:** MIT

## Возможности

- **Изолированные сборки** с отдельными версиями игры, модами, мирами, настройками и журналами.
- **Официальные метаданные Minecraft:** релизы, снапшоты, бета/альфа-версии, проверка SHA-1 и офлайн-кэш.
- **Загрузчики модов:** профили Fabric и Quilt, установщики Forge и NeoForge.
- **Каталог Modrinth** для модов, модпаков, ресурс-паков и шейдеров.
- **Модпаки CurseForge:** каталог, выбор версии и установка.
- **Импорт архивов:** NullLauncher, Modrinth `.mrpack`, CurseForge и готовые папки `.minecraft`.
- **Управление Java** с автоматическим выбором версии и загрузкой Temurin при необходимости.
- **Вход Microsoft по коду устройства**, офлайн-профили и гостевой режим. Токены защищаются Windows DPAPI.
- **Подготовка запуска игры:** проверка библиотек, нативные файлы, classpath и логи процесса.
- **Очередь загрузок** с паузой, возобновлением, повторами, проверкой SHA-1 и историей.
- **Миры и резервные копии:** импорт, экспорт, создание и восстановление.
- **Настройки и диагностика:** тема, память, Java, прокси, хранилище, журналы и отчёты о сбоях.

## Скачать

Готовые сборки для Windows опубликованы в разделе [GitHub Releases](https://github.com/rusty-prdc/NullLauncher/releases):

- `NullLauncher-<version>-Setup.exe` — установщик с ярлыками.
- `NullLauncher-<version>-win-x64.zip` — переносная версия.

Сборки предназначены для Windows 10/11 x64 и автономны. Требуется WebView2 Runtime; он уже есть в большинстве установок Windows 11 и актуальных Windows 10.

## Сборка из исходников

Требования: .NET 8 SDK и Windows 10/11 x64.

```powershell
# Сборка
.\build.ps1

# Сборка и запуск
.\build.ps1 -Run

# Release-конфигурация
.\build.ps1 -Configuration Release
```

Автономная публикация:

```powershell
.\publish.ps1
.\publish.ps1 -SingleFile
```

Сборка ZIP и установщика с Inno Setup 6:

```powershell
.\installer\make-installer.ps1
```

## Пользовательские данные

По умолчанию данные хранятся в `%APPDATA%\NullLauncher\`: настройки, сборки, версии Minecraft, Java, загрузки, резервные копии, журналы и база SQLite с защищёнными токенами. Путь можно изменить в **Настройки → Хранилище**.

## Структура проекта

`Core/` — настройки, HTTP, пути и журналы; `Instances/`, `Versions/`, `Minecraft/`, `Java/` и `Accounts/` — соответствующие сервисы; `UI/` — интерфейс WebView; контракт IPC описан в `docs/IPC.md`.

## Поддержка и правовая информация

- Поддержка: [xprodc@yandex.ru](mailto:xprodc@yandex.ru)
- Исходный код и сообщения об ошибках: [github.com/rusty-prdc/NullLauncher](https://github.com/rusty-prdc/NullLauncher)
- NullLauncher — независимый проект, не связанный с Mojang Studios или Microsoft. Minecraft — товарный знак Mojang Studios.
