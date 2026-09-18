import { forceSimulation, forceLink, forceManyBody, forceCollide, forceX, forceY } from './vendor/d3-force.mjs';

export function hash(value) {
  let result = 2166136261;
  for (const character of String(value)) result = Math.imul(result ^ character.charCodeAt(0), 16777619);
  return result >>> 0;
}

const clamp01 = value => Math.max(0, Math.min(1, value));
const mix = (left, right, amount) => left + (right - left) * amount;
const toHex = rgb => '#' + rgb.map(value => Math.round(value).toString(16).padStart(2, '0')).join('');

function lerpStops(stops, amount) {
  const t = clamp01(amount);
  for (let index = 1; index < stops.length; index++) {
    if (t <= stops[index][0]) {
      const [start, from] = stops[index - 1];
      const [end, to] = stops[index];
      const u = (t - start) / Math.max(1e-6, end - start);
      return toHex(from.map((channel, channelIndex) => mix(channel, to[channelIndex], u)));
    }
  }
  return toHex(stops.at(-1)[1]);
}

export function sunHeat(radius) {
  return clamp01((radius - 18) / 22);
}

export function sunColorFor(radius) {
  return lerpStops([
    [0, [255, 107, 61]],
    [0.25, [255, 177, 78]],
    [0.5, [255, 210, 122]],
    [0.75, [255, 244, 208]],
    [1, [158, 203, 255]],
  ], sunHeat(radius));
}

export function sunRimFor(radius) {
  return lerpStops([
    [0, [122, 28, 12]],
    [0.5, [90, 42, 15]],
    [1, [21, 39, 72]],
  ], sunHeat(radius));
}

export function sunHighlightFor(radius) {
  return lerpStops([
    [0, [255, 232, 196]],
    [0.5, [255, 246, 223]],
    [1, [238, 245, 255]],
  ], sunHeat(radius));
}

const KIND_RANK = { asteroid: 0, moon: 1, planet: 2, sun: 3, blackhole: 4, galaxy: 5 };

function childKindOf(parentKind) {
  if (parentKind === 'galaxy') return 'blackhole';
  if (parentKind === 'blackhole') return 'sun';
  if (parentKind === 'sun') return 'planet';
  if (parentKind === 'planet') return 'moon';
  return 'asteroid';
}

function preferKind(left, right) {
  return (KIND_RANK[left] ?? 0) >= (KIND_RANK[right] ?? 0) ? left : right;
}

export function kindFor(depth, descendants = 0, parentKind = null) {
  const mass = descendants >= 80 ? 'galaxy'
    : descendants >= 32 ? 'blackhole'
      : descendants >= 8 ? 'sun'
        : descendants >= 3 ? 'planet'
          : 'asteroid';
  const role = parentKind
    ? childKindOf(parentKind)
    : depth <= 0 ? 'sun'
      : depth === 1 ? 'planet'
        : depth === 2 ? 'moon'
          : 'asteroid';
  return preferKind(mass, role);
}

const massive = kind => kind === 'galaxy' || kind === 'blackhole' || kind === 'sun' || kind === 'planet';

export function radiusFor(depth, degree = 0, missing = false, descendants = 0, kind = null) {
  if (missing) return 6;
  kind ??= kindFor(depth, descendants);
  const family = Math.log2(1 + descendants);
  const links = Math.min(2, Math.log2(1 + degree) * 0.35);
  if (kind === 'galaxy') return 28 + family * 5.2 + links;
  if (kind === 'blackhole') return 22 + family * 4.6 + links;
  if (kind === 'sun') return 18 + family * 4.2 + links;
  if (kind === 'planet') return 11 + family * 2.1 + links * 0.5;
  if (kind === 'moon') return 7 + family * 1.2 + links * 0.3;
  return Math.max(3.5, 5.5 - Math.max(0, depth - 3) * 0.5);
}

function stamp(value) {
  if (typeof value === 'number' && Number.isFinite(value) && value > 0) return value;
  if (typeof value === 'string' && value) {
    const parsed = Date.parse(value);
    if (Number.isFinite(parsed)) return parsed;
  }
  return 0;
}

