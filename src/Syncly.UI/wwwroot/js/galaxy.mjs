import { collapsed, createLayout, diveCrumbs, hash, lifeDiff, lodHidden, lodOpen, miniMapBounds, miniMapRect, miniToWorld, miniViewport, openAmount, pointToSegment, recencyHeat, sunColorFor, sunHighlightFor, sunRimFor, wormholeDestination, worldToMini } from './galaxy-layout.mjs';

const instances = new Map();
const palettes = {
  planet: ['#7fd8ff', '#9adfb0', '#c792ea', '#7ea8ff', '#5fd9c9', '#e3a8f2'],
  moon: ['#cfd6e4', '#b9c2d4', '#a8b3c9', '#dfe4ee'],
  asteroid: ['#8d97a8', '#767f8f', '#9aa2b0'],
};
const cachePrefix = 'syncly.galaxy.v2.space.';
const optionsKey = 'syncly.galaxy.v2.options';
const scaleMin = 0.02;
const scaleMax = 6;
const clamp = (value, minimum, maximum) => Math.max(minimum, Math.min(maximum, value));
const read = (storage, key) => { try { return JSON.parse(storage.getItem(key)); } catch { return null; } };
const write = (storage, key, value) => { try { storage.setItem(key, JSON.stringify(value)); } catch { } };

export function mount(id, dotnet) {
  destroy(id);
  const canvas = document.getElementById(id);
  const context = canvas?.getContext('2d', { alpha: false });
  if (!context) throw new Error('Canvas rendering is unavailable.');
  const stored = read(localStorage, optionsKey);
  const view = { id, canvas, context, dotnet, layout: createLayout(), spaceKey: null,
    width: 1, height: 1, dpr: 1, camera: { x: 0, y: 0, scale: 1 },
    currentId: null, selectedId: null, hoverId: null, diveId: null, zoomTo: null, zoomAnchor: null, zoomScreen: null, mode: 'all', filter: 'all', query: '',
    options: { labels: stored?.labels !== false, recent: stored?.recent === true, orbit: stored?.orbit !== false },
    frame: 0, disposed: false, initialFit: true, flight: null, overview: null,
    pointers: new Map(), gesture: null, longPress: 0, abort: new AbortController(),
    sprites: new Map(), labelWidths: new Map(), screenNodes: [], hitGrid: new Map(),
    labels: [], frames: 0, maxTickMs: 0, media: matchMedia('(prefers-reduced-motion: reduce)'),
    stars: [], comets: [], nextComet: 0, startedAt: performance.now(), lastSave: 0,
    known: new Set(), lastBodies: new Map(), births: new Map(), deaths: [], hoverLink: null };

  instances.set(id, view);
  const on = (target, event, handler, options = {}) => target.addEventListener(event, handler, { ...options, signal: view.abort.signal });
  view.observer = new ResizeObserver(() => resize(view));
  view.observer.observe(canvas);
  on(view.media, 'change', () => { view.flight = null; invalidate(view); });
  on(document, 'visibilitychange', () => {
    if (document.hidden) { cancelAnimationFrame(view.frame); view.frame = 0; save(view); }
    else wake(view);
  });
  on(window, 'pagehide', () => save(view));
  on(document, 'keydown', event => {
    if (event.key !== 'Tab') return;
    const dialog = canvas.closest('.graph-page')?.querySelector('.graph-dialog');
    if (!dialog) return;
    const controls = [...dialog.querySelectorAll('button:not(:disabled), input:not(:disabled)')];
    const first = controls[0], last = controls.at(-1);
    if (event.shiftKey && (document.activeElement === first || !dialog.contains(document.activeElement))) {
      event.preventDefault(); last?.focus();
    } else if (!event.shiftKey && (document.activeElement === last || !dialog.contains(document.activeElement))) {
      event.preventDefault(); first?.focus();
    }
  });
  on(canvas, 'wheel', event => {
    event.preventDefault();
    view.initialFit = false;
    const point = local(view, event);
    const delta = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? view.height : 1);
    zoomAt(view, point.x, point.y, Math.exp(-clamp(delta, -120, 120) * 0.0055));
  }, { passive: false });
  on(canvas, 'pointerdown', event => pointerDown(view, event));
  on(canvas, 'pointermove', event => pointerMove(view, event));
  on(canvas, 'pointerup', event => pointerUp(view, event));
  on(canvas, 'pointercancel', event => cancelPointer(view, event));
  on(canvas, 'lostpointercapture', event => cancelPointer(view, event));
  on(canvas, 'pointerleave', () => { if (!view.pointers.size) { view.hoverId = null; view.hoverLink = null; invalidate(view); } });
  on(canvas, 'contextmenu', event => {
    event.preventDefault();
    clearTimeout(view.longPress);
    const point = local(view, event);
    const node = hit(view, point.x, point.y, event.pointerType === 'touch');
    notify(view, 'OnContext', node?.id ?? null, event.clientX, event.clientY);
  });
  on(canvas, 'dblclick', event => {
    const point = local(view, event);
    const node = hit(view, point.x, point.y);
    if (node) focus(id, node.id);
  });
  on(canvas, 'keydown', event => keydown(view, event));
  resize(view);
  return view.options;
}

export function update(id, data) {
  const view = instances.get(id);
  if (!view) return;
  const changedSpace = view.spaceKey !== data.spaceKey;
  if (changedSpace) {
    save(view);
    view.layout.destroy();
    view.spaceKey = data.spaceKey;
    const saved = read(sessionStorage, cachePrefix + data.spaceKey);
    view.layout = createLayout(data, saved?.nodes, saved?.topology);
    const camera = saved?.camera;
    const valid = camera && [camera.x, camera.y, camera.scale].every(Number.isFinite)
      && Math.abs(camera.x) < 1e6 && Math.abs(camera.y) < 1e6 && camera.scale >= scaleMin && camera.scale <= scaleMax;
    view.camera = valid ? { ...camera } : { x: 0, y: 0, scale: 1 };
    view.initialFit = !valid;
    view.selectedId = view.hoverId = view.diveId = null;
    view.overview = view.flight = null;
    view.zoomTo = view.zoomAnchor = view.zoomScreen = null;
    view.mode = 'all';
    view.filter = 'all';
    view.query = '';
    view.sprites.clear();
    view.births.clear();
    view.deaths = [];
    view.known = new Set();
    view.lastBodies = new Map();
    view.hoverLink = null;
  } else {
    view.layout.update(data);
  }
  view.currentId = data.currentId;
  if (!view.layout.byId.has(view.selectedId)) { view.selectedId = null; view.mode = 'all'; }
  if (!view.layout.byId.has(view.hoverId)) view.hoverId = null;
  if (view.diveId && !view.layout.byId.has(view.diveId)) clearDive(view);
  syncLife(view, changedSpace);
  wake(view);
}

function syncLife(view, changedSpace) {
  const next = new Set(view.layout.byId.keys());
  if (changedSpace || view.media.matches) {
    view.births.clear();
    view.deaths = [];
    view.known = next;
    rememberBodies(view);
    return;
  }
  const { born, died } = lifeDiff(view.known, next);
  const now = performance.now();
  for (const id of born) view.births.set(id, now);
  for (const node of view.layout.nodes) {
    const prior = view.lastBodies.get(node.id);
    if (prior?.missing && !node.missing) view.births.set(node.id, now);
  }
  for (const id of died) {
    const prior = view.lastBodies.get(id);
    if (!prior) continue;
    view.deaths.push({ ...prior, at: now });
  }
  view.known = next;
  rememberBodies(view);
}

function rememberBodies(view) {
  view.lastBodies = new Map(view.layout.nodes.map(node => [node.id, {
    id: node.id, x: node.x, y: node.y, radius: node.radius, kind: node.kind, title: node.title,
    color: colorFor(node, 0), missing: !!node.missing,
  }]));
}

function birthProgress(view, id, now) {
  const at = view.births.get(id);
  if (at == null) return 1;
  const amount = clamp((now - at) / 780, 0, 1);
  if (amount >= 1) view.births.delete(id);
  return amount;
}

export function configure(id, settings) {
  const view = instances.get(id);
  if (!view) return;
  if (settings.mode && settings.mode !== view.mode) {
    if (settings.mode === 'connections' && view.selectedId) {
      view.overview = { ...view.camera };
      view.mode = 'connections';
      fit(id);
    } else {
      view.mode = 'all';
      if (view.overview) moveCamera(view, view.overview);
      view.overview = null;
    }
  }
  if (settings.filter !== undefined) view.filter = settings.filter;
  if (settings.query !== undefined) view.query = settings.query.trim().toLocaleLowerCase();
  if (settings.labels !== undefined) view.options.labels = !!settings.labels;
  if (settings.recent !== undefined) view.options.recent = !!settings.recent;
  if (settings.orbit !== undefined) view.options.orbit = !!settings.orbit;
  write(localStorage, optionsKey, view.options);
  wake(view);
}

export function select(id, nodeId, center = false) {
  const view = instances.get(id);
  if (!view) return;
  view.selectedId = view.layout.byId.has(nodeId) ? nodeId : null;
  if (!view.selectedId && view.mode === 'connections') configure(id, { mode: 'all' });
  if (center && view.selectedId) focus(id, view.selectedId);
  wake(view);
}

export function setCurrent(id, nodeId) {
  const view = instances.get(id);
  if (view) { view.currentId = nodeId; wake(view); }
}

