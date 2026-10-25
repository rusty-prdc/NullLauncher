/* =============================================================
   NullLauncher — core (bridge, router, UI primitives)
   ============================================================= */
"use strict";

/* ---------------- bridge ---------------- */
const _pending = new Map();
const _listeners = new Map();
let _seq = 0;
/* долгие операции: импорт архивов качает сотни файлов */
const API_TIMEOUTS = {
  "instances.import": 1800000,
  "instances.importMrpack": 1800000,
  "instances.repair": 1800000,
  "instances.restore": 1800000,
  "accounts.check": 600000,
  "skins.add": 180000,
  "skins.apply": 120000,
};
const _hasBridge = typeof window.chrome !== "undefined" && !!window.chrome.webview;

function api(method, params) {
  if (!_hasBridge) return Promise.reject(new Error("Хост недоступен: приложение должно запускаться из NullLauncher.exe"));
  /* импорт больших паков (CurseForge на 250+ файлов) идёт дольше пяти минут */
  const timeoutMs = API_TIMEOUTS[method] || 300000;
  return new Promise((resolve, reject) => {
    const id = ++_seq;
    _pending.set(id, { resolve, reject });
    window.chrome.webview.postMessage({ id, method, params: params || {} });
    setTimeout(() => {
      if (_pending.has(id)) { _pending.delete(id); reject({ code: "Тайм-аут", message: `Операция «${method}» не завершилась вовремя` }); }
    }, timeoutMs);
  });
}

/* fire & forget (не ждём ответа, но ошибки покажем) */
function call(method, params) { api(method, params).catch(e => notifyError(e)); }

if (_hasBridge) {
  window.chrome.webview.addEventListener("message", (ev) => {
    const m = ev.data;
    if (!m) return;
    if (m.id !== undefined && (m.result !== undefined || m.error !== undefined)) {
      const p = _pending.get(m.id);
      if (!p) return;
      _pending.delete(m.id);
      m.error ? p.reject(m.error) : p.resolve(m.result);
      return;
    }
    if (m.event) emit(m.event, m.data);
  });
}

function on(event, fn) {
  if (!_listeners.has(event)) _listeners.set(event, new Set());
  _listeners.get(event).add(fn);
  return () => _listeners.get(event)?.delete(fn);
}
function emit(event, data) {
  const set = _listeners.get(event);
  if (set) for (const fn of [...set]) { try { fn(data); } catch (e) { console.error(e); } }
  const wild = _listeners.get("*");
  if (wild) for (const fn of [...wild]) { try { fn(event, data); } catch (e) { console.error(e); } }
}

/* ---------------- helpers ---------------- */
const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const uid = () => Math.random().toString(36).slice(2, 10);
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));

function debounce(fn, ms = 220) { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; }

function icon(name, cls = "") {
  return `<svg class="${cls}"><use href="#i-${name}"/></svg>`;
}

function fmtBytes(n) {
  if (n === null || n === undefined || isNaN(n)) return "—";
  const en = window.I18N && I18N.lang === "en";
  if (n < 1024) return n + (en ? " B" : " Б");
  const u = en ? ["KB", "MB", "GB", "TB"] : ["КБ", "МБ", "ГБ", "ТБ"];
  let i = -1, v = n;
  do { v /= 1024; i++; } while (v >= 1024 && i < u.length - 1);
  return (v >= 100 ? v.toFixed(0) : v >= 10 ? v.toFixed(1) : v.toFixed(2)) + " " + u[i];
}
function fmtNum(n) {
  if (n === null || n === undefined || isNaN(n)) return "—";
  return new Intl.NumberFormat((window.I18N && I18N.lang === "en") ? "en-US" : "ru-RU").format(n);
}
function fmtDate(iso) {
  if (!iso) return (window.I18N && I18N.lang === "en") ? "never" : "никогда";
  const d = new Date(iso);
  if (isNaN(d)) return "—";
  const loc = (window.I18N && I18N.lang === "en") ? "en-US" : "ru-RU";
  return d.toLocaleDateString(loc, { day: "2-digit", month: "short", year: "numeric" }) + " " +
         d.toLocaleTimeString(loc, { hour: "2-digit", minute: "2-digit" });
}
function fmtRelative(iso) {
  const en = !!(window.I18N && I18N.lang === "en");
  if (!iso) return en ? "never" : "никогда";
  const d = new Date(iso);
  if (isNaN(d)) return "—";
  const diff = Date.now() - d.getTime();
  const m = Math.floor(diff / 60000);
  if (m < 1) return en ? "just now" : "только что";
  if (m < 60) return en ? `${m} min ago` : `${m} мин назад`;
  const h = Math.floor(m / 60);
  if (h < 24) return en ? `${h} h ago` : `${h} ч назад`;
  const dd = Math.floor(h / 24);
  if (dd === 1) return en ? "yesterday" : "вчера";
  if (dd < 30) return en ? `${dd} d ago` : `${dd} дн. назад`;
  const mo = Math.floor(dd / 30);
  if (mo < 12) return en ? `${mo} mo ago` : `${mo} мес. назад`;
  return en ? `${Math.floor(mo / 12)} y ago` : `${Math.floor(mo / 12)} г. назад`;
}
function fmtDuration(ms) {
  const en = !!(window.I18N && I18N.lang === "en");
  if (!ms) return "—";
  const s = Math.floor(ms / 1000);
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60);
  if (h > 0) return en ? `${h} h ${m} min` : `${h} ч ${m} мин`;
  if (m > 0) return en ? `${m} min` : `${m} мин`;
  return en ? `${s} s` : `${s} с`;
}
function fmtSpeed(bps) {
  return fmtBytes(bps) + ((window.I18N && I18N.lang === "en") ? "/s" : "/с");
}
function fmtEta(sec) {
  const en = !!(window.I18N && I18N.lang === "en");
  if (!isFinite(sec) || sec <= 0) return "—";
  if (sec < 60) return en ? `${Math.round(sec)} s` : `${Math.round(sec)} с`;
  if (sec < 3600) return en
    ? `${Math.floor(sec / 60)} min ${Math.round(sec % 60)} s`
    : `${Math.floor(sec / 60)} мин ${Math.round(sec % 60)} с`;
  return en
    ? `${Math.floor(sec / 3600)} h ${Math.floor((sec % 3600) / 60)} min`
    : `${Math.floor(sec / 3600)} ч ${Math.floor((sec % 3600) / 60)} мин`;
}
function initials(name) {
  // "_Rvsty_", "Steve the Miner" → «R», «SM»: отбрасываем не-буквы/цифры по краям
  const clean = String(name || "?").replace(/[^A-Za-zА-Яа-я0-9_]+/g, " ").trim() || "?";
  const p = clean.split(/\s+/);
  const ch = (w) => { const m = w.match(/[A-Za-zА-Яа-я0-9]/); return m ? m[0] : ""; };
  return ((ch(p[0]) || "?") + ch(p[1] || "")).toUpperCase();
}
function errText(e) {
  if (!e) return "Неизвестная ошибка";
  if (typeof e === "string") return e;
  return e.message || e.code || "Неизвестная ошибка";
}
function errDetail(e) { return (e && (e.detail || e.stack)) || ""; }