export function recencyHeat(node, now = Date.now()) {
  const at = stamp(node?.heatAt ?? node?.updatedAt);
  if (!at) return 0;
  const window = 7 * 86400000;
  const age = now - at;
  if (age < 0 || age > window) return 0;
  return 1 - age / window;
}

export function divePath(byId, diveId) {
  const path = new Set();
  let node = diveId && byId ? byId.get(diveId) : null;
  while (node) {
    path.add(node.id);
    node = node.parentId ? byId.get(node.parentId) : null;
  }
  return path;
}

export function openAmount(node, scale) {
  if (node.kind === 'galaxy') return clamp01(((node.radius || 0) * scale - 10) / 48);
  const nested = (node.childCount || 0) > 0 || (node.descendants || 0) > 0;
  if ((node.kind === 'blackhole' || node.kind === 'sun') && nested) {
    const body = Math.min(node.radius || 0, 24) * scale;
    return clamp01((body - 40) / 40);
  }
  return 1;
}

export function collapsed(node, scale, options = {}) {
  const size = (node.systemRadius || node.radius || 0) * scale;
  if (node.kind === 'planet') return size < 22;
  if (node.kind === 'galaxy') return openAmount(node, scale) < 0.12;
  const nested = (node.childCount || 0) > 0 || (node.descendants || 0) > 0;
  if ((node.kind === 'blackhole' || node.kind === 'sun') && nested) return openAmount(node, scale) < 0.12;
  if (node.kind === 'blackhole' || node.kind === 'sun') return size < 40;
  return false;
}

export function lodOpen(byId, keepIds) {
  const open = new Set();
  for (const id of keepIds || []) {
    let node = byId.get(id);
    while (node) {
      open.add(node.id);
      node = node.parentId ? byId.get(node.parentId) : null;
    }
  }
  return open;
}

export function lodHidden(node, byId, scale, open, options = {}) {
  if (open?.has(node.id)) return false;
  const lod = { ...options, byId };
  let parent = node.parentId ? byId.get(node.parentId) : null;
  while (parent) {
    if (collapsed(parent, scale, lod)) return true;
    parent = parent.parentId ? byId.get(parent.parentId) : null;
  }
  return false;
}

const compare = (left, right) => left < right ? -1 : left > right ? 1 : 0;
const pairKey = (from, to) => JSON.stringify([from, to]);
const finite = value => typeof value === 'number' && Number.isFinite(value) && Math.abs(value) < 1e6;

function countFamily(nodes, byId) {
  const childrenOf = new Map();
  for (const node of nodes) {
    node.childCount = 0;
    node.descendants = 0;
    if (node.depth <= 0 || !byId.has(node.parentId)) continue;
    if (!childrenOf.has(node.parentId)) childrenOf.set(node.parentId, []);
    childrenOf.get(node.parentId).push(node);
  }
  for (const [parentId, children] of childrenOf) byId.get(parentId).childCount = children.length;
  const seen = new Set();
  const walk = node => {
    if (seen.has(node.id)) return node.descendants;
    seen.add(node.id);
    let total = 0;
    let heat = stamp(node.updatedAt);
    for (const child of childrenOf.get(node.id) || []) {
      total += 1 + walk(child);
      heat = Math.max(heat, child.heatAt || 0);
    }
    node.descendants = total;
    node.heatAt = heat;
    return total;
  };
  for (const node of nodes) walk(node);
}