export function focus(id, nodeId) {
  const view = instances.get(id);
  const node = view?.layout.byId.get(nodeId);
  if (!node) return;
  view.initialFit = false;
  const target = diveNode(node, view.layout.byId);
  setDive(view, target);
  const viewport = Math.min(view.width, view.height);
  const body = Math.max(24, target.radius || 24);
  const scale = target.kind === 'galaxy'
    ? clamp(96 / body, scaleMin, scaleMax)
    : clamp((viewport * 0.7) / Math.max(40, target.systemRadius || body), scaleMin, scaleMax);
  moveCamera(view, { x: target.x, y: target.y, scale });
}

export function fit(id) {
  const view = instances.get(id);
  if (!view) return;
  view.initialFit = false;
  clearDive(view);
  moveCamera(view, fitCamera(view));
}

export function zoom(id, factor) {
  const view = instances.get(id);
  if (view) { view.initialFit = false; zoomAt(view, view.width / 2, view.height / 2, factor); }
}

export function reset(id) {
  const view = instances.get(id);
  if (view) { view.layout.reset(); view.initialFit = true; clearDive(view); wake(view); }
}

export function focusElement(id) { document.getElementById(id)?.focus({ preventScroll: true }); }

export async function copyText(text) {
  if (navigator.clipboard?.writeText) {
    try { await navigator.clipboard.writeText(text); return; } catch { }
  }
  const input = document.createElement('textarea');
  input.value = text;
  input.style.cssText = 'position:fixed;opacity:0;pointer-events:none';
  document.body.append(input);
  const previous = document.activeElement;
  input.select();
  const copied = document.execCommand('copy');
  input.remove();
  previous?.focus();
  if (!copied) throw new Error('Clipboard is unavailable.');
}

export function destroy(id) {
  const view = instances.get(id);
  if (!view) return;
  save(view);
  view.disposed = true;
  cancelAnimationFrame(view.frame);
  clearTimeout(view.longPress);
  view.abort.abort();
  view.observer.disconnect();
  view.layout.destroy();
  instances.delete(id);
}

function save(view) {
  if (view.spaceKey === null) return;
  write(sessionStorage, cachePrefix + view.spaceKey, { camera: view.camera, nodes: view.layout.snapshot(),
    topology: view.layout.active ? null : view.layout.topology, time: Date.now() });
  try {
    const keys = Object.keys(sessionStorage).filter(key => key.startsWith(cachePrefix));
    keys.sort((left, right) => (read(sessionStorage, right)?.time || 0) - (read(sessionStorage, left)?.time || 0));
    for (const key of keys.slice(6)) sessionStorage.removeItem(key);
  } catch { }
}

function notify(view, method, ...args) {
  if (!view.disposed) view.dotnet.invokeMethodAsync(method, ...args).catch(() => {});
}

function resize(view) {
  const bounds = view.canvas.getBoundingClientRect();
  if (!bounds.width || !bounds.height) return;
  const hadSize = view.width > 1;
  view.width = bounds.width;
  view.height = bounds.height;
  view.dpr = Math.min(devicePixelRatio || 1, 2);
  view.canvas.width = Math.round(view.width * view.dpr);
  view.canvas.height = Math.round(view.height * view.dpr);
  view.background = document.createElement('canvas');
  view.background.width = view.canvas.width;
  view.background.height = view.canvas.height;
  const background = view.background.getContext('2d');
  background.scale(view.dpr, view.dpr);
  background.fillStyle = '#06070c';
  background.fillRect(0, 0, view.width, view.height);
  const nebulae = [
    { color: '#4a2f8f', a: 0.22 }, { color: '#164a6b', a: 0.18 },
    { color: '#5c2350', a: 0.16 }, { color: '#1f5a4d', a: 0.12 },
  ];
  nebulae.forEach((cloud, index) => {
    const seed = hash(`nebula-${index}`);
    const x = (seed % 10000) / 10000 * view.width;
    const y = (hash(seed + 1) % 10000) / 10000 * view.height;
    const radius = Math.max(view.width, view.height) * (0.32 + (seed % 5) / 18);
    const gradient = background.createRadialGradient(x, y, 0, x, y, radius);
    const alphaHex = Math.round(cloud.a * 255).toString(16).padStart(2, '0');
    gradient.addColorStop(0, cloud.color + alphaHex);
    gradient.addColorStop(1, cloud.color + '00');
    background.fillStyle = gradient;
    background.fillRect(0, 0, view.width, view.height);
  });
  view.stars = [];
  const count = Math.round(view.width * view.height / 4600);
  for (let index = 0; index < count; index++) {
    const seed = hash(`star-${index}`);
    view.stars.push({
      x: (seed % 10000) / 10000 * view.width,
      y: (hash(seed) % 10000) / 10000 * view.height,
      size: index % 13 === 0 ? 1.7 : index % 5 === 0 ? 1.1 : 0.6,
      phase: (seed % 6283) / 1000,
      speed: 0.0009 + (seed % 50) / 60000,
      base: 0.12 + (seed % 9) / 55,
    });
  }
  if (hadSize && view.selectedId) {
    const node = view.layout.byId.get(view.selectedId);
    if (node) {
      const point = screen(view, node);
      const padding = Math.min(64, view.width / 4, view.height / 4);
      view.camera.x += (point.x - clamp(point.x, padding, view.width - padding)) / view.camera.scale;
      view.camera.y += (point.y - clamp(point.y, padding, view.height - padding)) / view.camera.scale;
      view.flight = null;
    }
  }
  wake(view);
}

function local(view, event) {
  const rectangle = view.canvas.getBoundingClientRect();
  return { x: event.clientX - rectangle.left, y: event.clientY - rectangle.top };
}

function world(view, point) {
  return { x: (point.x - view.width / 2) / view.camera.scale + view.camera.x,
    y: (point.y - view.height / 2) / view.camera.scale + view.camera.y };
}

function screen(view, point) {
  return { x: (point.x - view.camera.x) * view.camera.scale + view.width / 2,
    y: (point.y - view.camera.y) * view.camera.scale + view.height / 2 };
}

function visible(view, node) {
  if (node.id === view.selectedId) return true;
  if (view.mode === 'connections' && !view.layout.neighbors.get(view.selectedId)?.has(node.id)) return false;
  if (view.filter === 'unlinked' && (node.missing || node.degree !== 0)) return false;
  if (view.filter === 'unresolved' && !node.missing) return false;
  return true;
}

function fitCamera(view) {
  const nodes = view.layout.nodes.filter(node => visible(view, node));
  if (!nodes.length) return { x: 0, y: 0, scale: 1 };
  const roots = nodes.filter(node => node.depth <= 0);
  const glyphFit = roots.length <= 3 && roots.every(node => node.kind === 'galaxy' || node.kind === 'blackhole');
  let left = Infinity, right = -Infinity, top = Infinity, bottom = -Infinity;
  for (const node of nodes) {
    const extent = node.depth > 0 ? node.radius
      : glyphFit && (node.kind === 'galaxy' || node.kind === 'blackhole') ? node.radius * 8
      : (node.systemRadius || node.radius);
    left = Math.min(left, node.x - extent); right = Math.max(right, node.x + extent);
    top = Math.min(top, node.y - extent); bottom = Math.max(bottom, node.y + extent);
  }
  return { x: (left + right) / 2, y: (top + bottom) / 2,
    scale: clamp(Math.min(Math.max(80, view.width - 140) / Math.max(120, right - left),
      Math.max(80, view.height - 140) / Math.max(120, bottom - top)), scaleMin, 1.35) };
}

function moveCamera(view, camera, duration = 260) {
  view.zoomTo = view.zoomAnchor = view.zoomScreen = null;
  if (view.media.matches) { view.camera = { ...camera }; view.flight = null; }
  else view.flight = { from: { ...view.camera }, to: { ...camera }, started: performance.now(), duration };
  wake(view);
}

function zoomAt(view, x, y, factor) {
  const anchor = world(view, { x, y });
  view.flight = null;
  if (view.media.matches) {
    view.zoomTo = null;
    view.camera.scale = clamp(view.camera.scale * factor, scaleMin, scaleMax);
    view.camera.x = anchor.x - (x - view.width / 2) / view.camera.scale;
    view.camera.y = anchor.y - (y - view.height / 2) / view.camera.scale;
  } else {
    view.zoomAnchor = anchor;
    view.zoomScreen = { x, y };
    view.zoomTo = clamp((view.zoomTo ?? view.camera.scale) * factor, scaleMin, scaleMax);
  }
  syncDive(view);
  wake(view);
}

function applyZoomLerp(view) {
  if (view.zoomTo == null || view.flight) return;
  const gap = view.zoomTo - view.camera.scale;
  if (Math.abs(gap) < 0.0004 * Math.max(1, view.zoomTo)) {
    view.camera.scale = view.zoomTo;
    view.zoomTo = null;
  } else {
    view.camera.scale += gap * 0.48;
  }
  if (view.zoomAnchor && view.zoomScreen) {
    view.camera.x = view.zoomAnchor.x - (view.zoomScreen.x - view.width / 2) / view.camera.scale;
    view.camera.y = view.zoomAnchor.y - (view.zoomScreen.y - view.height / 2) / view.camera.scale;
  }
  syncDive(view);
}