/* ---------------- notifications ---------------- */
function notify(type, title, message, actions, timeout) {
  const host = $("#notify-host");
  const el = document.createElement("div");
  el.className = "toast " + type;
  const ic = type === "error" ? "alert" : type === "warn" ? "alert" : type === "info" ? "bell" : "check";
  el.innerHTML = `
    <div class="t-icon">${icon(ic)}</div>
    <div class="t-body">
      <div class="t-title">${esc(title)}</div>
      ${message ? `<div class="t-msg">${esc(message)}</div>` : ""}
      <div class="t-actions"></div>
    </div>
    <button class="t-close" title="Закрыть">${icon("x")}</button>`;
  const close = () => { el.classList.add("out"); setTimeout(() => el.remove(), 200); };
  el.querySelector(".t-close").onclick = close;
  if (actions && actions.length) {
    const box = el.querySelector(".t-actions");
    for (const a of actions) {
      const b = document.createElement("button");
      b.className = "btn btn-sm " + (a.primary ? "btn-primary" : "btn-ghost");
      b.textContent = a.label;
      b.onclick = () => { close(); a.onClick && a.onClick(); };
      box.appendChild(b);
    }
  }
  const ms = timeout ?? (type === "error" ? 9000 : type === "warn" ? 7000 : 4200);
  if (ms > 0) {
    const bar = document.createElement("i");
    bar.className = "t-bar";
    bar.style.animationDuration = ms + "ms";
    el.appendChild(bar);
    setTimeout(close, ms);
  }
  host.appendChild(el);
  while (host.children.length > 5) host.firstChild.remove();
  return { close };
}
const notifySuccess = (t, m) => notify("success", t, m);
const notifyError = (e, t) => notify("error", t || "Не удалось выполнить действие", errText(e));
const notifyWarn = (t, m) => notify("warn", t, m);
const notifyInfo = (t, m) => notify("info", t, m);

/* Копирует текст в буфер обмена: clipboard API, иначе — IPC system.clipboard. */
async function copyText(text, okMsg) {
  try {
    if (navigator.clipboard && navigator.clipboard.writeText) await navigator.clipboard.writeText(text);
    else await api("system.clipboard", { text });
    notifySuccess("Скопировано", okMsg || "Текст в буфере обмена");
    return true;
  } catch (e) {
    try {
      await api("system.clipboard", { text });
      notifySuccess("Скопировано", okMsg || "Текст в буфере обмена");
      return true;
    } catch (e2) {
      notifyError(e2, "Не удалось скопировать");
      return false;
    }
  }
}

/* ---------------- dialogs ---------------- */
let _dialogStack = [];

function openDialog(opts) {
  const host = $("#dialog-host");
  const back = document.createElement("div");
  back.className = "dialog-backdrop";
  const width = opts.width ? " " + opts.width : "";
  back.innerHTML = `
    <div class="dialog${width}" role="dialog" aria-modal="true">
      <div class="dialog-head">
        ${opts.icon ? `<div class="dialog-icon ${opts.iconType || ""}">${icon(opts.icon)}</div>` : ""}
        <div style="flex:1;min-width:0">
          <h3>${esc(opts.title)}</h3>
          ${opts.sub ? `<div class="sub">${opts.subHtml ? opts.sub : esc(opts.sub)}</div>` : ""}
        </div>
        <button class="btn btn-ghost btn-icon btn-sm" data-close title="Закрыть">${icon("x")}</button>
      </div>
      <div class="dialog-body"></div>
      <div class="dialog-foot"></div>
    </div>`;
  host.appendChild(back);
  const dlg = back.querySelector(".dialog");
  const body = back.querySelector(".dialog-body");
  const foot = back.querySelector(".dialog-foot");
  if (typeof opts.body === "string") body.innerHTML = opts.body;
  else if (opts.body instanceof Node) body.appendChild(opts.body);

  const ctx = { el: dlg, body, foot, close };
  const buttons = opts.buttons || [{ label: "Закрыть", role: "cancel" }];
  for (const b of buttons) {
    const el = document.createElement("button");
    el.className = "btn " + (b.cls || (b.role === "primary" ? "btn-primary" : b.role === "danger" ? "btn-danger" : ""));
    if (b.left) el.classList.add("left");
    el.textContent = b.label;
    el.dataset.act = b.id || b.label;
    if (b.disabled) el.disabled = true;
    el.onclick = async () => {
      if (b.onClick) {
        const r = await b.onClick(ctx);
        if (r === false) return;
      }
      if (!b.keepOpen) close();
    };
    foot.appendChild(el);
  }
  if (!buttons.length) foot.remove();

  function onKey(e) {
    if (e.key === "Escape") { e.stopPropagation(); close(); }
  }
  function onDown(e) { if (e.target === back && opts.dismissable !== false) close(); }
  back.addEventListener("keydown", onKey);
  back.addEventListener("mousedown", onDown);
  back.querySelector("[data-close]").onclick = close;
  _dialogStack.push(ctx);
  setTimeout(() => {
    const f = body.querySelector("input,textarea,select,button") || foot.querySelector("button");
    f && f.focus();
  }, 60);
  return ctx;

  function close() {
    back.removeEventListener("keydown", onKey);
    back.removeEventListener("mousedown", onDown);
    _dialogStack = _dialogStack.filter((d) => d !== ctx);
    back.remove();
    opts.onClose && opts.onClose();
  }
}

function confirmDialog({ title, message, okLabel = "Продолжить", cancelLabel = "Отмена", danger, icon: ic, messageHtml }) {
  return new Promise((resolve) => {
    openDialog({
      title, sub: message, subHtml: !!messageHtml, icon: ic || (danger ? "alert" : "alert"), iconType: danger ? "error" : "warn",
      buttons: [
        { label: cancelLabel, role: "cancel", onClick: () => resolve(false) },
        { label: okLabel, role: danger ? "danger" : "primary", onClick: () => resolve(true) },
      ],
      onClose: () => resolve(false),
    });
  });
}

