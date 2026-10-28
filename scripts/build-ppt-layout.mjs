// 依現場最終版 PPT（scripts/floor-plan/ppt-labels.json）與目前的分區，產生「哪台 FCU 放哪個分區、什麼座標、叫什麼名字」的對照表。
// 執行：npm run build:ppt-layout
//
// 產出：scripts/floor-plan/ppt-layout.json（給 scripts/apply-ppt-layout.mjs 寫入後端）、
//       docs/floor-plan-ppt-mapping.md（給人審閱，尤其是「需確認」那一欄）。
//
// 分區判定：標籤位置落在哪個分區就歸哪個；標籤壓在牆線上、落在走道或圖外的，在 ZONE_OVERRIDES 逐台指定。
// 座標：從標籤位置出發，找分區內「離標籤最近、而且通得過前端 canPlace（完整落在分區內、與其他設備保持間距）」的點，
// 所以存檔不會在前端驗證階段失敗（CLAUDE.md 記過這個真實 bug）。
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildSync } from 'esbuild';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
// floor-plan.ts 的底圖網址用到 Vite 的 define（astro.config.mjs），這裡不經過 Vite，要自己補；本腳本不會用到底圖網址，值隨意。
const { outputFiles } = buildSync({
  entryPoints: [resolve(ROOT, 'src/apps/monitoring/floor-plan/floor-plan.ts')],
  bundle: true, write: false, platform: 'node', format: 'esm',
  define: { __FLOOR_PLAN_VERSION__: '"script"' },
});
const { areasOf, planOf, canPlace, equipmentSize } = await import(
  `data:text/javascript;base64,${Buffer.from(outputFiles[0].text).toString('base64')}`);

const ppt = JSON.parse(readFileSync(resolve(ROOT, 'scripts/floor-plan/ppt-labels.json'), 'utf8'));

/**
 * 標籤位置判斷不了（壓在牆線、落在走道或圖外）的設備，逐台指定分區。
 * 值是 [分區 id, 理由]；理由會寫進對照表「需確認」欄，請現場確認後再改。
 */
const ZONE_OVERRIDES = {
  B1: {
    '服務台2': ['B1-Z18', '標籤在 B1002（16 sqm）旁'],
    '工坊2': ['B1-Z06', '標籤在圓形小室與 F1 的房間之間，取較近的圓形小室'],
    'V廚': ['B1-Z69', '標籤在 VIP 廚房外側'],
    'V6': ['B1-Z75', '標籤壓在 V6、V5 兩間的牆線上'],
    'V3': ['B1-Z74', 'V3、V4 在同一個紅框'],
    'V4': ['B1-Z74', 'V3、V4 在同一個紅框'],
    'V宴前走道': ['B1-Z66', '標籤在宴廳前的走道（沒有紅框），歸宴廳'],
    '辦公室': ['B1-Z34', '標籤在辦公室（31 sqm）旁，取面積相近的那個'],
    'A9': ['B1-Z45', 'A 排 9 格由左到右是 A9…A1（A6、A3 沒裝）'],
    'A8': ['B1-Z35', 'A 排 9 格由左到右是 A9…A1（A6、A3 沒裝）'],
    'A7': ['B1-Z36', 'A 排 9 格由左到右是 A9…A1（A6、A3 沒裝）'],
    'A5': ['B1-Z38', 'A 排 9 格由左到右是 A9…A1（A6、A3 沒裝）'],
    'A4': ['B1-Z39', 'A 排 9 格由左到右是 A9…A1（A6、A3 沒裝）'],
    'A2': ['B1-Z41', 'A 排 9 格由左到右是 A9…A1（A6、A3 沒裝）'],
    'A1': ['B1-Z46', 'A 排 9 格由左到右是 A9…A1（A6、A3 沒裝）'],
    '1F大門': ['B1-Z09', '1F 小圖（重繪的簡化版）上的 1F 大門'],
    '1F電梯前': ['B1-Z15', '1F 小圖（重繪的簡化版）上的 1F 電梯前'],
  },
  B2: {},
};

/** 目前資料庫裡兩台冰水主機的位置（旋轉前的座標），旋轉 180° 後放進對應的分區；朝向同步加 180°。 */
const CHILLERS = [
  { code: 'CH-1', before: [494, 412] },
  { code: 'CH-2', before: [193, 475] },
];