function diveNode(node, byId) {
  if (node.kind === 'galaxy' || node.kind === 'blackhole' || node.kind === 'sun') return node;
  let parent = node.parentId ? byId.get(node.parentId) : null;
  while (parent) {
    if (parent.kind === 'sun' || parent.kind === 'blackhole' || parent.kind === 'galaxy') return parent;
    parent = parent.parentId ? byId.get(parent.parentId) : null;
  }
  return node;
}

function setDive(view, node) {
  const id = node?.id ?? null;
  if (view.diveId === id) return;
  view.diveId = id;
  notify(view, 'OnDive', id, diveCrumbs(view.layout.byId, id));
}

function clearDive(view) {
  if (!view.diveId) return;
  view.diveId = null;
  notify(view, 'OnDive', null, []);
}

function popDive(view) {
  const node = view.layout.byId.get(view.diveId);
  const parent = node?.parentId ? view.layout.byId.get(node.parentId) : null;
  if (parent) focus(view.id, parent.id);
  else fit(view.id);
}

function syncDive(view) {
  if (view.flight) return;
  const lod = lodState(view);
  const cx = view.camera.x;
  const cy = view.camera.y;
  let best = null;
  let bestScore = Infinity;
  for (const node of view.layout.nodes) {
    if (node.kind !== 'galaxy' && node.kind !== 'blackhole' && node.kind !== 'sun') continue;
    if ((node.childCount || 0) === 0 && (node.descendants || 0) === 0) continue;
    if (collapsed(node, view.camera.scale, lod)) continue;
    const dist = Math.hypot(node.x - cx, node.y - cy);
    const reach = node.systemRadius || node.radius || 0;
    if (dist > reach * 1.2) continue;
    const score = dist / Math.max(8, node.radius);
    if (score < bestScore - 0.05 || (score <= bestScore + 0.05 && (!best || node.depth > best.depth))) {
      best = node;
      bestScore = score;
    }
  }
  if (best) setDive(view, best);
  else clearDive(view);
}

function lodState(view) {
  return { diveId: view.diveId, byId: view.layout.byId, viewport: Math.min(view.width, view.height) };
}

function hit(view, x, y, touch = false) {
  let winner = null;
  let closest = Infinity;
  const cellX = Math.floor(x / 64), cellY = Math.floor(y / 64);
  for (let offsetX = -1; offsetX <= 1; offsetX++) {
    for (let offsetY = -1; offsetY <= 1; offsetY++) {
      for (const item of view.hitGrid.get(`${cellX + offsetX}:${cellY + offsetY}`) || []) {
        const distance = Math.hypot(item.x - x, item.y - y);
        const reach = item.node.kind === 'galaxy' || item.node.kind === 'blackhole'
          ? item.radius * 1.6 + 5 : Math.max(item.radius + 5, touch ? 22 : 12);
        if (distance <= Math.max(reach, touch ? 22 : 12) && distance < closest) {
          winner = item.node; closest = distance;
        }
      }
    }
  }
  return winner;
}

function miniCandidates(view) {
  return view.layout.nodes.filter(node => visible(view, node)
    && (node.depth <= 2 || node.kind === 'galaxy' || node.kind === 'blackhole' || node.kind === 'sun'
      || node.id === view.currentId));
}

function miniState(view) {
  const rect = miniMapRect(view.width, view.height);
  if (!rect) return null;
  return { rect, bounds: miniMapBounds(miniCandidates(view)) };
}

function hitMini(rect, point) {
  return point.x >= rect.x && point.x <= rect.x + rect.width
    && point.y >= rect.y && point.y <= rect.y + rect.height;
}

function linkKey(from, to) {
  return from < to ? `${from}|${to}` : `${to}|${from}`;
}

function hitWormhole(view, x, y, touch = false) {
  let winner = null;
  let closest = touch ? 16 : 9;
  for (const link of view.layout.links) {
    const source = screen(view, link.source);
    const target = screen(view, link.target);
    const distance = pointToSegment(x, y, source.x, source.y, target.x, target.y);
    if (distance < closest) {
      winner = link;
      closest = distance;
    }
  }
  return winner;
}

function travelWormhole(view, link, point) {
  const source = { ...screen(view, link.source), node: link.source };
  const target = { ...screen(view, link.target), node: link.target };
  const pick = wormholeDestination(source, target, point).node;
  focus(view.id, pick.id);
  if (view.flight) view.flight.duration = 560;
  select(view.id, pick.id);
  notify(view, 'OnSelect', pick.id);
}

function jumpMini(view, mini, point, animate) {
  const worldPoint = miniToWorld(point, mini.bounds, mini.rect);
  if (animate) moveCamera(view, { x: worldPoint.x, y: worldPoint.y, scale: view.camera.scale });
  else {
    view.zoomTo = view.zoomAnchor = view.zoomScreen = null;
    view.flight = null;
    view.camera.x = worldPoint.x;
    view.camera.y = worldPoint.y;
    syncDive(view);
    wake(view);
  }
}

function pointerDown(view, event) {
  if (event.button !== 0) return;
  view.canvas.focus({ preventScroll: true });
  view.canvas.setPointerCapture(event.pointerId);
  const point = local(view, event);
  view.pointers.set(event.pointerId, point);
  clearTimeout(view.longPress);
  view.flight = null;
  view.initialFit = false;
  if (view.pointers.size > 1) {
    const [first, second] = [...view.pointers.values()];
    view.gesture = { kind: 'pinch', distance: Math.hypot(first.x - second.x, first.y - second.y),
      midpoint: { x: (first.x + second.x) / 2, y: (first.y + second.y) / 2 }, suppress: true };
    return;
  }
  const mini = miniState(view);
  if (mini && hitMini(mini.rect, point)) {
    view.gesture = { kind: 'minimap', start: point, last: point, suppress: true, dragged: false };
    return;
  }
  const node = hit(view, point.x, point.y, event.pointerType === 'touch');
  const wormhole = node ? null : hitWormhole(view, point.x, point.y, event.pointerType === 'touch');
  view.gesture = { kind: 'pending', start: point, last: point, nodeId: node?.id, wormhole, suppress: false };
  if (event.pointerType === 'touch') {
    view.longPress = setTimeout(() => {
      if (view.gesture?.kind !== 'pending') return;
      view.gesture.suppress = true;
      const rect = view.canvas.getBoundingClientRect();
      notify(view, 'OnContext', node?.id ?? null, rect.left + point.x, rect.top + point.y);
    }, 500);
  }
}

function pointerMove(view, event) {
  const point = local(view, event);
  if (!view.pointers.has(event.pointerId)) {
    if (event.pointerType !== 'touch') {
      const mini = miniState(view);
      if (mini && hitMini(mini.rect, point)) {
        if (view.hoverId || view.hoverLink) { view.hoverId = null; view.hoverLink = null; wake(view); }
        view.canvas.style.cursor = 'pointer';
        return;
      }
      const hovered = hit(view, point.x, point.y)?.id ?? null;
      const link = hovered ? null : hitWormhole(view, point.x, point.y);
      const pair = link ? linkKey(link.source.id, link.target.id) : null;
      if (hovered !== view.hoverId || pair !== view.hoverLink) {
        view.hoverId = hovered;
        view.hoverLink = pair;
        wake(view);
      }
      view.canvas.style.cursor = hovered || link ? 'pointer' : 'grab';
    }
    return;
  }
  view.pointers.set(event.pointerId, point);
  const gesture = view.gesture;
  if (!gesture) return;
  if (gesture.kind === 'minimap') {
    if (Math.hypot(point.x - gesture.start.x, point.y - gesture.start.y) > 4) gesture.dragged = true;
    if (gesture.dragged) {
      const mini = miniState(view);
      if (mini) jumpMini(view, mini, point, false);
    }
    view.canvas.style.cursor = 'grabbing';
    wake(view);
    return;
  }
  if (gesture.kind === 'pinch') {
    const [first, second] = [...view.pointers.values()];
    if (!second) return;
    const midpoint = { x: (first.x + second.x) / 2, y: (first.y + second.y) / 2 };
    const distance = Math.max(1, Math.hypot(first.x - second.x, first.y - second.y));
    view.zoomTo = null;
    const anchor = world(view, gesture.midpoint);
    view.camera.scale = clamp(view.camera.scale * distance / Math.max(1, gesture.distance), scaleMin, scaleMax);
    view.camera.x = anchor.x - (midpoint.x - view.width / 2) / view.camera.scale;
    view.camera.y = anchor.y - (midpoint.y - view.height / 2) / view.camera.scale;
    gesture.midpoint = midpoint;
    gesture.distance = distance;
    syncDive(view);
  } else if (gesture.kind !== 'cancelled') {
    if (gesture.kind === 'pending' && Math.hypot(point.x - gesture.start.x, point.y - gesture.start.y) > 6) {
      gesture.kind = gesture.nodeId ? 'node' : 'pan';
      gesture.suppress = true;
      clearTimeout(view.longPress);
    }
    if (gesture.kind === 'node') {
      const position = world(view, point);
      view.layout.pin(gesture.nodeId, position.x, position.y);
    } else if (gesture.kind === 'pan') {
      view.camera.x -= (point.x - gesture.last.x) / view.camera.scale;
      view.camera.y -= (point.y - gesture.last.y) / view.camera.scale;
    }
    gesture.last = point;
  }
  view.canvas.style.cursor = 'grabbing';
  wake(view);
}