function promptDialog({ title, message, value = "", placeholder = "", okLabel = "OK", validate }) {
  return new Promise((resolve) => {
    const wrap = document.createElement("div");
    wrap.className = "field";
    wrap.innerHTML = `${message ? `<div class="hint">${esc(message)}</div>` : ""}
      <input type="text" value="${esc(value)}" placeholder="${esc(placeholder)}" />
      <div class="err" hidden></div>`;
    const dlg = openDialog({
      title,
      body: wrap,
      buttons: [
        { label: "Отмена", role: "cancel", onClick: () => resolve(null) },
        {
          label: okLabel, role: "primary",
          onClick: () => {
            const v = wrap.querySelector("input").value.trim();
            const err = wrap.querySelector(".err");
            if (!v) { err.textContent = "Введите значение"; err.hidden = false; return false; }
            if (validate) {
              const m = validate(v);
              if (m) { err.textContent = m; err.hidden = false; return false; }
            }
            resolve(v);
          },
        },
      ],
      onClose: () => resolve(null),
    });
    const input = wrap.querySelector("input");
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter") { e.preventDefault(); dlg.foot.querySelector(".btn-primary")?.click(); }
    });
    setTimeout(() => { input.select(); }, 70);
  });
}

/* ---------------- context menu ---------------- */
function contextMenu(x, y, items) {
  const host = $("#ctx-host");
  host.innerHTML = "";
  const menu = document.createElement("div");
  menu.className = "ctx";
  for (const it of items) {
    if (it === "-" || it.sep) { const s = document.createElement("div"); s.className = "ctx-sep"; menu.appendChild(s); continue; }
    if (it.label && it.header) { const h = document.createElement("div"); h.className = "ctx-label"; h.textContent = it.label; menu.appendChild(h); continue; }
    const b = document.createElement("button");
    b.className = "ctx-item" + (it.danger ? " danger" : "");
    b.innerHTML = `${it.icon ? icon(it.icon) : '<svg style="opacity:0"></svg>'}<span>${esc(it.label)}</span>`;
    if (it.disabled) b.disabled = true;
    b.onclick = (e) => { e.stopPropagation(); close(); it.onClick && it.onClick(); };
    menu.appendChild(b);
  }
  host.appendChild(menu);
  const r = menu.getBoundingClientRect();
  menu.style.left = Math.min(x, innerWidth - r.width - 8) + "px";
  menu.style.top = Math.min(y, innerHeight - r.height - 8) + "px";
  setTimeout(() => {
    const onDown = (e) => { if (!menu.contains(e.target)) close(); };
    const onKey = (e) => { if (e.key === "Escape") close(); };
    document.addEventListener("mousedown", onDown, { once: true });
    document.addEventListener("keydown", onKey, { once: true });
  }, 0);
  function close() { host.innerHTML = ""; }
  return { close };
}
function bindContextMenu(el, itemsFn) {
  el.addEventListener("contextmenu", (e) => {
    e.preventDefault();
    contextMenu(e.clientX, e.clientY, itemsFn(e));
  });
}

/* ---------------- router ---------------- */
const Pages = {};
const App = {
  route: null,
  params: {},
  info: null,
  settings: {},
  page: null,
  history: [],
};

async function loadPageHTML(route) {
  const res = await fetch(`pages/${route}.html`, { cache: "no-cache" });
  if (!res.ok) throw { code: "Страница не найдена", message: `Не удалось загрузить «${route}»` };
  return res.text();
}

function execScripts(container) {
  const scripts = [...container.querySelectorAll("script")];
  for (const s of scripts) {
    const n = document.createElement("script");
    if (s.src) { n.src = s.src; } else { n.textContent = s.textContent; }
    s.replaceWith(n);
  }
}

async function navigate(route, params = {}, opts = {}) {
  if (!opts.replace && App.route && App.page) {
    const last = App.history[App.history.length - 1];
    if (!last || last.route !== App.route || JSON.stringify(last.params) !== JSON.stringify(App.params)) {
      App.history.push({ route: App.route, params: App.params });
    }
  }
  if (App.history.length > 40) App.history.shift();
  await render(route, params);
}

async function render(route, params) {
  const view = $("#view");
  try {
    if (App.page && App.page.dispose) { try { App.page.dispose(); } catch (e) { console.error(e); } }
  } catch (e) { /* noop */ }
  App.page = null;
  App.route = route;
  App.params = params || {};
  view.scrollTop = 0;

  let html;
  try { html = await loadPageHTML(route); }
  catch (e) {
    view.innerHTML = `<div class="page"><div class="state error">${icon("alert")}<h3>Не удалось открыть страницу</h3>
      <p>${esc(errText(e))}</p><div class="actions"><button class="btn" onclick="App.reload()">Повторить</button></div></div></div>`;
    return;
  }
  view.innerHTML = html;
  execScripts(view);
  const page = Pages[route];
  updateNav(route);
  const titleEl = $("#tb-title");
  if (!page) {
    view.innerHTML = `<div class="page"><div class="state error">${icon("alert")}<h3>Страница не реализована</h3><p>Модуль «${esc(route)}» отсутствует.</p></div></div>`;
    titleEl.textContent = route;
    return;
  }
  titleEl.textContent = page.title || "";
  App.page = page;
  try {
    await page.init({ root: view, params: App.params, navigate, reload: () => render(route, params) });
  } catch (e) {
    console.error(e);
    view.innerHTML = `<div class="page"><div class="state error">${icon("alert")}<h3>Ошибка отрисовки</h3>
      <p>${esc(errText(e))}</p>${errDetail(e) ? `<p class="mono dim" style="font-size:11px">${esc(errDetail(e))}</p>` : ""}
      <div class="actions"><button class="btn btn-primary" onclick="App.reload()">Повторить</button></div></div></div>`;
  }
}

function updateNav(route) {
  const base = route.split("/")[0];
  $$(".nav-item").forEach((n) => n.classList.toggle("active", n.dataset.route === base));
}
App.reload = () => render(App.route, App.params);
App.navigate = navigate;

function goBack() {
  const prev = App.history.pop();
  if (prev) render(prev.route, prev.params);
}

/* ---------------- window controls ---------------- */
function windowAction(action) { call("system.window", { action }); }