function assignOrbits(nodes, byId) {
  const groups = new Map();
  for (const node of nodes) {
    node.systemRadius = node.radius;
    if (node.depth <= 0 || !byId.has(node.parentId)) continue;
    if (!groups.has(node.parentId)) groups.set(node.parentId, []);
    groups.get(node.parentId).push(node);
  }
  const parents = [...groups.keys()].map(id => byId.get(id)).sort((left, right) => right.depth - left.depth);
  for (const parent of parents) {
    const children = groups.get(parent.id);
    children.sort((left, right) => compare(left.id, right.id));
    let outer = parent.radius;
    children.forEach((child, index) => {
      const glyph = parent.kind === 'galaxy' ? child.radius : child.systemRadius;
      const gap = parent.kind === 'galaxy' ? 28
        : massive(child.kind) ? 36
          : child.kind === 'moon' ? 18 : 12;
      child.orbitRadius = outer + gap + glyph;
      outer = child.orbitRadius + glyph;
      const direction = hash(child.id) % 2 === 0 ? 1 : -1;
      const base = massive(child.kind) ? 0.000062 : child.kind === 'moon' ? 0.00016 : 0.00028;
      child.orbitSpeed = direction * base / (1 + index * 0.12);
      child.orbitAngle0 = (hash(child.id + ':a') % 6283) / 1000;
    });
    parent.systemRadius = Math.max(parent.radius, outer);
  }
}

export function restorePositions(value) {
  if (!Array.isArray(value)) return new Map();
  return new Map(value.slice(0, 2000)
    .filter(node => node && typeof node.id === 'string' && finite(node.x) && finite(node.y))
    .map(node => [node.id, { x: node.x, y: node.y,
      fx: finite(node.fx) ? node.fx : null, fy: finite(node.fy) ? node.fy : null }]));
}

