/* Skin3D — 3D-визуализация скина Minecraft на canvas без зависимостей.
   Модель: 6 кубов (голова, тело, руки, ноги) + накладные слои (hat, jacket,
   sleeves, pants) с UV-развёрткой стандартного скина 64×64 (и legacy 64×32).
   Рендер: перспектива, z-сортировка граней, текстура через аффинный мапинг
   треугольников (clip + setTransform). Управление: drag — вращение, колесо — зум. */
(function () {
  "use strict";

  var TEX = {
    head: [0, 0, 8, 8, 8],
    body: [16, 16, 8, 12, 4],
    armR: [40, 16, 4, 12, 4],
    armL: [32, 48, 4, 12, 4],
    legR: [0, 16, 4, 12, 4],
    legL: [16, 48, 4, 12, 4],
    hat: [32, 0, 8, 8, 8],
    jacket: [16, 32, 8, 12, 4],
    sleeveR: [40, 32, 4, 12, 4],
    sleeveL: [48, 48, 4, 12, 4],
    pantR: [0, 32, 4, 12, 4],
    pantL: [16, 48, 4, 12, 4],
  };

  /* куб: центр, полуразмеры, UV (u,v,w,h) развёртки, размеры куба в текстуре */
  function boxParts() {
    var grow = 0.5; /* накладные слои чуть больше базовых */
    return [
      { key: "head", c: [0, 28, 0], h: [4, 4, 4], uv: TEX.head },
      { key: "body", c: [0, 18, 0], h: [4, 6, 2], uv: TEX.body },
      { key: "armR", c: [-6, 18, 0], h: [2, 6, 2], uv: TEX.armR },
      { key: "armL", c: [6, 18, 0], h: [2, 6, 2], uv: TEX.armL },
      { key: "legR", c: [-2, 6, 0], h: [2, 6, 2], uv: TEX.legR },
      { key: "legL", c: [2, 6, 0], h: [2, 6, 2], uv: TEX.legL },
      { key: "hat", c: [0, 28, 0], h: [4 + grow, 4 + grow, 4 + grow], uv: TEX.hat },
      { key: "jacket", c: [0, 18, 0], h: [4 + grow, 6 + grow, 2 + grow], uv: TEX.jacket },
      { key: "sleeveR", c: [-6, 18, 0], h: [2 + grow, 6 + grow, 2 + grow], uv: TEX.sleeveR },
      { key: "sleeveL", c: [6, 18, 0], h: [2 + grow, 6 + grow, 2 + grow], uv: TEX.sleeveL },
      { key: "pantR", c: [-2, 6, 0], h: [2 + grow, 6 + grow, 2 + grow], uv: TEX.pantR },
      { key: "pantL", c: [2, 6, 0], h: [2 + grow, 6 + grow, 2 + grow], uv: TEX.pantL },
    ];
  }

  /* UV-прямоугольники 6 граней куба (стандартная Minecraft-развёртка) */
  function faceUVs(uv) {
    var u = uv[0], v = uv[1], w = uv[2], h = uv[3], d = uv[4];
    return {
      front: [u + d, v + d, w, h],
      back: [u + 2 * d + w, v + d, w, h],
      right: [u, v + d, d, h],   /* правая сторона персонажа (экранно слева) */
      left: [u + d + w, v + d, d, h],
      top: [u + d, v, w, d],
      bottom: [u + d + w, v, w, d],
    };
  }

  /* вершины граней: [x,y,z]*4 в локальных координатах куба (TL,TR,BR,BL) и uv */
  function buildFaces(part) {
    var hx = part.h[0], hy = part.h[1], hz = part.h[2];
    var f = faceUVs(part.uv);
    function quad(r, pts) {
      return {
        pts: pts,
        uv: [[r[0], r[1]], [r[0] + r[2], r[1]], [r[0] + r[2], r[1] + r[3]], [r[0], r[1] + r[3]]],
        z: 0,
      };
    }
    return [
      quad(f.front, [[-hx, hy, -hz], [hx, hy, -hz], [hx, -hy, -hz], [-hx, -hy, -hz]]),
      quad(f.back, [[hx, hy, hz], [-hx, hy, hz], [-hx, -hy, hz], [hx, -hy, hz]]),
      quad(f.right, [[-hx, hy, -hz], [-hx, hy, hz], [-hx, -hy, hz], [-hx, -hy, -hz]]),
      quad(f.left, [[hx, hy, hz], [hx, hy, -hz], [hx, -hy, -hz], [hx, -hy, hz]]),
      quad(f.top, [[-hx, hy, -hz], [hx, hy, -hz], [hx, hy, hz], [-hx, hy, hz]]),
      quad(f.bottom, [[hx, -hy, -hz], [-hx, -hy, -hz], [-hx, -hy, hz], [hx, -hy, hz]]),
    ];
  }

  /* аффинный мапинг треугольника текстуры → экран */
  function drawTri(ctx, img, s, d) {
    var den = (s[1].x - s[0].x) * (s[2].y - s[0].y) - (s[2].x - s[0].x) * (s[1].y - s[0].y);
    if (!den) return;
    ctx.save();
    ctx.beginPath();
    ctx.moveTo(d[0].x, d[0].y);
    ctx.lineTo(d[1].x, d[1].y);
    ctx.lineTo(d[2].x, d[2].y);
    ctx.closePath();
    ctx.clip();
    var a = ((d[1].x - d[0].x) * (s[2].y - s[0].y) - (d[2].x - d[0].x) * (s[1].y - s[0].y)) / den;
    var b = ((d[2].x - d[0].x) * (s[1].x - s[0].x) - (d[1].x - d[0].x) * (s[2].x - s[0].x)) / den;
    var c = ((d[1].y - d[0].y) * (s[2].y - s[0].y) - (d[2].y - d[0].y) * (s[1].y - s[0].y)) / den;
    var e = ((d[2].y - d[0].y) * (s[1].x - s[0].x) - (d[1].y - d[0].y) * (s[2].x - s[0].x)) / den;
    var f = d[0].x - a * s[0].x - b * s[0].y;
    var g = d[0].y - c * s[0].x - e * s[0].y;
    ctx.setTransform(a, c, b, e, f, g);
    ctx.drawImage(img, 0, 0);
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.restore();
  }

  function create(canvas, opts) {
    opts = opts || {};
    var ctx = canvas.getContext("2d");
    var img = null;
    var legacy = false;
    var yaw = opts.yaw !== undefined ? opts.yaw : -0.5;
    var pitch = opts.pitch !== undefined ? opts.pitch : -0.12;
    var zoom = 1;
    var auto = opts.auto !== false;
    var dead = false;
    var raf = 0;
    var faces = null;
    var last = 0;

    var CAM = 90, BASE = 5.4;

    function isLegacy() { return !!(img && img.naturalHeight && img.naturalHeight <= 32); }

    function parts() {
      if (!faces) faces = boxParts();
      if (!legacy) return faces;
      /* legacy 64×32: нет накладных слоев (кроме hat), левые конечности — развёртка правых */
      var list = [];
      faces.forEach(function (p) {
        if (/^(jacket|sleeve|pant)/.test(p.key)) return;
        if (p.key === "armL") p = Object.assign({}, p, { uv: TEX.armR });
        if (p.key === "legL") p = Object.assign({}, p, { uv: TEX.legR });
        list.push(p);
      });
      return list;
    }

    function project(p, w, h) {
      var cy = Math.cos(yaw), sy = Math.sin(yaw);
      var cp = Math.cos(pitch), sp = Math.sin(pitch);
      var x = p[0], y = p[1] - 16, z = p[2];
      var x1 = x * cy - z * sy;
      var z1 = x * sy + z * cy;
      var y2 = y * cp - z1 * sp;
      var z2 = y * sp + z1 * cp;
      var k = (BASE * zoom) * CAM / (CAM + z2);
      return { x: w / 2 + x1 * k, y: h / 2 - y2 * k + 6 * zoom, z: z2 };
    }

    function render() {
      if (dead) return;
      var dpr = window.devicePixelRatio || 1;
      var w = canvas.clientWidth || 260;
      var h = canvas.clientHeight || 340;
      var pw = Math.round(w * dpr), ph = Math.round(h * dpr);
      if (canvas.width !== pw || canvas.height !== ph) { canvas.width = pw; canvas.height = ph; }
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      ctx.clearRect(0, 0, pw, ph);
      if (!img) return;

      legacy = isLegacy();
      var quads = [];
      parts().forEach(function (part) {
        buildFaces(part).forEach(function (q) {
          var scr = [0, 1, 2, 3].map(function (i) { return project(add(part.c, q.pts[i]), pw, ph); });
          q.scr = scr;
          q.z = (scr[0].z + scr[1].z + scr[2].z + scr[3].z) / 4;
          quads.push(q);
        });
      });
      quads.sort(function (a, b) { return b.z - a.z; }); /* дальние первыми */

      for (var i = 0; i < quads.length; i++) {
        var q = quads[i];
        var uv = q.uv, S = [0, 1, 2, 3].map(function (j) { return { x: uv[j][0], y: uv[j][1] }; });
        var D = q.scr;
        drawTri(ctx, img, [S[0], S[1], S[2]], [D[0], D[1], D[2]]);
        drawTri(ctx, img, [S[0], S[2], S[3]], [D[0], D[2], D[3]]);
      }
    }

    function add(a, b) { return [a[0] + b[0], a[1] + b[1], a[2] + b[2]]; }

    function loop(ts) {
      if (dead) return;
      var dt = last ? Math.min(64, ts - last) : 16;
      last = ts;
      if (auto && !dragging) yaw += 0.00045 * dt;
      render();
      raf = requestAnimationFrame(loop);
    }

    /* ---------------- вращение мышью/пальцем ---------------- */
    var dragging = false, lx = 0, ly = 0;
    function down(e) {
      dragging = true;
      lx = e.clientX; ly = e.clientY;
      canvas.setPointerCapture && canvas.setPointerCapture(e.pointerId);
    }
    function move(e) {
      if (!dragging) return;
      yaw += (e.clientX - lx) * 0.011;
      pitch = Math.max(-1.1, Math.min(1.1, pitch + (e.clientY - ly) * 0.008));
      lx = e.clientX; ly = e.clientY;
      e.preventDefault();
    }
    function up() { dragging = false; }
    function wheel(e) {
      zoom = Math.max(0.55, Math.min(2.2, zoom * (e.deltaY > 0 ? 0.92 : 1.08)));
      e.preventDefault();
    }
    canvas.addEventListener("pointerdown", down);
    canvas.addEventListener("pointermove", move);
    canvas.addEventListener("pointerup", up);
    canvas.addEventListener("pointercancel", up);
    canvas.addEventListener("wheel", wheel, { passive: false });

    raf = requestAnimationFrame(loop);

    return {
      setImage: function (url) {
        if (!url) { img = null; render(); return; }
        var im = new Image();
        im.onload = function () { if (!dead) { img = im; render(); } };
        im.onerror = function () { if (!dead) { img = null; render(); } };
        im.src = url;
      },
      setAuto: function (v) { auto = !!v; },
      isAuto: function () { return auto; },
      dispose: function () {
        dead = true;
        cancelAnimationFrame(raf);
        canvas.removeEventListener("pointerdown", down);
        canvas.removeEventListener("pointermove", move);
        canvas.removeEventListener("pointerup", up);
        canvas.removeEventListener("pointercancel", up);
        canvas.removeEventListener("wheel", wheel);
        img = null;
      },
    };
  }

  window.Skin3D = { create: create };
})();