const distanceToPolygon = (area, x, y) => {
  let best = Infinity;
  for (let i = 0; i < area.points.length; i += 1) {
    const [x1, y1] = area.points[i];
    const [x2, y2] = area.points[(i + 1) % area.points.length];
    const dx = x2 - x1;
    const dy = y2 - y1;
    const t = Math.max(0, Math.min(1, ((x - x1) * dx + (y - y1) * dy) / (dx * dx + dy * dy || 1)));
    best = Math.min(best, Math.hypot(x - x1 - t * dx, y - y1 - t * dy));
  }
  return best;
};
const inside = (area, x, y) => {
  let result = false;
  for (let i = 0, j = area.points.length - 1; i < area.points.length; j = i++) {
    const [xi, yi] = area.points[i];
    const [xj, yj] = area.points[j];
    if ((yi > y) !== (yj > y) && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) result = !result;
  }
  return result;
};

/** 在分區內找離 target 最近、通得過 canPlace 的點（先試 target 本身，再以 1 單位格點掃描）。 */
function placeNear(floor, kind, area, target, others, rotation = 0) {
  const fits = (x, y) => canPlace({ deviceId: 'candidate', kind, areaId: area.id, x, y, rotation }, others, floor);
  const rounded = [Math.round(target[0] * 10) / 10, Math.round(target[1] * 10) / 10];
  if (fits(...rounded)) return rounded;
  const xs = area.points.map((p) => p[0]);
  const ys = area.points.map((p) => p[1]);
  let best = null;
  let bestDistance = Infinity;
  for (let x = Math.floor(Math.min(...xs)); x <= Math.ceil(Math.max(...xs)); x += 1) {
    for (let y = Math.floor(Math.min(...ys)); y <= Math.ceil(Math.max(...ys)); y += 1) {
      const distance = Math.hypot(x - target[0], y - target[1]);
      if (distance < bestDistance && fits(x, y)) { best = [x, y]; bestDistance = distance; }
    }
  }
  return best;
}

const rows = [];
const problems = [];

for (const floor of ['B1', 'B2']) {
  const plan = planOf(floor);
  const areas = areasOf(floor);
  const byId = new Map(areas.map((a) => [a.id, a]));
  const { scale, origin } = ppt.registration[floor];
  const placed = [];

  // 冰水主機只在 B1，先放，FCU 才會避開它。
  if (floor === 'B1') {
    for (const chiller of CHILLERS) {
      const target = [plan.width - chiller.before[0], plan.height - chiller.before[1]];
      const area = areas.find((a) => inside(a, ...target));
      const at = area && placeNear(floor, 'chiller', area, target, placed, 180);
      if (!area || !at) { problems.push(`${chiller.code} 旋轉後的位置 ${target} 找不到可放的分區`); continue; }
      placed.push({ deviceId: `chiller:${chiller.code}`, kind: 'chiller', areaId: area.id, x: at[0], y: at[1], rotation: 180 });
      rows.push({ floor, kind: 'chiller', code: chiller.code, areaId: area.id, x: at[0], y: at[1], rotation: 180, notes: ['沿用原本的位置旋轉 180°'] });
    }
  }

  const labels = ppt.labels.filter((l) => l.floor === floor)
    .sort((a, b) => a.station - b.station || a.position - b.position);
  for (const label of labels) {
    const point = [origin[0] + scale * label.px, origin[1] + scale * label.py];
    const notes = [];
    let area = areas.find((a) => inside(a, ...point));
    const override = ZONE_OVERRIDES[floor][label.name];
    if (override) {
      area = byId.get(override[0]);
      if (!area) { problems.push(`${floor} ${label.name} 指定的分區 ${override[0]} 不存在，請重新確認編號`); continue; }
      notes.push(override[1]);
    } else if (area) {
      const margin = distanceToPolygon(area, ...point);
      if (margin < 5) notes.push(`標籤靠近分區邊界（${margin.toFixed(1)} 單位）`);
    } else {
      problems.push(`${floor} ${label.name} 的標籤不在任何分區內，也沒有指定分區`);
      continue;
    }
    // 1F 小圖是重繪的，PPT 標籤在原圖的位置對不上，直接用分區標籤點當目標。
    const target = label.name.startsWith('1F') ? area.label : point;
    const at = placeNear(floor, 'fcu', area, target, placed);
    if (!at) { problems.push(`${floor} ${label.name} 在 ${area.id} 放不下（分區太小或已被占滿）`); continue; }
    const vendorCode = `FC_MC${label.station}_${String(label.position).padStart(2, '0')}`;
    placed.push({ deviceId: `fcu:${vendorCode}`, kind: 'fcu', areaId: area.id, x: at[0], y: at[1], rotation: 0 });
    rows.push({
      floor, kind: 'fcu', channel: floor === 'B1' ? 1 : 2, station: label.station, position: label.position,
      vendorCode, name: label.name, areaId: area.id, x: at[0], y: at[1], rotation: 0, notes,
    });
  }
}