function pointerUp(view, event) {
  if (!view.pointers.has(event.pointerId)) return;
  clearTimeout(view.longPress);
  const gesture = view.gesture;
  view.pointers.delete(event.pointerId);
  if (gesture?.kind === 'minimap' && !gesture.dragged) {
    const mini = miniState(view);
    if (mini) jumpMini(view, mini, local(view, event), true);
  } else if (gesture?.kind === 'pending' && !gesture.suppress) {
    const point = local(view, event);
    if (Math.hypot(point.x - gesture.start.x, point.y - gesture.start.y) <= 6) {
      const nodeId = hit(view, point.x, point.y, event.pointerType === 'touch')?.id ?? null;
      if (nodeId) {
        select(view.id, nodeId);
        notify(view, 'OnSelect', nodeId);
      } else if (gesture.wormhole) {
        travelWormhole(view, gesture.wormhole, point);
      } else {
        select(view.id, null);
        notify(view, 'OnSelect', null);
      }
    }
  }
  view.gesture = view.pointers.size ? { kind: 'cancelled', suppress: true } : null;
  if (view.canvas.hasPointerCapture(event.pointerId)) view.canvas.releasePointerCapture(event.pointerId);
  view.canvas.style.cursor = 'grab';
  save(view);
  wake(view);
}

function cancelPointer(view, event) {
  if (!view.pointers.has(event.pointerId)) return;
  view.pointers.delete(event.pointerId);
  clearTimeout(view.longPress);
  view.gesture = view.pointers.size ? { kind: 'cancelled', suppress: true } : null;
  view.canvas.style.cursor = 'grab';
  invalidate(view);
}

function keydown(view, event) {
  if (event.ctrlKey || event.metaKey || event.altKey) return;
  const key = event.key;
  if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Enter', 'Escape', 'Home', 'Backspace', '+', '=', '-', '/', 'f', 'F'].includes(key)) return;
  event.preventDefault();
  event.stopPropagation();
  if (key === 'Enter' && view.selectedId) notify(view, 'OnOpen', view.selectedId);
  else if (key === 'Escape') notify(view, 'OnEscape');
  else if (key === '/') notify(view, 'OnFocusSearch');
  else if (key === 'Home') fit(view.id);
  else if (key === 'Backspace') popDive(view);
  else if (key === '+' || key === '=') zoom(view.id, 1.25);
  else if (key === '-') zoom(view.id, 0.8);
  else if (key.toLowerCase() === 'f' && view.selectedId) focus(view.id, view.selectedId);
  else if (key.startsWith('Arrow')) {
    const origin = view.layout.byId.get(view.selectedId) || view.camera;
    const horizontal = key === 'ArrowLeft' || key === 'ArrowRight';
    const sign = key === 'ArrowLeft' || key === 'ArrowUp' ? -1 : 1;
    const candidates = view.layout.nodes.filter(node => node.id !== view.selectedId && visible(view, node))
      .map(node => ({ node, along: (horizontal ? node.x - origin.x : node.y - origin.y) * sign,
        across: Math.abs(horizontal ? node.y - origin.y : node.x - origin.x) }))
      .filter(candidate => candidate.along > 0)
      .sort((left, right) => left.along + left.across * 2 - right.along - right.across * 2);
    const target = candidates[0]?.node;
    if (target) { select(view.id, target.id, true); notify(view, 'OnSelect', target.id); }
  }
}

function frozen(view) {
  return view.media.matches || view.options.orbit === false;
}

function orbitClock(view, time) {
  if (frozen(view)) {
    view.orbitHold ??= time - (view.orbitShift || 0);
    return view.orbitHold;
  }
  if (view.orbitHold != null) {
    view.orbitShift = time - view.orbitHold;
    view.orbitHold = null;
  }
  return time - (view.orbitShift || 0);
}

function invalidate(view) {
  if (!view.frame && !view.disposed && !document.hidden) view.frame = requestAnimationFrame(time => frame(view, time));
}

// Alias kept at interaction call sites for readability; ambient animation runs continuously below.
function wake(view) {
  invalidate(view);
}

function frame(view, time) {
  view.frame = 0;
  if (view.disposed || document.hidden) return;
  const still = frozen(view);
  const clock = orbitClock(view, time);
  const started = performance.now();
  while (view.layout.active && performance.now() - started < 4 && !view.pointers.size) {
    const tickStarted = performance.now();
    view.layout.tick();
    view.maxTickMs = Math.max(view.maxTickMs, performance.now() - tickStarted);
  }
  view.layout.orbit(clock);
  if (view.initialFit) view.camera = fitCamera(view);
  applyZoomLerp(view);
  if (view.flight) {
    const duration = Math.max(1, view.flight.duration || 260);
    const progress = clamp((time - view.flight.started) / duration, 0, 1);
    const eased = 1 - (1 - progress) ** 3;
    for (const key of ['x', 'y', 'scale']) view.camera[key] = view.flight.from[key] + (view.flight.to[key] - view.flight.from[key]) * eased;
    if (progress === 1) { view.camera = { ...view.flight.to }; view.flight = null; syncDive(view); }
  }
  if (!still && !view.pointers.size && time >= view.nextComet) {
    view.nextComet = time + 7000 + Math.random() * 11000;
    const fromLeft = Math.random() < 0.5;
    view.comets.push({ x: fromLeft ? -30 : view.width + 30, y: Math.random() * view.height * 0.65,
      vx: (fromLeft ? 1 : -1) * (240 + Math.random() * 180), vy: 70 + Math.random() * 70, born: time, life: 1100 });
  }
  draw(view, clock);
  rememberBodies(view);
  view.frames++;
  const living = !view.media.matches && (view.births.size > 0 || view.deaths.length > 0);
  const animating = (!still && !view.pointers.size) || (view.layout.active && !view.pointers.size)
    || view.flight || view.zoomTo != null || living;
  if (animating) invalidate(view);
  else if (!view.pointers.size) { view.initialFit = false; save(view); }
  if (time - view.lastSave > 4000) { view.lastSave = time; save(view); }
}


function lodKeepIds(view) {
  const keep = [];
  if (view.selectedId) keep.push(view.selectedId);
  if (view.currentId) keep.push(view.currentId);
  if (view.query) {
    for (const node of view.layout.nodes) {
      if (node.title.toLocaleLowerCase().includes(view.query)) keep.push(node.id);
    }
  }
  return keep;
}

function mixHex(left, right, amount) {
  const parse = hex => [1, 3, 5].map(index => parseInt(hex.slice(index, index + 2), 16));
  const from = parse(left);
  const to = parse(right);
  if (from.some(Number.isNaN) || to.some(Number.isNaN)) return left;
  const t = Math.max(0, Math.min(1, amount));
  return '#' + from.map((channel, index) => Math.round(channel + (to[index] - channel) * t).toString(16).padStart(2, '0')).join('');
}

function heatFor(view, node) {
  return view.options.recent ? recencyHeat(node) : 0;
}

function colorFor(node, heat = 0) {
  let color = node.kind === 'sun' ? sunColorFor(node.radius)
    : node.kind === 'galaxy' ? '#c9d4ff'
      : node.kind === 'blackhole' ? '#ffb14e'
        : (palettes[node.kind] || palettes.planet)[hash(node.id) % (palettes[node.kind] || palettes.planet).length];
  return heat > 0 ? mixHex(color, '#ff9a4a', heat * 0.55) : color;
}

function screenRadius(node, scale) {
  const minimum = node.kind === 'galaxy' ? 10
    : node.kind === 'blackhole' ? 7
    : node.kind === 'sun' ? 5
    : node.kind === 'planet' ? 4 : 2.5;
  return Math.max(minimum, (node.radius || 4) * scale);
}

function parentReveal(node, byId, scale) {
  const parent = node.parentId ? byId.get(node.parentId) : null;
  if (!parent) return 1;
  return openAmount(parent, scale);
}

function sprite(view, color, kind, radius = 18) {
  const key = kind + ':' + color;
  if (view.sprites.has(key)) return view.sprites.get(key);
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 96;
  const context = canvas.getContext('2d');
  context.beginPath(); context.arc(48, 48, 44, 0, Math.PI * 2); context.clip();
  const highlight = kind === 'sun' ? sunHighlightFor(radius) : kind === 'planet' ? '#f5f1e8' : '#eceff4';
  const rim = kind === 'sun' ? sunRimFor(radius) : '#1d222c';
  const surface = context.createRadialGradient(30, 26, 1, 53, 53, 57);
  surface.addColorStop(0, highlight);
  surface.addColorStop(kind === 'sun' ? 0.14 : 0.23, color);
  surface.addColorStop(0.65, color);
  surface.addColorStop(1, rim);
  context.fillStyle = surface; context.fillRect(0, 0, 96, 96);
  if (kind === 'sun') {
    context.strokeStyle = '#ffffff26'; context.lineWidth = 2;
    for (let index = 0; index < 6; index++) {
      context.beginPath(); context.ellipse(43, 20 + index * 14, 55, 8, -0.3, 0, Math.PI * 2); context.stroke();
    }
  } else if (kind === 'moon' || kind === 'asteroid') {
    context.fillStyle = '#00000024';
    for (let index = 0; index < 4; index++) {
      const seed = hash(color + ':' + index);
      context.beginPath(); context.arc(18 + (seed % 52), 22 + (hash(seed) % 48), 3 + (seed % 5), 0, Math.PI * 2); context.fill();
    }
  }
  const shadow = context.createLinearGradient(15, 0, 90, 70);
  shadow.addColorStop(0, '#00000000');
  shadow.addColorStop(0.55, kind === 'sun' ? '#00000006' : '#00000012');
  shadow.addColorStop(1, kind === 'sun' ? '#00000030' : '#000000a0');
  context.fillStyle = shadow; context.fillRect(0, 0, 96, 96);
  view.sprites.set(key, canvas);
  return canvas;
}

