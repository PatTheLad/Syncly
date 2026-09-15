import { createLayout, hash } from './galaxy-layout.mjs';

const instances = new Map();
const palettes = {
  sun: ['#ffd27a', '#ffb14e', '#ff9d5c', '#ffe29a', '#ff8f6b'],
  planet: ['#7fd8ff', '#9adfb0', '#c792ea', '#7ea8ff', '#5fd9c9', '#e3a8f2'],
  moon: ['#cfd6e4', '#b9c2d4', '#a8b3c9', '#dfe4ee'],
  asteroid: ['#8d97a8', '#767f8f', '#9aa2b0'],
};
const cachePrefix = 'syncly.galaxy.v2.space.';
const optionsKey = 'syncly.galaxy.v2.options';
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
    currentId: null, selectedId: null, hoverId: null, mode: 'all', filter: 'all', query: '',
    options: { labels: stored?.labels !== false, recent: stored?.recent === true },
    frame: 0, disposed: false, initialFit: true, flight: null, overview: null,
    pointers: new Map(), gesture: null, longPress: 0, abort: new AbortController(),
    sprites: new Map(), labelWidths: new Map(), screenNodes: [], hitGrid: new Map(),
    labels: [], frames: 0, maxTickMs: 0, media: matchMedia('(prefers-reduced-motion: reduce)'),
    stars: [], comets: [], nextComet: 0, startedAt: performance.now(), lastSave: 0, animateUntil: 0 };

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
    zoomAt(view, point.x, point.y, Math.exp(-clamp(delta, -120, 120) * 0.006));
  }, { passive: false });
  on(canvas, 'pointerdown', event => pointerDown(view, event));
  on(canvas, 'pointermove', event => pointerMove(view, event));
  on(canvas, 'pointerup', event => pointerUp(view, event));
  on(canvas, 'pointercancel', event => cancelPointer(view, event));
  on(canvas, 'lostpointercapture', event => cancelPointer(view, event));
  on(canvas, 'pointerleave', () => { if (!view.pointers.size) { view.hoverId = null; invalidate(view); } });
  on(canvas, 'contextmenu', event => {
    event.preventDefault();
    clearTimeout(view.longPress);
    const point = local(view, event);
    const node = hit(view, point.x, point.y, event.pointerType === 'touch');
    notify(view, 'OnContext', node?.id ?? null);
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
      && Math.abs(camera.x) < 1e6 && Math.abs(camera.y) < 1e6 && camera.scale >= 0.04 && camera.scale <= 4;
    view.camera = valid ? { ...camera } : { x: 0, y: 0, scale: 1 };
    view.initialFit = !valid;
    view.selectedId = view.hoverId = null;
    view.overview = view.flight = null;
    view.mode = 'all';
    view.filter = 'all';
    view.query = '';
    view.sprites.clear();
  } else {
    view.layout.update(data);
  }
  view.currentId = data.currentId;
  if (!view.layout.byId.has(view.selectedId)) { view.selectedId = null; view.mode = 'all'; }
  if (!view.layout.byId.has(view.hoverId)) view.hoverId = null;
  wake(view);
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
  moveCamera(view, { x: node.x, y: node.y, scale: clamp(view.camera.scale, 0.8, 1.6) });
}

export function fit(id) {
  const view = instances.get(id);
  if (!view) return;
  view.initialFit = false;
  moveCamera(view, fitCamera(view));
}

export function zoom(id, factor) {
  const view = instances.get(id);
  if (view) { view.initialFit = false; zoomAt(view, view.width / 2, view.height / 2, factor); }
}

export function reset(id) {
  const view = instances.get(id);
  if (view) { view.layout.reset(); view.initialFit = true; wake(view); }
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
  let left = Infinity, right = -Infinity, top = Infinity, bottom = -Infinity;
  for (const node of nodes) {
    left = Math.min(left, node.x - node.radius); right = Math.max(right, node.x + node.radius);
    top = Math.min(top, node.y - node.radius); bottom = Math.max(bottom, node.y + node.radius);
  }
  return { x: (left + right) / 2, y: (top + bottom) / 2,
    scale: clamp(Math.min(Math.max(80, view.width - 140) / Math.max(120, right - left),
      Math.max(80, view.height - 140) / Math.max(120, bottom - top)), 0.04, 1.35) };
}