function setupWindow() {
  $("#tb-min").onclick = () => windowAction("minimize");
  $("#tb-close").onclick = () => windowAction("close");
  const maxBtn = $("#tb-max");
  maxBtn.onclick = () => windowAction("toggleMax");
  $$(".tb-drag").forEach((d) => {
    d.addEventListener("mousedown", (e) => {
      if (e.target.closest("button")) return;
      if (e.button === 0) windowAction(e.detail > 1 ? "toggleMax" : "drag");
    });
  });
  on("window.maximized", (v) => document.documentElement.dataset.windowMax = v ? "1" : "0");
}

/* ---------------- ежедневная награда ---------------- */
async function maybeShowDaily() {
  var st;
  try { st = await api("auth.dailyState"); } catch (e) { return; }
  if (!st || st.claimedToday) return;
  openDaily(st);
}

function openDaily(st) {
  if (document.getElementById("daily-host")) return;
  var host = document.createElement("div");
  host.id = "daily-host";
  document.body.appendChild(host);
  var claimed = null;

  function escClose(e) { if (e.key === "Escape") close(); }
  function close() {
    document.removeEventListener("keydown", escClose);
    if (host.parentNode) host.parentNode.removeChild(host);
  }

  function render() {
    var doneUpTo = 0;
    var target = st.nextSlot || 1;
    if (claimed) { doneUpTo = claimed.slot; target = 0; }
    else if (st.continues) { doneUpTo = target - 1; }

    var tiles = "";
    for (var day = 1; day <= 7; day++) {
      var cls = day <= doneUpTo ? "done" : (day === target ? "now" : "");
      tiles += '<div class="dly-t ' + cls + '"><div class="d">День ' + day + '</div>' +
        '<div class="n"><svg><use href="#i-coin"/></svg> +' + st.rewards[day - 1] + "</div></div>";
    }

    var series = claimed ? claimed.streak : (st.continues ? st.streak : 0);
    var best = Math.max(st.bestStreak || 0, series || 0);
    var now = new Date();
    var hours = Math.max(1, Math.round(
      (Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + 1) - now.getTime()) / 3600000));

    var bottom;
    if (claimed) {
      bottom = '<div class="dly-got"><svg><use href="#i-coin"/></svg> Получено +' + claimed.claimed + " · день " + claimed.slot + "</div>" +
        '<div class="dly-foot">Следующая награда +' + st.rewards[claimed.slot % 7] + " — примерно через " + hours + " ч</div>";
    } else {
      bottom = '<div class="dly-claim"><button class="btn btn-primary" id="dly-go">Забрать +' + (st.nextReward || 3) + "</button>" +
        '<span class="dly-foot">Сегодня день ' + target + "</span></div>";
    }

    host.innerHTML =
      '<div class="dly" role="dialog" aria-modal="true">' +
        '<div class="dly-head">' +
          '<div class="dly-ic"><svg><use href="#i-pixgift"/></svg></div>' +
          '<div><div class="dly-title">Ежедневная награда</div>' +
          '<div class="dly-sub">Награда растёт с каждым днём подряд — пропустишь день, начнёшь заново.</div></div>' +
          '<button class="dly-x" id="dly-x" title="Закрыть"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M6 6l12 12M18 6 6 18"/></svg></button>' +
        "</div>" +
        '<div class="dly-meta"><span class="dly-fire">Серия: ' + series + " дн.</span>" +
          '<span class="dly-rec">Рекорд: ' + best + " дн.</span></div>" +
        '<div class="dly-grid">' + tiles + "</div>" +
        bottom +
      "</div>";

    host.querySelector("#dly-x").onclick = close;
    var go = host.querySelector("#dly-go");
    if (go) go.onclick = async function () {
      go.disabled = true;
      go.textContent = "Забираю…";
      try {
        var r = await api("auth.daily");
        claimed = { slot: r.slot, claimed: r.claimed, streak: r.streak };
        notifySuccess("Награда получена", "+" + r.claimed + " NullCoin · баланс " + r.coins);
        render();
      } catch (e) {
        notifyError("Не удалось получить награду", ui.errText(e));
        go.disabled = false;
        go.textContent = "Забрать";
      }
    };
    host.onclick = function (e) { if (e.target === host) close(); };
  }

  render();
  document.addEventListener("keydown", escClose);
}

