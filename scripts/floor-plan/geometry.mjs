// CAD 圖層轉互動分區用的幾何工具；只服務 scripts/extract-floor-plan.mjs，不進 bundle。
const NUMBER = /[-+]?(?:\d*\.\d+|\d+)(?:[eE][-+]?\d+)?/g;
const COMMAND = /([MmLlHhVvCcSsQqTtAaZz])([^MmLlHhVvCcSsQqTtAaZz]*)/g;
const CURVE_STEPS = 10;

function cubic(p0, p1, p2, p3, steps = CURVE_STEPS) {
  const out = [];
  for (let i = 1; i <= steps; i += 1) {
    const t = i / steps;
    const u = 1 - t;
    out.push([
      u * u * u * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t * t * t * p3[0],
      u * u * u * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t * t * t * p3[1],
    ]);
  }
  return out;
}

// 只支援 Illustrator 由 DWG 匯出會用到的指令；弧線以固定段數展平，分區邊界不需要曲線精度。
export function flattenPath(d) {
  const points = [];
  let cursor = [0, 0];
  let start = [0, 0];
  let previousControl = null;
  for (const [, command, rawArgs] of d.matchAll(COMMAND)) {
    const values = (rawArgs.match(NUMBER) ?? []).map(Number);
    const relative = command === command.toLowerCase();
    const kind = command.toUpperCase();
    const absolute = (x, y) => (relative ? [cursor[0] + x, cursor[1] + y] : [x, y]);
    let i = 0;
    const push = (point) => { points.push(point); cursor = point; };
    if (kind === 'Z') { cursor = start; previousControl = null; continue; }
    while (i < values.length) {
      if (kind === 'M') {
        const point = absolute(values[i], values[i + 1]);
        if (i === 0) start = point;
        push(point);
        i += 2;
      } else if (kind === 'L') {
        push(absolute(values[i], values[i + 1]));
        i += 2;
      } else if (kind === 'H') {
        push([relative ? cursor[0] + values[i] : values[i], cursor[1]]);
        i += 1;
      } else if (kind === 'V') {
        push([cursor[0], relative ? cursor[1] + values[i] : values[i]]);
        i += 1;
      } else if (kind === 'C' || kind === 'S') {
        const control1 = kind === 'C' ? absolute(values[i], values[i + 1])
          : previousControl ? [2 * cursor[0] - previousControl[0], 2 * cursor[1] - previousControl[1]] : cursor;
        const offset = kind === 'C' ? 2 : 0;
        const control2 = absolute(values[i + offset], values[i + offset + 1]);
        const end = absolute(values[i + offset + 2], values[i + offset + 3]);
        for (const point of cubic(cursor, control1, control2, end)) points.push(point);
        cursor = end;
        previousControl = control2;
        i += kind === 'C' ? 6 : 4;
        continue;
      } else if (kind === 'Q' || kind === 'T' || kind === 'A') {
        // 圖層裡沒有出現這些指令；真的出現時取端點，並在呼叫端以警告揭露。
        const size = kind === 'A' ? 7 : kind === 'Q' ? 4 : 2;
        push(absolute(values[i + size - 2], values[i + size - 1]));
        i += size;
        continue;
      } else break;
      previousControl = null;
    }
  }
  return points;
}

export function elementPoints(node) {
  const attr = (name, fallback = 0) => Number(node.attributes[name] ?? fallback);
  const list = (raw) => {
    const values = (raw ?? '').match(NUMBER)?.map(Number) ?? [];
    const out = [];
    for (let i = 0; i + 1 < values.length; i += 2) out.push([values[i], values[i + 1]]);
    return out;
  };
  switch (node.name) {
    case 'path': return flattenPath(node.attributes.d ?? '');
    case 'polygon':
    case 'polyline': return list(node.attributes.points);
    case 'rect': {
      const [x, y, w, h] = [attr('x'), attr('y'), attr('width'), attr('height')];
      return [[x, y], [x + w, y], [x + w, y + h], [x, y + h]];
    }
    case 'line': return [[attr('x1'), attr('y1')], [attr('x2'), attr('y2')]];
    case 'circle': {
      const [cx, cy, r] = [attr('cx'), attr('cy'), attr('r')];
      return [[cx - r, cy - r], [cx + r, cy + r]];
    }
    case 'ellipse': {
      const [cx, cy, rx, ry] = [attr('cx'), attr('cy'), attr('rx'), attr('ry')];
      return [[cx - rx, cy - ry], [cx + rx, cy + ry]];
    }
    default: return [];
  }
}

// 用迴圈而不是 Math.min(...xs)：底圖動輒十萬個點，展開運算子會爆掉呼叫堆疊。
export function boundingBox(points) {
  if (!points.length) return null;
  let [minX, minY, maxX, maxY] = [Infinity, Infinity, -Infinity, -Infinity];
  for (const [x, y] of points) {
    if (x < minX) minX = x;
    if (y < minY) minY = y;
    if (x > maxX) maxX = x;
    if (y > maxY) maxY = y;
  }
  return [minX, minY, maxX, maxY];
}

export function signedArea(points) {
  let total = 0;
  for (let i = 0; i < points.length; i += 1) {
    const [x1, y1] = points[i];
    const [x2, y2] = points[(i + 1) % points.length];
    total += x1 * y2 - x2 * y1;
  }
  return total / 2;
}

