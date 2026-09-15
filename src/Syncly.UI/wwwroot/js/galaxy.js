// Galaxy renderer for the page graph. Blazor owns the data and the HUD; this file owns the
// canvas: camera, motion, hit-testing, and painting. Nothing here touches the CRDT.
(function () {
  const WORLD = 1000;               // layout units → world pixels at scale 1
  const MIN_SCALE = 0.12;
  const MAX_SCALE = 9;
  const FOCUS_DIM = 0.16;
  const HOVER_DEBOUNCE_MS = 40;
  const CLICK_DELAY_MS = 230;       // wait this long so a double-click can cancel the open

  const BODY = {
    star:     { r: 34,  min: 5.5, max: 190, font: 13, weight: 650, label: 'always' },
    planet:   { r: 17,  min: 2.8, max: 120, font: 12, weight: 550, label: 'zoom' },
    moon:     { r: 10,  min: 1.9, max: 80,  font: 11, weight: 500, label: 'zoom' },
    asteroid: { r: 6.5, min: 1.4, max: 50,  font: 11, weight: 500, label: 'hover' },
    meteor:   { r: 4.5, min: 1.3, max: 40,  font: 11, weight: 500, label: 'hover' },
    dust:     { r: 7,   min: 1.6, max: 60,  font: 11, weight: 500, label: 'zoom' },
  };

  const STAR_TONES = [
    { core: '#ffffff', mid: '#dff1ff', edge: '#7fb8ff', glow: 'rgba(120,170,255,' },
    { core: '#ffffff', mid: '#fff6dc', edge: '#ffd27a', glow: 'rgba(255,214,130,' },
    { core: '#fffdf4', mid: '#ffe1a0', edge: '#f39a4a', glow: 'rgba(250,160,80,' },
    { core: '#fff2e0', mid: '#ffb98a', edge: '#e2634f', glow: 'rgba(240,120,110,' },
  ];

  const PLANET_HUES = [200, 168, 34, 120, 268, 12, 292, 48];

  const instances = new Map();

  // ---------------------------------------------------------------- helpers

  function hash(value) {
    let h = 2166136261;
    const s = String(value ?? '');
    for (let i = 0; i < s.length; i++) {
      h = Math.imul(h ^ s.charCodeAt(i), 16777619) >>> 0;
    }
    return h >>> 0;
  }

  function rng(seed) {
    let state = seed >>> 0 || 0x9e3779b9;
    return () => {
      state ^= state << 13; state >>>= 0;
      state ^= state >>> 17;
      state ^= state << 5; state >>>= 0;
      return (state & 0x00ffffff) / 0x00ffffff;
    };
  }

  const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);
  const lerp = (a, b, t) => a + (b - a) * t;
  const ease = (dt, speed) => 1 - Math.exp(-dt * speed);

  function reducedMotionPreferred() {
    return !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  }

  // ---------------------------------------------------------------- instance

  function mount(canvasId, dotnet, options) {
    destroy(canvasId);
    const canvas = document.getElementById(canvasId);
    if (!canvas || !canvas.getContext) return false;

    const g = {
      id: canvasId,
      canvas,
      ctx: canvas.getContext('2d', { alpha: false }),
      dotnet,
      width: 1,
      height: 1,
      dpr: 1,
      nodes: [],
      byId: new Map(),
      edges: [],
      rings: [],
      currentId: null,
      spaceKey: null,
      motion: !(options && options.reducedMotion) && !reducedMotionPreferred(),
      simTime: 0,
      lastFrame: 0,
      frame: 0,
      running: false,
      dirty: true,
      fitted: false,
      cam: { x: WORLD / 2, y: WORLD / 2, scale: 0.6 },
      target: { x: WORLD / 2, y: WORLD / 2, scale: 0.6 },
      minScale: MIN_SCALE,
      hoverId: null,
      hoverMix: 0,
      focus: null,           // Set of ids lit while hovering
      focusEdges: null,      // Set of edge indexes lit while hovering
      hits: new Set(),
      hidden: new Set(),
      pointer: { down: false, dragging: false, id: null, x: 0, y: 0, startX: 0, startY: 0, moved: 0 },
      pinch: null,
      hoverTimer: 0,
      clickTimer: 0,
      pendingHover: undefined,
      comets: [],
      nextComet: 4,
      backdrop: null,
      specks: [],
      nebulae: [],
      observer: null,
      listeners: [],
    };

    instances.set(canvasId, g);
    attachEvents(g);
    resize(g);
    return true;
  }

  function destroy(canvasId) {
    const g = instances.get(canvasId);
    if (!g) return false;
    stop(g);
    if (g.observer) g.observer.disconnect();
    for (const [target, type, fn, opts] of g.listeners) target.removeEventListener(type, fn, opts);
    window.clearTimeout(g.hoverTimer);
    window.clearTimeout(g.clickTimer);
    instances.delete(canvasId);
    return true;
  }

  function on(g, target, type, fn, opts) {
    target.addEventListener(type, fn, opts);
    g.listeners.push([target, type, fn, opts]);
  }

  // ---------------------------------------------------------------- data

  function setData(canvasId, data) {
    const g = instances.get(canvasId);
    if (!g || !data) return;

    const spaceChanged = g.spaceKey !== (data.spaceKey ?? null);
    g.spaceKey = data.spaceKey ?? null;
    g.currentId = data.currentId ?? null;

    const previous = g.byId;
    const nodes = (data.nodes || []).map((n) => {
      const kind = String(n.body || 'star').toLowerCase();
      const spec = BODY[kind] || BODY.planet;
      const h = hash(n.id);
      const old = previous.get(n.id);
      return {
        id: n.id,
        title: n.title || 'Untitled',
        icon: n.icon || '',
        kind,
        spec,
        depth: n.depth | 0,
        parentId: n.parentId || null,
        missing: !!n.missing,
        baseX: n.x * WORLD,
        baseY: n.y * WORLD,
        radius: spec.r,
        hash: h,
        tone: h % STAR_TONES.length,
        hue: PLANET_HUES[h % PLANET_HUES.length] + ((h >>> 8) % 24) - 12,
        ringed: kind === 'planet' && h % 4 === 0,
        banded: kind === 'planet' && (h >>> 3) % 3 !== 1,
        dir: (h >>> 5) & 1 ? 1 : -1,
        phase: ((h >>> 9) % 1000) / 1000 * Math.PI * 2,
        x: n.x * WORLD,
        y: n.y * WORLD,
        orbitR: 0,
        angle0: 0,
        omega: 0,
        alpha: old ? old.alpha : 1,
        targetAlpha: 1,
        hover: old ? old.hover : 0,
        screenX: 0,
        screenY: 0,
        screenR: 0,
        visible: true,
        children: [],
      };
    });

    const byId = new Map(nodes.map((n) => [n.id, n]));
    for (const n of nodes) {
      if (n.parentId && byId.has(n.parentId) && n.parentId !== n.id) {
        const p = byId.get(n.parentId);
        p.children.push(n);
        const dx = n.baseX - p.baseX;
        const dy = n.baseY - p.baseY;
        n.orbitR = Math.hypot(dx, dy);
        n.angle0 = Math.atan2(dy, dx);
        // Kepler-ish: closer worlds move faster, everything stays slow enough to read.
        n.omega = n.orbitR > 0.5 ? 0.11 / Math.sqrt(Math.max(0.35, n.orbitR / 60)) : 0;
      } else {
        n.parentId = null;
      }
    }

    // Depth order so a moon's centre (its planet) is placed before the moon itself.
    nodes.sort((a, b) => a.depth - b.depth || b.radius - a.radius);

    g.nodes = nodes;
    g.byId = byId;
    g.edges = (data.edges || [])
      .filter((e) => byId.has(e.from) && byId.has(e.to) && e.from !== e.to)
      .filter((e) => byId.get(e.from).parentId !== e.to && byId.get(e.to).parentId !== e.from)
      .map((e) => ({ from: e.from, to: e.to }));

    // One ring per distinct child orbit radius per parent.
    const rings = [];
    for (const p of nodes) {
      if (p.children.length === 0) continue;
      const seen = new Map();
      for (const c of p.children) {
        if (c.orbitR < 0.5) continue;
        const key = Math.round(c.orbitR * 2);
        if (!seen.has(key)) seen.set(key, { parent: p, radius: c.orbitR, depth: p.depth, dust: c.missing });
        else if (!c.missing) seen.get(key).dust = false;
      }
      rings.push(...seen.values());
    }
    g.rings = rings;

    applyFilter(g);
    if (g.hoverId && !byId.has(g.hoverId)) clearHover(g, true);
    for (const id of [...g.hits]) if (!byId.has(id)) g.hits.delete(id);

    g.minScale = Math.min(MIN_SCALE, fitScale(g, worldBounds(g, nodes)) * 0.45);
    if (!g.fitted || spaceChanged) {
      fit(canvasId, true);
      g.fitted = true;
    }

    invalidate(g);
  }

  function setCurrent(canvasId, id) {
    const g = instances.get(canvasId);
    if (!g) return;
    g.currentId = id || null;
    invalidate(g);
  }

  function setFilter(canvasId, hiddenKinds) {
    const g = instances.get(canvasId);
    if (!g) return;
    g.hidden = new Set((hiddenKinds || []).map((k) => String(k).toLowerCase()));
    applyFilter(g);
    invalidate(g);
  }

  function applyFilter(g) {
    for (const n of g.nodes) {
      const parentHidden = n.parentId ? g.byId.get(n.parentId).visible === false : false;
      n.visible = !g.hidden.has(n.kind) && !parentHidden;
    }
  }

  function setMotion(canvasId, enabled) {
    const g = instances.get(canvasId);
    if (!g) return;
    g.motion = !!enabled;
    invalidate(g);
  }

  function isMotion(canvasId) {
    const g = instances.get(canvasId);
    return g ? g.motion : false;
  }

  function search(canvasId, query) {
    const g = instances.get(canvasId);
    if (!g) return 0;
    const q = String(query || '').trim().toLowerCase();
    g.hits = new Set();
    if (q.length === 0) {
      invalidate(g);
      return 0;
    }

    const found = [];
    for (const n of g.nodes) {
      if (!n.visible) continue;
      if (n.title.toLowerCase().includes(q)) {
        g.hits.add(n.id);
        found.push(n);
      }
    }

    if (found.length > 0) {
      const bounds = worldBounds(g, found.length <= 12 ? found : [found[0]]);
      flyToBounds(g, bounds, found.length === 1 ? 2.2 : 1.35);
    }

    invalidate(g);
    return found.length;
  }

  function fit(canvasId, immediate) {
    const g = instances.get(canvasId);
    if (!g) return;
    const bounds = worldBounds(g, g.nodes.filter((n) => n.visible));
    const scale = fitScale(g, bounds);
    g.target.scale = scale;
    g.target.x = (bounds.minX + bounds.maxX) / 2;
    g.target.y = (bounds.minY + bounds.maxY) / 2;
    if (immediate) {
      g.cam.x = g.target.x;
      g.cam.y = g.target.y;
      g.cam.scale = g.target.scale;
    }
    invalidate(g);
  }

  function locate(canvasId) {
    const g = instances.get(canvasId);
    if (!g || !g.currentId) return false;
    const n = g.byId.get(g.currentId);
    if (!n) return false;
    flyToNode(g, n);
    return true;
  }

  function flyToNode(g, n) {
    if (n.children.length > 0) {
      flyToBounds(g, worldBounds(g, systemOf(n)), 1.1);
    } else {
      const parent = n.parentId ? g.byId.get(n.parentId) : null;
      const group = parent ? [parent, ...parent.children] : [n];
      flyToBounds(g, worldBounds(g, group), 1.25);
    }
  }

  function systemOf(root) {
    const out = [];
    const stack = [root];
    while (stack.length) {
      const n = stack.pop();
      out.push(n);
      for (const c of n.children) stack.push(c);
    }
    return out;
  }

  function worldBounds(g, list) {
    if (!list || list.length === 0) {
      return { minX: 0, minY: 0, maxX: WORLD, maxY: WORLD };
    }
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (const n of list) {
      const pad = n.radius * (n.kind === 'star' ? 3.4 : 2) + 14;
      minX = Math.min(minX, n.x - pad);
      minY = Math.min(minY, n.y - pad);
      maxX = Math.max(maxX, n.x + pad);
      maxY = Math.max(maxY, n.y + pad);
    }
    return { minX, minY, maxX, maxY };
  }

  function fitScale(g, bounds) {
    const bw = Math.max(40, bounds.maxX - bounds.minX);
    const bh = Math.max(40, bounds.maxY - bounds.minY);
    const padX = clamp(g.width * 0.08, 24, 90);
    const padY = clamp(g.height * 0.1, 64, 120);
    return clamp(Math.min((g.width - padX * 2) / bw, (g.height - padY * 2) / bh), MIN_SCALE * 0.4, MAX_SCALE);
  }

  function flyToBounds(g, bounds, zoomBias) {
    const scale = clamp(fitScale(g, bounds) * (zoomBias || 1), g.minScale, MAX_SCALE);
    g.target.scale = scale;
    g.target.x = (bounds.minX + bounds.maxX) / 2;
    g.target.y = (bounds.minY + bounds.maxY) / 2;
    invalidate(g);
  }

  // ---------------------------------------------------------------- sizing

  function resize(g) {
    const parent = g.canvas.parentElement || g.canvas;
    const rect = parent.getBoundingClientRect();
    const width = Math.max(1, Math.round(rect.width));
    const height = Math.max(1, Math.round(rect.height));
    const dpr = clamp(window.devicePixelRatio || 1, 1, 3);
    if (width === g.width && height === g.height && dpr === g.dpr && g.backdrop) return;

    const wasFitted = g.fitted;
    g.width = width;
    g.height = height;
    g.dpr = dpr;
    g.canvas.width = Math.round(width * dpr);
    g.canvas.height = Math.round(height * dpr);
    g.canvas.style.width = width + 'px';
    g.canvas.style.height = height + 'px';
    buildBackdrop(g);
    if (wasFitted && g.nodes.length) {
      // Keep the whole galaxy in view when the window changes shape.
      const bounds = worldBounds(g, g.nodes.filter((n) => n.visible));
      const s = fitScale(g, bounds);
      if (Math.abs(g.target.scale - g.cam.scale) < 0.001 && Math.abs(g.cam.scale - s) > 0.001 && isNearFit(g, bounds)) {
        g.target.scale = s;
        g.cam.scale = s;
      }
    }
    invalidate(g);
  }

  function isNearFit(g, bounds) {
    const cx = (bounds.minX + bounds.maxX) / 2;
    const cy = (bounds.minY + bounds.maxY) / 2;
    return Math.hypot(g.cam.x - cx, g.cam.y - cy) < 4;
  }

  function buildBackdrop(g) {
    const seed = hash('galaxy:' + (g.spaceKey || 'space'));
    const rand = rng(seed);
    const w = g.width, h = g.height;

    const off = document.createElement('canvas');
    off.width = Math.round(w * g.dpr);
    off.height = Math.round(h * g.dpr);
    const c = off.getContext('2d');
    c.setTransform(g.dpr, 0, 0, g.dpr, 0, 0);

    const space = c.createRadialGradient(w * 0.5, h * 0.46, 0, w * 0.5, h * 0.46, Math.max(w, h) * 0.78);
    space.addColorStop(0, '#11142b');
    space.addColorStop(0.5, '#080a18');
    space.addColorStop(1, '#020309');
    c.fillStyle = space;
    c.fillRect(0, 0, w, h);

    // Nebulae: a few soft blobs in the house palette (violet, blue, a touch of rose).
    const palette = [
      [118, 104, 255], [78, 178, 232], [214, 96, 168], [92, 110, 220],
    ];
    const blobs = 5 + Math.round(rand() * 3);
    for (let i = 0; i < blobs; i++) {
      const [r, gg, b] = palette[i % palette.length];
      const x = w * (0.1 + rand() * 0.8);
      const y = h * (0.12 + rand() * 0.76);
      const rad = Math.max(w, h) * (0.18 + rand() * 0.26);
      const grad = c.createRadialGradient(x, y, 0, x, y, rad);
      grad.addColorStop(0, `rgba(${r},${gg},${b},${0.14 + rand() * 0.08})`);
      grad.addColorStop(0.55, `rgba(${r},${gg},${b},0.04)`);
      grad.addColorStop(1, `rgba(${r},${gg},${b},0)`);
      c.fillStyle = grad;
      c.save();
      c.translate(x, y);
      c.rotate(rand() * Math.PI);
      c.scale(1, 0.55 + rand() * 0.4);
      c.translate(-x, -y);
      c.fillRect(x - rad, y - rad, rad * 2, rad * 2);
      c.restore();
    }

    // A faint milky band across the frame.
    c.save();
    c.translate(w / 2, h / 2);
    c.rotate(-0.42 + rand() * 0.2);
    const band = c.createLinearGradient(0, -h * 0.22, 0, h * 0.22);
    band.addColorStop(0, 'rgba(180,190,255,0)');
    band.addColorStop(0.5, 'rgba(200,205,255,0.07)');
    band.addColorStop(1, 'rgba(180,190,255,0)');
    c.fillStyle = band;
    c.fillRect(-w, -h * 0.22, w * 2, h * 0.44);
    c.restore();

    // Vignette.
    const vig = c.createRadialGradient(w / 2, h / 2, Math.min(w, h) * 0.35, w / 2, h / 2, Math.max(w, h) * 0.78);
    vig.addColorStop(0, 'rgba(0,0,0,0)');
    vig.addColorStop(1, 'rgba(0,1,8,0.75)');
    c.fillStyle = vig;
    c.fillRect(0, 0, w, h);

    g.backdrop = off;

    // Specks live in unit space so parallax can wrap them around the viewport.
    const count = clamp(Math.round((w * h) / 4200), 140, 520);
    const specks = [];
    for (let i = 0; i < count; i++) {
      const brightness = Math.pow(rand(), 2.8);
      specks.push({
        x: rand(),
        y: rand(),
        r: 0.5 + brightness * 1.5,
        a: 0.22 + brightness * 0.7,
        depth: 0.015 + rand() * 0.05,
        tone: (rand() * 3) | 0,
        twinkle: brightness > 0.45,
        phase: rand() * Math.PI * 2,
        speed: 0.6 + rand() * 1.4,
      });
    }
    g.specks = specks;
  }

  // ---------------------------------------------------------------- events

  function attachEvents(g) {
    const canvas = g.canvas;
    canvas.style.touchAction = 'none';

    if (window.ResizeObserver) {
      g.observer = new ResizeObserver(() => resize(g));
      g.observer.observe(canvas.parentElement || canvas);
    } else {
      on(g, window, 'resize', () => resize(g));
    }

    on(g, canvas, 'wheel', (e) => {
      e.preventDefault();
      const rect = canvas.getBoundingClientRect();
      const px = e.clientX - rect.left;
      const py = e.clientY - rect.top;
      const delta = e.deltaMode === 1 ? e.deltaY * 16 : e.deltaY;
      zoomAt(g, px, py, Math.exp(-delta * 0.0016));
    }, { passive: false });

    on(g, canvas, 'pointerdown', (e) => {
      try { canvas.setPointerCapture?.(e.pointerId); } catch { /* synthetic or stale pointer */ }
      const rect = canvas.getBoundingClientRect();
      const px = e.clientX - rect.left;
      const py = e.clientY - rect.top;

      if (g.pointer.down && e.pointerId !== g.pointer.id) {
        // Second finger: start a pinch.
        g.pinch = { idA: g.pointer.id, idB: e.pointerId, ax: g.pointer.x, ay: g.pointer.y, bx: px, by: py };
        g.pointer.dragging = true;
        return;
      }

      g.pointer.down = true;
      g.pointer.dragging = false;
      g.pointer.id = e.pointerId;
      g.pointer.x = g.pointer.startX = px;
      g.pointer.y = g.pointer.startY = py;
      g.pointer.moved = 0;
      canvas.classList.add('is-pressing');
    });

    on(g, canvas, 'pointermove', (e) => {
      const rect = canvas.getBoundingClientRect();
      const px = e.clientX - rect.left;
      const py = e.clientY - rect.top;

      if (g.pinch) {
        if (e.pointerId === g.pinch.idA) { g.pinch.ax = px; g.pinch.ay = py; }
        else if (e.pointerId === g.pinch.idB) { g.pinch.bx = px; g.pinch.by = py; }
        else return;
        if (g.pinch.lastDist === undefined) {
          g.pinch.lastDist = Math.hypot(g.pinch.ax - g.pinch.bx, g.pinch.ay - g.pinch.by);
          g.pinch.lastMx = (g.pinch.ax + g.pinch.bx) / 2;
          g.pinch.lastMy = (g.pinch.ay + g.pinch.by) / 2;
          return;
        }
        const dist = Math.hypot(g.pinch.ax - g.pinch.bx, g.pinch.ay - g.pinch.by);
        const mx = (g.pinch.ax + g.pinch.bx) / 2;
        const my = (g.pinch.ay + g.pinch.by) / 2;
        panBy(g, mx - g.pinch.lastMx, my - g.pinch.lastMy, true);
        if (g.pinch.lastDist > 0) zoomAt(g, mx, my, dist / g.pinch.lastDist, true);
        g.pinch.lastDist = dist;
        g.pinch.lastMx = mx;
        g.pinch.lastMy = my;
        return;
      }

      if (g.pointer.down && e.pointerId === g.pointer.id) {
        const dx = px - g.pointer.x;
        const dy = py - g.pointer.y;
        g.pointer.moved += Math.abs(dx) + Math.abs(dy);
        if (!g.pointer.dragging && g.pointer.moved > 5) {
          g.pointer.dragging = true;
          canvas.classList.add('is-dragging');
          clearHover(g, true);
        }
        if (g.pointer.dragging) panBy(g, dx, dy, true);
        g.pointer.x = px;
        g.pointer.y = py;
        return;
      }

      g.pointer.x = px;
      g.pointer.y = py;
      updateHover(g, px, py, e.clientX, e.clientY);
    });

    const release = (e) => {
      if (g.pinch && (e.pointerId === g.pinch.idA || e.pointerId === g.pinch.idB)) {
        g.pinch = null;
        g.pointer.down = false;
        g.pointer.dragging = false;
        canvas.classList.remove('is-dragging', 'is-pressing');
        return;
      }
      if (!g.pointer.down || e.pointerId !== g.pointer.id) return;
      const wasDrag = g.pointer.dragging;
      g.pointer.down = false;
      g.pointer.dragging = false;
      canvas.classList.remove('is-dragging', 'is-pressing');
      if (wasDrag || e.type === 'pointercancel') return;

      const hit = hitTest(g, g.pointer.x, g.pointer.y);
      if (!hit) return;
      window.clearTimeout(g.clickTimer);
      g.clickTimer = window.setTimeout(() => {
        g.clickTimer = 0;
        notify(g, 'OnOpen', hit.id);
      }, CLICK_DELAY_MS);
    };
    on(g, canvas, 'pointerup', release);
    on(g, canvas, 'pointercancel', release);

    on(g, canvas, 'dblclick', (e) => {
      e.preventDefault();
      window.clearTimeout(g.clickTimer);
      g.clickTimer = 0;
      const rect = canvas.getBoundingClientRect();
      const hit = hitTest(g, e.clientX - rect.left, e.clientY - rect.top);
      if (hit) flyToNode(g, hit);
      else zoomAt(g, e.clientX - rect.left, e.clientY - rect.top, 1.8);
    });

    on(g, canvas, 'pointerleave', () => {
      if (!g.pointer.down) clearHover(g, false);
    });

    on(g, canvas, 'keydown', (e) => {
      const step = 60 / g.cam.scale;
      switch (e.key) {
        case 'Home':
        case 'Escape':
          fit(g.id, false);
          break;
        case '+':
        case '=':
          zoomAt(g, g.width / 2, g.height / 2, 1.3);
          break;
        case '-':
        case '_':
          zoomAt(g, g.width / 2, g.height / 2, 1 / 1.3);
          break;
        case 'ArrowLeft': g.target.x -= step; break;
        case 'ArrowRight': g.target.x += step; break;
        case 'ArrowUp': g.target.y -= step; break;
        case 'ArrowDown': g.target.y += step; break;
        default: return;
      }
      e.preventDefault();
      invalidate(g);
    });

    on(g, document, 'visibilitychange', () => {
      if (document.hidden) stop(g);
      else invalidate(g);
    });
  }

  function zoomAt(g, px, py, factor, immediate) {
    const cx = g.width / 2;
    const cy = g.height / 2;
    const from = immediate ? g.cam : g.target;
    const worldX = from.x + (px - cx) / from.scale;
    const worldY = from.y + (py - cy) / from.scale;
    const scale = clamp(from.scale * factor, g.minScale, MAX_SCALE);
    g.target.scale = scale;
    g.target.x = worldX - (px - cx) / scale;
    g.target.y = worldY - (py - cy) / scale;
    if (immediate) {
      g.cam.scale = scale;
      g.cam.x = g.target.x;
      g.cam.y = g.target.y;
    }
    invalidate(g);
  }

  function panBy(g, dx, dy, immediate) {
    g.target.x -= dx / g.cam.scale;
    g.target.y -= dy / g.cam.scale;
    if (immediate) {
      g.cam.x = g.target.x;
      g.cam.y = g.target.y;
    }
    invalidate(g);
  }

  // ---------------------------------------------------------------- hover

  function hitTest(g, px, py) {
    let best = null;
    let bestDist = Infinity;
    for (const n of g.nodes) {
      if (!n.visible || n.alpha < 0.05) continue;
      const hitR = Math.max(n.screenR + 7, 15);
      const d = Math.hypot(n.screenX - px, n.screenY - py);
      if (d <= hitR && d - n.screenR < bestDist) {
        bestDist = d - n.screenR;
        best = n;
      }
    }
    return best;
  }

  function updateHover(g, px, py, clientX, clientY) {
    const hit = hitTest(g, px, py);
    const id = hit ? hit.id : null;
    g.canvas.classList.toggle('is-hover', !!hit);
    if (id === g.hoverId) {
      if (hit) g.hoverClient = { x: clientX, y: clientY };
      return;
    }

    g.hoverId = id;
    g.hoverClient = { x: clientX, y: clientY };
    computeFocus(g);
    invalidate(g);

    g.pendingHover = id;
    window.clearTimeout(g.hoverTimer);
    g.hoverTimer = window.setTimeout(() => {
      g.hoverTimer = 0;
      const c = g.hoverClient || { x: clientX, y: clientY };
      notify(g, 'OnHover', g.pendingHover, c.x, c.y);
    }, HOVER_DEBOUNCE_MS);
  }

  function clearHover(g, silentIfNone) {
    g.canvas.classList.remove('is-hover');
    if (g.hoverId === null && silentIfNone) return;
    g.hoverId = null;
    g.focus = null;
    g.focusEdges = null;
    window.clearTimeout(g.hoverTimer);
    g.hoverTimer = 0;
    notify(g, 'OnHover', null, 0, 0);
    invalidate(g);
  }

  function computeFocus(g) {
    if (!g.hoverId) {
      g.focus = null;
      g.focusEdges = null;
      return;
    }
    const n = g.byId.get(g.hoverId);
    if (!n) return;
    const set = new Set([n.id]);
    let cursor = n;
    let guard = 0;
    while (cursor.parentId && guard++ < 64) {
      set.add(cursor.parentId);
      cursor = g.byId.get(cursor.parentId);
      if (!cursor) break;
    }
    for (const c of n.children) set.add(c.id);
    const edges = new Set();
    g.edges.forEach((e, i) => {
      if (e.from === n.id || e.to === n.id) {
        set.add(e.from);
        set.add(e.to);
        edges.add(i);
      }
    });
    g.focus = set;
    g.focusEdges = edges;
  }

  function notify(g, method, ...args) {
    if (!g.dotnet) return;
    try {
      const p = g.dotnet.invokeMethodAsync(method, ...args);
      if (p && p.catch) p.catch(() => {});
    } catch {
      // Blazor circuit gone; nothing to do.
    }
  }

  // ---------------------------------------------------------------- loop

  function invalidate(g) {
    g.dirty = true;
    if (!g.running) {
      g.running = true;
      g.lastFrame = 0;
      g.frame = window.requestAnimationFrame((t) => tick(g, t));
    }
  }

  function stop(g) {
    if (g.frame) window.cancelAnimationFrame(g.frame);
    g.frame = 0;
    g.running = false;
  }

  function tick(g, now) {
    if (!instances.has(g.id)) return;
    const dt = g.lastFrame ? clamp((now - g.lastFrame) / 1000, 0, 0.05) : 0.016;
    g.lastFrame = now;

    let animating = false;

    // Camera easing.
    const k = ease(dt, 8.5);
    const c = g.cam, t = g.target;
    if (Math.abs(c.x - t.x) > 0.01 || Math.abs(c.y - t.y) > 0.01 || Math.abs(c.scale - t.scale) > 0.0005) {
      c.x = lerp(c.x, t.x, k);
      c.y = lerp(c.y, t.y, k);
      c.scale = lerp(c.scale, t.scale, k);
      animating = true;
    } else {
      c.x = t.x; c.y = t.y; c.scale = t.scale;
    }

    if (g.motion) {
      g.simTime += dt;
      animating = true;
    }

    // Per-node alpha + hover mix easing.
    const ka = ease(dt, 10);
    for (const n of g.nodes) {
      const focusTarget = g.focus ? (g.focus.has(n.id) ? 1 : FOCUS_DIM) : 1;
      n.targetAlpha = n.visible ? focusTarget : 0;
      if (Math.abs(n.alpha - n.targetAlpha) > 0.004) { n.alpha = lerp(n.alpha, n.targetAlpha, ka); animating = true; }
      else n.alpha = n.targetAlpha;
      const hoverTarget = n.id === g.hoverId ? 1 : 0;
      if (Math.abs(n.hover - hoverTarget) > 0.004) { n.hover = lerp(n.hover, hoverTarget, ka); animating = true; }
      else n.hover = hoverTarget;
    }

    if (g.hits.size > 0 || g.currentId) animating = animating || g.motion;

    updateComets(g, dt);
    if (g.comets.length > 0) animating = true;

    layoutPositions(g);
    draw(g, now / 1000);
    g.dirty = false;

    if (animating || g.dirty) {
      g.frame = window.requestAnimationFrame((ts) => tick(g, ts));
    } else {
      g.running = false;
      g.frame = 0;
    }
  }

  function layoutPositions(g) {
    const t = g.simTime;
    for (const n of g.nodes) {
      const p = n.parentId ? g.byId.get(n.parentId) : null;
      if (p && n.orbitR > 0.5) {
        const ang = n.angle0 + n.dir * n.omega * t;
        n.x = p.x + Math.cos(ang) * n.orbitR;
        n.y = p.y + Math.sin(ang) * n.orbitR;
        n.heading = ang + n.dir * Math.PI / 2;
      } else {
        n.x = n.baseX;
        n.y = n.baseY;
        n.heading = 0;
      }
    }
  }

  function updateComets(g, dt) {
    if (!g.motion) { g.comets.length = 0; return; }
    g.nextComet -= dt;
    if (g.nextComet <= 0 && g.comets.length < 2) {
      const rand = Math.random;
      const fromLeft = rand() < 0.5;
      const angle = (fromLeft ? 0.35 : Math.PI - 0.35) + (rand() - 0.5) * 0.7;
      g.comets.push({
        x: fromLeft ? -40 : g.width + 40,
        y: g.height * (0.05 + rand() * 0.5),
        vx: Math.cos(angle) * (620 + rand() * 380),
        vy: Math.sin(angle) * (620 + rand() * 380),
        life: 0,
        ttl: 1.1 + rand() * 0.6,
        len: 90 + rand() * 120,
      });
      g.nextComet = 7 + rand() * 11;
    }
    for (const cmt of g.comets) {
      cmt.life += dt;
      cmt.x += cmt.vx * dt;
      cmt.y += cmt.vy * dt;
    }
    g.comets = g.comets.filter((cmt) => cmt.life < cmt.ttl);
  }

  // ---------------------------------------------------------------- drawing

  function draw(g, time) {
    const ctx = g.ctx;
    const w = g.width, h = g.height;
    ctx.setTransform(g.dpr, 0, 0, g.dpr, 0, 0);

    if (g.backdrop) ctx.drawImage(g.backdrop, 0, 0, w, h);
    else { ctx.fillStyle = '#05060f'; ctx.fillRect(0, 0, w, h); }

    drawSpecks(g, ctx, time);

    const cx = w / 2, cy = h / 2;
    const s = g.cam.scale;
    const toX = (x) => (x - g.cam.x) * s + cx;
    const toY = (y) => (y - g.cam.y) * s + cy;

    // Project everything once.
    for (const n of g.nodes) {
      n.screenX = toX(n.x);
      n.screenY = toY(n.y);
      n.screenR = clamp(n.radius * s, n.spec.min, n.spec.max);
    }

    drawRings(g, ctx, toX, toY, s);
    drawLinks(g, ctx);
    drawBodies(g, ctx, time, s);
    drawLabels(g, ctx, s);
    drawComets(g, ctx);
  }

  function drawSpecks(g, ctx, time) {
    const w = g.width, h = g.height;
    const camX = g.cam.x, camY = g.cam.y;
    const motion = g.motion;
    for (const sp of g.specks) {
      // Parallax: the far field slides a little against the camera and wraps.
      let x = ((sp.x * w - camX * sp.depth * g.cam.scale * 8) % w + w) % w;
      let y = ((sp.y * h - camY * sp.depth * g.cam.scale * 8) % h + h) % h;
      let a = sp.a;
      if (motion && sp.twinkle) a *= 0.55 + 0.45 * Math.sin(time * sp.speed + sp.phase);
      const tone = sp.tone === 0 ? '235,240,255' : sp.tone === 1 ? '255,240,215' : '200,220,255';
      ctx.fillStyle = `rgba(${tone},${a.toFixed(3)})`;
      const r = sp.r;
      if (r < 1.1) ctx.fillRect(x, y, 1, 1);
      else {
        ctx.beginPath();
        ctx.arc(x, y, r * 0.8, 0, Math.PI * 2);
        ctx.fill();
      }
    }
  }

  function drawRings(g, ctx, toX, toY, s) {
    ctx.lineWidth = 1;
    for (const ring of g.rings) {
      const p = ring.parent;
      if (!p.visible) continue;
      const alpha = p.alpha * (ring.depth === 0 ? 0.26 : ring.depth === 1 ? 0.2 : 0.14) * (ring.dust ? 0.6 : 1);
      if (alpha < 0.01) continue;
      const r = ring.radius * s;
      if (r < 6) continue;
      ctx.beginPath();
      ctx.arc(toX(p.x), toY(p.y), r, 0, Math.PI * 2);
      ctx.strokeStyle = ring.depth === 0
        ? `rgba(255,214,140,${alpha.toFixed(3)})`
        : `rgba(170,190,255,${alpha.toFixed(3)})`;
      if (ring.dust || ring.depth >= 2) ctx.setLineDash([3, 6]);
      else ctx.setLineDash([]);
      ctx.stroke();
    }
    ctx.setLineDash([]);
  }

  function drawLinks(g, ctx) {
    ctx.lineWidth = 1.2;
    g.edges.forEach((e, i) => {
      const a = g.byId.get(e.from);
      const b = g.byId.get(e.to);
      if (!a || !b || !a.visible || !b.visible) return;
      const lit = g.focusEdges ? g.focusEdges.has(i) : false;
      const linkedToCurrent = g.currentId && (a.id === g.currentId || b.id === g.currentId);
      let alpha = Math.min(a.alpha, b.alpha) * 0.28;
      if (lit) alpha = 0.85;
      else if (g.focus) alpha *= 0.5;
      else if (linkedToCurrent) alpha = 0.5;
      if (alpha < 0.01) return;

      const x1 = a.screenX, y1 = a.screenY, x2 = b.screenX, y2 = b.screenY;
      const dx = x2 - x1, dy = y2 - y1;
      const len = Math.max(1, Math.hypot(dx, dy));
      const bend = Math.min(80, len * 0.16);
      const mx = (x1 + x2) / 2 - dy / len * bend;
      const my = (y1 + y2) / 2 + dx / len * bend;
      ctx.beginPath();
      ctx.moveTo(x1, y1);
      ctx.quadraticCurveTo(mx, my, x2, y2);
      ctx.strokeStyle = lit
        ? `rgba(196,176,255,${alpha.toFixed(3)})`
        : `rgba(150,140,230,${alpha.toFixed(3)})`;
      ctx.setLineDash(lit ? [] : [5, 7]);
      ctx.lineWidth = lit ? 1.8 : 1.1;
      ctx.stroke();
    });
    ctx.setLineDash([]);
  }

  function drawBodies(g, ctx, time, s) {
    // Big things first so glows sit under smaller worlds.
    const order = g.nodes.slice().sort((a, b) => b.screenR - a.screenR);
    for (const n of order) {
      if (!n.visible || n.alpha < 0.01) continue;
      const x = n.screenX, y = n.screenY;
      if (x < -200 || y < -200 || x > g.width + 200 || y > g.height + 200) continue;
      const r = n.screenR * (1 + n.hover * 0.12);
      ctx.globalAlpha = n.alpha;
      switch (n.kind) {
        case 'star': drawStar(ctx, n, x, y, r, time, g.motion); break;
        case 'planet': drawPlanet(ctx, n, x, y, r); break;
        case 'moon': drawMoon(ctx, n, x, y, r); break;
        case 'asteroid': drawAsteroid(ctx, n, x, y, r); break;
        case 'meteor': drawMeteor(ctx, n, x, y, r, s); break;
        default: drawDust(ctx, n, x, y, r, time); break;
      }
      ctx.globalAlpha = 1;
    }

    // Overlays: search hits, current page, hover ring.
    for (const n of g.nodes) {
      if (!n.visible || n.alpha < 0.01) continue;
      const x = n.screenX, y = n.screenY;
      const r = n.screenR * (1 + n.hover * 0.12);
      if (g.hits.has(n.id)) {
        const pulse = 0.5 + 0.5 * Math.sin(time * 4 + n.phase);
        ctx.beginPath();
        ctx.arc(x, y, r + 6 + pulse * 5, 0, Math.PI * 2);
        ctx.strokeStyle = `rgba(255,214,120,${(0.55 + pulse * 0.35).toFixed(3)})`;
        ctx.lineWidth = 2;
        ctx.stroke();
      }
      if (n.id === g.currentId) {
        ctx.save();
        ctx.translate(x, y);
        ctx.rotate(g.motion ? time * 0.7 : 0);
        ctx.beginPath();
        ctx.arc(0, 0, r + 7, 0, Math.PI * 2);
        ctx.setLineDash([6, 5]);
        ctx.strokeStyle = 'rgba(255,255,255,0.9)';
        ctx.lineWidth = 1.6;
        ctx.stroke();
        ctx.setLineDash([]);
        ctx.restore();
      }
      if (n.hover > 0.01) {
        ctx.beginPath();
        ctx.arc(x, y, r + 4 + n.hover * 3, 0, Math.PI * 2);
        ctx.strokeStyle = `rgba(255,255,255,${(0.75 * n.hover).toFixed(3)})`;
        ctx.lineWidth = 1.4;
        ctx.stroke();
      }
    }
  }

  function drawStar(ctx, n, x, y, r, time, motion) {
    const tone = STAR_TONES[n.tone];
    const pulse = motion ? 1 + 0.05 * Math.sin(time * 1.3 + n.phase) : 1;
    const glowR = r * 3.4 * pulse;
    const glow = ctx.createRadialGradient(x, y, r * 0.6, x, y, glowR);
    glow.addColorStop(0, tone.glow + '0.55)');
    glow.addColorStop(0.35, tone.glow + '0.18)');
    glow.addColorStop(1, tone.glow + '0)');
    ctx.fillStyle = glow;
    ctx.beginPath();
    ctx.arc(x, y, glowR, 0, Math.PI * 2);
    ctx.fill();

    if (r > 9) {
      // Soft cross flare that fades out toward the tips.
      const reach = r * 2.4 * pulse;
      ctx.lineWidth = Math.max(1, r * 0.06);
      ctx.lineCap = 'round';
      for (const [dx, dy] of [[1, 0], [0, 1]]) {
        const flare = ctx.createLinearGradient(x - dx * reach, y - dy * reach, x + dx * reach, y + dy * reach);
        flare.addColorStop(0, tone.glow + '0)');
        flare.addColorStop(0.5, tone.glow + '0.42)');
        flare.addColorStop(1, tone.glow + '0)');
        ctx.strokeStyle = flare;
        ctx.beginPath();
        ctx.moveTo(x - dx * reach, y - dy * reach);
        ctx.lineTo(x + dx * reach, y + dy * reach);
        ctx.stroke();
      }
      ctx.lineCap = 'butt';
    }

    const core = ctx.createRadialGradient(x - r * 0.3, y - r * 0.3, r * 0.05, x, y, r);
    core.addColorStop(0, tone.core);
    core.addColorStop(0.45, tone.mid);
    core.addColorStop(1, tone.edge);
    ctx.fillStyle = core;
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fill();
  }

  function drawPlanet(ctx, n, x, y, r) {
    const hue = n.hue;
    if (n.ringed && r > 3) {
      ctx.save();
      ctx.translate(x, y);
      ctx.rotate((n.hash % 70 - 25) * Math.PI / 180);
      ctx.beginPath();
      ctx.ellipse(0, 0, r * 2.2, r * 0.55, 0, Math.PI, Math.PI * 2);
      ctx.strokeStyle = `hsla(${hue + 20},40%,80%,0.55)`;
      ctx.lineWidth = Math.max(1, r * 0.22);
      ctx.stroke();
      ctx.restore();
    }

    if (r > 2.5) {
      const atmo = ctx.createRadialGradient(x, y, r * 0.9, x, y, r * 1.35);
      atmo.addColorStop(0, `hsla(${hue},70%,70%,0.32)`);
      atmo.addColorStop(1, `hsla(${hue},70%,70%,0)`);
      ctx.fillStyle = atmo;
      ctx.beginPath();
      ctx.arc(x, y, r * 1.35, 0, Math.PI * 2);
      ctx.fill();
    }

    const grad = ctx.createRadialGradient(x - r * 0.35, y - r * 0.35, r * 0.1, x, y, r);
    grad.addColorStop(0, `hsl(${hue},62%,78%)`);
    grad.addColorStop(0.45, `hsl(${hue},58%,52%)`);
    grad.addColorStop(1, `hsl(${hue + 10},55%,18%)`);
    ctx.fillStyle = grad;
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fill();

    if (n.banded && r > 5) {
      ctx.save();
      ctx.beginPath();
      ctx.arc(x, y, r, 0, Math.PI * 2);
      ctx.clip();
      ctx.fillStyle = `hsla(${hue + 8},50%,30%,0.35)`;
      ctx.beginPath();
      ctx.ellipse(x, y + r * 0.1, r * 1.1, r * 0.12, 0, 0, Math.PI * 2);
      ctx.fill();
      ctx.beginPath();
      ctx.ellipse(x, y + r * 0.42, r * 1.05, r * 0.08, 0, 0, Math.PI * 2);
      ctx.fill();
      ctx.restore();
    }

    if (n.ringed && r > 3) {
      ctx.save();
      ctx.translate(x, y);
      ctx.rotate((n.hash % 70 - 25) * Math.PI / 180);
      ctx.beginPath();
      ctx.ellipse(0, 0, r * 2.2, r * 0.55, 0, 0, Math.PI);
      ctx.strokeStyle = `hsla(${hue + 20},45%,86%,0.8)`;
      ctx.lineWidth = Math.max(1, r * 0.22);
      ctx.stroke();
      ctx.restore();
    }
  }

  function drawMoon(ctx, n, x, y, r) {
    const grad = ctx.createRadialGradient(x - r * 0.3, y - r * 0.3, r * 0.1, x, y, r);
    grad.addColorStop(0, '#f3f5fa');
    grad.addColorStop(0.5, '#b7bfcd');
    grad.addColorStop(1, '#39404d');
    ctx.fillStyle = grad;
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fill();
    if (r > 4) {
      const rand = rng(n.hash);
      ctx.fillStyle = 'rgba(70,78,95,0.45)';
      for (let i = 0; i < 3; i++) {
        const ang = rand() * Math.PI * 2;
        const d = Math.sqrt(rand()) * r * 0.55;
        ctx.beginPath();
        ctx.arc(x + Math.cos(ang) * d, y + Math.sin(ang) * d, r * (0.1 + rand() * 0.14), 0, Math.PI * 2);
        ctx.fill();
      }
    }
  }

  function drawAsteroid(ctx, n, x, y, r) {
    const sides = 8;
    ctx.beginPath();
    for (let i = 0; i < sides; i++) {
      const jitter = ((n.hash >>> ((i % 9) * 3)) & 7) / 7;
      const ang = (Math.PI * 2 * i) / sides + jitter * 0.3;
      const rad = r * (0.72 + jitter * 0.38);
      const px = x + Math.cos(ang) * rad;
      const py = y + Math.sin(ang) * rad;
      if (i === 0) ctx.moveTo(px, py); else ctx.lineTo(px, py);
    }
    ctx.closePath();
    const grad = ctx.createLinearGradient(x - r, y - r, x + r, y + r);
    grad.addColorStop(0, '#b8a790');
    grad.addColorStop(0.5, '#746a60');
    grad.addColorStop(1, '#2f2b30');
    ctx.fillStyle = grad;
    ctx.fill();
  }

  function drawMeteor(ctx, n, x, y, r, s) {
    ctx.save();
    ctx.translate(x, y);
    ctx.rotate(n.heading || 0);
    const tail = r * 6.5;
    const grad = ctx.createLinearGradient(-tail, 0, r, 0);
    grad.addColorStop(0, 'rgba(120,200,255,0)');
    grad.addColorStop(0.7, 'rgba(170,220,255,0.35)');
    grad.addColorStop(1, 'rgba(255,244,222,0.95)');
    ctx.fillStyle = grad;
    ctx.beginPath();
    ctx.moveTo(-tail, -r * 0.7);
    ctx.quadraticCurveTo(-r * 2.5, 0, r * 1.2, 0);
    ctx.quadraticCurveTo(-r * 2.5, 0, -tail, r * 0.7);
    ctx.closePath();
    ctx.fill();
    ctx.fillStyle = '#ffe9c8';
    ctx.beginPath();
    ctx.ellipse(0, 0, r * 1.4, r * 0.55, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();
  }

  function drawDust(ctx, n, x, y, r, time) {
    ctx.beginPath();
    ctx.arc(x, y, r * 1.8, 0, Math.PI * 2);
    ctx.fillStyle = 'rgba(190,200,255,0.06)';
    ctx.fill();
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.setLineDash([3, 3]);
    ctx.lineDashOffset = -time * 6;
    ctx.strokeStyle = 'rgba(200,210,255,0.6)';
    ctx.lineWidth = 1.2;
    ctx.stroke();
    ctx.setLineDash([]);
    ctx.lineDashOffset = 0;
  }

  function drawLabels(g, ctx, s) {
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    const font = getComputedStyle(document.documentElement).getPropertyValue('--font') || 'system-ui, sans-serif';
    for (const n of g.nodes) {
      if (!n.visible || n.alpha < 0.05) continue;
      const forced = n.hover > 0.3 || n.id === g.currentId || g.hits.has(n.id);
      let show = forced;
      if (!show) {
        if (n.spec.label === 'always') show = n.screenR >= 4;
        else if (n.spec.label === 'zoom') show = n.screenR >= (n.kind === 'planet' ? 7 : 8);
      }
      if (!show) continue;
      const x = n.screenX, y = n.screenY;
      if (x < -120 || y < -60 || x > g.width + 120 || y > g.height + 60) continue;

      const size = n.spec.font;
      ctx.font = `${n.spec.weight} ${size}px ${font}`;
      const alpha = n.alpha * (forced ? 1 : n.kind === 'star' ? 0.92 : 0.78);
      const label = n.icon ? `${n.icon} ${trim(n.title, n.kind === 'star' ? 28 : 20)}` : trim(n.title, n.kind === 'star' ? 28 : 20);
      const ly = y + n.screenR * (1 + n.hover * 0.12) + (n.kind === 'star' ? 8 : 6);
      ctx.lineWidth = 3;
      ctx.lineJoin = 'round';
      ctx.strokeStyle = `rgba(3,5,16,${(alpha * 0.85).toFixed(3)})`;
      ctx.strokeText(label, x, ly);
      ctx.fillStyle = n.missing
        ? `rgba(160,170,210,${alpha.toFixed(3)})`
        : `rgba(236,238,255,${alpha.toFixed(3)})`;
      ctx.fillText(label, x, ly);
    }
  }

  function trim(text, cap) {
    const t = text && text.trim().length ? text.trim() : 'Untitled';
    return t.length <= cap ? t : t.slice(0, cap - 1) + '…';
  }

  function drawComets(g, ctx) {
    for (const c of g.comets) {
      const fade = Math.sin(Math.PI * clamp(c.life / c.ttl, 0, 1));
      const dirX = c.vx / Math.hypot(c.vx, c.vy);
      const dirY = c.vy / Math.hypot(c.vx, c.vy);
      const tx = c.x - dirX * c.len;
      const ty = c.y - dirY * c.len;
      const grad = ctx.createLinearGradient(tx, ty, c.x, c.y);
      grad.addColorStop(0, 'rgba(160,210,255,0)');
      grad.addColorStop(1, `rgba(255,250,235,${(0.9 * fade).toFixed(3)})`);
      ctx.strokeStyle = grad;
      ctx.lineWidth = 1.6;
      ctx.beginPath();
      ctx.moveTo(tx, ty);
      ctx.lineTo(c.x, c.y);
      ctx.stroke();
      ctx.fillStyle = `rgba(255,255,255,${(0.95 * fade).toFixed(3)})`;
      ctx.beginPath();
      ctx.arc(c.x, c.y, 1.6, 0, Math.PI * 2);
      ctx.fill();
    }
  }

  window.synclyGalaxy = {
    mount,
    destroy,
    setData,
    setCurrent,
    setFilter,
    setMotion,
    isMotion,
    search,
    fit,
    locate,
  };
})();