function drawGalaxy(view, item, time, alpha) {
  const open = openAmount(item.node, view.camera.scale);
  if (open < 1) drawGalaxyGlyph(view, item, time, alpha * (1 - Math.max(0, open - 0.12) / 0.88));
  if (open > 0.12) {
    const core = (open - 0.12) / 0.88;
    drawGalacticHalo(view, item, time, alpha * core);
    drawSupermassiveCore(view, item, time, alpha * core);
  }
}

function drawGalaxyGlyph(view, item, time, alpha) {
  const { x, y, radius, node } = item;
  const context = view.context;
  const reduced = frozen(view);
  const spin = reduced ? (hash(node.id) % 628) / 100 : time * 0.00018 + hash(node.id);
  const heat = heatFor(view, node);
  context.save();
  context.globalAlpha = alpha;
  const haze = context.createRadialGradient(x, y, radius * 0.15, x, y, radius * 2.6);
  haze.addColorStop(0, (heat > 0 ? mixHex('#e8f0ff', '#ffe3b8', heat) : '#e8f0ff') + '66');
  haze.addColorStop(0.35, (heat > 0 ? mixHex('#8aa4ff', '#ff9a4a', heat * 0.85) : '#8aa4ff') + '33');
  haze.addColorStop(0.7, (heat > 0 ? mixHex('#6a4dff', '#ff6a24', heat * 0.7) : '#6a4dff') + '18');
  haze.addColorStop(1, '#00000000');
  context.fillStyle = haze;
  context.beginPath(); context.arc(x, y, radius * 2.6, 0, Math.PI * 2); context.fill();
  context.translate(x, y);
  context.rotate(spin);
  context.scale(1, 0.58);
  context.drawImage(galaxySprite(view), -radius * 1.4, -radius * 1.4, radius * 2.8, radius * 2.8);
  context.restore();
  if (heat > 0 && openAmount(node, view.camera.scale) < 0.35) {
    context.save();
    context.globalAlpha = alpha * (0.28 + heat * 0.5);
    const core = context.createRadialGradient(x, y, 0, x, y, radius * 0.72);
    core.addColorStop(0, '#fff6d8');
    core.addColorStop(0.45, '#ffb14e99');
    core.addColorStop(1, '#ff6a2400');
    context.fillStyle = core;
    context.beginPath(); context.arc(x, y, radius * 0.72, 0, Math.PI * 2); context.fill();
    context.restore();
  }
}

function drawGalacticHalo(view, item, time, alpha) {
  const { x, y, radius, node } = item;
  const context = view.context;
  const reduced = frozen(view);
  const spin = reduced ? (hash(node.id) % 628) / 100 : time * 0.00008 + hash(node.id);
  const heat = heatFor(view, node);
  const span = radius * 2.3;
  context.save();
  context.globalAlpha = alpha * 0.55;
  const bulge = context.createRadialGradient(x, y, radius * 0.4, x, y, span);
  bulge.addColorStop(0, (heat > 0 ? mixHex('#ffe3b8', '#ff9a4a', heat) : '#ffe6c4') + '33');
  bulge.addColorStop(0.35, '#8aa4ff18');
  bulge.addColorStop(0.7, '#3a2a6a10');
  bulge.addColorStop(1, '#00000000');
  context.fillStyle = bulge;
  context.beginPath(); context.arc(x, y, span, 0, Math.PI * 2); context.fill();
  context.translate(x, y);
  context.rotate(spin);
  context.scale(1, 0.42);
  context.globalAlpha = alpha * 0.28;
  context.drawImage(galaxySprite(view), -span, -span, span * 2, span * 2);
  context.restore();
}

function drawSupermassiveCore(view, item, time, alpha) {
  drawKerrHole(view, item, time, alpha, true);
}

function drawBlackHole(view, item, time, alpha) {
  drawKerrHole(view, item, time, alpha, false);
}

function drawKerrHole(view, item, time, alpha, massive) {
  const { x, y, radius, node } = item;
  const context = view.context;
  const reduced = frozen(view);
  const heat = heatFor(view, node);
  const spin = reduced ? hash(node.id) % 1000 : time * (massive ? 0.00042 : 0.0009) + hash(node.id);
  const pulse = reduced ? 1 : 1 + Math.sin(time * (massive ? 0.0014 : 0.0026) + hash(node.id) % 80) * (0.04 + heat * 0.05);
  const tilt = massive ? -0.16 : -0.28 - (hash(node.id) % 18) / 90;
  const hole = radius * (massive ? 0.48 : 0.58);
  const rx = hole * (massive ? 2.4 : 2.15);
  const ry = hole * (massive ? 0.78 : 0.88);
  context.save();
  context.globalAlpha = alpha;
  const corona = context.createRadialGradient(x, y, hole * 0.4, x, y, radius * (massive ? 2.8 : 3.2));
  corona.addColorStop(0, massive ? '#08040ee8' : '#10060cd0');
  corona.addColorStop(0.22, (heat > 0 ? mixHex(massive ? '#3a1860' : '#5a1a3a', '#ff6a24', heat * 0.55) : massive ? '#24103a' : '#4a1428') + '88');
  corona.addColorStop(0.48, (massive ? '#7ecbff' : '#ff8a3a') + '24');
  corona.addColorStop(1, '#00000000');
  context.fillStyle = corona;
  context.beginPath(); context.arc(x, y, radius * (massive ? 2.8 : 3.2) * pulse, 0, Math.PI * 2); context.fill();
  context.translate(x, y);
  context.rotate(tilt);
  if (massive) {
    drawRelativisticJet(context, radius, spin, false);
    drawRelativisticJet(context, radius, spin, true);
  }
  drawAccretionBand(context, rx, ry, hole, spin, massive, Math.PI, Math.PI);
  drawLensedWrap(context, hole, rx, ry, massive);
  const shadow = context.createRadialGradient(-hole * 0.18, -hole * 0.16, hole * 0.04, 0, 0, hole);
  shadow.addColorStop(0, massive ? '#14081c' : '#1a1014');
  shadow.addColorStop(0.38, '#050208');
  shadow.addColorStop(1, '#000000');
  context.fillStyle = shadow;
  context.beginPath(); context.arc(0, 0, hole, 0, Math.PI * 2); context.fill();
  const photon = context.createRadialGradient(0, 0, hole * 0.92, 0, 0, hole * 1.16);
  photon.addColorStop(0, '#00000000');
  photon.addColorStop(0.55, massive ? '#fff4d8cc' : '#ffe7b8aa');
  photon.addColorStop(0.78, massive ? '#9ecbff66' : '#ffb14e55');
  photon.addColorStop(1, '#00000000');
  context.fillStyle = photon;
  context.beginPath(); context.arc(0, 0, hole * 1.16, 0, Math.PI * 2); context.fill();
  context.strokeStyle = massive ? '#f8fbff' : '#fff6d8';
  context.globalAlpha = alpha * (reduced ? 0.95 : 0.82 + Math.sin(time * 0.0032) * 0.1);
  context.lineWidth = Math.max(1.4, hole * (massive ? 0.085 : 0.07));
  context.beginPath(); context.arc(0, 0, hole * 1.04, 0, Math.PI * 2); context.stroke();
  context.globalAlpha = alpha;
  if (massive) {
    context.strokeStyle = '#9ecbff55';
    context.lineWidth = Math.max(1, hole * 0.04);
    context.beginPath(); context.arc(0, 0, hole * 1.14, 0, Math.PI * 2); context.stroke();
  }
  drawAccretionBand(context, rx, ry, hole, spin, massive, 0, Math.PI);
  context.restore();
}

function drawAccretionBand(context, rx, ry, hole, spin, massive, start, sweep) {
  context.save();
  context.beginPath();
  context.ellipse(0, 0, rx, ry, 0, 0, Math.PI * 2);
  context.ellipse(0, 0, hole * 1.02, Math.max(1, hole * (ry / rx) * 1.02), 0, 0, Math.PI * 2, true);
  context.clip('evenodd');
  context.beginPath();
  context.rect(-rx - 2, start === 0 ? 0 : -ry - 2, rx * 2 + 4, ry + 2);
  context.clip();
  const shift = Math.sin(spin) * rx * 0.06;
  const band = context.createLinearGradient(-rx + shift, 0, rx + shift, 0);
  if (massive) {
    band.addColorStop(0, '#081428');
    band.addColorStop(0.14, '#1c4a8a');
    band.addColorStop(0.32, '#7ecbff');
    band.addColorStop(0.46, '#fff7e4');
    band.addColorStop(0.54, '#ffe29a');
    band.addColorStop(0.7, '#ff8a3a');
    band.addColorStop(0.88, '#8a220c');
    band.addColorStop(1, '#2a0808');
  } else {
    band.addColorStop(0, '#2a0c08');
    band.addColorStop(0.16, '#c43a12');
    band.addColorStop(0.34, '#ff7a2c');
    band.addColorStop(0.48, '#ffe29a');
    band.addColorStop(0.56, '#fff4d8');
    band.addColorStop(0.7, '#ff9a4a');
    band.addColorStop(0.86, '#8a240c');
    band.addColorStop(1, '#240804');
  }
  context.fillStyle = band;
  context.fillRect(-rx, -ry, rx * 2, ry * 2);
  context.restore();
  context.strokeStyle = massive ? '#d4ecff66' : '#ffd7a366';
  context.lineWidth = Math.max(1, ry * 0.16);
  context.beginPath(); context.ellipse(0, 0, rx, ry, 0, start, start + sweep); context.stroke();
}