function moveCamera(view, camera) {
  if (view.media.matches) { view.camera = { ...camera }; view.flight = null; }
  else view.flight = { from: { ...view.camera }, to: { ...camera }, started: performance.now() };
  wake(view);
}

function zoomAt(view, x, y, factor) {
  const anchor = world(view, { x, y });
  view.flight = null;
  view.camera.scale = clamp(view.camera.scale * factor, 0.04, 4);
  view.camera.x = anchor.x - (x - view.width / 2) / view.camera.scale;
  view.camera.y = anchor.y - (y - view.height / 2) / view.camera.scale;
  wake(view);
}

function hit(view, x, y, touch = false) {
  let winner = null;
  let closest = Infinity;
  const cellX = Math.floor(x / 64), cellY = Math.floor(y / 64);
  for (let offsetX = -1; offsetX <= 1; offsetX++) {
    for (let offsetY = -1; offsetY <= 1; offsetY++) {
      for (const item of view.hitGrid.get(`${cellX + offsetX}:${cellY + offsetY}`) || []) {
        const distance = Math.hypot(item.x - x, item.y - y);
        if (distance <= Math.max(item.radius + 5, touch ? 22 : 12) && distance < closest) {
          winner = item.node; closest = distance;
        }
      }
    }
  }
  return winner;
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
  const node = hit(view, point.x, point.y, event.pointerType === 'touch');
  view.gesture = { kind: 'pending', start: point, last: point, nodeId: node?.id, suppress: false };
  if (event.pointerType === 'touch') {
    view.longPress = setTimeout(() => {
      if (view.gesture?.kind !== 'pending') return;
      view.gesture.suppress = true;
      notify(view, 'OnContext', node?.id ?? null);
    }, 500);
  }
}

