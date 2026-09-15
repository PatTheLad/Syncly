import { forceSimulation, forceLink, forceManyBody, forceCollide, forceX, forceY } from './vendor/d3-force.mjs';

export function hash(value) {
  let result = 2166136261;
  for (const character of String(value)) result = Math.imul(result ^ character.charCodeAt(0), 16777619);
  return result >>> 0;
}

export function radiusFor(depth, degree, missing = false) {
  if (missing) return 6;
  const boost = Math.min(9, Math.log2(1 + degree) * 1.7);
  if (depth <= 0) return 21 + boost;
  if (depth === 1) return 12.5 + boost * 0.6;
  if (depth === 2) return 7.5 + boost * 0.35;
  return Math.max(3.5, 5.5 - (depth - 3) * 0.5);
}

export function kindFor(depth) {
  return depth <= 0 ? 'sun' : depth === 1 ? 'planet' : depth === 2 ? 'moon' : 'asteroid';
}

const compare = (left, right) => left < right ? -1 : left > right ? 1 : 0;
const pairKey = (from, to) => JSON.stringify([from, to]);
const finite = value => typeof value === 'number' && Number.isFinite(value) && Math.abs(value) < 1e6;

function assignOrbits(nodes, byId) {
  const groups = new Map();
  for (const node of nodes) {
    if (node.depth <= 0 || !byId.has(node.parentId)) continue;
    if (!groups.has(node.parentId)) groups.set(node.parentId, []);
    groups.get(node.parentId).push(node);
  }
  for (const [parentId, children] of groups) {
    children.sort((left, right) => compare(left.id, right.id));
    const parent = byId.get(parentId);
    children.forEach((child, index) => {
      const gap = child.depth === 1 ? 48 : child.depth === 2 ? 24 : 14;
      child.orbitRadius = (parent.radius || 16) + child.radius + 30 + index * gap;
      const direction = hash(child.id) % 2 === 0 ? 1 : -1;
      const base = child.depth === 1 ? 0.000062 : child.depth === 2 ? 0.00016 : 0.00028;
      child.orbitSpeed = direction * base / (1 + index * 0.12);
      child.orbitAngle0 = (hash(child.id + ':a') % 6283) / 1000;
    });
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
  const restored = restorePositions(saved);
  const layout = { nodes: [], edges: [], links: [], neighbors: new Map(), byId: new Map(), roots: [],
    update, tick, pin, reset, destroy, snapshot, orbit,
    get active() { return remaining > 0 && simulation?.alpha() > 0.001; },
    get topology() { return signature; } };

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
        missing: !!metadata.missing, parentId: metadata.parentId || null, depth, kind: kindFor(depth),
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
    for (const node of byId.values()) {
      node.degree = neighbors.get(node.id).size;
      node.radius = radiusFor(node.depth, node.degree, node.missing);
      if (!previous.has(node.id) && !restored.has(node.id)) {
        const anchors = [...neighbors.get(node.id)].map(id => previous.get(id)).filter(Boolean);
        if (anchors.length) {
          node.x = anchors.reduce((total, anchor) => total + anchor.x, 0) / anchors.length + Math.cos(hash(node.id)) * 30;
          node.y = anchors.reduce((total, anchor) => total + anchor.y, 0) / anchors.length + Math.sin(hash(node.id)) * 30;
        }
      }
    }
    const roots = [...byId.values()].filter(node => node.depth <= 0);
    assignOrbits([...byId.values()], byId);
    const rootLinks = links.filter(link => link.source.depth <= 0 && link.target.depth <= 0);
    const nextSignature = JSON.stringify([[...byId.values()].map(node => [node.id, node.missing, node.parentId]), idLinks]);
    layout.nodes = [...byId.values()].sort((left, right) => left.depth - right.depth);
    layout.roots = roots;
    layout.byId = byId;
    layout.edges = edges;
    layout.neighbors = neighbors;
    if (nextSignature === signature) return false;
    signature = nextSignature;
    simulation?.stop();
    layout.links = links;
    simulation = forceSimulation(roots).stop()
      .force('link', forceLink(rootLinks).id(node => node.id).distance(220).strength(0.4))
      .force('charge', forceManyBody().strength(-620).distanceMax(1400))
      .force('collision', forceCollide(node => node.radius + 90).iterations(2))
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
    for (const node of layout.nodes) {
      if (node.depth <= 0 || node.fx != null) continue;
      const parent = layout.byId.get(node.parentId);
      if (!parent) continue;
      const angle = node.orbitAngle0 + time * node.orbitSpeed;
      node.x = parent.x + Math.cos(angle) * node.orbitRadius;
      node.y = parent.y + Math.sin(angle) * node.orbitRadius;
    }
  }

  function tick(count = 1) {
    while (count-- > 0 && layout.active) {
      simulation.tick();
      remaining--;
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