export function containsPoint(points, x, y) {
  let inside = false;
  for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
    const [xi, yi] = points[i];
    const [xj, yj] = points[j];
    if ((yi > y) !== (yj > y) && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

// CAD 匯出常見的重複收尾點會讓多邊形自我相交，earcut 三角化會失敗，先清乾淨。
export function dedupe(points, epsilon = 0.05) {
  const out = [];
  for (const point of points) {
    const last = out[out.length - 1];
    if (!last || Math.hypot(point[0] - last[0], point[1] - last[1]) > epsilon) out.push(point);
  }
  while (out.length > 1 && Math.hypot(out[0][0] - out[out.length - 1][0], out[0][1] - out[out.length - 1][1]) <= epsilon) out.pop();
  return out;
}

export function dropCollinear(points, epsilon = 0.02) {
  const out = [];
  for (let i = 0; i < points.length; i += 1) {
    const previous = points[(i - 1 + points.length) % points.length];
    const current = points[i];
    const next = points[(i + 1) % points.length];
    const cross = (current[0] - previous[0]) * (next[1] - previous[1]) - (current[1] - previous[1]) * (next[0] - previous[0]);
    if (Math.abs(cross) > epsilon) out.push(current);
  }
  return out.length >= 3 ? out : points;
}

function segmentsCross(p, q, r, s) {
  const cross = (o, a, b) => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
  const [d1, d2, d3, d4] = [cross(r, s, p), cross(r, s, q), cross(p, q, r), cross(p, q, s)];
  return (d1 > 0) !== (d2 > 0) && (d3 > 0) !== (d4 > 0);
}

export function selfIntersections(points) {
  const found = [];
  const n = points.length;
  for (let i = 0; i < n; i += 1) {
    for (let j = i + 2; j < n; j += 1) {
      if (i === 0 && j === n - 1) continue;
      if (segmentsCross(points[i], points[(i + 1) % n], points[j], points[(j + 1) % n])) found.push([i, j]);
    }
  }
  return found;
}

// 相鄰分區共用牆線，頂點會落在彼此邊界上，用頂點判斷包含關係並不可靠；改採內部取樣。
export function interiorSamples(points, target = 400) {
  const [x0, y0, x1, y1] = boundingBox(points);
  const step = Math.max(0.5, Math.sqrt(((x1 - x0) * (y1 - y0)) / target));
  const out = [];
  for (let x = x0 + step / 2; x < x1; x += step) {
    for (let y = y0 + step / 2; y < y1; y += step) {
      if (containsPoint(points, x, y)) out.push([x, y]);
    }
  }
  return out.length ? out : [[(x0 + x1) / 2, (y0 + y1) / 2]];
}

export function coverage(samples, predicate) {
  return samples.filter(([x, y]) => predicate(x, y)).length / samples.length;
}

function distanceToEdges(points, x, y) {
  let best = Infinity;
  for (let i = 0; i < points.length; i += 1) {
    const [x1, y1] = points[i];
    const [x2, y2] = points[(i + 1) % points.length];
    const dx = x2 - x1;
    const dy = y2 - y1;
    const lengthSquared = dx * dx + dy * dy;
    const t = lengthSquared ? Math.max(0, Math.min(1, ((x - x1) * dx + (y - y1) * dy) / lengthSquared)) : 0;
    best = Math.min(best, Math.hypot(x - (x1 + t * dx), y - (y1 + t * dy)));
  }
  return best;
}

// L 形與帶弧邊的分區用 bbox 中心會把標籤丟到牆外，改取離邊界最遠的內部點。
export function labelPoint(points, resolution = 40) {
  const [x0, y0, x1, y1] = boundingBox(points);
  const stepX = (x1 - x0) / resolution;
  const stepY = (y1 - y0) / resolution;
  let best = null;
  let bestDistance = -1;
  for (let ix = 0; ix <= resolution; ix += 1) {
    for (let iy = 0; iy <= resolution; iy += 1) {
      const x = x0 + ix * stepX;
      const y = y0 + iy * stepY;
      if (!containsPoint(points, x, y)) continue;
      const distance = distanceToEdges(points, x, y);
      if (distance > bestDistance) { bestDistance = distance; best = [x, y]; }
    }
  }
  return best ?? [(x0 + x1) / 2, (y0 + y1) / 2];
}

// Andrew's monotone chain；用來從所有分區頂點算出一個粗略的樓層外框，
// 供 placementArea() 的第一層過濾與 3D 樓地板量體使用。非凸角（例如右側斜切角）
// 會被填平，這是安全方向的誤差：外框只會偏大，真正的邊界仍由各分區精確判定。
export function convexHull(points) {
  const sorted = [...new Map(points.map((p) => [`${p[0]},${p[1]}`, p])).values()]
    .sort((a, b) => a[0] - b[0] || a[1] - b[1]);
  if (sorted.length < 3) return sorted;
  const cross = (o, a, b) => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
  const build = (list) => {
    const hull = [];
    for (const point of list) {
      while (hull.length >= 2 && cross(hull[hull.length - 2], hull[hull.length - 1], point) <= 0) hull.pop();
      hull.push(point);
    }
    return hull;
  };
  const lower = build(sorted);
  const upper = build([...sorted].reverse());
  return [...lower.slice(0, -1), ...upper.slice(0, -1)];
}

// 把凸包由形心方向外推 margin：凸包頂點本身常常就是某個分區的真實角點，
// containsPoint() 的射線判定在邊界上不穩定（可能判為外部）。外推後所有原始點
// 都嚴格落在外框「內部」，而不是卡在邊界上，placementArea() 的粗篩才不會誤判。
export function padConvexHull(hull, margin) {
  const cx = hull.reduce((sum, [x]) => sum + x, 0) / hull.length;
  const cy = hull.reduce((sum, [, y]) => sum + y, 0) / hull.length;
  return hull.map(([x, y]) => {
    const dx = x - cx;
    const dy = y - cy;
    const length = Math.hypot(dx, dy) || 1;
    return [x + (dx / length) * margin, y + (dy / length) * margin];
  });
}

export function round(value, digits = 1) {
  const factor = 10 ** digits;
  return Math.round(value * factor) / factor;
}
