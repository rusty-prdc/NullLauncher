# NullLauncher Release Notes

English is the primary language for release notes. Russian translations follow each release.

## 1.0.4 — 2026-10-23

- Added a skin wardrobe with a high-quality 3D preview: save skins, try them on in 3D (animations, rotation, zoom, name tag) and apply one to a Microsoft account in a single click. Slim/classic arms are detected automatically, and Steve/Alex are included by default.
- Added a game statistics page: total playtime, launch counts, per-instance breakdown, a 14-day activity chart and recent session history. Playtime now counts actual in-game time, excluding download and preparation.
- Added automatic crash analysis: after a game exits with an error the launcher finds the newest crash report, identifies the likely cause (out of memory, mod conflicts, missing libraries, Java issues) and offers concrete fixes with a link to the report.
- Added importing MultiMC / Prism instances directly from a folder (`mmc-pack.json` + `instance.cfg`) alongside the existing archive import, which now also accepts plain game folders.
- Added a CurseForge API key setting (Modrinth → CurseForge) for using your own key from console.curseforge.com.
- Fixed a race in Microsoft sign-in polling that could surface "device_code has already been used": polls no longer overlap and a consumed device code is retired immediately.
- Fixed a statistics page crash (`Invalid time value`) when parsing the activity date range.

### Русский

- Добавлен гардероб скинов с качественным 3D-просмотром: сохраняйте скины, примеряйте их в 3D (анимации, вращение, зум, ник) и применяйте к аккаунту Microsoft в один клик. Слим/классика определяются автоматически, Steve и Alex в комплекте.
- Добавлена страница статистики игры: общее время, количество запусков, разбивка по сборкам, график активности за 14 дней и история последних сессий. Время игры теперь считается реальное, без учёта скачивания и подготовки.
- Добавлен автоматический анализ сбоев: при аварийном завершении игры лаунчер находит свежий отчёт о сбое, определяет вероятную причину (нехватка памяти, конфликт модов, отсутствие библиотек, проблемы с Java) и предлагает конкретные исправления со ссылкой на отчёт.
- Добавлен импорт инстансов MultiMC / Prism прямо из папки (`mmc-pack.json` + `instance.cfg`) в дополнение к существующему импорту архивов, который теперь принимает и обычные папки игры.
- Добавлена настройка API-ключа CurseForge (Modrinth → CurseForge) для использования собственного ключа с console.curseforge.com.
- Исправлена гонка опроса при входе через Microsoft, из-за которой могла появляться ошибка «device_code has already been used»: опросы больше не пересекаются, а использованный код сразу удаляется.
- Исправлено падение страницы статистики (`Invalid time value`) при разборе диапазона дат.

## 1.0.3 — 2026-10-17

- Added a CurseForge modpack catalog with project search, filters, version selection and installation.
- Expanded archive import to recognize NullLauncher packages, Modrinth `.mrpack`, CurseForge modpacks and existing `.minecraft` folders by their contents.
- Improved downloads: interrupted transfers and unexpected HTTP range responses are retried without accepting incomplete files.
- Improved modpack installation progress and error reporting.

### Русский

- Добавлен каталог модпаков CurseForge: поиск, фильтры, выбор версии и установка.
- Расширен импорт архивов: NullLauncher, Modrinth `.mrpack`, модпаки CurseForge и готовые папки `.minecraft` распознаются по содержимому.
- Улучшены загрузки: обрывы и неожиданные ответы HTTP Range обрабатываются повтором, незавершённые файлы не принимаются.
- Улучшены прогресс установки модпаков и сообщения об ошибках.

## 1.0.2 — 2026-10-05

- Added modpack version selection before installation.
- Added Modrinth project covers to instance cards.
- Improved HTML formatting in confirmation dialogs.

### Русский

- Добавлен выбор версии модпака перед установкой.
- На карточках сборок показываются обложки проектов Modrinth.
- Улучшено отображение HTML в диалогах подтверждения.

## 1.0.1 — 2026-10-04

- Fixed Fabric, Quilt, Forge and NeoForge library downloads by using their Maven repositories.
- Added setup-time checks for the .NET Desktop Runtime 8 and Microsoft Edge WebView2 Runtime.

### Русский

- Исправлена загрузка библиотек Fabric, Quilt, Forge и NeoForge из их Maven-репозиториев.
- Добавлены проверки и установка .NET Desktop Runtime 8 и Microsoft Edge WebView2 Runtime при необходимости.

## 1.0.0 — 2026-10-04

- Initial public release for Windows 10/11 x64.
- Added Minecraft version and instance management, Microsoft device-code sign-in, offline profiles, Modrinth integration, downloads, backups and launcher settings.
- Added Setup.exe installer and portable ZIP distribution.

### Русский

- Первый публичный релиз для Windows 10/11 x64.
- Добавлены управление версиями и сборками Minecraft, вход Microsoft по коду устройства, офлайн-профили, интеграция Modrinth, загрузки, резервные копии и настройки.
- Добавлены установщик Setup.exe и переносной ZIP-архив.