if (problems.length) {
  console.error(`產生失敗，${problems.length} 個問題：\n${problems.map((p) => `  - ${p}`).join('\n')}`);
  process.exit(1);
}

// 同一個分區放了幾台，也是審閱時要看的資訊。
const countInZone = {};
for (const row of rows.filter((r) => r.kind === 'fcu')) countInZone[row.areaId] = (countInZone[row.areaId] ?? 0) + 1;
for (const row of rows.filter((r) => r.kind === 'fcu')) {
  if (countInZone[row.areaId] > 1) row.notes.push(`與同分區共 ${countInZone[row.areaId]} 台`);
}

const fcuRows = rows.filter((r) => r.kind === 'fcu');
writeFileSync(resolve(ROOT, 'scripts/floor-plan/ppt-layout.json'), `${JSON.stringify({
  generatedBy: 'npm run build:ppt-layout',
  source: 'ClubM B1+B2F分區送風機編號示意圖-0909.pptx',
  rows,
}, null, 1)}\n`);

const ddc = (row) => `DDC${row.channel} · ${row.vendorCode}`;
const fmt = (n) => (Math.round(n * 10) / 10).toString();
const section = (floor) => {
  const list = fcuRows.filter((r) => r.floor === floor);
  const review = list.filter((r) => r.notes.some((n) => !n.startsWith('與同分區')));
  return `## ${floor}（${list.length} 台 FCU）

| 設備 | 自訂名稱 | 分區 | 座標 | 備註 |
| --- | --- | --- | --- | --- |
${list.map((r) => `| ${ddc(r)} | ${r.name} | ${r.areaId} | ${fmt(r.x)}, ${fmt(r.y)} | ${r.notes.join('；')} |`).join('\n')}

需確認的有 ${review.length} 台（備註欄不是只有「與同分區共 N 台」的）。
`;
};
const chillerRows = rows.filter((r) => r.kind === 'chiller');
writeFileSync(resolve(ROOT, 'docs/floor-plan-ppt-mapping.md'), `# 現場最終版 PPT 對照表

由 \`npm run build:ppt-layout\` 產生，來源是 \`ClubM B1+B2F分區送風機編號示意圖-0909.pptx\`（標籤資料在
\`scripts/floor-plan/ppt-labels.json\`）。名稱 / 序號與廠商 Excel（\`大直明水路集中控制器+APP 顯示名稱20260831.xlsx\`）逐筆核對過，
完全一致。圖面已旋轉 180° 對齊 PPT 方向，分區編號是旋轉後重編的。

- 設備：\`DDC1 · FC_MC{群組}_{序號}\`；B1 的 MC1～MC5 是 VIP 區、辦公室、開放區、工坊區、1F；B2 是 DDC2 的 FC_MC1_01～31。
- 座標只是依 PPT 標籤位置推得、並調整到分區內且符合最小間距，**不是現場實際位置**，需要現場人員用「空間設備配置」頁微調。
- 備註欄有說明的（標籤壓在牆線上、走道上、或靠近分區邊界）都是需要現場確認的案例。

${section('B1')}
${section('B2')}
## 冰水主機

| 設備 | 分區 | 座標 | 備註 |
| --- | --- | --- | --- |
${chillerRows.map((r) => `| ${r.code} | ${r.areaId} | ${fmt(r.x)}, ${fmt(r.y)} | ${r.notes.join('；')} |`).join('\n')}
`);

console.log(`FCU ${fcuRows.length} 台（B1 ${fcuRows.filter((r) => r.floor === 'B1').length}、B2 ${fcuRows.filter((r) => r.floor === 'B2').length}）、冰水主機 ${chillerRows.length} 台`);
console.log('對照表：docs/floor-plan-ppt-mapping.md、scripts/floor-plan/ppt-layout.json');