function pointerMove(view, event) {
  const point = local(view, event);
  if (!view.pointers.has(event.pointerId)) {
    if (event.pointerType !== 'touch') {
      const hovered = hit(view, point.x, point.y)?.id ?? null;
      if (hovered !== view.hoverId) { view.hoverId = hovered; wake(view); }
      view.canvas.style.cursor = hovered ? 'pointer' : 'grab';
    }
    return;
  }
  view.pointers.set(event.pointerId, point);
  const gesture = view.gesture;
  if (!gesture) return;
  if (gesture.kind === 'pinch') {
    const [first, second] = [...view.pointers.values()];
    if (!second) return;
    const midpoint = { x: (first.x + second.x) / 2, y: (first.y + second.y) / 2 };
    const distance = Math.max(1, Math.hypot(first.x - second.x, first.y - second.y));
    const anchor = world(view, gesture.midpoint);
    view.camera.scale = clamp(view.camera.scale * distance / Math.max(1, gesture.distance), 0.04, 4);
    view.camera.x = anchor.x - (midpoint.x - view.width / 2) / view.camera.scale;
    view.camera.y = anchor.y - (midpoint.y - view.height / 2) / view.camera.scale;
    gesture.midpoint = midpoint;
    gesture.distance = distance;
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
  if (gesture?.kind === 'pending' && !gesture.suppress) {
    const point = local(view, event);
    if (Math.hypot(point.x - gesture.start.x, point.y - gesture.start.y) <= 6) {
      const nodeId = hit(view, point.x, point.y, event.pointerType === 'touch')?.id ?? null;
      select(view.id, nodeId);
      notify(view, 'OnSelect', nodeId);
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
  if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Enter', 'Escape', 'Home', '+', '=', '-', '/', 'f', 'F'].includes(key)) return;
  event.preventDefault();
  event.stopPropagation();
  if (key === 'Enter' && view.selectedId) notify(view, 'OnOpen', view.selectedId);
  else if (key === 'Escape') notify(view, 'OnEscape');
  else if (key === '/') notify(view, 'OnFocusSearch');
  else if (key === 'Home') fit(view.id);
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

function invalidate(view) {
  if (!view.frame && !view.disposed && !document.hidden) view.frame = requestAnimationFrame(time => frame(view, time));
}

// Extends the cosmetic-animation window (orbits/twinkle/comets) so interactions feel alive, then the loop settles again.
function wake(view, duration = 1200) {
  view.animateUntil = Math.max(view.animateUntil, performance.now() + duration);
  invalidate(view);
}

function frame(view, time) {
  view.frame = 0;
  if (view.disposed || document.hidden) return;
  const reduced = view.media.matches;
  const started = performance.now();
  while (view.layout.active && performance.now() - started < 4 && !view.pointers.size) {
    const tickStarted = performance.now();
    view.layout.tick();
    view.maxTickMs = Math.max(view.maxTickMs, performance.now() - tickStarted);
  }
  const ambient = !reduced && time < view.animateUntil;
  if (ambient) view.layout.orbit(time);
  if (view.initialFit) view.camera = fitCamera(view);
  if (view.flight) {
    const progress = clamp((time - view.flight.started) / 260, 0, 1);
    const eased = 1 - (1 - progress) ** 3;
    for (const key of ['x', 'y', 'scale']) view.camera[key] = view.flight.from[key] + (view.flight.to[key] - view.flight.from[key]) * eased;
    if (progress === 1) { view.camera = { ...view.flight.to }; view.flight = null; }
  }
  if (ambient && !view.pointers.size && time >= view.nextComet) {
    view.nextComet = time + 7000 + Math.random() * 11000;
    const fromLeft = Math.random() < 0.5;
    const life = 1100;
    view.comets.push({ x: fromLeft ? -30 : view.width + 30, y: Math.random() * view.height * 0.65,
      vx: (fromLeft ? 1 : -1) * (240 + Math.random() * 180), vy: 70 + Math.random() * 70, born: time, life });
    view.animateUntil = Math.max(view.animateUntil, time + life + 50);
  }
  draw(view, time);
  view.frames++;
  const animating = (ambient && !view.pointers.size) || (view.layout.active && !view.pointers.size) || view.flight;
  if (animating) invalidate(view);
  else if (!view.pointers.size) { view.initialFit = false; save(view); }
  if (time - view.lastSave > 4000) { view.lastSave = time; save(view); }
}


function colorFor(node) {
  const set = palettes[node.kind] || palettes.planet;
  return set[hash(node.id) % set.length];
}

function sprite(view, color, kind) {
  const key = kind + ':' + color;
  if (view.sprites.has(key)) return view.sprites.get(key);
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 96;
  const context = canvas.getContext('2d');
  context.beginPath(); context.arc(48, 48, 44, 0, Math.PI * 2); context.clip();
  const highlight = kind === 'sun' ? '#fff6df' : kind === 'planet' ? '#f5f1e8' : '#eceff4';
  const surface = context.createRadialGradient(30, 26, 1, 53, 53, 57);
  surface.addColorStop(0, highlight);
  surface.addColorStop(kind === 'sun' ? 0.14 : 0.23, color);
  surface.addColorStop(0.65, color);
  surface.addColorStop(1, kind === 'sun' ? '#5a2a0f' : '#1d222c');
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

function drawStars(view, time) {
  if (!view.stars.length) return;
  const context = view.context;
  const reduced = view.media.matches;
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
  const projected = new Map();
  view.screenNodes = [];
  view.hitGrid.clear();
  for (const node of view.layout.nodes) {
    if (!visible(view, node)) continue;
    const point = screen(view, node);
    const radius = clamp(node.radius * Math.sqrt(view.camera.scale), 3, 34);
    const relevant = !focusId || node.id === focusId || neighborhood?.has(node.id);
    const matches = !view.query || node.title.toLocaleLowerCase().includes(view.query);
    const item = { node, ...point, radius, alpha: relevant && matches ? 1 : 0.22, matches };
    projected.set(node.id, item);
    if (point.x < -100 || point.y < -100 || point.x > view.width + 100 || point.y > view.height + 100) continue;
    view.screenNodes.push(item);
    const cell = `${Math.floor(point.x / 64)}:${Math.floor(point.y / 64)}`;
    if (!view.hitGrid.has(cell)) view.hitGrid.set(cell, []);
    view.hitGrid.get(cell).push(item);
  }
  for (const node of view.layout.nodes) {
    if (node.depth <= 0 || !visible(view, node)) continue;
    const parent = view.layout.byId.get(node.parentId);
    if (!parent) continue;
    const center = screen(view, parent);
    const radius = node.orbitRadius * view.camera.scale;
    if (radius < 8 || radius > Math.max(view.width, view.height) * 1.6) continue;
    context.globalAlpha = node.id === focusId ? 0.4 : 0.14;
    context.strokeStyle = node.depth === 1 ? '#ffcf8a' : node.depth === 2 ? '#9fd6ff' : '#c3c8d4';
    context.lineWidth = 1;
    context.setLineDash([1.5, 5]);
    context.beginPath(); context.arc(center.x, center.y, radius, 0, Math.PI * 2); context.stroke();
  }
  context.setLineDash([]);
  const directed = new Set(view.layout.edges.map(edge => JSON.stringify([edge.from, edge.to])));
  for (const link of view.layout.links) {
    const source = projected.get(link.source.id), target = projected.get(link.target.id);
    if (!source || !target) continue;
    if ((source.x < 0 && target.x < 0) || (source.x > view.width && target.x > view.width)
      || (source.y < 0 && target.y < 0) || (source.y > view.height && target.y > view.height)) continue;
    const highlighted = source.node.id === focusId || target.node.id === focusId;
    context.strokeStyle = highlighted ? '#bcd8f2' : '#79858f';
    context.globalAlpha = highlighted ? 0.8 : focusId || view.query ? 0.065 : 0.23;
    context.lineWidth = highlighted ? 1.4 : 0.8;
    if (highlighted) { context.setLineDash([5, 4]); context.lineDashOffset = -(time * 0.026) % 9; }
    context.beginPath(); context.moveTo(source.x, source.y); context.lineTo(target.x, target.y); context.stroke();
    context.setLineDash([]);
    if (highlighted) {
      if (directed.has(JSON.stringify([source.node.id, target.node.id]))) arrow(context, source, target);
      if (directed.has(JSON.stringify([target.node.id, source.node.id]))) arrow(context, target, source);
    }
  }
  for (const item of view.screenNodes) {
    const { node, x, y, radius, alpha } = item;
    const color = colorFor(node);
    context.globalAlpha = alpha;
    if (node.kind === 'sun') {
      const pulse = 1 + Math.sin(time * 0.0012 + hash(node.id) % 1000) * 0.16;
      const glow = context.createRadialGradient(x, y, radius * 0.3, x, y, radius * 2.6 * pulse);
      glow.addColorStop(0, color + 'b0');
      glow.addColorStop(1, color + '00');
      context.fillStyle = glow;
      context.beginPath(); context.arc(x, y, radius * 2.6 * pulse, 0, Math.PI * 2); context.fill();
    } else if (node.kind === 'planet' && hash(node.id) % 3 === 0) {
      context.strokeStyle = color + '80'; context.lineWidth = Math.max(1, radius * 0.16);
      context.beginPath(); context.ellipse(x, y, radius * 1.75, radius * 0.55, 0.5, 0, Math.PI * 2); context.stroke();
    }
    context.globalAlpha = alpha;
    if (node.id === view.selectedId || node.id === view.hoverId) {
      context.strokeStyle = node.id === view.selectedId ? '#ffffff' : '#9ba6b3';
      context.lineWidth = node.id === view.selectedId ? 1.7 : 1;
      context.beginPath(); context.arc(x, y, radius + 5, 0, Math.PI * 2); context.stroke();
    }
    if (node.missing) {
      context.strokeStyle = '#a6a3b3'; context.lineWidth = 1.4; context.setLineDash([3, 3]);
      context.beginPath(); context.arc(x, y, radius, 0, Math.PI * 2); context.stroke(); context.setLineDash([]);
    } else {
      context.drawImage(sprite(view, color, node.kind), x - radius, y - radius, radius * 2, radius * 2);
    }
    if (node.id === view.currentId) {
      context.fillStyle = '#ffffff'; context.beginPath(); context.arc(x + radius, y - radius, 3, 0, Math.PI * 2); context.fill();
    }
    const updated = typeof node.updatedAt === 'number' ? node.updatedAt : Date.parse(node.updatedAt);
    if (view.options.recent && updated && Date.now() - updated < 7 * 86400000) {
      context.strokeStyle = '#8bdbad'; context.lineWidth = 2; context.beginPath();
      context.arc(x, y, radius + 2, -Math.PI / 2, Math.PI / 2); context.stroke();
    }
  }
  labels(view);
  context.globalAlpha = 1;
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
  context.font = '500 12px "Segoe UI", sans-serif';
  context.textAlign = 'center'; context.textBaseline = 'middle';
  const priority = item => (item.node.id === view.selectedId ? 100000 : 0)
    + (item.node.id === view.hoverId ? 90000 : 0) + (item.node.id === view.currentId ? 80000 : 0)
    + (view.query && item.matches ? 10000 : 0) + item.node.degree;
  const candidates = [...view.screenNodes].sort((left, right) => priority(right) - priority(left));
  const placed = [];
  const budget = Math.max(10, Math.floor(view.width * view.height / 7500));
  for (const item of candidates) {
    const important = priority(item) >= 80000;
    if ((!view.options.labels || placed.length >= budget || item.radius < 5.5 || item.alpha < 0.5) && !important) continue;
    let text = item.node.title;
    if (text.length > 26) text = text.slice(0, 25) + '\u2026';
    let width = view.labelWidths.get(text);
    if (width === undefined) { width = context.measureText(text).width + 12; view.labelWidths.set(text, width); }
    let rectangle;
    for (const direction of [1, -1]) {
      const candidate = { x: item.x - width / 2, y: item.y + direction * (item.radius + 17) - 10, width, height: 20 };
      const topInset = view.canvas.classList.contains('graph-canvas') ? 40 : 4;
      if (candidate.x < 4 || candidate.x + width > view.width - 4 || candidate.y < topInset || candidate.y + 20 > view.height - 4) continue;
      if (placed.some(other => candidate.x < other.x + other.width + 4 && candidate.x + width + 4 > other.x
        && candidate.y < other.y + other.height + 3 && candidate.y + 23 > other.y)) continue;
      if (view.screenNodes.some(other => other !== item && other.x + other.radius > candidate.x
        && other.x - other.radius < candidate.x + width && other.y + other.radius > candidate.y && other.y - other.radius < candidate.y + 20)) continue;
      rectangle = candidate; break;
    }
    if (!rectangle) continue;
    placed.push(rectangle);
    context.globalAlpha = important ? 1 : 0.9;
    context.fillStyle = '#090a0deb'; context.fillRect(rectangle.x, rectangle.y, width, 20);
    context.fillStyle = important ? '#ffffff' : '#c4c8d0';
    context.fillText(text, rectangle.x + width / 2, rectangle.y + 10);
  }
  if (view.labelWidths.size > 2000) view.labelWidths.clear();
  view.labels = placed;
}

export function inspect(id) {
  const view = instances.get(id);
  if (!view) return null;
  return { camera: { ...view.camera }, selectedId: view.selectedId, mode: view.mode,
    active: view.layout.active, scheduled: !!view.frame, frames: view.frames, maxTickMs: view.maxTickMs,
    nodes: view.screenNodes.map(item => ({ id: item.node.id, x: item.x, y: item.y, radius: item.radius })),
    positions: view.layout.snapshot(), labels: view.labels, edges: view.layout.edges.length };
}