function drawLensedWrap(context, hole, rx, ry, massive) {
  context.save();
  const wrap = context.createLinearGradient(-hole, 0, hole, 0);
  wrap.addColorStop(0, massive ? '#4aa3ff00' : '#ff6b3d00');
  wrap.addColorStop(0.35, massive ? '#c9e7ffcc' : '#ffe29acc');
  wrap.addColorStop(0.5, '#fffaf0');
  wrap.addColorStop(0.65, massive ? '#ffd27acc' : '#ff9a4acc');
  wrap.addColorStop(1, massive ? '#4aa3ff00' : '#ff6b3d00');
  context.strokeStyle = wrap;
  context.lineWidth = Math.max(2, hole * (massive ? 0.16 : 0.14));
  context.lineCap = 'round';
  context.beginPath();
  context.ellipse(0, 0, hole * 0.98, hole * 0.98, 0, -Math.PI * 0.78, -Math.PI * 0.22);
  context.stroke();
  context.lineWidth = Math.max(1.2, hole * 0.08);
  context.beginPath();
  context.ellipse(0, 0, hole * 0.98, hole * 0.98, 0, Math.PI * 0.22, Math.PI * 0.78);
  context.stroke();
  context.restore();
}

function drawRelativisticJet(context, radius, spin, down) {
  context.save();
  context.rotate(down ? Math.PI : 0);
  context.rotate(Math.sin(spin) * 0.04);
  const length = radius * 3.15;
  const jet = context.createLinearGradient(0, -radius * 0.55, 0, -length);
  jet.addColorStop(0, '#ffffffd8');
  jet.addColorStop(0.08, '#e8f4ffcc');
  jet.addColorStop(0.22, '#9ecbff88');
  jet.addColorStop(0.55, '#6a8cff33');
  jet.addColorStop(1, '#7ecbff00');
  context.fillStyle = jet;
  context.beginPath();
  context.moveTo(-radius * 0.11, -radius * 0.55);
  context.lineTo(radius * 0.11, -radius * 0.55);
  context.lineTo(radius * 0.028, -length);
  context.lineTo(-radius * 0.028, -length);
  context.closePath();
  context.fill();
  const core = context.createLinearGradient(0, -radius * 0.55, 0, -length * 0.72);
  core.addColorStop(0, '#ffffffee');
  core.addColorStop(1, '#9ecbff00');
  context.strokeStyle = core;
  context.lineWidth = Math.max(1.2, radius * 0.04);
  context.lineCap = 'round';
  context.beginPath();
  context.moveTo(0, -radius * 0.62);
  context.lineTo(0, -length * 0.78);
  context.stroke();
  context.restore();
}

function galaxySprite(view) {
  const key = 'galaxy:body';
  if (view.sprites.has(key)) return view.sprites.get(key);
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 160;
  const context = canvas.getContext('2d');
  const cx = 80, cy = 80;
  const haze = context.createRadialGradient(cx, cy, 6, cx, cy, 76);
  haze.addColorStop(0, '#fff6e8cc');
  haze.addColorStop(0.16, '#c9a0ff88');
  haze.addColorStop(0.42, '#7f9bff66');
  haze.addColorStop(0.72, '#24306a44');
  haze.addColorStop(1, '#00000000');
  context.fillStyle = haze;
  context.beginPath(); context.arc(cx, cy, 76, 0, Math.PI * 2); context.fill();
  context.lineCap = 'round';
  for (let arm = 0; arm < 3; arm++) {
    context.beginPath();
    for (let step = 0; step <= 48; step++) {
      const t = step / 48;
      const angle = arm * 2.094 + t * 5.4;
      const r = 10 + t * 64;
      const x = cx + Math.cos(angle) * r;
      const y = cy + Math.sin(angle) * r;
      if (step === 0) context.moveTo(x, y); else context.lineTo(x, y);
    }
    context.strokeStyle = `rgba(232,240,255,${0.22 + arm * 0.08})`;
    context.lineWidth = 7 - arm;
    context.stroke();
  }
  context.fillStyle = '#ffffffa8';
  for (let index = 0; index < 28; index++) {
    const seed = hash('arm:' + index);
    const t = (seed % 900) / 900;
    const arm = seed % 3;
    const angle = arm * 2.094 + t * 5.4;
    const r = 12 + t * 60;
    context.globalAlpha = 0.35 + (seed % 50) / 120;
    context.beginPath();
    context.arc(cx + Math.cos(angle) * r, cy + Math.sin(angle) * r, 0.8 + (seed % 3) * 0.4, 0, Math.PI * 2);
    context.fill();
  }
  context.globalAlpha = 1;
  const core = context.createRadialGradient(cx, cy, 1, cx, cy, 18);
  core.addColorStop(0, '#fffaf0');
  core.addColorStop(0.45, '#ffd7a0');
  core.addColorStop(1, '#ffb14e00');
  context.fillStyle = core;
  context.beginPath(); context.arc(cx, cy, 18, 0, Math.PI * 2); context.fill();
  view.sprites.set(key, canvas);
  return canvas;
}

function drawStars(view, time) {
  if (!view.stars.length) return;
  const context = view.context;
  const reduced = frozen(view);
  context.setTransform(view.dpr, 0, 0, view.dpr, 0, 0);
  for (const star of view.stars) {
    const twinkle = reduced ? star.base : star.base + Math.sin(time * star.speed + star.phase) * 0.1;
    context.globalAlpha = clamp(twinkle, 0.02, 0.95);
    context.fillStyle = '#e9edf9';
    context.beginPath(); context.arc(star.x, star.y, star.size, 0, Math.PI * 2); context.fill();
  }
  context.globalAlpha = 1;
}

function drawComets(view, time) {
  if (!view.comets.length) return;
  const context = view.context;
  context.setTransform(view.dpr, 0, 0, view.dpr, 0, 0);
  view.comets = view.comets.filter(comet => time - comet.born < comet.life);
  for (const comet of view.comets) {
    const t = clamp((time - comet.born) / comet.life, 0, 1);
    const x = comet.x + comet.vx * t;
    const y = comet.y + comet.vy * t;
    const angle = Math.atan2(comet.vy, comet.vx);
    const length = 76;
    const tailX = x - Math.cos(angle) * length, tailY = y - Math.sin(angle) * length;
    const fade = t < 0.15 ? t / 0.15 : 1 - (t - 0.15) / 0.85;
    const gradient = context.createLinearGradient(x, y, tailX, tailY);
    gradient.addColorStop(0, `rgba(255,255,255,${0.85 * fade})`);
    gradient.addColorStop(1, 'rgba(255,255,255,0)');
    context.strokeStyle = gradient; context.lineWidth = 2; context.lineCap = 'round';
    context.beginPath(); context.moveTo(x, y); context.lineTo(tailX, tailY); context.stroke();
    context.fillStyle = `rgba(255,255,255,${fade})`;
    context.beginPath(); context.arc(x, y, 1.6, 0, Math.PI * 2); context.fill();
  }
  context.globalAlpha = 1;
}

