/* i18n — перевод интерфейса (ru → en).
   Как работает:
   - словарь DICT: точное совпадение русской строки → английская;
   - PATTERNS: regexp-правила для динамических строк («5 сборок», «Последний запуск …»);
   - translate(root) проходит по DOM (текстовые узлы + title/placeholder/alt) и заменяет;
   - MutationObserver следит за новым содержимым (страницы, диалоги, уведомления, меню);
   - строки, не найденные в словаре, остаются как есть — перевод дополняется постепенно. */
(function () {
  "use strict";

  var lang = "ru";

  var DICT = {
    /* ---------- оболочка (index.html) ---------- */
    "Майнкрафт лаунчер": "Minecraft launcher",
    "Главная": "Home",
    "Мои сборки": "My instances",
    "Загрузки": "Downloads",
    "Настройки": "Settings",
    "Гость": "Guest",
    "Не вошли": "Not signed in",
    "Профиль и аккаунты": "Profile and accounts",
    "Свернуть": "Minimize",
    "Развернуть": "Maximize",
    "Закрыть": "Close",
    "Открыть загрузки": "Open downloads",
    "Поиск сборок, модов, настроек…": "Search instances, mods, settings…",
    "Перетащите файлы": "Drop files here",
    ".jar — мод · .zip — ресурс-пак / шейдер · .mrpack — модпак":
      ".jar — mod · .zip — resource pack / shader · .mrpack — modpack",
    "Независимый проект. Не связан с Mojang Studios и Microsoft. Для игры может потребоваться лицензия Minecraft.":
      "Independent project. Not affiliated with Mojang Studios or Microsoft. A Minecraft licence may be required to play.",
    "Исходный код на GitHub": "Source code on GitHub",
    "Сайт проекта": "Project website",

    /* ---------- общие кнопки / состояния ---------- */
    "Отмена": "Cancel",
    "Отменить": "Cancel",
    "Готово": "Done",
    "Повторить": "Retry",
    "Сохранить": "Save",
    "Сохранено": "Saved",
    "Удалить": "Delete",
    "Играть": "Play",
    "Играть без модов": "Play without mods",
    "Открыть": "Open",
    "Открыть папку": "Open folder",
    "Обновить": "Refresh",
    "Назад": "Back",
    "Далее": "Next",
    "Загрузка…": "Loading…",
    "Ошибка": "Error",
    "Предупреждение": "Warning",
    "Уведомление": "Notice",
    "Не удалось загрузить": "Failed to load",
    "Не удалось выполнить действие": "Something went wrong",
    "Нет данных": "No data",
    "Поиск…": "Search…",
    "Все": "All",
    "Моды": "Mods",
    "Модпаки": "Modpacks",
    "Ресурс-паки": "Resource packs",
    "Шейдеры": "Shaders",
    "Датапаки": "Datapacks",
    "Миры": "Worlds",
    "Серверы": "Servers",
    "Журнал": "Logs",
    "Файлы": "Files",
    "Контент": "Content",
    "Доступ": "Access",
    "Название": "Name",
    "Описание": "Description",
    "Версия": "Version",
    "Загрузчик": "Loader",
    "Ядро": "Loader",
    "Создать сборку": "Create instance",
    "Импорт": "Import",
    "Экспорт": "Export",
    "Сбросить фильтры": "Reset filters",
    "Сортировка": "Sort",
    "По релевантности": "Relevance",
    "По загрузкам": "Downloads",
    "По подписчикам": "Follows",
    "По обновлению": "Updated",
    "Нет подключения к Modrinth. Показаны сохранённые данные.": "No connection to Modrinth. Showing cached data.",

    /* ---------- аккаунты ---------- */
    "Аккаунты": "Accounts",
    "Аккаунтов нет — войдите, чтобы играть": "No accounts — sign in to play",
    "Войти через Microsoft": "Sign in with Microsoft",
    "Офлайн-аккаунт": "Offline account",
    "Аккаунт переключён": "Account switched",
    "Не удалось переключить аккаунт": "Failed to switch account",
    "Гостевой профиль добавлен": "Guest profile added",
    "Не удалось создать гостевой профиль": "Failed to create guest profile",

    /* ---------- обновления / палитра / прочее ---------- */
    "Доступно обновление NullLauncher": "NullLauncher update available",
    "Обновить": "Update",
    "Позже": "Later",
    "Действия": "Actions",
    "Профиль": "Profile",
    "Скин": "Skin",
    "Изменить версию": "Change version",
    "Проверить целостность": "Verify integrity",
    "Клонировать": "Duplicate",
    "Безопасный запуск": "Safe mode launch",
    "Удалить сборку": "Delete instance",
    "Переименовать": "Rename",
    "Резервная копия": "Backup",
    "Настройки": "Settings",

    /* ---------- страница сборки / общие блоки ---------- */
    "Основные": "General",
    "Параметры запуска": "Launch options",
    "Память": "Memory",
    "Java": "Java",
    "Окно": "Window",
    "Дополнительно": "Advanced",
    "Экспертный режим": "Expert mode",
    "Безопасный запуск": "Safe mode",
    "Конфиденциальность": "Privacy",
    "Применить": "Apply",
    "Это текущая версия — менять нечего": "This is the current version",
    "Открыть папку сборки": "Open instance folder",
    "Проверить целостность": "Verify integrity",
    "Экспорт сборки": "Export instance",
    "Минимум RAM": "Min RAM",
    "Максимум RAM": "Max RAM",
    "Рекомендуемый минимум": "Recommended minimum",
    "Готово": "Done",
    "Сохранить": "Save",
    "Создать": "Create",
    "Импортировать": "Import",
    "Экспортировать": "Export",
    "Удалить": "Delete",
    "Переименовать": "Rename",
    "Открыть папку": "Open folder",
    "Копировать": "Copy",
    "Скачать": "Download",
    "Установить": "Install",
    "Загрузчик": "Loader",
    "Категория": "Category",
    "Обновления": "Updates",
    "Установлено": "Installed",
    "Всего": "Total",
    "автор": "by",
    "Последний запуск": "Last played",
    "ещё": "and",
    "осталось": "left",

    /* ---------- навигация: новые страницы ---------- */
    "Скины": "Skins",
    "Статистика": "Stats",

    /* ---------- гардероб скинов ---------- */
    "Гардероб скинов": "Skin wardrobe",
    "Сохраняйте скины, примеряйте в 3D и меняйте в один клик": "Save skins, try them on in 3D and switch in one click",
    "Добавить скин": "Add skin",
    "Применить скин": "Apply skin",
    "Открыть PNG": "Open PNG",
    "Стоять": "Idle",
    "Ходить": "Walk",
    "Бегать": "Run",
    "Плавать": "Swim",
    "Махать": "Wave",
    "Летать": "Fly",
    "Присесть": "Crouch",
    "Вращение": "Rotate",
    "Вращение выкл": "Rotate off",
    "Ник вкл": "Name on",
    "Ник выкл": "Name off",
    "Зум": "Zoom",
    "Перетаскивайте — вращение · колесо — зум · анимация переключается кнопками выше":
      "Drag to rotate · scroll to zoom · switch animations with the buttons above",
    "Слим (узкие руки)": "Slim (narrow arms)",
    "Классическая (широкие руки)": "Classic (wide arms)",
    "Гардероб пуст": "Wardrobe is empty",
    "Добавьте PNG-скин (64×64 или 64×32), чтобы он появился здесь и его можно было примерить в 3D.":
      "Add a PNG skin (64×64 or 64×32) so it shows up here and can be tried on in 3D.",
    "Загрузить PNG с диска…": "Upload PNG from disk…",
    "Добавить по ссылке…": "Add from URL…",
    "Выберите PNG-скин (64×64)": "Choose a PNG skin (64×64)",
    "Скин добавлен": "Skin added",
    "Скин не найден": "Skin not found",
    "Применить скин": "Apply skin",
    "Нужен лицензионный аккаунт": "A licensed account is required",
    "Смена скина работает только с аккаунтом Microsoft.": "Changing a skin only works with a Microsoft account.",
    "Войдите через Microsoft, чтобы применять скины к аккаунту. Гардероб доступен и без входа.":
      "Sign in with Microsoft to apply skins to your account. The wardrobe works without signing in.",
    "Войти через Microsoft": "Sign in with Microsoft",
    "Скин применён": "Skin applied",

    /* ---------- статистика ---------- */
    "Статистика игры": "Game statistics",
    "Сколько вы играете и в какие сборки": "How much you play and in which instances",
    "Общее время": "Total playtime",
    "Запусков": "Launches",
    "Сборок": "Instances",
    "Средняя сессия": "Average session",
    "Активность за 14 дней": "Activity over 14 days",
    "По сборкам": "By instance",
    "Последние сессии": "Recent sessions",
    "запусков": "launches",
    "Пока нет статистики": "No statistics yet",
    "Запустите Minecraft хотя бы раз — здесь появится время по каждой сборке, дневной график и история сессий.":
      "Launch Minecraft at least once — per-instance playtime, a daily chart and session history will appear here.",

    /* ---------- импорт: меню ---------- */
    "Архив (.zip / .mrpack / .nlpkg)…": "Archive (.zip / .mrpack / .nlpkg)…",
    "Папка (MultiMC / Prism / .minecraft)…": "Folder (MultiMC / Prism / .minecraft)…",
  };

  /* regexp-правила для динамических строк (первое совпадение выигрывает) */
  var PATTERNS = [
    [/^(\d+) сборка(и|ок)?$/, "$1 instance(s)"],
    [/^(\d+) мод(а|ов)?$/, "$1 mod(s)"],
    [/^(\d+) (мод|модпак|мир|файл|проект)(а|ов|а)?$/, "$1"],
    [/^Последний запуск (.+)$/, "Last played $1"],
    [/^Играет (.+)$/, "Playing $1"],
    [/^Minecraft (.+)$/, "Minecraft $1"],
    [/^Скачивание: (.+)$/, "Downloading: $1"],
    [/^Уже запустить ([\s\S]+)\?$/, "Launch $1 anyway?"],
    [/^(\d+) г. назад$/, "$1 y ago"],
    [/^(\d+) мес. назад$/, "$1 mo ago"],
    [/^(\d+) дн. назад$/, "$1 d ago"],
    [/^(\d+) ч назад$/, "$1 h ago"],
    [/^(\d+) мин назад$/, "$1 min ago"],
  ];

  var translated = new WeakSet();
  var busy = false;
  var applyTimer = null;

  function isEn() { return lang === "en"; }

  function trText(s) {
    if (!isEn()) return s;
    var key = String(s).trim();
    if (!key) return s;
    if (DICT[key] !== undefined) return DICT[key];
    for (var i = 0; i < PATTERNS.length; i++) {
      var m = key.match(PATTERNS[i][0]);
      if (m) return key.replace(PATTERNS[i][0], PATTERNS[i][1]);
    }
    return s;
  }

  var ATTRS = ["title", "placeholder", "alt", "aria-label"];

  function translateNode(node) {
    if (node.nodeType === 3) { /* текстовый узел */
      if (translated.has(node)) return;
      var v = node.nodeValue;
      if (!v || !/[А-Яа-яЁё]/.test(v)) { translated.add(node); return; }
      var nv = trText(v);
      if (nv !== v) { node._i18nOrig = v; node.nodeValue = nv; }
      translated.add(node);
    } else if (node.nodeType === 1) { /* элемент */
      if (node.tagName === "SCRIPT" || node.tagName === "STYLE") return;
      for (var a = 0; a < ATTRS.length; a++) {
        var name = ATTRS[a];
        var av = node.getAttribute && node.getAttribute(name);
        if (av && /[А-Яа-яЁё]/.test(av)) {
          node.setAttribute("data-i18n-" + name, av);
          node.setAttribute(name, trText(av));
        }
      }
      var kids = node.childNodes;
      for (var i = 0; i < kids.length; i++) translateNode(kids[i]);
    }
  }

  function translate(root) {
    if (!isEn() || !root) return;
    busy = true;
    try { translateNode(root); } finally { busy = false; }
  }

  /* возврат на ru: восстановить исходные строки в оболочке (view перерисует App.reload) */
  function restoreNode(node) {
    if (node.nodeType === 3) {
      if (node._i18nOrig !== undefined) { node.nodeValue = node._i18nOrig; node._i18nOrig = undefined; }
    } else if (node.nodeType === 1) {
      if (node.attributes) {
        for (var i = node.attributes.length - 1; i >= 0; i--) {
          var at = node.attributes[i];
          if (at.name.indexOf("data-i18n-") === 0) {
            node.setAttribute(at.name.slice(10), at.value);
            node.removeAttribute(at.name);
          }
        }
      }
      var kids = node.childNodes;
      for (var k = 0; k < kids.length; k++) restoreNode(kids[k]);
    }
  }

  /* повторный проход по body (например, после смены языка) */
  function retranslate() {
    translated = new WeakSet();
    translate(document.body);
  }

  function schedule(el) {
    if (applyTimer) clearTimeout(applyTimer);
    applyTimer = setTimeout(function () {
      applyTimer = null;
      translate(el);
    }, 120);
  }

  var observer = null;
  function ensureObserver() {
    if (observer || !document.body) return;
    observer = new MutationObserver(function (muts) {
      if (busy || !isEn()) return;
      for (var i = 0; i < muts.length; i++) {
        var m = muts[i];
        if (m.type === "childList") {
          for (var j = 0; j < m.addedNodes.length; j++) schedule(m.addedNodes[j]);
        } else if (m.type === "characterData") {
          schedule(m.target.parentNode);
        } else if (m.type === "attributes" && ATTRS.indexOf(m.attributeName) >= 0) {
          schedule(m.target);
        }
      }
    });
    observer.observe(document.body, {
      subtree: true, childList: true, characterData: true,
      attributes: true, attributeFilter: ATTRS,
    });
  }

  function setLang(next) {
    next = String(next || "ru").toLowerCase();
    if (next === lang) { if (next === "en") ensureObserver(); return; }
    lang = next;
    document.documentElement.lang = next === "en" ? "en" : "ru";
    if (next === "en") { ensureObserver(); retranslate(); }
    /* при возврате на ru восстанавливаем исходные строки оболочки и перерисовываем страницу */
    if (next === "ru") {
      busy = true;
      try { restoreNode(document.body); } finally { busy = false; }
      if (window.App && typeof App.reload === "function") {
        try { App.reload(); } catch (e) { /* noop */ }
      }
    }
  }

  window.I18N = {
    get lang() { return lang; },
    setLang: setLang,
    t: trText,
    translate: translate,
  };
})();