/* ---------------- media pill (SMTC: Spotify и другие плееры) ---------------- */
const MediaLogos = {
  Spotify: { svg: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="12" fill="#1DB954"/><path fill="none" stroke="#0a0a0a" stroke-width="1.9" stroke-linecap="round" d="M6.3 9.3c3.7-1.1 8.7-.7 11.9 1.3M7 12.5c3-.9 7.1-.5 9.8 1.2M7.7 15.5c2.5-.7 5.7-.4 7.9 1"/></svg>' },
  Chrome: { color: "#4285F4" },
  Firefox: { color: "#FF7139" },
  Edge: { color: "#0C8CE9" },
  VLC: { color: "#FF8800" },
};

function mediaLogoHtml(app) {
  const m = MediaLogos[app];
  if (m && m.svg) return m.svg;
  const letter = ((app || "?").trim().charAt(0) || "?").toUpperCase();
  return `<span class="tbp-ic" style="background:${(m && m.color) || "var(--accent)"}">${esc(letter)}</span>`;
}

function setupMediaPill() {
  const box = $("#tb-media");
  if (!box) return;
  const pop = $("#tb-pop"), main = $("#tbm-main"), meta = $("#tbm-meta"), logo = $("#tbm-logo"), eq = $("#tbm-eq");
  const btnPlay = $("#tbm-play"), btnNext = $("#tbm-next"), usePlay = $("#tbm-play-use");
  const pArt = $("#tbp-art"), pArtFb = $("#tbp-artfb"), pTitle = $("#tbp-title"), pArtist = $("#tbp-artist"),
        pAlbum = $("#tbp-album"), pApp = $("#tbp-app"), pCur = $("#tbp-cur"), pDur = $("#tbp-dur"),
        pBar = $("#tbp-bar"), pFill = $("#tbp-fill"), pPlay = $("#tbp-play"), pUsePlay = $("#tbp-play-use"),
        pPrev = $("#tbp-prev"), pNext = $("#tbp-next");
  let st = { active: false, playing: false, positionMs: 0, durationMs: 0 };
  let anchor = { pos: 0, at: Date.now() };

  const fmtT = (ms) => {
    if (!ms || ms < 0 || !isFinite(ms)) return "–:–";
    const s = Math.floor(ms / 1000);
    return Math.floor(s / 60) + ":" + String(s % 60).padStart(2, "0");
  };

  function applyState(n) {
    st = Object.assign({ active: false, playing: false, positionMs: 0, durationMs: 0 }, n);
    anchor = { pos: st.positionMs || 0, at: Date.now() };
    box.hidden = !st.active;
    if (!st.active) { pop.hidden = true; return; }

    logo.innerHTML = mediaLogoHtml(st.app);
    meta.innerHTML = st.title
      ? `<b>${esc(st.title)}</b>${st.artist ? ` — <i>${esc(st.artist)}</i>` : ""}`
      : `<i>${esc(st.app || "Сейчас играет")}</i>`;
    meta.title = [st.title, st.artist, st.album].filter(Boolean).join(" · ") || st.app || "";
    eq.classList.toggle("paused", !st.playing);

    const ic = st.playing ? "#i-pause" : "#i-play";
    usePlay.setAttribute("href", ic);
    pUsePlay.setAttribute("href", ic);
    btnPlay.title = pPlay.title = st.playing ? "Пауза" : "Играть";
    btnNext.hidden = !st.canNext;
    pNext.hidden = !st.canNext;
    pPrev.hidden = !st.canPrev;

    if (st.art) { if (pArt.getAttribute("src") !== st.art) pArt.src = st.art; pArt.hidden = false; pArtFb.hidden = true; }
    else { pArt.hidden = true; pArt.removeAttribute("src"); pArtFb.hidden = false; }
    pTitle.textContent = st.title || "";
    pArtist.textContent = st.artist || "";
    pAlbum.textContent = st.album || "";
    pApp.innerHTML = mediaLogoHtml(st.app) + `<span>${esc(st.app || "")}</span>`;
    pApp.hidden = !st.app;
    pDur.textContent = fmtT(st.durationMs);
    tickBar();
  }

  function tickBar() {
    if (!st.active) return;
    let pos = anchor.pos;
    if (st.playing) pos += Math.min(Date.now() - anchor.at, 9000);
    if (st.durationMs > 0) pos = Math.min(pos, st.durationMs);
    pCur.textContent = fmtT(pos);
    pFill.style.width = st.durationMs > 0 ? (Math.min(100, pos / st.durationMs * 100)).toFixed(2) + "%" : "0%";
  }
  setInterval(tickBar, 500);

  async function ctrl(action, extra) {
    try { await api("media.control", Object.assign({ action }, extra || {})); }
    catch (e) { console.debug("media.control:", (e && e.message) || e); }
    try { applyState(await api("media.state")); } catch (e) { /* старый хост */ }
  }

  btnPlay.onclick = (e) => { e.stopPropagation(); ctrl("playPause"); };
  btnNext.onclick = (e) => { e.stopPropagation(); ctrl("next"); };
  pPlay.onclick = () => ctrl("playPause");
  pNext.onclick = () => ctrl("next");
  pPrev.onclick = () => ctrl("prev");
  main.onclick = () => { pop.hidden = !pop.hidden; if (!pop.hidden) tickBar(); };
  pBar.onclick = (e) => {
    if (st.durationMs <= 0) return;
    const r = pBar.getBoundingClientRect();
    const pct = Math.min(1, Math.max(0, (e.clientX - r.left) / r.width));
    ctrl("seek", { positionMs: Math.round(pct * st.durationMs) });
  };
  document.addEventListener("mousedown", (e) => {
    if (!pop.hidden && !box.contains(e.target)) pop.hidden = true;
  });
  document.addEventListener("keydown", (e) => { if (e.key === "Escape") pop.hidden = true; });

  on("media.changed", applyState);
  api("media.state").then(applyState).catch(() => { box.hidden = true; });
  /* страховка: даже если событие потерялось, состояние свежается само раз в 5 с */
  setInterval(() => { api("media.state").then(applyState).catch(() => {}); }, 5000);
}

/* ---------------- hotkeys ---------------- */
const DefaultHotkeys = {
  search: "Ctrl+K",
  refresh: "Ctrl+R",
  newInstance: "Ctrl+N",
  settings: "Ctrl+,",
  logs: "Ctrl+Shift+L",
  play: "Ctrl+Enter",
};

function normalizeCombo(e) {
  const parts = [];
  if (e.ctrlKey) parts.push("Ctrl");
  if (e.altKey) parts.push("Alt");
  if (e.shiftKey) parts.push("Shift");
  if (e.metaKey) parts.push("Win");
  let k = e.key;
  if (["Control", "Shift", "Alt", "Meta"].includes(k)) return null;
  if (k === " ") k = "Space";
  else if (k.length === 1) k = k.toUpperCase();
  else k = k.replace("Arrow", "");
  parts.push(k);
  return parts.join("+");
}

function comboMatches(e, combo) {
  if (!combo) return false;
  const cur = normalizeCombo(e);
  return cur && cur.toLowerCase() === combo.toLowerCase();
}

function handleHotkey(e) {
  const hk = { ...DefaultHotkeys, ...(App.settings.hotkeys || {}) };
  if (comboMatches(e, hk.search)) { e.preventDefault(); openPalette(); return true; }
  if (comboMatches(e, hk.settings)) { e.preventDefault(); navigate("settings"); return true; }
  if (comboMatches(e, hk.logs)) { e.preventDefault(); navigate("settings", { section: "logs" }); return true; }
  if (comboMatches(e, hk.newInstance)) {
    e.preventDefault();
    if (App.page && App.page.createInstance) App.page.createInstance(); else navigate("instances", { create: 1 });
    return true;
  }
  if (comboMatches(e, hk.refresh)) { e.preventDefault(); App.reload(); return true; }
  if (comboMatches(e, hk.play)) {
    const btn = document.querySelector("[data-play-quick]");
    if (btn) { e.preventDefault(); btn.click(); return true; }
  }
  return false;
}

/* ---------------- command palette ---------------- */
function openPalette() {
  const p = $("#palette");
  p.hidden = false;
  const inp = $("#palette-q");
  inp.value = "";
  $("#palette-results").innerHTML = `<div class="palette-empty">Начните вводить запрос…</div>`;
  setTimeout(() => inp.focus(), 30);
}
function closePalette() { $("#palette").hidden = true; }

async function paletteSearch(q) {
  const box = $("#palette-results");
  if (!q.trim()) { box.innerHTML = `<div class="palette-empty">Начните вводить запрос…</div>`; return; }
  box.innerHTML = `<div class="palette-empty"><span class="spinner" style="display:inline-block"></span></div>`;
  let res;
  try { res = await api("search.global", { query: q.trim() }); }
  catch (e) { box.innerHTML = `<div class="palette-empty">${esc(errText(e))}</div>`; return; }
  const groups = [
    ["Сборки", res.instances, "grid", (i) => ({ title: i.name, sub: `Minecraft ${i.mcVersion} · ${i.loader || "vanilla"}`, go: () => navigate("instance", { id: i.id }) })],
    ["Миры", res.worlds, "globe", (i) => ({ title: i.name, sub: `${i.instanceName} · ${fmtBytes(i.sizeBytes)}`, go: () => navigate("instance", { id: i.instanceId, tab: "worlds" }) })],
    ["Версии Minecraft", res.versions, "cube", (i) => ({ title: i.id, sub: i.type, go: () => navigate("instances", { version: i.id }) })],
    ["Настройки", res.settings, "sliders", (i) => ({ title: i.title, sub: i.section, go: () => navigate("settings", { section: i.sectionId || "", q: i.key }) })],
    ["Проекты Modrinth", res.modrinth, "compass", (i) => ({ title: i.title, sub: `${i.projectType} · ${fmtNum(i.downloads)} загрузок`, go: () => navigate("discover", { project: i.projectId || i.slug }) })],
  ];
  let html = "", count = 0, idx = 0;
  const flat = [];
  for (const [label, items, ic, map] of groups) {
    if (!items || !items.length) continue;
    html += `<div class="palette-group">${label}</div>`;
    for (const raw of items.slice(0, 6)) {
      const it = map(raw);
      flat.push({ i: idx++, go: it.go });
      html += `<button class="palette-item" data-idx="${idx - 1}">${icon(ic)}<span class="grow"><span class="p-title">${esc(it.title)}</span><span class="p-sub">${esc(it.sub || "")}</span></span>${icon("chevron")}</button>`;
      count++;
    }
  }
  if (!count) { box.innerHTML = `<div class="palette-empty">Ничего не найдено по запросу «${esc(q)}»</div>`; return; }
  box.innerHTML = html;
  box.querySelectorAll(".palette-item").forEach((b) => {
    b.onclick = () => { closePalette(); flat[+b.dataset.idx].go(); };
  });
  box.querySelector(".palette-item")?.classList.add("sel");
  box._flat = flat;
}

function setupPalette() {
  const inp = $("#palette-q");
  inp.addEventListener("input", debounce(() => paletteSearch(inp.value), 260));
  inp.addEventListener("keydown", (e) => {
    const items = $$("#palette-results .palette-item");
    if (!items.length) return;
    let cur = items.findIndex((i) => i.classList.contains("sel"));
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      items[cur]?.classList.remove("sel");
      cur = e.key === "ArrowDown" ? (cur + 1) % items.length : (cur - 1 + items.length) % items.length;
      items[cur].classList.add("sel");
      items[cur].scrollIntoView({ block: "nearest" });
    } else if (e.key === "Enter") {
      e.preventDefault();
      (items[cur >= 0 ? cur : 0]).click();
    }
  });
  $("#palette").addEventListener("mousedown", (e) => { if (e.target.dataset.close) closePalette(); });
}