function draw(view, time = performance.now()) {
  const context = view.context;
  context.setTransform(1, 0, 0, 1, 0, 0);
  if (view.background) context.drawImage(view.background, 0, 0);
  drawStars(view, time);
  drawComets(view, time);
  context.setTransform(view.dpr, 0, 0, view.dpr, 0, 0);
  const focusId = view.hoverId || view.selectedId;
  const neighborhood = view.layout.neighbors.get(focusId);
  const open = lodOpen(view.layout.byId, lodKeepIds(view));
  const lod = lodState(view);
  const projected = new Map();
  view.screenNodes = [];
  view.hitGrid.clear();
  for (const node of view.layout.nodes) {
    if (!visible(view, node) || lodHidden(node, view.layout.byId, view.camera.scale, open, lod)) continue;
    const folded = collapsed(node, view.camera.scale, lod);
    const point = screen(view, node);
    const radius = screenRadius(node, view.camera.scale);
    const relevant = !focusId || node.id === focusId || neighborhood?.has(node.id);
    const matches = !view.query || node.title.toLocaleLowerCase().includes(view.query);
    const reveal = parentReveal(node, view.layout.byId, view.camera.scale);
    const item = { node, ...point, radius, alpha: (relevant && matches ? 1 : 0.22) * reveal, matches,
      expanded: node.kind === 'galaxy' && !folded };
    projected.set(node.id, item);
    if (point.x < -100 || point.y < -100 || point.x > view.width + 100 || point.y > view.height + 100) continue;
    view.screenNodes.push(item);
    const cell = `${Math.floor(point.x / 64)}:${Math.floor(point.y / 64)}`;
    if (!view.hitGrid.has(cell)) view.hitGrid.set(cell, []);
    view.hitGrid.get(cell).push(item);
  }
  for (const node of view.layout.nodes) {
    if (node.depth <= 0 || !visible(view, node)) continue;
    if (lodHidden(node, view.layout.byId, view.camera.scale, open, lod)) continue;
    const parent = view.layout.byId.get(node.parentId);
    if (!parent || collapsed(parent, view.camera.scale, lod)) continue;
    const center = screen(view, parent);
    const radius = node.orbitRadius * view.camera.scale;
    if (radius < 8 || radius > Math.max(view.width, view.height) * 1.6) continue;
    context.globalAlpha = (node.id === focusId ? 0.4 : 0.14) * openAmount(parent, view.camera.scale);
    context.strokeStyle = parent.kind === 'galaxy' ? '#9ecbff'
      : parent.kind === 'blackhole' ? '#c9a0ff'
      : node.kind === 'planet' || node.kind === 'sun' || node.kind === 'blackhole' || node.kind === 'galaxy' ? '#ffcf8a'
      : node.kind === 'moon' ? '#9fd6ff' : '#c3c8d4';
    context.lineWidth = 1;
    context.setLineDash([1.5, 5]);
    context.beginPath(); context.arc(center.x, center.y, radius, 0, Math.PI * 2); context.stroke();
  }
  context.setLineDash([]);
  const directed = new Set(view.layout.edges.map(edge => JSON.stringify([edge.from, edge.to])));
  const reduced = frozen(view);
  for (const link of view.layout.links) {
    const source = projected.get(link.source.id), target = projected.get(link.target.id);
    if (!source || !target) continue;
    if ((source.x < 0 && target.x < 0) || (source.x > view.width && target.x > view.width)
      || (source.y < 0 && target.y < 0) || (source.y > view.height && target.y > view.height)) continue;
    const highlighted = source.node.id === focusId || target.node.id === focusId
      || view.hoverLink === linkKey(source.node.id, target.node.id);
    drawWormhole(context, source, target, time, highlighted, reduced, focusId || view.query);
    if (highlighted) {
      if (directed.has(JSON.stringify([source.node.id, target.node.id]))) arrow(context, source, target);
      if (directed.has(JSON.stringify([target.node.id, source.node.id]))) arrow(context, target, source);
    }
  }
  for (const item of view.screenNodes) {
    const { node, x, y, radius, alpha } = item;
    const ignite = view.media.matches ? 1 : birthProgress(view, node.id, performance.now());
    const grow = 0.18 + ignite * 0.82;
    item.radius = radius * grow;
    const heat = heatFor(view, node);
    const color = colorFor(node, heat);
    context.globalAlpha = alpha * Math.max(0.2, ignite);
    if (ignite < 1) drawIgnite(view, item, ignite, color);
    if (node.missing) {
      drawGhostMoon(view, item);
    } else if (node.kind === 'galaxy') {
      drawGalaxy(view, item, time, alpha);
    } else if (node.kind === 'blackhole') {
      drawBlackHole(view, item, time, alpha);
    } else if (node.kind === 'sun') {
      const pulse = reduced ? 1 : 1 + Math.sin(time * 0.0012 + hash(node.id) % 1000) * (0.16 + heat * 0.1);
      const glowScale = 2.6 + heat * 1.15;
      const glow = context.createRadialGradient(x, y, item.radius * 0.3, x, y, item.radius * glowScale * pulse);
      glow.addColorStop(0, color + (heat > 0.25 ? 'e8' : 'b0'));
      glow.addColorStop(1, color + '00');
      context.fillStyle = glow;
      context.beginPath(); context.arc(x, y, item.radius * glowScale * pulse, 0, Math.PI * 2); context.fill();
    } else if (node.kind === 'planet' && hash(node.id) % 3 === 0) {
      context.strokeStyle = color + '80'; context.lineWidth = Math.max(1, item.radius * 0.16);
      context.beginPath(); context.ellipse(x, y, item.radius * 1.75, item.radius * 0.55, 0.5, 0, Math.PI * 2); context.stroke();
    }
    context.globalAlpha = alpha * Math.max(0.2, ignite);
    if (node.id === view.selectedId || node.id === view.hoverId) {
      context.strokeStyle = node.id === view.selectedId ? '#ffffff' : '#9ba6b3';
      context.lineWidth = node.id === view.selectedId ? 1.7 : 1;
      const halo = item.expanded ? 16 : node.kind === 'galaxy' ? 10 : node.kind === 'blackhole' ? 8 : 5;
      context.beginPath(); context.arc(x, y, item.radius + halo, 0, Math.PI * 2); context.stroke();
    }
    if (!node.missing && node.kind !== 'blackhole' && node.kind !== 'galaxy') {
      context.drawImage(sprite(view, color, node.kind, node.radius), x - item.radius, y - item.radius, item.radius * 2, item.radius * 2);
    }
    if (node.id === view.currentId && !node.missing) drawLighthouse(view, item, time, reduced);
  }
  drawDeaths(view);
  labels(view);
  drawMiniMap(view);
  context.globalAlpha = 1;
}

function drawIgnite(view, item, amount, color) {
  const { x, y, radius, node } = item;
  const context = view.context;
  const dust = 1 - amount;
  const seed = hash(node.id);
  context.save();
  for (let index = 0; index < 8; index++) {
    const angle = (seed + index * 2.4) + amount * 2.1;
    const reach = radius * (2.8 + (index % 3) * 0.7) * dust;
    const px = x + Math.cos(angle) * reach;
    const py = y + Math.sin(angle) * reach;
    context.globalAlpha = item.alpha * dust * 0.85;
    context.fillStyle = index % 2 ? '#f4e4c4' : color;
    context.beginPath();
    context.arc(px, py, Math.max(1.1, radius * 0.18 * dust), 0, Math.PI * 2);
    context.fill();
  }
  const glow = context.createRadialGradient(x, y, 0, x, y, radius * 5 * dust + 8);
  glow.addColorStop(0, '#fff6d8aa');
  glow.addColorStop(1, '#fff6d800');
  context.globalAlpha = item.alpha * dust;
  context.fillStyle = glow;
  context.beginPath(); context.arc(x, y, radius * 5 * dust + 8, 0, Math.PI * 2); context.fill();
  context.restore();
}

function drawDeaths(view) {
  const now = performance.now();
  const context = view.context;
  const life = 920;
  view.deaths = view.deaths.filter(death => now - death.at < life);
  for (const death of view.deaths) {
    const t = clamp((now - death.at) / life, 0, 1);
    const point = screen(view, death);
    const radius = Math.max(4, (death.radius || 8) * view.camera.scale);
    const shock = radius * (1 + t * 4.8);
    context.save();
    context.globalAlpha = (1 - t) * 0.9;
    context.strokeStyle = '#ffd7a3';
    context.lineWidth = Math.max(1.2, 3.2 * (1 - t));
    context.beginPath(); context.arc(point.x, point.y, shock, 0, Math.PI * 2); context.stroke();
    const flash = context.createRadialGradient(point.x, point.y, 0, point.x, point.y, shock);
    flash.addColorStop(0, `rgba(255,244,210,${0.55 * (1 - t)})`);
    flash.addColorStop(0.35, `rgba(255,140,70,${0.28 * (1 - t)})`);
    flash.addColorStop(1, 'rgba(255,80,40,0)');
    context.fillStyle = flash;
    context.beginPath(); context.arc(point.x, point.y, shock, 0, Math.PI * 2); context.fill();
    const seed = hash(death.id);
    for (let index = 0; index < 10; index++) {
      const angle = seed + index * 0.66;
      const dist = radius * (0.6 + t * 5.5) * (0.65 + (hash(death.id + ':' + index) % 40) / 100);
      context.fillStyle = index % 3 ? '#ffe7c2' : (death.color || '#ffb14e');
      context.globalAlpha = (1 - t) * 0.8;
      context.beginPath();
      context.arc(point.x + Math.cos(angle) * dist, point.y + Math.sin(angle) * dist,
        Math.max(1.2, radius * 0.22 * (1 - t)), 0, Math.PI * 2);
      context.fill();
    }
    context.restore();
  }
}

function drawWormhole(context, source, target, time, highlighted, reduced, dimmed) {
  const dx = target.x - source.x;
  const dy = target.y - source.y;
  const length = Math.hypot(dx, dy);
  if (length < 4) return;
  const nx = dx / length;
  const ny = dy / length;
  const sx = source.x + nx * source.radius;
  const sy = source.y + ny * source.radius;
  const tx = target.x - nx * target.radius;
  const ty = target.y - ny * target.radius;
  const tube = context.createLinearGradient(sx, sy, tx, ty);
  tube.addColorStop(0, '#7cf0d8');
  tube.addColorStop(0.5, highlighted ? '#e0c8ff' : '#8a7cff');
  tube.addColorStop(1, '#7ecbff');
  context.strokeStyle = tube;
  context.globalAlpha = highlighted ? 0.95 : dimmed ? 0.12 : 0.42;
  context.lineWidth = highlighted ? 3.2 : 1.8;
  context.lineCap = 'round';
  if (!reduced) {
    context.setLineDash([10, 14]);
    context.lineDashOffset = -(time * 0.045) % 24;
  }
  context.beginPath(); context.moveTo(sx, sy); context.lineTo(tx, ty); context.stroke();
  context.setLineDash([]);
  context.lineCap = 'butt';
  drawPortal(context, sx, sy, highlighted);
  drawPortal(context, tx, ty, highlighted);
}

