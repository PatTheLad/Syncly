// Galaxy renderer for the page graph. Blazor owns the data and the HUD; this file owns the
// canvas: camera, motion, hit-testing, lenses, and painting. Nothing here touches the CRDT.
(function () {
  const WORLD = 1000;               // layout units → world pixels at scale 1
  const MIN_SCALE = 0.12;
  const MAX_SCALE = 9;
  const FOCUS_DIM = 0.16;
  const HOVER_DEBOUNCE_MS = 40;
  const LONG_PRESS_MS = 500;
  const DAY_MS = 86400000;
  const RECENCY_SPAN_MS = 30 * DAY_MS;
  const OPTIONS_KEY = 'syncly.galaxy.options';
  const CAMERA_KEY = 'syncly.galaxy.cam.';

  const BODY = {
    star:     { r: 34,  min: 5.5, max: 190, font: 13, weight: 650, label: 'always', prio: 3 },
    planet:   { r: 17,  min: 2.8, max: 120, font: 12, weight: 550, label: 'zoom',   prio: 2 },
    moon:     { r: 10,  min: 1.9, max: 80,  font: 11, weight: 500, label: 'zoom',   prio: 1 },
    asteroid: { r: 6.5, min: 1.4, max: 50,  font: 11, weight: 500, label: 'hover',  prio: 0 },
    meteor:   { r: 4.5, min: 1.3, max: 40,  font: 11, weight: 500, label: 'hover',  prio: 0 },
    dust:     { r: 7,   min: 1.6, max: 60,  font: 11, weight: 500, label: 'zoom',   prio: 0 },
  };

  const STAR_TONES = [
    { core: '#ffffff', mid: '#dff1ff', edge: '#7fb8ff', glow: 'rgba(120,170,255,' },
    { core: '#ffffff', mid: '#fff6dc', edge: '#ffd27a', glow: 'rgba(255,214,130,' },
    { core: '#fffdf4', mid: '#ffe1a0', edge: '#f39a4a', glow: 'rgba(250,160,80,' },
    { core: '#fff2e0', mid: '#ffb98a', edge: '#e2634f', glow: 'rgba(240,120,110,' },
  ];

  const PLANET_HUES = [200, 168, 34, 120, 268, 12, 292, 48];

  const MINIMAP = { w: 160, h: 110, pad: 8, margin: 16, bottom: 46 };

  const DEFAULT_OPTIONS = { motion: true, labels: 'auto', density: 1, sound: false, effects: true };

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
  const easeOutCubic = (t) => 1 - Math.pow(1 - t, 3);

  function reducedMotionPreferred() {
    return !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  }

  function readJson(storage, key) {
    try {
      const raw = storage.getItem(key);
      return raw ? JSON.parse(raw) : null;
    } catch {
      return null;
    }
  }

  function writeJson(storage, key, value) {
    try {
      storage.setItem(key, JSON.stringify(value));
    } catch {
      // storage full or disabled; not important
    }
  }

  function loadOptions(overrides) {
    const stored = readJson(window.localStorage, OPTIONS_KEY) || {};
    const opt = Object.assign({}, DEFAULT_OPTIONS, stored, overrides || {});
    if (!('motion' in stored) && !(overrides && 'motion' in overrides) && reducedMotionPreferred()) opt.motion = false;
    if (reducedMotionPreferred() && !(overrides && overrides.effects)) opt.effects = false;
    opt.density = clamp(Number(opt.density) || 1, 0.5, 1.5);
    if (!['auto', 'all', 'min'].includes(opt.labels)) opt.labels = 'auto';
    return opt;
  }

  // ---------------------------------------------------------------- instance

  function mount(canvasId, dotnet, options) {
    destroy(canvasId);
    const canvas = document.getElementById(canvasId);
    if (!canvas || !canvas.getContext) return false;

    const opt = loadOptions(options && options.reducedMotion ? { motion: false, effects: false } : null);

    const g = {
      id: canvasId,
      canvas,
      ctx: canvas.getContext('2d', { alpha: false }),
      dotnet,
      width: 1,
      height: 1,
      dpr: 1,
      opt,
      nodes: [],
      byId: new Map(),
      edges: [],
      rings: [],
      motes: [],
      currentId: null,
      selectedId: null,
      spaceKey: null,
      motion: opt.motion,
      lens: 'none',
      timeCursor: null,
      timeWindow: 0,
      simTime: 0,
      lastFrame: 0,
      frame: 0,
      running: false,
      dirty: true,
      fitted: false,
      cam: { x: WORLD / 2, y: WORLD / 2, scale: 0.6 },
      target: { x: WORLD / 2, y: WORLD / 2, scale: 0.6 },
      flight: null,
      minScale: MIN_SCALE,
      camSaved: null,
      camMoved: true,
      hoverId: null,
      hoverClient: null,
      focus: null,
      focusEdges: null,
      hits: new Set(),
      hidden: new Set(),
      pointer: { down: false, dragging: false, id: null, type: 'mouse', x: 0, y: 0, startX: 0, startY: 0, moved: 0 },
      pinch: null,
      minimapDrag: false,
      hoverTimer: 0,
      longPressTimer: 0,
      pendingHover: undefined,
      focusSystemId: null,
      focusSystemTimer: 0,
      focusSystemCheck: 0,
      comets: [],
      nextComet: 4,
      backdrop: null,
      nebulaFar: null,
      nebulaNear: null,
      specks: [],
      labelWidths: new Map(),
      audio: null,
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
    saveCamera(g);
    stop(g);
    if (g.observer) g.observer.disconnect();
    for (const [target, type, fn, opts] of g.listeners) target.removeEventListener(type, fn, opts);
    window.clearTimeout(g.hoverTimer);
    window.clearTimeout(g.longPressTimer);
    window.clearTimeout(g.focusSystemTimer);
    if (g.audio && g.audio.close) g.audio.close().catch(() => {});
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

    const spaceKey = data.spaceKey ?? null;
    const spaceChanged = g.spaceKey !== spaceKey;
    if (spaceChanged && g.spaceKey !== null) saveCamera(g);
    g.spaceKey = spaceKey;
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
        createdAt: toMs(n.createdAt),
        updatedAt: toMs(n.updatedAt),
        inbound: n.inbound | 0,
        outbound: n.outbound | 0,
        degree: 0,
        baseX: n.x * WORLD,
        baseY: n.y * WORLD,
        radius: spec.r,
        hash: h,
        tone: h % STAR_TONES.length,
        hue: PLANET_HUES[h % PLANET_HUES.length] + ((h >>> 8) % 24) - 12,
        ringed: kind === 'planet' && h % 4 === 0,
        banded: kind === 'planet' && (h >>> 3) % 3 !== 1,
        cloudy: kind === 'planet' && (h >>> 6) % 3 !== 0,
        tilt: ((h % 70) - 25) * Math.PI / 180,
        dir: (h >>> 5) & 1 ? 1 : -1,
        phase: ((h >>> 9) % 1000) / 1000 * Math.PI * 2,
        x: n.x * WORLD,
        y: n.y * WORLD,
        orbitR: 0,
        angle0: 0,
        omega: 0,
        heading: 0,
        alpha: old ? old.alpha : 1,
        targetAlpha: 1,
        hover: old ? old.hover : 0,
        select: old ? old.select : 0,
        lensScale: 1,
        lensAlpha: 1,
        hot: 0,
        screenX: 0,
        screenY: 0,
        screenR: 0,
        visible: true,
        children: [],
        star: null,
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

    // Each body knows its star so day/night shading has a light source.
    for (const n of nodes) {
      let cursor = n;
      let guard = 0;
      while (cursor.parentId && guard++ < 64) cursor = byId.get(cursor.parentId);
      n.star = cursor === n ? null : cursor;
    }

    // Depth order so a moon's centre (its planet) is placed before the moon itself.
    nodes.sort((a, b) => a.depth - b.depth || b.radius - a.radius);

    g.nodes = nodes;
    g.byId = byId;
    g.edges = (data.edges || [])
      .filter((e) => byId.has(e.from) && byId.has(e.to) && e.from !== e.to)
      .filter((e) => byId.get(e.from).parentId !== e.to && byId.get(e.to).parentId !== e.from)
      .map((e) => ({ from: e.from, to: e.to }));
    for (const e of g.edges) {
      byId.get(e.from).degree++;
      byId.get(e.to).degree++;
    }

    // Link motes: one or two per wikilink, seeded so they do not all start together.
    g.motes = g.edges.map((e, i) => {
      const seed = hash(e.from + '→' + e.to);
      const rand = rng(seed);
      const count = 1 + (seed & 1);
      const list = [];
      for (let k = 0; k < count; k++) list.push({ edge: i, t: rand(), speed: 0.1 + rand() * 0.1, size: 1.2 + rand() * 0.9 });
      return list;
    }).flat();

    // One ring per distinct child orbit radius per parent, with a belt when it carries rubble.
    const rings = [];
    for (const p of nodes) {
      if (p.children.length === 0) continue;
      const seen = new Map();
      for (const c of p.children) {
        if (c.orbitR < 0.5) continue;
        const key = Math.round(c.orbitR * 2);
        let ring = seen.get(key);
        if (!ring) {
          ring = { parent: p, radius: c.orbitR, depth: p.depth, dust: c.missing, rubble: 0, belt: null, omega: c.omega, dir: c.dir };
          seen.set(key, ring);
        } else if (!c.missing) {
          ring.dust = false;
        }
        if (c.kind === 'asteroid' || c.kind === 'meteor') ring.rubble++;
      }
      for (const ring of seen.values()) {
        if (ring.rubble > 0) {
          const rand = rng(hash(ring.parent.id + ':' + ring.radius));
          const belt = [];
          const count = 28 + Math.min(40, ring.rubble * 8);
          for (let i = 0; i < count; i++) {
            belt.push({ a: rand() * Math.PI * 2, dr: (rand() - 0.5) * ring.radius * 0.12, s: 0.6 + rand() * 1.1, l: 0.35 + rand() * 0.5 });
          }
          ring.belt = belt;
        }
        rings.push(ring);
      }
    }
    g.rings = rings;

    applyVisibility(g);
    if (g.hoverId && !byId.has(g.hoverId)) clearHover(g, true);
    if (g.selectedId && !byId.has(g.selectedId)) g.selectedId = null;
    for (const id of [...g.hits]) if (!byId.has(id)) g.hits.delete(id);

    g.minScale = Math.min(MIN_SCALE, fitScale(g, worldBounds(g, nodes)) * 0.45);

    if (!g.fitted || spaceChanged) {
      const remembered = loadCamera(g);
      if (remembered) {
        g.cam.x = g.target.x = remembered.x;
        g.cam.y = g.target.y = remembered.y;
        g.cam.scale = g.target.scale = clamp(remembered.scale, g.minScale, MAX_SCALE);
        g.flight = null;
      } else {
        introFlight(g);
      }
      g.fitted = true;
    }

    g.camMoved = true;
    invalidate(g);
  }

  function toMs(value) {
    if (!value) return 0;
    if (typeof value === 'number') return value;
    const t = Date.parse(value);
    return Number.isFinite(t) && t > 0 ? t : 0;
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
    applyVisibility(g);
    invalidate(g);
  }

  function applyVisibility(g) {
    for (const n of g.nodes) {
      const parentHidden = n.parentId ? g.byId.get(n.parentId).visible === false : false;
      const unborn = g.timeCursor !== null && n.createdAt > 0 && n.createdAt > g.timeCursor;
      n.visible = !g.hidden.has(n.kind) && !parentHidden && !unborn;
    }
  }

  function setMotion(canvasId, enabled) {
    const g = instances.get(canvasId);
    if (!g) return;
    g.motion = !!enabled;
    g.opt.motion = g.motion;
    writeJson(window.localStorage, OPTIONS_KEY, g.opt);
    invalidate(g);
  }

  function isMotion(canvasId) {
    const g = instances.get(canvasId);
    return g ? g.motion : false;
  }

  function setOptions(canvasId, patch) {
    const g = instances.get(canvasId);
    if (!g) return null;
    const before = g.opt.density;
    Object.assign(g.opt, patch || {});
    g.opt.density = clamp(Number(g.opt.density) || 1, 0.5, 1.5);
    if (!['auto', 'all', 'min'].includes(g.opt.labels)) g.opt.labels = 'auto';
    g.motion = !!g.opt.motion;
    writeJson(window.localStorage, OPTIONS_KEY, g.opt);
    if (before !== g.opt.density) buildSpecks(g);
    if (g.opt.sound) ensureAudio(g);
    invalidate(g);
    return Object.assign({}, g.opt);
  }

  function getOptions(canvasId) {
    const g = instances.get(canvasId);
    return g ? Object.assign({}, g.opt) : Object.assign({}, DEFAULT_OPTIONS);
  }

  function setLens(canvasId, lens) {
    const g = instances.get(canvasId);
    if (!g) return;
    g.lens = ['recency', 'links', 'orphans'].includes(lens) ? lens : 'none';
    invalidate(g);
  }

  function setTimeCursor(canvasId, ms, windowMs) {
    const g = instances.get(canvasId);
    if (!g) return;
    g.timeCursor = ms === null || ms === undefined ? null : Number(ms);
    g.timeWindow = Math.max(0, Number(windowMs) || 0);
    applyVisibility(g);
    invalidate(g);
  }

  function select(canvasId, id, fly) {
    const g = instances.get(canvasId);
    if (!g) return false;
    const n = id ? g.byId.get(id) : null;
    g.selectedId = n ? n.id : null;
    if (n && fly) flyToNode(g, n);
    invalidate(g);
    return !!n;
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
    g.flight = null;
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

  function flyTo(canvasId, id) {
    const g = instances.get(canvasId);
    if (!g) return false;
    const n = g.byId.get(id);
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
    g.flight = null;
    g.target.scale = scale;
    g.target.x = (bounds.minX + bounds.maxX) / 2;
    g.target.y = (bounds.minY + bounds.maxY) / 2;
    invalidate(g);
  }

  function introFlight(g) {
    const bounds = worldBounds(g, g.nodes.filter((n) => n.visible));
    const scale = fitScale(g, bounds);
    const cx = (bounds.minX + bounds.maxX) / 2;
    const cy = (bounds.minY + bounds.maxY) / 2;
    g.target.x = cx;
    g.target.y = cy;
    g.target.scale = scale;
    if (!g.motion) {
      g.cam.x = cx; g.cam.y = cy; g.cam.scale = scale;
      g.flight = null;
      return;
    }
    g.cam.x = cx;
    g.cam.y = cy;
    g.cam.scale = Math.max(g.minScale, scale * 0.4);
    g.flight = { from: { x: cx, y: cy, scale: g.cam.scale }, to: { x: cx, y: cy, scale }, t: 0, dur: 1.2 };
  }

  // Camera memory ---------------------------------------------------------

  function cameraKey(g) {
    return g.spaceKey ? CAMERA_KEY + g.spaceKey : null;
  }

  function loadCamera(g) {
    const key = cameraKey(g);
    if (!key) return null;
    const cam = readJson(window.sessionStorage, key);
    if (!cam || !Number.isFinite(cam.x) || !Number.isFinite(cam.y) || !Number.isFinite(cam.scale)) return null;
    return cam;
  }

  function saveCamera(g) {
    const key = cameraKey(g);
    if (!key || !g.fitted) return;
    const snap = { x: Math.round(g.cam.x * 10) / 10, y: Math.round(g.cam.y * 10) / 10, scale: Math.round(g.cam.scale * 1000) / 1000 };
    if (g.camSaved && g.camSaved.x === snap.x && g.camSaved.y === snap.y && g.camSaved.scale === snap.scale) return;
    g.camSaved = snap;
    writeJson(window.sessionStorage, key, snap);
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
    buildSpecks(g);
    if (wasFitted && g.nodes.length && !g.flight) {
      // Keep the whole galaxy in view when the window changes shape.
      const bounds = worldBounds(g, g.nodes.filter((n) => n.visible));
      const s = fitScale(g, bounds);
      if (Math.abs(g.target.scale - g.cam.scale) < 0.001 && Math.abs(g.cam.scale - s) > 0.001 && isNearFit(g, bounds)) {
        g.target.scale = s;
        g.cam.scale = s;
      }
    }
    g.camMoved = true;
    invalidate(g);
  }

  function isNearFit(g, bounds) {
    const cx = (bounds.minX + bounds.maxX) / 2;
    const cy = (bounds.minY + bounds.maxY) / 2;
    return Math.hypot(g.cam.x - cx, g.cam.y - cy) < 4;
  }

  function layer(g, w, h) {
    const off = document.createElement('canvas');
    off.width = Math.max(1, Math.round(w * g.dpr));
    off.height = Math.max(1, Math.round(h * g.dpr));
    const c = off.getContext('2d');
    c.setTransform(g.dpr, 0, 0, g.dpr, 0, 0);
    return { canvas: off, ctx: c };
  }

  function buildBackdrop(g) {
    const seed = hash('galaxy:' + (g.spaceKey || 'space'));
    const rand = rng(seed);
    const w = g.width, h = g.height;

    // Base: space gradient plus vignette; this one never moves.
    const base = layer(g, w, h);
    const c = base.ctx;
    const space = c.createRadialGradient(w * 0.5, h * 0.46, 0, w * 0.5, h * 0.46, Math.max(w, h) * 0.78);
    space.addColorStop(0, '#11142b');
    space.addColorStop(0.5, '#080a18');
    space.addColorStop(1, '#020309');
    c.fillStyle = space;
    c.fillRect(0, 0, w, h);

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
    g.backdrop = base.canvas;

    // Two nebula layers, oversized so parallax can slide them without showing edges.
    const over = 1.3;
    const palette = [
      [118, 104, 255], [78, 178, 232], [214, 96, 168], [92, 110, 220],
    ];
    const paint = (count, sizeMin, sizeMax, alphaMin, alphaMax) => {
      const lw = w * over, lh = h * over;
      const l = layer(g, lw, lh);
      for (let i = 0; i < count; i++) {
        const [r, gg, b] = palette[(i + (seed >>> 3)) % palette.length];
        const x = lw * (0.08 + rand() * 0.84);
        const y = lh * (0.1 + rand() * 0.8);
        const rad = Math.max(lw, lh) * (sizeMin + rand() * (sizeMax - sizeMin));
        const grad = l.ctx.createRadialGradient(x, y, 0, x, y, rad);
        grad.addColorStop(0, `rgba(${r},${gg},${b},${(alphaMin + rand() * (alphaMax - alphaMin)).toFixed(3)})`);
        grad.addColorStop(0.55, `rgba(${r},${gg},${b},0.035)`);
        grad.addColorStop(1, `rgba(${r},${gg},${b},0)`);
        l.ctx.fillStyle = grad;
        l.ctx.save();
        l.ctx.translate(x, y);
        l.ctx.rotate(rand() * Math.PI);
        l.ctx.scale(1, 0.5 + rand() * 0.45);
        l.ctx.translate(-x, -y);
        l.ctx.fillRect(x - rad, y - rad, rad * 2, rad * 2);
        l.ctx.restore();
      }
      return l.canvas;
    };
    g.nebulaFar = paint(4 + Math.round(rand() * 2), 0.2, 0.42, 0.1, 0.16);
    g.nebulaNear = paint(3 + Math.round(rand() * 2), 0.08, 0.2, 0.12, 0.2);

    // Vignette drawn last on the base so it sits above the nebulae visually? No: nebulae are
    // drawn over the base, so the vignette becomes its own tiny layer painted after them.
    const vig = layer(g, w, h);
    const vg = vig.ctx.createRadialGradient(w / 2, h / 2, Math.min(w, h) * 0.35, w / 2, h / 2, Math.max(w, h) * 0.78);
    vg.addColorStop(0, 'rgba(0,0,0,0)');
    vg.addColorStop(1, 'rgba(0,1,8,0.75)');
    vig.ctx.fillStyle = vg;
    vig.ctx.fillRect(0, 0, w, h);
    g.vignette = vig.canvas;
  }

  function buildSpecks(g) {
    const rand = rng(hash('specks:' + (g.spaceKey || 'space')));
    const w = g.width, h = g.height;
    const count = clamp(Math.round((w * h) / 4200 * g.opt.density), 80, 800);
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

  // ---------------------------------------------------------------- audio

  function ensureAudio(g) {
    if (g.audio || !g.opt.sound) return;
    const Ctx = window.AudioContext || window.webkitAudioContext;
    if (!Ctx) return;
    try { g.audio = new Ctx(); } catch { g.audio = null; }
  }

  function hoverTick(g) {
    if (!g.opt.sound || !g.audio) return;
    const a = g.audio;
    if (a.state === 'suspended') a.resume().catch(() => {});
    try {
      const t0 = a.currentTime;
      const osc = a.createOscillator();
      const gain = a.createGain();
      osc.type = 'sine';
      osc.frequency.setValueAtTime(880, t0);
      osc.frequency.exponentialRampToValueAtTime(520, t0 + 0.04);
      gain.gain.setValueAtTime(0.03, t0);
      gain.gain.exponentialRampToValueAtTime(0.0001, t0 + 0.06);
      osc.connect(gain).connect(a.destination);
      osc.start(t0);
      osc.stop(t0 + 0.07);
    } catch {
      // audio unavailable; stay silent
    }
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
      if (g.opt.sound) ensureAudio(g);

      if (g.pointer.down && e.pointerId !== g.pointer.id) {
        // Second finger: start a pinch.
        window.clearTimeout(g.longPressTimer);
        g.pinch = { idA: g.pointer.id, idB: e.pointerId, ax: g.pointer.x, ay: g.pointer.y, bx: px, by: py };
        g.pointer.dragging = true;
        return;
      }

      if (e.button === 2) return; // contextmenu handles it

      g.pointer.down = true;
      g.pointer.dragging = false;
      g.pointer.id = e.pointerId;
      g.pointer.type = e.pointerType || 'mouse';
      g.pointer.x = g.pointer.startX = px;
      g.pointer.y = g.pointer.startY = py;
      g.pointer.moved = 0;
      canvas.classList.add('is-pressing');

      if (inMinimap(g, px, py)) {
        g.minimapDrag = true;
        minimapJump(g, px, py);
        return;
      }

      if (g.pointer.type === 'touch') {
        window.clearTimeout(g.longPressTimer);
        g.longPressTimer = window.setTimeout(() => {
          g.longPressTimer = 0;
          if (!g.pointer.down || g.pointer.dragging) return;
          g.pointer.down = false;
          canvas.classList.remove('is-pressing');
          const hit = hitTest(g, g.pointer.x, g.pointer.y);
          notify(g, 'OnContextMenu', hit ? hit.id : null, e.clientX, e.clientY);
        }, LONG_PRESS_MS);
      }
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
        if (g.minimapDrag) {
          minimapJump(g, px, py);
          g.pointer.x = px;
          g.pointer.y = py;
          return;
        }
        const dx = px - g.pointer.x;
        const dy = py - g.pointer.y;
        g.pointer.moved += Math.abs(dx) + Math.abs(dy);
        if (!g.pointer.dragging && g.pointer.moved > 5) {
          g.pointer.dragging = true;
          window.clearTimeout(g.longPressTimer);
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
      if (inMinimap(g, px, py)) {
        canvas.classList.toggle('is-hover', true);
        if (g.hoverId) clearHover(g, false);
        return;
      }
      updateHover(g, px, py, e.clientX, e.clientY);
    });

    const release = (e) => {
      window.clearTimeout(g.longPressTimer);
      if (g.pinch && (e.pointerId === g.pinch.idA || e.pointerId === g.pinch.idB)) {
        g.pinch = null;
        g.pointer.down = false;
        g.pointer.dragging = false;
        canvas.classList.remove('is-dragging', 'is-pressing');
        return;
      }
      if (!g.pointer.down || e.pointerId !== g.pointer.id) return;
      const wasDrag = g.pointer.dragging;
      const wasMinimap = g.minimapDrag;
      g.minimapDrag = false;
      g.pointer.down = false;
      g.pointer.dragging = false;
      canvas.classList.remove('is-dragging', 'is-pressing');
      if (wasDrag || wasMinimap || e.type === 'pointercancel') return;

      const hit = hitTest(g, g.pointer.x, g.pointer.y);
      g.selectedId = hit ? hit.id : null;
      invalidate(g);
      notify(g, 'OnSelect', g.selectedId, e.clientX, e.clientY);
    };
    on(g, canvas, 'pointerup', release);
    on(g, canvas, 'pointercancel', release);

    on(g, canvas, 'dblclick', (e) => {
      e.preventDefault();
      const rect = canvas.getBoundingClientRect();
      const px = e.clientX - rect.left;
      const py = e.clientY - rect.top;
      if (inMinimap(g, px, py)) return;
      const hit = hitTest(g, px, py);
      if (hit) flyToNode(g, hit);
      else zoomAt(g, px, py, 1.8);
    });

    on(g, canvas, 'contextmenu', (e) => {
      e.preventDefault();
      const rect = canvas.getBoundingClientRect();
      const px = e.clientX - rect.left;
      const py = e.clientY - rect.top;
      if (inMinimap(g, px, py)) return;
      const hit = hitTest(g, px, py);
      if (hit) {
        g.selectedId = hit.id;
        invalidate(g);
      }
      notify(g, 'OnContextMenu', hit ? hit.id : null, e.clientX, e.clientY);
    });

    on(g, canvas, 'pointerleave', () => {
      if (!g.pointer.down) clearHover(g, false);
    });

    on(g, canvas, 'keydown', (e) => {
      if (g.opt.sound) ensureAudio(g);
      if (e.ctrlKey || e.metaKey || e.altKey) return;
      const selected = g.selectedId ? g.byId.get(g.selectedId) : null;
      const step = 60 / g.cam.scale;
      switch (e.key) {
        case 'Home':
          fit(g.id, false);
          break;
        case 'Escape':
          if (g.selectedId) {
            g.selectedId = null;
            notify(g, 'OnSelect', null, 0, 0);
          } else {
            fit(g.id, false);
          }
          break;
        case 'Enter':
          if (selected) notify(g, 'OnOpen', selected.id);
          else return;
          break;
        case 'f':
        case 'F':
          if (selected) flyToNode(g, selected);
          else fit(g.id, false);
          break;
        case '/':
          notify(g, 'OnFocusSearch');
          break;
        case '+':
        case '=':
          zoomAt(g, g.width / 2, g.height / 2, 1.3);
          break;
        case '-':
        case '_':
          zoomAt(g, g.width / 2, g.height / 2, 1 / 1.3);
          break;
        case 'Tab':
          if (!selected) return;
          cycleSibling(g, selected, e.shiftKey ? -1 : 1);
          break;
        case 'ArrowLeft':
          if (selected) moveSelection(g, selected, -1, 0); else g.target.x -= step;
          break;
        case 'ArrowRight':
          if (selected) moveSelection(g, selected, 1, 0); else g.target.x += step;
          break;
        case 'ArrowUp':
          if (selected) moveSelection(g, selected, 0, -1); else g.target.y -= step;
          break;
        case 'ArrowDown':
          if (selected) moveSelection(g, selected, 0, 1); else g.target.y += step;
          break;
        default: return;
      }
      e.preventDefault();
      g.flight = null;
      invalidate(g);
    });

    on(g, document, 'visibilitychange', () => {
      if (document.hidden) { saveCamera(g); stop(g); }
      else invalidate(g);
    });
  }

  function zoomAt(g, px, py, factor, immediate) {
    g.flight = null;
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
    g.flight = null;
    g.target.x -= dx / g.cam.scale;
    g.target.y -= dy / g.cam.scale;
    if (immediate) {
      g.cam.x = g.target.x;
      g.cam.y = g.target.y;
    }
    invalidate(g);
  }

  // Keyboard navigation ----------------------------------------------------

  function moveSelection(g, from, dx, dy) {
    let best = null;
    let bestScore = Infinity;
    for (const n of g.nodes) {
      if (n === from || !n.visible || n.alpha < 0.05) continue;
      const vx = n.screenX - from.screenX;
      const vy = n.screenY - from.screenY;
      const along = vx * dx + vy * dy;
      if (along <= 0) continue;
      const perp = Math.abs(vx * dy - vy * dx);
      if (perp > along * 1.4) continue;
      const score = along + perp * 2;
      if (score < bestScore) { bestScore = score; best = n; }
    }
    if (!best) return;
    g.selectedId = best.id;
    keepOnScreen(g, best);
    notify(g, 'OnSelect', best.id, screenToClient(g, best.screenX), screenToClientY(g, best.screenY));
  }

  function cycleSibling(g, from, dir) {
    const parent = from.parentId ? g.byId.get(from.parentId) : null;
    const siblings = (parent ? parent.children : g.nodes.filter((n) => !n.parentId)).filter((n) => n.visible);
    if (siblings.length < 2) return;
    const idx = siblings.indexOf(from);
    const next = siblings[(idx + dir + siblings.length) % siblings.length];
    g.selectedId = next.id;
    keepOnScreen(g, next);
    notify(g, 'OnSelect', next.id, screenToClient(g, next.screenX), screenToClientY(g, next.screenY));
  }

  function keepOnScreen(g, n) {
    const margin = 80;
    if (n.screenX < margin || n.screenY < margin || n.screenX > g.width - margin || n.screenY > g.height - margin) {
      g.flight = null;
      g.target.x = n.x;
      g.target.y = n.y;
    }
  }

  function screenToClient(g, sx) {
    return g.canvas.getBoundingClientRect().left + sx;
  }

  function screenToClientY(g, sy) {
    return g.canvas.getBoundingClientRect().top + sy;
  }

  // Minimap -----------------------------------------------------------------

  function minimapVisible(g) {
    return g.nodes.length > 0 && g.width >= 560 && g.height >= 360;
  }

  function minimapRect(g) {
    return { x: g.width - MINIMAP.w - MINIMAP.margin, y: g.height - MINIMAP.h - MINIMAP.bottom, w: MINIMAP.w, h: MINIMAP.h };
  }

  function inMinimap(g, px, py) {
    if (!minimapVisible(g)) return false;
    const r = minimapRect(g);
    return px >= r.x && py >= r.y && px <= r.x + r.w && py <= r.y + r.h;
  }

  function minimapTransform(g) {
    const r = minimapRect(g);
    const bounds = worldBounds(g, g.nodes.filter((n) => n.visible));
    const bw = Math.max(40, bounds.maxX - bounds.minX);
    const bh = Math.max(40, bounds.maxY - bounds.minY);
    const scale = Math.min((r.w - MINIMAP.pad * 2) / bw, (r.h - MINIMAP.pad * 2) / bh);
    const ox = r.x + r.w / 2 - ((bounds.minX + bounds.maxX) / 2) * scale;
    const oy = r.y + r.h / 2 - ((bounds.minY + bounds.maxY) / 2) * scale;
    return { r, scale, ox, oy };
  }

  function minimapJump(g, px, py) {
    const m = minimapTransform(g);
    g.flight = null;
    g.target.x = (px - m.ox) / m.scale;
    g.target.y = (py - m.oy) / m.scale;
    g.cam.x = g.target.x;
    g.cam.y = g.target.y;
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
    if (hit) hoverTick(g);
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

  // Focused system (breadcrumb) --------------------------------------------

  function updateFocusSystem(g, now) {
    if (now - g.focusSystemCheck < 120) return;
    g.focusSystemCheck = now;

    let result = null;
    if (g.nodes.length > 0) {
      const all = worldBounds(g, g.nodes.filter((n) => n.visible));
      if (g.cam.scale > fitScale(g, all) * 1.5) {
        const vw = g.width / g.cam.scale;
        const vh = g.height / g.cam.scale;
        const minSpan = Math.min(vw, vh) * 0.45;
        let bestDepth = -1;
        for (const n of g.nodes) {
          if (n.children.length === 0 || !n.visible || n.depth <= bestDepth) continue;
          const b = worldBounds(g, systemOf(n));
          if (g.cam.x < b.minX || g.cam.x > b.maxX || g.cam.y < b.minY || g.cam.y > b.maxY) continue;
          if (Math.max(b.maxX - b.minX, b.maxY - b.minY) < minSpan) continue;
          bestDepth = n.depth;
          result = n.id;
        }
      }
    }

    if (result !== g.focusSystemId) {
      g.focusSystemId = result;
      window.clearTimeout(g.focusSystemTimer);
      g.focusSystemTimer = window.setTimeout(() => {
        g.focusSystemTimer = 0;
        notify(g, 'OnFocusSystem', g.focusSystemId);
      }, 150);
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
    const c = g.cam, t = g.target;

    if (g.flight) {
      const f = g.flight;
      f.t = Math.min(1, f.t + dt / f.dur);
      const k = easeOutCubic(f.t);
      c.x = lerp(f.from.x, f.to.x, k);
      c.y = lerp(f.from.y, f.to.y, k);
      c.scale = lerp(f.from.scale, f.to.scale, k);
      t.x = f.to.x; t.y = f.to.y; t.scale = f.to.scale;
      if (f.t >= 1) g.flight = null;
      animating = true;
      g.camMoved = true;
    } else {
      const k = ease(dt, 8.5);
      if (Math.abs(c.x - t.x) > 0.01 || Math.abs(c.y - t.y) > 0.01 || Math.abs(c.scale - t.scale) > 0.0005) {
        c.x = lerp(c.x, t.x, k);
        c.y = lerp(c.y, t.y, k);
        c.scale = lerp(c.scale, t.scale, k);
        animating = true;
        g.camMoved = true;
      } else if (g.camMoved) {
        c.x = t.x; c.y = t.y; c.scale = t.scale;
        saveCamera(g);
      }
    }

    if (g.motion) {
      g.simTime += dt;
      animating = true;
    }

    const nowMs = Date.now();
    const ka = ease(dt, 10);
    for (const n of g.nodes) {
      applyLens(g, n, nowMs);
      const focusTarget = g.focus ? (g.focus.has(n.id) ? 1 : FOCUS_DIM) : 1;
      n.targetAlpha = n.visible ? Math.max(focusTarget * n.lensAlpha, g.focus && g.focus.has(n.id) ? 0.9 : 0) : 0;
      if (Math.abs(n.alpha - n.targetAlpha) > 0.004) { n.alpha = lerp(n.alpha, n.targetAlpha, ka); animating = true; }
      else n.alpha = n.targetAlpha;
      const hoverTarget = n.id === g.hoverId ? 1 : 0;
      if (Math.abs(n.hover - hoverTarget) > 0.004) { n.hover = lerp(n.hover, hoverTarget, ka); animating = true; }
      else n.hover = hoverTarget;
      const selectTarget = n.id === g.selectedId ? 1 : 0;
      if (Math.abs(n.select - selectTarget) > 0.004) { n.select = lerp(n.select, selectTarget, ka); animating = true; }
      else n.select = selectTarget;
    }

    if (g.hits.size > 0 || g.currentId || g.selectedId) animating = animating || g.motion;

    if (g.opt.effects) {
      updateComets(g, dt);
      if (g.comets.length > 0) animating = true;
      if (g.motion) for (const m of g.motes) { m.t += dt * m.speed; if (m.t > 1) m.t -= 1; }
    } else {
      g.comets.length = 0;
    }

    layoutPositions(g);
    draw(g, now / 1000, nowMs);
    if (g.camMoved) {
      updateFocusSystem(g, now);
      if (!animating) g.camMoved = false;
    }
    g.dirty = false;

    if (animating || g.dirty) {
      g.frame = window.requestAnimationFrame((ts) => tick(g, ts));
    } else {
      g.running = false;
      g.frame = 0;
    }
  }

  function applyLens(g, n, nowMs) {
    n.lensAlpha = 1;
    n.lensScale = 1;
    n.hot = 0;
    switch (g.lens) {
      case 'recency': {
        const stamp = n.updatedAt || n.createdAt;
        if (stamp > 0) {
          const age = Math.max(0, nowMs - stamp);
          n.hot = clamp(1 - age / RECENCY_SPAN_MS, 0, 1);
        }
        n.lensAlpha = n.missing ? 0.35 : 0.4 + 0.6 * Math.sqrt(n.hot);
        break;
      }
      case 'links':
        n.lensScale = 1 + 0.35 * Math.log2(n.inbound + 1);
        n.lensAlpha = n.degree > 0 || n.kind === 'star' ? 1 : 0.45;
        break;
      case 'orphans': {
        const orphan = !n.parentId && n.children.length === 0 && n.degree === 0 && !n.missing;
        n.lensAlpha = orphan || n.missing ? 1 : 0.14;
        break;
      }
      default:
        break;
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

  function draw(g, time, nowMs) {
    const ctx = g.ctx;
    const w = g.width, h = g.height;
    ctx.setTransform(g.dpr, 0, 0, g.dpr, 0, 0);

    if (g.backdrop) ctx.drawImage(g.backdrop, 0, 0, w, h);
    else { ctx.fillStyle = '#05060f'; ctx.fillRect(0, 0, w, h); }

    drawNebula(g, ctx, g.nebulaFar, 0.018);
    drawNebula(g, ctx, g.nebulaNear, 0.045);
    if (g.vignette) ctx.drawImage(g.vignette, 0, 0, w, h);
    drawSpecks(g, ctx, time);

    const cx = w / 2, cy = h / 2;
    const s = g.cam.scale;
    const toX = (x) => (x - g.cam.x) * s + cx;
    const toY = (y) => (y - g.cam.y) * s + cy;

    // Project everything once.
    for (const n of g.nodes) {
      n.screenX = toX(n.x);
      n.screenY = toY(n.y);
      n.screenR = clamp(n.radius * s, n.spec.min, n.spec.max) * n.lensScale;
    }

    drawRings(g, ctx, toX, toY, s, time);
    drawLinks(g, ctx);
    drawBodies(g, ctx, time, s, nowMs);
    drawLabels(g, ctx, s);
    drawComets(g, ctx);
    drawMinimap(g, ctx);
  }

  function drawNebula(g, ctx, img, depth) {
    if (!img) return;
    const w = g.width, h = g.height;
    const over = 1.3;
    const lw = w * over, lh = h * over;
    const maxShift = (lw - w) / 2;
    const ox = clamp(-(g.cam.x - WORLD / 2) * depth * Math.sqrt(g.cam.scale), -maxShift, maxShift);
    const oy = clamp(-(g.cam.y - WORLD / 2) * depth * Math.sqrt(g.cam.scale), -maxShift, maxShift);
    ctx.drawImage(img, -(lw - w) / 2 + ox, -(lh - h) / 2 + oy, lw, lh);
  }

  function drawSpecks(g, ctx, time) {
    const w = g.width, h = g.height;
    const camX = g.cam.x, camY = g.cam.y;
    const motion = g.motion;
    for (const sp of g.specks) {
      // Parallax: the far field slides a little against the camera and wraps.
      const x = ((sp.x * w - camX * sp.depth * g.cam.scale * 8) % w + w) % w;
      const y = ((sp.y * h - camY * sp.depth * g.cam.scale * 8) % h + h) % h;
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

  function drawRings(g, ctx, toX, toY, s, time) {
    ctx.lineWidth = 1;
    for (const ring of g.rings) {
      const p = ring.parent;
      if (!p.visible) continue;
      const alpha = p.alpha * (ring.depth === 0 ? 0.26 : ring.depth === 1 ? 0.2 : 0.14) * (ring.dust ? 0.6 : 1);
      if (alpha < 0.01) continue;
      const r = ring.radius * s;
      if (r < 6) continue;
      const px = toX(p.x), py = toY(p.y);
      ctx.beginPath();
      ctx.arc(px, py, r, 0, Math.PI * 2);
      ctx.strokeStyle = ring.depth === 0
        ? `rgba(255,214,140,${alpha.toFixed(3)})`
        : `rgba(170,190,255,${alpha.toFixed(3)})`;
      if (ring.dust || ring.depth >= 2) ctx.setLineDash([3, 6]);
      else ctx.setLineDash([]);
      ctx.stroke();

      if (ring.belt && g.opt.effects && r > 18) {
        // Rubble drifting along the ring, a little slower than the worlds on it.
        const spin = g.simTime * ring.omega * ring.dir * 0.7;
        const beltAlpha = Math.min(1, alpha * 3);
        for (const b of ring.belt) {
          const ang = b.a + spin;
          const rr = r + b.dr * s;
          const bx = px + Math.cos(ang) * rr;
          const by = py + Math.sin(ang) * rr;
          ctx.fillStyle = `rgba(210,196,176,${(beltAlpha * b.l).toFixed(3)})`;
          const size = Math.max(0.8, b.s * Math.min(1.6, s * 0.9));
          ctx.fillRect(bx - size / 2, by - size / 2, size, size);
        }
      }
    }
    ctx.setLineDash([]);
  }

  function edgeGeometry(a, b) {
    const x1 = a.screenX, y1 = a.screenY, x2 = b.screenX, y2 = b.screenY;
    const dx = x2 - x1, dy = y2 - y1;
    const len = Math.max(1, Math.hypot(dx, dy));
    const bend = Math.min(80, len * 0.16);
    return { x1, y1, x2, y2, cx: (x1 + x2) / 2 - dy / len * bend, cy: (y1 + y2) / 2 + dx / len * bend };
  }

  function drawLinks(g, ctx) {
    const geometries = new Array(g.edges.length);
    const alphas = new Array(g.edges.length);
    ctx.lineWidth = 1.2;
    g.edges.forEach((e, i) => {
      const a = g.byId.get(e.from);
      const b = g.byId.get(e.to);
      if (!a || !b || !a.visible || !b.visible) return;
      const lit = g.focusEdges ? g.focusEdges.has(i) : false;
      const linkedToCurrent = g.currentId && (a.id === g.currentId || b.id === g.currentId);
      const linkedToSelected = g.selectedId && (a.id === g.selectedId || b.id === g.selectedId);
      let alpha = Math.min(a.alpha, b.alpha) * 0.28;
      if (lit) alpha = 0.85;
      else if (g.focus) alpha *= 0.5;
      else if (linkedToSelected) alpha = 0.7;
      else if (linkedToCurrent) alpha = 0.5;
      if (alpha < 0.01) return;

      const geo = edgeGeometry(a, b);
      geometries[i] = geo;
      alphas[i] = alpha;
      ctx.beginPath();
      ctx.moveTo(geo.x1, geo.y1);
      ctx.quadraticCurveTo(geo.cx, geo.cy, geo.x2, geo.y2);
      ctx.strokeStyle = lit || linkedToSelected
        ? `rgba(196,176,255,${alpha.toFixed(3)})`
        : `rgba(150,140,230,${alpha.toFixed(3)})`;
      ctx.setLineDash(lit || linkedToSelected ? [] : [5, 7]);
      ctx.lineWidth = lit ? 1.8 : 1.1;
      ctx.stroke();
    });
    ctx.setLineDash([]);

    if (!g.opt.effects || !g.motion) return;
    for (const m of g.motes) {
      const geo = geometries[m.edge];
      if (!geo) continue;
      const t = m.t, u = 1 - t;
      const x = u * u * geo.x1 + 2 * u * t * geo.cx + t * t * geo.x2;
      const y = u * u * geo.y1 + 2 * u * t * geo.cy + t * t * geo.y2;
      const a = Math.min(1, alphas[m.edge] * 2.2) * Math.sin(Math.PI * t);
      if (a < 0.02) continue;
      const glow = ctx.createRadialGradient(x, y, 0, x, y, m.size * 3);
      glow.addColorStop(0, `rgba(220,210,255,${a.toFixed(3)})`);
      glow.addColorStop(0.4, `rgba(190,170,255,${(a * 0.5).toFixed(3)})`);
      glow.addColorStop(1, 'rgba(190,170,255,0)');
      ctx.fillStyle = glow;
      ctx.beginPath();
      ctx.arc(x, y, m.size * 3, 0, Math.PI * 2);
      ctx.fill();
    }
  }

  function lightAngle(n) {
    if (!n.star) return -Math.PI * 0.75;
    return Math.atan2(n.star.screenY - n.screenY, n.star.screenX - n.screenX);
  }

  function drawBodies(g, ctx, time, s, nowMs) {
    // Big things first so glows sit under smaller worlds.
    const order = g.nodes.slice().sort((a, b) => b.screenR - a.screenR);
    const effects = g.opt.effects;
    for (const n of order) {
      if (!n.visible || n.alpha < 0.01) continue;
      const x = n.screenX, y = n.screenY;
      if (x < -200 || y < -200 || x > g.width + 200 || y > g.height + 200) continue;
      const r = n.screenR * (1 + n.hover * 0.12 + n.select * 0.06);
      ctx.globalAlpha = n.alpha;
      switch (n.kind) {
        case 'star': drawStar(ctx, n, x, y, r, time, g.motion, effects); break;
        case 'planet': drawPlanet(ctx, n, x, y, r, g.simTime, effects); break;
        case 'moon': drawMoon(ctx, n, x, y, r); break;
        case 'asteroid': drawAsteroid(ctx, n, x, y, r); break;
        case 'meteor': drawMeteor(ctx, n, x, y, r, s); break;
        default: drawDust(ctx, n, x, y, r, time); break;
      }
      ctx.globalAlpha = 1;
    }

    // Overlays: lens rims, time-lapse births, search hits, current, selection, hover.
    for (const n of g.nodes) {
      if (!n.visible || n.alpha < 0.01) continue;
      const x = n.screenX, y = n.screenY;
      const r = n.screenR * (1 + n.hover * 0.12 + n.select * 0.06);

      if (g.lens === 'recency' && (n.updatedAt || n.createdAt)) {
        const hue = 40 + (1 - n.hot) * 200;
        ctx.beginPath();
        ctx.arc(x, y, r + 2.5, 0, Math.PI * 2);
        ctx.strokeStyle = `hsla(${hue.toFixed(0)},90%,${(58 + n.hot * 28).toFixed(0)}%,${(0.3 + 0.55 * n.hot).toFixed(3)})`;
        ctx.lineWidth = 1.5 + n.hot * 1.5;
        ctx.stroke();
      }

      if (g.lens === 'links' && n.inbound > 0 && r >= 3) {
        drawBadge(ctx, x + r * 0.78, y - r * 0.78, String(n.inbound), n.alpha);
      }

      if (g.timeCursor !== null && g.timeWindow > 0 && n.createdAt > 0) {
        const since = g.timeCursor - n.createdAt;
        if (since >= 0 && since < g.timeWindow) {
          const f = 1 - since / g.timeWindow;
          ctx.beginPath();
          ctx.arc(x, y, r + 3 + (1 - f) * 22, 0, Math.PI * 2);
          ctx.strokeStyle = `rgba(255,255,255,${(f * 0.85).toFixed(3)})`;
          ctx.lineWidth = 1.5 + f * 1.5;
          ctx.stroke();
          const flash = ctx.createRadialGradient(x, y, 0, x, y, r * 2.4);
          flash.addColorStop(0, `rgba(255,255,255,${(f * 0.5).toFixed(3)})`);
          flash.addColorStop(1, 'rgba(255,255,255,0)');
          ctx.fillStyle = flash;
          ctx.beginPath();
          ctx.arc(x, y, r * 2.4, 0, Math.PI * 2);
          ctx.fill();
        }
      }

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
      if (n.select > 0.01) {
        const a = n.select;
        ctx.beginPath();
        ctx.arc(x, y, r + 5 + a * 2, 0, Math.PI * 2);
        ctx.strokeStyle = `rgba(169,184,255,${(0.95 * a).toFixed(3)})`;
        ctx.lineWidth = 2;
        ctx.stroke();
        ctx.beginPath();
        ctx.arc(x, y, r + 11 + a * 3, 0, Math.PI * 2);
        ctx.strokeStyle = `rgba(169,184,255,${(0.28 * a).toFixed(3)})`;
        ctx.lineWidth = 1;
        ctx.stroke();
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

  function drawBadge(ctx, x, y, text, alpha) {
    ctx.font = '600 10px system-ui, sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    const w = Math.max(16, ctx.measureText(text).width + 10);
    const h = 15;
    ctx.fillStyle = `rgba(169,184,255,${(0.92 * alpha).toFixed(3)})`;
    roundRect(ctx, x - w / 2, y - h / 2, w, h, 7.5);
    ctx.fill();
    ctx.fillStyle = `rgba(8,10,26,${alpha.toFixed(3)})`;
    ctx.fillText(text, x, y + 0.5);
  }

  function roundRect(ctx, x, y, w, h, r) {
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + w, y, x + w, y + h, r);
    ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r);
    ctx.arcTo(x, y, x + w, y, r);
    ctx.closePath();
  }

  function drawStar(ctx, n, x, y, r, time, motion, effects) {
    const tone = STAR_TONES[n.tone];
    const pulse = motion ? 1 + 0.05 * Math.sin(time * 1.3 + n.phase) : 1;

    if (effects && r > 40) {
      // Lens bloom for big, close stars.
      const bloomR = r * 6;
      const bloom = ctx.createRadialGradient(x, y, r, x, y, bloomR);
      bloom.addColorStop(0, tone.glow + '0.14)');
      bloom.addColorStop(1, tone.glow + '0)');
      ctx.fillStyle = bloom;
      ctx.beginPath();
      ctx.arc(x, y, bloomR, 0, Math.PI * 2);
      ctx.fill();
    }

    const glowR = r * 3.4 * pulse;
    const glow = ctx.createRadialGradient(x, y, r * 0.6, x, y, glowR);
    glow.addColorStop(0, tone.glow + '0.55)');
    glow.addColorStop(0.35, tone.glow + '0.18)');
    glow.addColorStop(1, tone.glow + '0)');
    ctx.fillStyle = glow;
    ctx.beginPath();
    ctx.arc(x, y, glowR, 0, Math.PI * 2);
    ctx.fill();

    if (effects && r > 6) {
      // Corona: three slowly counter-rotating lobes.
      for (let k = 0; k < 3; k++) {
        const ang = (motion ? time * 0.12 * (k % 2 ? 1 : -1) : 0) + n.phase + k * 2.1;
        const ox = Math.cos(ang) * r * 0.35;
        const oy = Math.sin(ang) * r * 0.35;
        const lobe = ctx.createRadialGradient(x + ox, y + oy, r * 0.5, x + ox, y + oy, r * 2.4);
        lobe.addColorStop(0, tone.glow + '0.16)');
        lobe.addColorStop(1, tone.glow + '0)');
        ctx.fillStyle = lobe;
        ctx.beginPath();
        ctx.arc(x + ox, y + oy, r * 2.4, 0, Math.PI * 2);
        ctx.fill();
      }
    }

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

  function drawPlanet(ctx, n, x, y, r, simTime, effects) {
    const hue = n.hue;
    const light = lightAngle(n);
    const lx = Math.cos(light), ly = Math.sin(light);

    if (n.ringed && r > 3) {
      ctx.save();
      ctx.translate(x, y);
      ctx.rotate(n.tilt);
      ctx.beginPath();
      ctx.ellipse(0, 0, r * 2.2, r * 0.55, 0, Math.PI, Math.PI * 2);
      ctx.strokeStyle = `hsla(${hue + 20},40%,80%,0.55)`;
      ctx.lineWidth = Math.max(1, r * 0.22);
      ctx.stroke();
      ctx.restore();
    }

    if (r > 2.5) {
      const atmo = ctx.createRadialGradient(x + lx * r * 0.2, y + ly * r * 0.2, r * 0.9, x, y, r * 1.35);
      atmo.addColorStop(0, `hsla(${hue},70%,70%,0.32)`);
      atmo.addColorStop(1, `hsla(${hue},70%,70%,0)`);
      ctx.fillStyle = atmo;
      ctx.beginPath();
      ctx.arc(x, y, r * 1.35, 0, Math.PI * 2);
      ctx.fill();
    }

    const grad = ctx.createRadialGradient(x + lx * r * 0.4, y + ly * r * 0.4, r * 0.1, x, y, r);
    grad.addColorStop(0, `hsl(${hue},62%,78%)`);
    grad.addColorStop(0.45, `hsl(${hue},58%,52%)`);
    grad.addColorStop(1, `hsl(${hue + 10},55%,18%)`);
    ctx.fillStyle = grad;
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fill();

    if (r > 5) {
      ctx.save();
      ctx.beginPath();
      ctx.arc(x, y, r, 0, Math.PI * 2);
      ctx.clip();

      if (n.banded) {
        ctx.fillStyle = `hsla(${hue + 8},50%,30%,0.35)`;
        ctx.beginPath();
        ctx.ellipse(x, y + r * 0.1, r * 1.1, r * 0.12, 0, 0, Math.PI * 2);
        ctx.fill();
        ctx.beginPath();
        ctx.ellipse(x, y + r * 0.42, r * 1.05, r * 0.08, 0, 0, Math.PI * 2);
        ctx.fill();
      }

      if (n.cloudy && effects) {
        // Drifting cloud blobs wrap around behind the limb.
        ctx.save();
        ctx.translate(x, y);
        ctx.rotate(n.tilt * 0.5);
        const drift = simTime * 0.08 * n.dir + n.phase;
        ctx.fillStyle = 'rgba(255,255,255,0.16)';
        for (let i = 0; i < 4; i++) {
          const span = 4 * r;
          const cxp = (((i * 1.35 * r + drift * r) % span) + span) % span - span / 2;
          const cyp = (i - 1.5) * r * 0.4 + Math.sin(n.phase + i) * r * 0.08;
          ctx.beginPath();
          ctx.ellipse(cxp, cyp, r * (0.55 + (i % 2) * 0.25), r * 0.13, 0, 0, Math.PI * 2);
          ctx.fill();
        }
        ctx.restore();
      }

      // Night side: a terminator that faces away from the star.
      const night = ctx.createLinearGradient(x + lx * r, y + ly * r, x - lx * r, y - ly * r);
      night.addColorStop(0, 'rgba(4,6,20,0)');
      night.addColorStop(0.42, 'rgba(4,6,20,0)');
      night.addColorStop(0.7, 'rgba(4,6,20,0.55)');
      night.addColorStop(1, 'rgba(4,6,20,0.9)');
      ctx.fillStyle = night;
      ctx.fillRect(x - r, y - r, r * 2, r * 2);

      // Rim light on the lit limb.
      ctx.beginPath();
      ctx.arc(x, y, r * 0.96, light - 1.15, light + 1.15);
      ctx.strokeStyle = `hsla(${hue},80%,88%,0.45)`;
      ctx.lineWidth = Math.max(0.8, r * 0.07);
      ctx.stroke();
      ctx.restore();
    }

    if (n.ringed && r > 3) {
      ctx.save();
      ctx.translate(x, y);
      ctx.rotate(n.tilt);
      ctx.beginPath();
      ctx.ellipse(0, 0, r * 2.2, r * 0.55, 0, 0, Math.PI);
      ctx.strokeStyle = `hsla(${hue + 20},45%,86%,0.8)`;
      ctx.lineWidth = Math.max(1, r * 0.22);
      ctx.stroke();
      ctx.restore();
    }
  }

  function drawMoon(ctx, n, x, y, r) {
    const light = lightAngle(n);
    const lx = Math.cos(light), ly = Math.sin(light);
    const grad = ctx.createRadialGradient(x + lx * r * 0.35, y + ly * r * 0.35, r * 0.1, x, y, r);
    grad.addColorStop(0, '#f3f5fa');
    grad.addColorStop(0.5, '#b7bfcd');
    grad.addColorStop(1, '#39404d');
    ctx.fillStyle = grad;
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fill();
    if (r > 4) {
      ctx.save();
      ctx.beginPath();
      ctx.arc(x, y, r, 0, Math.PI * 2);
      ctx.clip();
      const rand = rng(n.hash);
      ctx.fillStyle = 'rgba(70,78,95,0.45)';
      for (let i = 0; i < 3; i++) {
        const ang = rand() * Math.PI * 2;
        const d = Math.sqrt(rand()) * r * 0.55;
        ctx.beginPath();
        ctx.arc(x + Math.cos(ang) * d, y + Math.sin(ang) * d, r * (0.1 + rand() * 0.14), 0, Math.PI * 2);
        ctx.fill();
      }
      const night = ctx.createLinearGradient(x + lx * r, y + ly * r, x - lx * r, y - ly * r);
      night.addColorStop(0, 'rgba(4,6,20,0)');
      night.addColorStop(0.45, 'rgba(4,6,20,0)');
      night.addColorStop(0.75, 'rgba(4,6,20,0.5)');
      night.addColorStop(1, 'rgba(4,6,20,0.85)');
      ctx.fillStyle = night;
      ctx.fillRect(x - r, y - r, r * 2, r * 2);
      ctx.restore();
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

  function drawMeteor(ctx, n, x, y, r) {
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

  function drawLabels(g, ctx) {
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    const font = getComputedStyle(document.documentElement).getPropertyValue('--font') || 'system-ui, sans-serif';
    const mode = g.opt.labels;
    const cands = [];

    for (const n of g.nodes) {
      if (!n.visible || n.alpha < 0.05) continue;
      const forced = n.hover > 0.3 || n.id === g.currentId || n.id === g.selectedId || g.hits.has(n.id);
      let show = forced;
      if (!show) {
        if (mode === 'all') show = n.screenR >= 2;
        else if (mode === 'min') show = n.kind === 'star' && n.screenR >= 4;
        else if (n.spec.label === 'always') show = n.screenR >= 4;
        else if (n.spec.label === 'zoom') show = n.screenR >= (n.kind === 'planet' ? 7 : 8);
      }
      if (!show) continue;
      const x = n.screenX, y = n.screenY;
      if (x < -120 || y < -60 || x > g.width + 120 || y > g.height + 60) continue;

      const size = n.spec.font;
      const fontSpec = `${n.spec.weight} ${size}px ${font}`;
      const label = n.icon ? `${n.icon} ${trim(n.title, n.kind === 'star' ? 28 : 20)}` : trim(n.title, n.kind === 'star' ? 28 : 20);
      const key = fontSpec + '|' + label;
      let width = g.labelWidths.get(key);
      if (width === undefined) {
        ctx.font = fontSpec;
        width = ctx.measureText(label).width;
        if (g.labelWidths.size > 2000) g.labelWidths.clear();
        g.labelWidths.set(key, width);
      }
      const ly = y + n.screenR * (1 + n.hover * 0.12 + n.select * 0.06) + (n.kind === 'star' ? 8 : 6);
      cands.push({
        n, label, fontSpec, x, ly, width, height: size + 3,
        forced,
        prio: (forced ? 10 : 0) + n.spec.prio + n.screenR / 100,
      });
    }

    // Greedy collision pass: important labels claim space first.
    cands.sort((a, b) => b.prio - a.prio);
    const placed = [];
    for (const c of cands) {
      const rect = { x: c.x - c.width / 2 - 3, y: c.ly - 1, w: c.width + 6, h: c.height + 2 };
      if (!c.forced) {
        let collides = false;
        for (const p of placed) {
          if (rect.x < p.x + p.w && rect.x + rect.w > p.x && rect.y < p.y + p.h && rect.y + rect.h > p.y) { collides = true; break; }
        }
        if (collides) continue;
      }
      placed.push(rect);

      const n = c.n;
      ctx.font = c.fontSpec;
      let alpha = n.alpha * (c.forced ? 1 : n.kind === 'star' ? 0.92 : 0.78);
      if (g.lens === 'recency' && !c.forced) alpha *= 0.55 + 0.45 * n.hot;
      ctx.lineWidth = 3;
      ctx.lineJoin = 'round';
      ctx.strokeStyle = `rgba(3,5,16,${(alpha * 0.85).toFixed(3)})`;
      ctx.strokeText(c.label, c.x, c.ly);
      ctx.fillStyle = n.missing
        ? `rgba(160,170,210,${alpha.toFixed(3)})`
        : n.id === g.selectedId
          ? `rgba(214,222,255,${alpha.toFixed(3)})`
          : `rgba(236,238,255,${alpha.toFixed(3)})`;
      ctx.fillText(c.label, c.x, c.ly);
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

  function drawMinimap(g, ctx) {
    if (!minimapVisible(g)) return;
    const m = minimapTransform(g);
    const r = m.r;

    ctx.save();
    ctx.fillStyle = 'rgba(10,12,28,0.62)';
    roundRect(ctx, r.x, r.y, r.w, r.h, 10);
    ctx.fill();
    ctx.strokeStyle = 'rgba(190,200,255,0.14)';
    ctx.lineWidth = 1;
    ctx.stroke();
    roundRect(ctx, r.x, r.y, r.w, r.h, 10);
    ctx.clip();

    for (const n of g.nodes) {
      if (!n.visible) continue;
      const x = m.ox + n.x * m.scale;
      const y = m.oy + n.y * m.scale;
      let color;
      switch (n.kind) {
        case 'star': color = 'rgba(255,217,138,0.95)'; break;
        case 'planet': color = 'rgba(99,183,232,0.9)'; break;
        case 'moon': color = 'rgba(201,208,221,0.8)'; break;
        case 'dust': color = 'rgba(170,176,214,0.5)'; break;
        default: color = 'rgba(160,141,118,0.8)'; break;
      }
      const size = n.kind === 'star' ? 2.4 : n.kind === 'planet' ? 1.6 : 1.1;
      ctx.fillStyle = color;
      ctx.globalAlpha = Math.max(0.25, n.alpha);
      ctx.beginPath();
      ctx.arc(x, y, size, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.globalAlpha = 1;

    // Camera viewport.
    const vw = g.width / g.cam.scale * m.scale;
    const vh = g.height / g.cam.scale * m.scale;
    const vx = m.ox + g.cam.x * m.scale - vw / 2;
    const vy = m.oy + g.cam.y * m.scale - vh / 2;
    ctx.strokeStyle = 'rgba(255,255,255,0.7)';
    ctx.lineWidth = 1;
    ctx.strokeRect(Math.round(vx) + 0.5, Math.round(vy) + 0.5, Math.max(6, Math.round(vw)), Math.max(6, Math.round(vh)));
    ctx.fillStyle = 'rgba(255,255,255,0.05)';
    ctx.fillRect(vx, vy, vw, vh);
    ctx.restore();
  }

  function copyText(text) {
    const value = String(text ?? '');
    if (navigator.clipboard && navigator.clipboard.writeText) {
      return navigator.clipboard.writeText(value).then(() => true).catch(() => fallbackCopy(value));
    }
    return Promise.resolve(fallbackCopy(value));
  }

  function fallbackCopy(value) {
    try {
      const area = document.createElement('textarea');
      area.value = value;
      area.setAttribute('readonly', '');
      area.style.position = 'fixed';
      area.style.opacity = '0';
      document.body.appendChild(area);
      area.select();
      const ok = document.execCommand && document.execCommand('copy');
      document.body.removeChild(area);
      return !!ok;
    } catch {
      return false;
    }
  }

  window.synclyGalaxy = {
    mount,
    copyText,
    destroy,
    setData,
    setCurrent,
    setFilter,
    setMotion,
    isMotion,
    setOptions,
    getOptions,
    setLens,
    setTimeCursor,
    select,
    search,
    fit,
    locate,
    flyTo,
  };
})();