export function createLayout(data = {}, saved = [], savedTopology = null) {
  let simulation;
  let signature = '';
  let remaining = 0;
  let lastOrbitTime = 0;
  const restored = restorePositions(saved);
  const layout = { nodes: [], edges: [], links: [], neighbors: new Map(), byId: new Map(), roots: [],
    update, tick, pin, reset, destroy, snapshot, orbit,
    get active() { return remaining > 0 && simulation?.alpha() > 0.001; },
    get topology() { return signature; } };

  function placeSatellites(time = 0) {
    for (const node of layout.nodes) {
      if (node.depth <= 0 || node.fx != null) continue;
      const parent = layout.byId.get(node.parentId);
      if (!parent || !finite(node.orbitRadius)) continue;
      const angle = (node.orbitAngle0 || 0) + time * (node.orbitSpeed || 0);
      node.x = parent.x + Math.cos(angle) * node.orbitRadius;
      node.y = parent.y + Math.sin(angle) * node.orbitRadius;
      node.vx = node.vy = 0;
    }
  }

  function update(next) {
    const previous = layout.byId;
    const unique = new Map((next.nodes || []).filter(node => typeof node.id === 'string').map(node => [node.id, node]));
    const ordered = [...unique.values()].sort((left, right) => compare(left.id, right.id));
    const byId = new Map();
    for (const [index, metadata] of ordered.entries()) {
      const old = previous.get(metadata.id);
      const stored = restored.get(metadata.id);
      const angle = hash(metadata.id) / 4294967296 * Math.PI * 2;
      const spread = 40 + Math.sqrt(index + 1) * 25;
      const node = old || { x: stored?.x ?? Math.cos(angle) * spread, y: stored?.y ?? Math.sin(angle) * spread,
        vx: 0, vy: 0, fx: stored?.fx ?? null, fy: stored?.fy ?? null };
      const depth = Number.isFinite(metadata.depth) ? Math.max(0, metadata.depth) : 0;
      Object.assign(node, { id: metadata.id, title: metadata.title || 'Untitled', icon: metadata.icon || '',
        missing: !!metadata.missing, parentId: metadata.parentId || null, depth,
        updatedAt: metadata.updatedAt || 0, inbound: metadata.inbound || 0, outbound: metadata.outbound || 0 });
      byId.set(node.id, node);
    }
    const directed = new Map();
    const pairs = new Map();
    const neighbors = new Map([...byId.keys()].map(id => [id, new Set()]));
    for (const edge of next.edges || []) {
      if (edge.kind === 'parent' || edge.kind === 0 || !byId.has(edge.from) || !byId.has(edge.to) || edge.from === edge.to) continue;
      directed.set(pairKey(edge.from, edge.to), { from: edge.from, to: edge.to });
      const [source, target] = [edge.from, edge.to].sort(compare);
      pairs.set(pairKey(source, target), { source, target });
      neighbors.get(source).add(target);
      neighbors.get(target).add(source);
    }
    const edges = [...directed.values()].sort((left, right) => compare(pairKey(left.from, left.to), pairKey(right.from, right.to)));
    const idLinks = [...pairs.values()].sort((left, right) => compare(pairKey(left.source, left.target), pairKey(right.source, right.target)));
    const links = idLinks.map(pair => ({ source: byId.get(pair.source), target: byId.get(pair.target) }));
    const nodes = [...byId.values()];
    countFamily(nodes, byId);
    const family = [...nodes].sort((left, right) => left.depth - right.depth || compare(left.id, right.id));
    for (const node of family) {
      node.degree = neighbors.get(node.id).size;
      const parent = node.parentId ? byId.get(node.parentId) : null;
      node.kind = kindFor(node.depth, node.descendants, parent?.kind);
      node.radius = radiusFor(node.depth, node.degree, node.missing, node.descendants, node.kind);
      if (!previous.has(node.id) && !restored.has(node.id)) {
        const anchors = [...neighbors.get(node.id)].map(id => previous.get(id)).filter(Boolean);
        if (anchors.length) {
          node.x = anchors.reduce((total, anchor) => total + anchor.x, 0) / anchors.length + Math.cos(hash(node.id)) * 30;
          node.y = anchors.reduce((total, anchor) => total + anchor.y, 0) / anchors.length + Math.sin(hash(node.id)) * 30;
        }
      }
    }
    const roots = nodes.filter(node => node.depth <= 0);
    assignOrbits(nodes, byId);
    const rootLinks = links.filter(link => link.source.depth <= 0 && link.target.depth <= 0);
    const nextSignature = JSON.stringify([nodes.map(node => [node.id, node.missing, node.parentId]), idLinks]);
    layout.nodes = nodes.sort((left, right) => left.depth - right.depth);
    layout.roots = roots;
    layout.byId = byId;
    layout.edges = edges;
    layout.neighbors = neighbors;
    placeSatellites(lastOrbitTime);
    if (nextSignature === signature) return false;
    signature = nextSignature;
    simulation?.stop();
    layout.links = links;
    simulation = forceSimulation(roots).stop()
      .force('link', forceLink(rootLinks).id(node => node.id)
        .distance(link => (link.source.systemRadius || 0) + (link.target.systemRadius || 0) + 80).strength(0.4))
      .force('charge', forceManyBody().strength(-720).distanceMax(2400))
      .force('collision', forceCollide(node => (node.systemRadius || node.radius) + 48).iterations(3))
      .force('x', forceX(0).strength(0.01))
      .force('y', forceY(0).strength(0.01))
      .alpha(previous.size ? 0.35 : 1).alphaDecay(0.035).velocityDecay(0.45);
    remaining = 220;
    if (!previous.size && savedTopology === signature && roots.every(node => restored.has(node.id))) {
      remaining = 0;
      simulation.alpha(0);
    }
    return true;
  }

  function orbit(time) {
    lastOrbitTime = time;
    placeSatellites(time);
  }

  function tick(count = 1) {
    while (count-- > 0 && layout.active) {
      simulation.tick();
      remaining--;
      placeSatellites(lastOrbitTime);
    }
    return layout.active;
  }

  function pin(id, x, y) {
    const node = layout.byId.get(id);
    if (!node || !finite(x) || !finite(y)) return;
    node.x = node.fx = x;
    node.y = node.fy = y;
    node.vx = node.vy = 0;
  }

  function reset() {
    for (const node of layout.nodes) node.fx = node.fy = null;
    simulation?.alpha(0.6);
    remaining = 180;
  }

  function snapshot() {
    return layout.nodes.slice(0, 2000).map(({ id, x, y, fx, fy }) => ({ id, x, y, fx, fy }));
  }

  function destroy() {
    remaining = 0;
    simulation?.stop();
  }

  update(data);
  return layout;
}
