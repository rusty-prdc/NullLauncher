/* Экраны входа в аккаунт (общие для главной и мастера): оффлайн и Microsoft. */

(function () {
  const esc = (s) => ui.esc(s);

  const MS_LOGO =
    '<svg class="acc-ms" viewBox="0 0 23 23" aria-hidden="true">' +
    '<rect x="1" y="1" width="10" height="10" fill="#f25022"/>' +
    '<rect x="12" y="1" width="10" height="10" fill="#7fba00"/>' +
    '<rect x="1" y="12" width="10" height="10" fill="#00a4ef"/>' +
    '<rect x="12" y="12" width="10" height="10" fill="#ffb900"/></svg>';

  let poll = null;
  function stopPoll() { if (poll) { clearInterval(poll); poll = null; } }

  function host() {
    let h = document.getElementById("acc-host");
    if (!h) {
      h = document.createElement("div");
      h.id = "acc-host";
      document.body.appendChild(h);
    }
    return h;
  }

  function shell(opts) {
    /* opts: { title, html, onClose } — полноэкранный светлый экран */
    const h = host();
    h.innerHTML =
      '<div class="acc-card">' +
        '<button class="acc-close" data-act="close" title="Закрыть">' +
          '<svg><use href="#i-x"/></svg> Закрыть</button>' +
        opts.html +
      "</div>";
    h.classList.add("on");
    const close = () => { stopPoll(); h.classList.remove("on"); h.innerHTML = ""; if (opts.onClose) opts.onClose(); };
    h.querySelector('[data-act="close"]').onclick = close;
    const escH = (e) => { if (e.key === "Escape") { e.stopPropagation(); close(); } };
    document.addEventListener("keydown", escH, true);
    h._escH = escH;
    return { el: h, close };
  }

  function reopen(scr, build) { scr.close(); build(); }

  /* ---------------- оффлайн ---------------- */
  function openOffline(opts) {
    opts = opts || {};
    const scr = shell({
      html:
        '<h1 class="acc-h1">Играть без аккаунта</h1>' +
        '<p class="acc-p">Только ник: без скинов, плашек и друзей. Подойдёт для любых серверов, кроме лицензионных.</p>' +
        '<div class="acc-form">' +
          '<label class="acc-label" for="acc-nick">Ник</label>' +
          '<input class="acc-input" id="acc-nick" type="text" placeholder="Steve" maxlength="16" autocomplete="off" spellcheck="false" />' +
          '<div class="acc-err" id="acc-err"></div>' +
          '<button class="acc-go" id="acc-go">Продолжить</button>' +
        "</div>" +
        '<button class="acc-back" data-act="back">Назад ко входу</button>',
      onClose: opts.onClose,
    });

    const input = scr.el.querySelector("#acc-nick");
    const err = scr.el.querySelector("#acc-err");
    const go = scr.el.querySelector("#acc-go");
    input.focus();

    const submit = async () => {
      const name = (input.value || "").trim();
      if (!/^[A-Za-z0-9_]{3,16}$/.test(name)) {
        err.textContent = "От 3 до 16 символов: латиница, цифры и «_»";
        input.focus();
        return;
      }
      err.textContent = "";
      go.classList.add("loading");
      try {
        const acc = await api("accounts.addOffline", { name });
        scr.close();
        if (opts.onDone) opts.onDone(acc);
      } catch (e) {
        go.classList.remove("loading");
        err.textContent = ui.errText(e);
      }
    };

    go.onclick = submit;
    input.onkeydown = (e) => { if (e.key === "Enter") submit(); };
    input.oninput = () => { err.textContent = ""; };
    scr.el.querySelector('[data-act="back"]').onclick = () => {
      scr.close();
      if (opts.onBack) opts.onBack();
    };
    return scr;
  }

  /* ---------------- Microsoft ---------------- */
  function openMicrosoft(opts) {
    opts = opts || {};

    const build = () => {
      const scr = shell({
        html:
          '<div class="acc-title">' + MS_LOGO + '<h1 class="acc-h1">Вход через Microsoft</h1></div>' +
          '<p class="acc-p">Введите код на странице Microsoft — аккаунт добавится сам</p>' +
          '<div class="acc-cells" id="acc-cells" title="Нажмите, чтобы скопировать код"></div>' +
          '<p class="acc-p acc-note" id="acc-note">Запрашиваем код…</p>' +
          '<div class="acc-form">' +
            '<button class="acc-go" id="acc-go" disabled>Открыть страницу входа</button>' +
            '<div class="acc-err" id="acc-err"></div>' +
            '<div class="acc-hint" id="acc-status">Ожидание подтверждения…</div>' +
            '<div class="progress indeterminate" id="acc-prog"><i></i></div>' +
          "</div>" +
          '<button class="acc-back" data-act="back">Назад ко входу</button>',
        onClose: opts.onClose,
      });

      let flow = null;
      const cells = scr.el.querySelector("#acc-cells");
      const note = scr.el.querySelector("#acc-note");
      const go = scr.el.querySelector("#acc-go");
      const status = scr.el.querySelector("#acc-status");
      const prog = scr.el.querySelector("#acc-prog");

      const drawCode = (code) => {
        cells.innerHTML = code.split("").map((c) => "<span>" + esc(c) + "</span>").join("");
        note.innerHTML =
          "Открой <b data-act=\"copy-url\" title=\"Скопировать ссылку\">" + esc(flow.verificationUrl.replace(/^https?:\/\//, "")) +
          "</b>, введи код <b data-act=\"copy-code\" title=\"Скопировать код\">" + esc(code) +
          "</b> и войди в аккаунт Microsoft. Мы ждём и подхватим аккаунт автоматически.";
        note.querySelector('[data-act="copy-url"]').onclick = () => ui.copyText(flow.verificationUrl, "Ссылка на вход скопирована");
        note.querySelector('[data-act="copy-code"]').onclick = () => ui.copyText(code, "Код входа скопирован");
        cells.onclick = () => ui.copyText(code, "Код входа скопирован");
      };

      const fail = (msg) => {
        stopPoll();
        status.textContent = msg;
        prog.hidden = true;
        go.classList.remove("loading");
      };

      const startPoll = () => {
        stopPoll();
        poll = setInterval(async () => {
          try {
            const r = await api("accounts.pollMicrosoft", { uuid: flow.deviceCode });
            if (!poll) return;
            if (r && r.status === "done") {
              stopPoll();
              scr.close();
              if (opts.onDone) opts.onDone(r.account);
            } else if (r && r.status === "expired") {
              fail("Код истёк — запросите новый");
              go.textContent = "Получить новый код";
              go.onclick = () => reopen(scr, build);
            } else if (r && r.status === "error") {
              fail(r.message || "Не удалось завершить вход");
            }
          } catch (e) { /* кратковременные сбои опроса не прерываем */ }
        }, 3000);
      };

      go.onclick = () => {
        if (!flow) return;
        api("system.openUrl", { url: flow.verificationUrl }).catch((e) => ui.notifyError(e));
      };

      scr.el.querySelector('[data-act="back"]').onclick = () => {
        scr.close();
        if (opts.onBack) opts.onBack();
      };

      api("accounts.addMicrosoft").then((res) => {
        if (!res || !res.deviceCode) { fail("Не удалось получить код устройства"); return; }
        flow = res;
        drawCode(res.userCode || "");
        go.disabled = false;
        prog.hidden = false;
        startPoll();
      }).catch((e) => fail(ui.errText(e)));

      return scr;
    };

    return build();
  }

  window.AccUI = { openOffline, openMicrosoft };
})();