/* ---------------- settings / appearance ---------------- */
function applySettings(s) {
  App.settings = s || {};
  if (window.I18N) I18N.setLang((s && s.language) || "ru");
  const r = document.documentElement;
  const theme = s.theme || "light";
  const resolved = theme === "system"
    ? (window.matchMedia && matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark")
    : theme;
  r.dataset.theme = resolved;
  r.dataset.density = s.density || "normal";
  r.dataset.scale = String(s.uiScale || 100);
  r.dataset.motion = s.animations === false ? "off" : "on";
  r.dataset.rounded = s.rounding || "normal";
  r.dataset.contrast = s.highContrast ? "high" : "normal";
  document.body.style.opacity = s.opacity && s.opacity < 100 ? String(s.opacity / 100) : "";
  const accent = s.accent || "#3ecf8e";
  r.style.setProperty("--accent", accent);
  r.style.setProperty("--accent-hover", shade(accent, 12));
  r.style.setProperty("--accent-down", shade(accent, -14));
  r.style.setProperty("--accent-soft", hexA(accent, 0.14));
  r.style.setProperty("--accent-line", hexA(accent, 0.4));
  if (s.fontScale) r.style.setProperty("--fs", (13.5 * s.fontScale) + "px");
}

function hexToRgb(h) {
  h = (h || "#3ecf8e").replace("#", "");
  if (h.length === 3) h = h.split("").map((c) => c + c).join("");
  const n = parseInt(h, 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}
function shade(hex, pct) {
  const [r, g, b] = hexToRgb(hex);
  const f = (v) => Math.round(clamp(v + (pct / 100) * 255, 0, 255));
  return `rgb(${f(r)},${f(g)},${f(b)})`;
}
function hexA(hex, a) { const [r, g, b] = hexToRgb(hex); return `rgba(${r},${g},${b},${a})`; }

/* ---------------- drag & drop ---------------- */
let _dropHandler = null;
function onFilesDropped(fn) { _dropHandler = fn; }

function setupDrop() {
  const overlay = $("#drop-overlay");
  let depth = 0;
  const pathsFrom = (e) => {
    const items = e.dataTransfer?.items ? [...e.dataTransfer.items] : [];
    const files = e.dataTransfer?.files ? [...e.dataTransfer.files] : [];
    // WebView2 передаёт пути через getPathForDraggedFile (host), fallback — имя файла
    return files.map((f) => f.name);
  };
  window.addEventListener("dragenter", (e) => {
    e.preventDefault();
    if (e.dataTransfer && [...(e.dataTransfer.items || [])].some((i) => i.kind === "file")) {
      depth++; overlay.hidden = false;
    }
  });
  window.addEventListener("dragover", (e) => e.preventDefault());
  window.addEventListener("dragleave", (e) => { depth = Math.max(0, depth - 1); if (!depth) overlay.hidden = true; });
  window.addEventListener("drop", (e) => {
    e.preventDefault();
    depth = 0; overlay.hidden = true;
    const files = pathsFrom(e);
    if (!files.length) return;
    const ext = (files[0].split(".").pop() || "").toLowerCase();
    const kind = ext === "mrpack" || ext === "zip" || ext === "jar" ? ext : "file";
    if (_dropHandler) _dropHandler(files, kind, e);
    else notifyInfo("Файл получен", `Перетащено: ${files.join(", ")}. Откройте сборку, чтобы установить содержимое.`);
  });
}

/* ---------------- глобальная полоса загрузок ----------------
   Все скачивания (модпак, моды, ресурсы, шейдеры, файлы игры при запуске)
   идут через DownloadService и шлют downloads.progress — поэтому полоса
   внизу окна живёт на всех страницах без привязки к конкретной. */
function setupDownloadsBar() {
  const bar = document.getElementById("dlbar");
  if (!bar) return;
  const prog = bar.querySelector("#dlbar-progress");
  const fill = prog && prog.querySelector("i");
  const textEl = document.getElementById("dlbar-text");
  const pctEl = document.getElementById("dlbar-pct");
  const badge = document.getElementById("nav-downloads-badge");
  let hideTimer = null;

  bar.addEventListener("click", () => navigate("downloads"));

  function update(list) {
    if (!Array.isArray(list)) return;
    const act = list.filter((d) => d && (d.state === "running" || d.state === "queued"));
    const paused = list.some((d) => d && d.state === "paused");
    const n = act.length;

    if (badge) {
      badge.textContent = n > 99 ? "99+" : String(n);
      badge.hidden = n === 0;
    }
    if (!n) {
      /* между файлами модпака бывает пауза в очереди — прячем с задержкой, чтобы не мигало */
      if (!paused && !hideTimer) hideTimer = setTimeout(() => {
        bar.hidden = true; hideTimer = null;
        if (fill) fill.style.width = "0%";
        if (prog) prog.classList.remove("indeterminate");
        textEl.textContent = ""; pctEl.textContent = "";
      }, 1400);
      return;
    }
    if (hideTimer) { clearTimeout(hideTimer); hideTimer = null; }

    let got = 0, total = 0, speed = 0;
    for (const d of act) {
      got += d.receivedBytes || 0;
      total += d.totalBytes || 0;
      if (d.state === "running") speed += d.speedBps || 0;
    }
    const cur = act.find((d) => d.state === "running") || act[0];
    const name = (cur && (cur.title || cur.url)) || "файлов";
    const meta = [fmtBytes(got) + (total > 0 ? " / " + fmtBytes(total) : "")];
    if (speed > 0) meta.push(fmtSpeed(speed));
    if (speed > 0 && total > got) meta.push("осталось ~" + fmtEta((total - got) / speed));
    textEl.textContent = (n > 1 ? `Скачивание: ${name} · ещё ${n - 1}` : `Скачивание: ${name}`) + " · " + meta.join(" · ");

    if (total > 0 && fill) {
      prog.classList.remove("indeterminate");
      const pct = Math.max(0, Math.min(100, (got / total) * 100));
      fill.style.width = pct.toFixed(2) + "%";
      pctEl.textContent = (pct >= 10 ? Math.round(pct) : pct.toFixed(1)) + "%";
    } else if (prog) {
      prog.classList.add("indeterminate");
      pctEl.textContent = "";
    }
    bar.hidden = false;
  }

  on("downloads.progress", update);
  /* при старте UI сверяемся с очередью: если что-то качается ещё до перезагрузки страницы — покажем сразу */
  api("downloads.list").then((r) => { if (r && Array.isArray(r.items)) update(r.items); }).catch(() => {});
}

/* ---------------- boot ---------------- */
async function boot() {
  setupWindow();
  setupPalette();
  setupDrop();
  setupDownloadsBar();
  setupMediaPill();

  document.addEventListener("keydown", (e) => {
    if (!$("#palette").hidden && e.key === "Escape") { closePalette(); return; }
    if (_dialogStack.length) return;
    if (e.target.matches("input,textarea,select") && !e.ctrlKey && !e.metaKey) return;
    handleHotkey(e);
  });

  $$(".nav-item").forEach((n) => n.addEventListener("click", () => navigate(n.dataset.route)));
  /* чип аккаунта слева снизу: меню переключения/добавления аккаунта */
  $("#account-chip").onclick = async (ev) => {
    const r = ev.currentTarget.getBoundingClientRect();
    let list = [];
    try { list = (await api("accounts.list")) || []; } catch (e) { list = []; }
    const current = (App.settings && App.settings.accountUuid && list.find((a) => a.uuid === App.settings.accountUuid)) ||
      list.find((a) => a.default) || list[0];
    const items = [{ label: "Аккаунты", header: true }];
    if (!list.length) items.push({ label: "Аккаунтов нет — войдите, чтобы играть", disabled: true });
    list.forEach((a) => {
      items.push({
        label: a.name + (a.default ? " · основной" : ""),
        icon: current && a.uuid === current.uuid ? "check" : "user",
        onClick: async () => {
          if (current && a.uuid === current.uuid) return;
          try {
            await api("accounts.setDefault", { uuid: a.uuid });
            await api("settings.set", { accountUuid: a.uuid });
            updateAccountChip();
            notify("success", "Аккаунт переключён", a.name);
          } catch (e) { notify("error", "Не удалось переключить аккаунт", errText(e)); }
        },
      });
    });
    items.push("-");
    items.push({ label: App.auth && App.auth.authed ? "NullLauncher: " + App.auth.name : "Войти в NullLauncher", icon: "user", onClick: () => navigate("profiles") });
    items.push("-");
    items.push({ label: "Войти через Microsoft", icon: "user", onClick: () => AccUI.openMicrosoft({ onDone: updateAccountChip }) });
    items.push({ label: "Офлайн-аккаунт", icon: "user", onClick: () => AccUI.openOffline({ onDone: updateAccountChip }) });
    items.push({
      label: "Гость", icon: "user",
      onClick: async () => {
        try {
          await api("accounts.addGuest", { name: "Player" });
          updateAccountChip();
          notify("success", "Гостевой профиль добавлен", "");
        } catch (e) { notify("error", "Не удалось создать гостевой профиль", errText(e)); }
      },
    });
    contextMenu(Math.max(8, r.left), Math.max(8, r.top - 300), items);
  };
  const _gh = $("#link-github");
  if (_gh) _gh.onclick = () => call("system.openUrl", { url: "https://github.com/rusty-prdc/NullLauncher" });
  const _site = $("#link-site");
  if (_site) _site.onclick = () => call("system.openUrl", { url: "https://nulllaucnher.xprodc.site/" });

  App.auth = { authed: false };
  on("auth.changed", (s) => { App.auth = s || { authed: false }; });
  api("auth.state").then((s) => { App.auth = s || { authed: false }; emit("auth.changed", App.auth); if (s && s.authed && s.dailyAvailable) maybeShowDaily(); }).catch(() => {});
  on("notify", (d) => notify(d.type || "info", d.title, d.message, d.actions));
  on("app.updateAvailable", (d) => {
    notify("info", "Доступно обновление NullLauncher", `${d.current} → ${d.latest}`, [
      { label: "Обновить", primary: true, onClick: () => navigate("settings", { section: "about" }) },
      { label: "Позже" },
    ], 15000);
  });
  on("settings.changed", (s) => applySettings(s));
  on("app.deepLink", (d) => {
    if (!d || !d.ip) return;
    var addr = d.ip + (d.port ? ":" + d.port : "");
    confirmDialog({
      title: "Подключиться к серверу",
      message: addr + "\n Лаунчер откроет сборку и подключит вас к этому адресу.",
      okLabel: "Играть",
    }).then(function (ok) {
      if (!ok) return;
      api("servers.quickJoin", { ip: d.ip, port: d.port || null })
        .then(function () { notifySuccess("Подключение", addr); })
        .catch(function (e) { notifyError("Не удалось подключиться", (e && e.message) || String(e)); });
    });
  });
  on("accounts.changed", () => { updateAccountChip(); });
  on("instance.changed", () => {
    /* список сборок и главная зависят от состояния инстансов; страница сборки сама себя обновляет после действий */
    if (App.route === "instances" || App.route === "home") App.reload();
  });
  on("crash.detected", (d) => {
    /* автоанализ сбоя из C#: показываем причину и подсказки, можно открыть отчёт */
    const cause = d.cause || "Игра завершилась с ошибкой";
    const sugg = (d.suggestions || []).slice(0, 5);
    const msg = (d.summary ? d.summary + " " : "") +
      (sugg.length ? "Подсказка: " + sugg[0] : "");
    notify("error", "Сбой в «" + (d.instanceName || "сборке") + "»", cause + (msg ? " — " + msg : ""), [
      { label: "Как исправить", primary: true, onClick: () => {
          confirmDialog({
            title: "Как исправить сбой",
            messageHtml: true,
            message: (d.summary ? "<p style='margin:0 0 10px;line-height:1.5'>" + esc(d.summary) + "</p>" : "") +
              (sugg.length ? "<ul style='margin:0;padding-left:18px;line-height:1.7'>" + sugg.map((x) => "<li>" + esc(x) + "</li>").join("") + "</ul>" : ""),
            okLabel: d.path ? "Открыть отчёт" : "Понятно",
            cancelLabel: "Закрыть",
          }).then((ok) => { if (ok && d.path) call("system.openPath", { path: d.path }); });
        } },
      { label: "Открыть отчёт", onClick: () => { if (d.path) call("system.openPath", { path: d.path }); } },
    ], 16000);
  });

  try {
    App.info = await api("system.info");
    $("#app-version").textContent = App.info.appVersion;
  } catch (e) {
    console.error(e);
  }
  try {
    const s = await api("settings.get");
    applySettings(s);
  } catch (e) { /* noop */ }

  updateAccountChip();

  if (App.info && App.info.isFirstRun) {
    await render("welcome", {});
    return;
  }
  const start = (App.settings.startPage || "home");
  await render(start, {});
  if (App.info && App.info.updateAvailable) emit("app.updateAvailable", App.info.update);
}

async function updateAccountChip() {
  try {
    const list = await api("accounts.list");
    const acc = (App.settings && App.settings.accountUuid && list.find((a) => a.uuid === App.settings.accountUuid)) ||
                list.find((a) => a.default) || list[0];
    if (!acc) {
      $("#account-name").textContent = "Гость";
      $("#account-sub").textContent = "Войти в Microsoft";
      $("#account-avatar").textContent = "?";
      $("#account-avatar").style.backgroundImage = "";
      return;
    }
    $("#account-name").textContent = acc.name;
    $("#account-sub").textContent = "Microsoft";
    const av = $("#account-avatar");
    if (acc.skinUrl) { av.textContent = ""; applySkinFace(av, acc.skinUrl); }
    else { av.style.backgroundImage = ""; av.textContent = initials(acc.name); }
  } catch (e) { /* noop */ }
}

/* ---------- лицо скина вместо всей текстуры ----------
   Текстура скина: лицо (front head) — пиксели (8,8)–(16,16).
   При background-size 800% (8× размера аватара) это ровно размер аватара.
   64×32 (легаси-текстуры) имеет другую пропорцию высоты — подставляем 400%/33.3333%. */
const _skinFaceH = Object.create(null);
function applySkinFace(el, url) {
  if (!el || !url) return;
  el.style.backgroundImage = `url("${url}")`;
  el.style.backgroundRepeat = "no-repeat";
  el.style.backgroundSize = "800% 800%";
  el.style.backgroundPosition = "14.2857% 14.2857%";
  el.style.imageRendering = "pixelated";
  const h = _skinFaceH[url];
  if (h !== undefined) { fixSkinFacePos(el, h); return; }
  const img = new Image();
  img.onload = function () {
    _skinFaceH[url] = img.naturalHeight || 64;
    fixSkinFacePos(el, _skinFaceH[url]);
  };
  img.src = url;
}
function fixSkinFacePos(el, h) {
  if (h && h <= 32) {
    el.style.backgroundSize = "800% 400%";
    el.style.backgroundPosition = "14.2857% 33.3333%";
  } else {
    el.style.backgroundSize = "800% 800%";
    el.style.backgroundPosition = "14.2857% 14.2857%";
  }
}

/* Обложка сборки: instances.image (cover → icon) → <img> внутри el (класс has-img). */
function applyCover(el, id) {
  if (!el || !id) return;
  api("instances.image", { id: id, kind: "cover" })
    .then(function (url) {
      if (!url) return api("instances.image", { id: id, kind: "icon" });
      return url;
    })
    .then(function (url) {
      if (!url || !el.isConnected) return;
      el.classList.add("has-img");
      el.innerHTML = '<img src="' + url + '" alt="" loading="lazy">';
    })
    .catch(function () { /* без обложки — остаётся буква */ });
}

/* экспорт для страниц */
window.api = api;
window.on = on;
window.call = call;
window.App = App;
window.Pages = Pages;
window.navigate = navigate;
window.ui = {
  $, $$, esc, icon, uid, debounce, clamp,
  fmtBytes, fmtNum, fmtDate, fmtRelative, fmtDuration, fmtSpeed, fmtEta, initials,
  notify, notifySuccess, notifyError, notifyWarn, notifyInfo, copyText,
  openDialog, confirmDialog, promptDialog, contextMenu, bindContextMenu,
  onFilesDropped, errText, errDetail, goBack, updateAccountChip, applySettings,
  openPalette, DefaultHotkeys, applySkinFace, applyCover,
};

let _booted = false;
async function bootOnce() { if (_booted) return; _booted = true; await boot(); }
document.addEventListener("DOMContentLoaded", bootOnce);
if (document.readyState !== "loading") bootOnce();