function drawPortal(context, x, y, highlighted) {
  const radius = highlighted ? 6.5 : 4.5;
  const glow = context.createRadialGradient(x, y, 0, x, y, radius * 2.4);
  glow.addColorStop(0, '#e8fff8cc');
  glow.addColorStop(0.4, '#a78bfa88');
  glow.addColorStop(1, '#7ecbff00');
  context.fillStyle = glow;
  context.beginPath(); context.arc(x, y, radius * 2.4, 0, Math.PI * 2); context.fill();
  context.strokeStyle = '#d4f7ff';
  context.lineWidth = highlighted ? 1.6 : 1;
  context.beginPath(); context.arc(x, y, radius, 0, Math.PI * 2); context.stroke();
}

function drawGhostMoon(view, item) {
  const { x, y, radius } = item;
  const context = view.context;
  context.save();
  context.globalAlpha = item.alpha * 0.55;
  context.strokeStyle = '#b7b0c8';
  context.lineWidth = 1;
  context.setLineDash([2.5, 3.5]);
  context.beginPath(); context.arc(x, y, radius * 1.55, 0, Math.PI * 2); context.stroke();
  context.setLineDash([]);
  context.fillStyle = 'rgba(176, 170, 196, 0.22)';
  context.beginPath(); context.arc(x, y, radius, 0, Math.PI * 2); context.fill();
  context.save();
  context.beginPath(); context.arc(x, y, radius, 0, Math.PI * 2); context.clip();
  context.fillStyle = 'rgba(210, 206, 224, 0.5)';
  context.beginPath(); context.arc(x - radius * 0.38, y - radius * 0.12, radius, 0, Math.PI * 2); context.fill();
  context.restore();
  context.strokeStyle = '#c4bfd4';
  context.lineWidth = 1.2;
  context.setLineDash([3, 3]);
  context.beginPath(); context.arc(x, y, radius, 0, Math.PI * 2); context.stroke();
  context.setLineDash([]);
  context.restore();
}

function drawLighthouse(view, item, time, reduced) {
  const { x, y, radius } = item;
  const context = view.context;
  context.save();
  const glow = context.createRadialGradient(x, y, radius * 0.4, x, y, radius * 4.2);
  glow.addColorStop(0, 'rgba(255,248,216,0.4)');
  glow.addColorStop(1, 'rgba(255,248,216,0)');
  context.globalAlpha = 1;
  context.fillStyle = glow;
  context.beginPath(); context.arc(x, y, radius * 4.2, 0, Math.PI * 2); context.fill();
  context.strokeStyle = '#fff4c8';
  context.lineWidth = 1.7;
  context.beginPath(); context.arc(x, y, radius + 5.5, 0, Math.PI * 2); context.stroke();
  if (!reduced) {
    const angle = time * 0.00105;
    const span = 0.38;
    context.translate(x, y);
    context.rotate(angle);
    const sweep = context.createRadialGradient(0, 0, 0, 0, 0, radius * 9);
    sweep.addColorStop(0, 'rgba(255,250,230,0.48)');
    sweep.addColorStop(0.4, 'rgba(255,236,170,0.14)');
    sweep.addColorStop(1, 'rgba(255,236,170,0)');
    context.fillStyle = sweep;
    context.beginPath();
    context.moveTo(0, 0);
    context.arc(0, 0, radius * 9, -span, span);
    context.closePath();
    context.fill();
  }
  context.restore();
}

function drawMiniMap(view) {
  const mini = miniState(view);
  if (!mini) return;
  const { rect, bounds } = mini;
  const context = view.context;
  context.save();
  context.globalAlpha = 1;
  context.fillStyle = '#0c0d12ee';
  context.strokeStyle = '#3b3e48';
  context.lineWidth = 1;
  context.beginPath();
  if (context.roundRect) context.roundRect(rect.x, rect.y, rect.width, rect.height, 8);
  else context.rect(rect.x, rect.y, rect.width, rect.height);
  context.fill();
  context.stroke();
  context.beginPath();
  context.rect(rect.x, rect.y, rect.width, rect.height);
  context.clip();
  const dots = miniCandidates(view).filter(node => node.kind === 'galaxy' || node.kind === 'blackhole'
    || node.kind === 'sun' || node.id === view.currentId);
  for (const node of dots) {
    const point = worldToMini(node, bounds, rect);
    const size = node.kind === 'galaxy' ? 3.2 : node.kind === 'blackhole' ? 2.4 : 1.6;
    context.fillStyle = node.id === view.currentId ? '#fff4c8'
      : node.id === view.diveId ? '#d8dec6'
        : node.kind === 'galaxy' ? '#9ecbff'
          : node.kind === 'blackhole' ? '#c9a0ff' : '#ffcf8a';
    context.beginPath(); context.arc(point.x, point.y, size, 0, Math.PI * 2); context.fill();
  }
  const viewport = miniViewport(view.camera, view.width, view.height, bounds, rect);
  context.strokeStyle = '#e8e4c8';
  context.lineWidth = 1;
  context.strokeRect(viewport.x, viewport.y, viewport.width, viewport.height);
  context.restore();
}

function arrow(context, source, target) {
  const angle = Math.atan2(target.y - source.y, target.x - source.x);
  const distance = Math.hypot(target.x - source.x, target.y - source.y);
  if (distance < target.radius + source.radius + 20) return;
  const x = target.x - Math.cos(angle) * (target.radius + 8);
  const y = target.y - Math.sin(angle) * (target.radius + 8);
  context.beginPath();
  context.moveTo(x - Math.cos(angle - 0.45) * 6, y - Math.sin(angle - 0.45) * 6);
  context.lineTo(x, y);
  context.lineTo(x - Math.cos(angle + 0.45) * 6, y - Math.sin(angle + 0.45) * 6);
  context.stroke();
}

function labels(view) {
  const context = view.context;
  context.textAlign = 'center'; context.textBaseline = 'middle';
  const priority = item => (item.node.id === view.selectedId ? 100000 : 0)
    + (item.node.id === view.hoverId ? 90000 : 0) + (item.node.id === view.currentId ? 80000 : 0)
    + (view.query && item.matches ? 10000 : 0) + item.node.degree;
  const candidates = [...view.screenNodes].sort((left, right) => priority(right) - priority(left));
  const placed = [];
  const budget = Math.max(10, Math.floor(view.width * view.height / 7500));
  const mini = miniMapRect(view.width, view.height);
  const topInset = view.canvas.classList.contains('graph-canvas') ? 58 : 4;
  const bottomInset = mini ? view.height - mini.y + 4 : 4;
  for (const item of candidates) {
    const important = priority(item) >= 80000;
    const ghost = !!item.node.missing;
    if ((!view.options.labels || placed.length >= budget || item.radius < 5.5 || item.alpha < 0.5) && !important) continue;
    let text = item.node.title;
    if (text.length > 26) text = text.slice(0, 25) + '\u2026';
    context.font = `${ghost ? 'italic ' : ''}500 12px "Segoe UI", sans-serif`;
    const key = (ghost ? 'i:' : '') + text;
    let width = view.labelWidths.get(key);
    if (width === undefined) { width = context.measureText(text).width + 12; view.labelWidths.set(key, width); }
    let rectangle;
    for (const direction of [1, -1]) {
      const pad = item.expanded ? 52 : item.node.kind === 'galaxy' ? 34 : item.node.kind === 'blackhole' ? 28 : 17;
      const candidate = { x: item.x - width / 2, y: item.y + direction * (item.radius + pad) - 10, width, height: 20 };
      if (candidate.x < 4 || candidate.x + width > view.width - 4 || candidate.y < topInset || candidate.y + 20 > view.height - bottomInset) continue;
      if (placed.some(other => candidate.x < other.x + other.width + 4 && candidate.x + width + 4 > other.x
        && candidate.y < other.y + other.height + 3 && candidate.y + 23 > other.y)) continue;
      if (view.screenNodes.some(other => other !== item && other.x + other.radius > candidate.x
        && other.x - other.radius < candidate.x + width && other.y + other.radius > candidate.y && other.y - other.radius < candidate.y + 20)) continue;
      rectangle = candidate; break;
    }
    if (!rectangle) continue;
    placed.push(rectangle);
    context.globalAlpha = important ? 1 : ghost ? 0.7 : 0.9;
    context.fillStyle = '#090a0deb'; context.fillRect(rectangle.x, rectangle.y, width, 20);
    context.fillStyle = important ? '#ffffff' : ghost ? '#9b97a8' : '#c4c8d0';
    context.fillText(text, rectangle.x + width / 2, rectangle.y + 10);
  }
  if (view.labelWidths.size > 2000) view.labelWidths.clear();
  view.labels = placed;
}

export function inspect(id) {
  const view = instances.get(id);
  if (!view) return null;
  return { camera: { ...view.camera }, selectedId: view.selectedId, diveId: view.diveId, mode: view.mode,
    active: view.layout.active, scheduled: !!view.frame, frames: view.frames, maxTickMs: view.maxTickMs,
    // Physically settled: force layout finished and no camera flight running. Ambient orbit/twinkle/comet
    // redraws keep `scheduled` true forever by design, so callers waiting for a stable graph should use this.
    settled: !view.layout.active && !view.flight && view.zoomTo == null,
    nodes: view.screenNodes.map(item => ({ id: item.node.id, x: item.x, y: item.y, radius: item.radius })),
    positions: view.layout.snapshot(), labels: view.labels, edges: view.layout.edges.length };
}