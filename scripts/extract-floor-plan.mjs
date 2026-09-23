// 由 CLUB-M CAD SVG 產生前端可用的樓層底圖與分區資料。
// 執行：npm run extract:floor-plan [-- --floor B1|B2]（不指定就兩層都跑）
//
// 每層的來源檔與抽取方式寫在 scripts/floor-plan/zone-plan.mjs 的 FLOOR_PLANS。
// 兩層都是兩個檔案：精簡版當底圖、紅框版當分區來源，兩檔 viewBox 一致所以不需對位。
// 紅框的抽法依匯出管道而異：B1 靠 class 篩，B2 的紅框自己一個 <g>。
//
// 產出：public/floor-plans/<floor>-base.svg、src/apps/monitoring/floor-plan/<floor>-areas.generated.ts、
// 以及 docs/floor-plan-extract-report.md（兩層合併一份報告）。
// 面積換算僅供判讀，不是施工尺寸。
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { optimize } from 'svgo';
import {
  elementPoints, boundingBox, signedArea, containsPoint, convexHull, padConvexHull,
  dedupe, dropCollinear, selfIntersections, labelPoint, round,
} from './floor-plan/geometry.mjs';
import { FLOOR_PLANS, FLOOR_IDS, unitScaleOf } from './floor-plan/zone-plan.mjs';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const option = (name, fallback) => {
  const index = args.indexOf(name);
  return index === -1 ? fallback : args[index + 1];
};
const REPORT_OUT = resolve(ROOT, 'docs/floor-plan-extract-report.md');

const BASE_TONE = '#8fa8ba';   // 深色 UI 上的底圖線條色
/** 分區編號排序用：label 的 y 落在同一個 band 視為同一橫排（numbering: 'position' 才用得到） */
const ROW_BAND = 28;
/** FCU 標記在 B1 圖面單位下的尺寸；用來提醒哪些分區小到塞不下設備 */
const FCU_MARKER = [11, 8];

function parse(file) {
  let root = null;
  optimize(readFileSync(file, 'utf8'), { plugins: [{ name: 'capture-ast', fn: (ast) => { root = ast; return {}; } }] });
  return root.children.find((node) => node.name === 'svg');
}

function serialize(node) {
  if (node.type === 'text' || node.type === 'cdata') return node.value ?? '';
  if (node.type !== 'element') return '';
  const attributes = Object.entries(node.attributes ?? {})
    .map(([key, value]) => ` ${key}="${String(value).replace(/"/g, '&quot;')}"`).join('');
  const children = (node.children ?? []).map(serialize).join('');
  return children ? `<${node.name}${attributes}>${children}</${node.name}>` : `<${node.name}${attributes}/>`;
}

function* walk(node) {
  for (const child of node.children ?? []) {
    if (child.type === 'element') { yield child; yield* walk(child); }
  }
}

const topLevel = (svg) => svg.children.filter((child) => child.type === 'element');

/**
 * 可見底圖圖層：頂層的 <g> 且 id 不是底線開頭
 * （底線開頭的是 Illustrator 匯出時 display:none 的 CAD 圖層，例如 _空調、_大樑）。
 * 沒有 id 的群組就是實際畫出來的那層，會被保留。
 */
function baseLayersOf(svg, pick) {
  if (pick.base.kind !== 'layers') throw new Error(`未知的底圖抽法 ${pick.base.kind}`);
  return topLevel(svg).filter((child) => child.name === 'g' && !(child.attributes.id ?? '').startsWith('_'));
}

/** 紅框元素；group 抽法會連帶把設計端給的 id 一起帶出來 */
function zoneElementsOf(svg, pick) {
  if (pick.zones.kind === 'group') {
    const group = topLevel(svg).find((child) => child.attributes?.id === pick.zones.group);
    if (!group) throw new Error(`找不到分區群組 <g id="${pick.zones.group}">`);
    return [...walk(group)].filter((node) => ['polygon', 'path', 'rect'].includes(node.name));
  }
  return topLevel(svg).filter((child) => child.attributes?.class === pick.zones.className);
}

function sampledPoints(layers) {
  const out = [];
  for (const layer of layers) {
    for (const node of walk(layer)) {
      const points = elementPoints(node);
      for (let i = 0; i < points.length; i += 3) out.push(points[i]);
    }
  }
  return out;
}

// 凸包會留下大量幾乎共線的點，拉平成十來個角點才好當外框用。
function simplifyOutline(points, tolerance = 1.2) {
  const out = points.slice();
  let changed = true;
  while (changed && out.length > 4) {
    changed = false;
    for (let i = 0; i < out.length; i += 1) {
      const a = out[(i - 1 + out.length) % out.length];
      const b = out[i];
      const c = out[(i + 1) % out.length];
      const length = Math.hypot(c[0] - a[0], c[1] - a[1]) || 1;
      const distance = Math.abs((c[0] - a[0]) * (a[1] - b[1]) - (a[0] - b[0]) * (c[1] - a[1])) / length;
      if (distance < tolerance) { out.splice(i, 1); changed = true; break; }
    }
  }
  return out;
}

const viewBoxOf = (svg) => (svg.attributes.viewBox ?? '0 0 712.3 543.3').split(/\s+/).map(Number);
const quote = (text) => `'${String(text).replace(/\\/g, '\\\\').replace(/'/g, "\\'")}'`;

// ================================================================ 單一樓層

function extractFloor(floor) {
  const plan = FLOOR_PLANS[floor];
  const lower = floor.toLowerCase();
  const baseSource = resolve(ROOT, plan.base);
  const zoneSource = resolve(ROOT, plan.zones);
  const sameFile = baseSource === zoneSource;
  const warnings = [];
  const unitScale = unitScaleOf(floor);
  const minZoneArea = plan.minZoneSqm * plan.unitsPerSqm;
  const minZoneWidth = plan.minZoneWidth;

  // ---------------------------------------------------------------- 底圖

  const baseSvg = parse(baseSource);
  const baseLayers = baseLayersOf(baseSvg, plan.pick);
  if (!baseLayers.length) throw new Error(`${plan.base} 找不到可見底圖圖層，請確認檔案未被重新匯出`);

  const baseBox = boundingBox(sampledPoints(baseLayers));
  const [, , PLAN_WIDTH, PLAN_HEIGHT] = viewBoxOf(baseSvg);

  // 判定用的是貼齊底圖的外框；匯出的 OUTLINE 再往外推 4 個單位，
  // 否則剛好落在邊界上的分區角點，會被 containsPoint 的射線判定當成「在外面」。
  const OUTLINE = padConvexHull(simplifyOutline(convexHull(sampledPoints(baseLayers))), 4 * unitScale)
    .map(([x, y]) => [round(x), round(y)]);

  // ---------------------------------------------------------------- 紅框分區

  const zoneSvg = sameFile ? baseSvg : parse(zoneSource);
  if (!sameFile) {
    // 兩檔的匯出管道不同，viewBox 的小數位數也不同（Illustrator 取 1 位、PDF 轉出取 2 位），
    // 所以容許捨入誤差；差超過 0.5 單位才是真的沒對齊。
    const zoneViewBox = viewBoxOf(zoneSvg);
    if (Math.abs(zoneViewBox[2] - PLAN_WIDTH) > 0.5 || Math.abs(zoneViewBox[3] - PLAN_HEIGHT) > 0.5) {
      throw new Error(`底圖與分區檔的 viewBox 不一致（${PLAN_WIDTH}x${PLAN_HEIGHT} vs ${zoneViewBox[2]}x${zoneViewBox[3]}），紅框無法直接套用`);
    }
  }

  const dropped = [];
  const zones = [];
  for (const element of zoneElementsOf(zoneSvg, plan.pick)) {
    const points = dropCollinear(dedupe(elementPoints(element)));
    if (points.length < 3) continue;
    const area = Math.abs(signedArea(points));
    const box = boundingBox(points);
    const shortSide = Math.min(box[2] - box[0], box[3] - box[1]);
    if (area < minZoneArea || shortSide < minZoneWidth) {
      dropped.push({ area, tag: element.name, box, shortSide, reason: area < minZoneArea ? '面積過小' : '短邊過窄' });
      continue;
    }
    zones.push({
      sourceId: element.attributes?.id ?? '',
      points: points.map(([x, y]) => [round(x), round(y)]),
      area,
      label: labelPoint(points).map(round),
    });
  }
  if (!zones.length) throw new Error(`${plan.zones} 找不到任何紅框`);

  // 對位檢查：紅框整體必須落在底圖的繪圖範圍內。
  // 不比對兩檔的 bbox——底圖用精簡版、紅框檔是完整設備配置圖，兩者的繪圖範圍本來就會差
  // （B2 差 8 單位），比對只會誤報。真正要確認的是紅框有沒有跑到圖外，也就是下面這個；
  // 更精確的逐區檢查在後面的「頂點落在建築外框之外」。
  const zoneBox = boundingBox(zones.flatMap((zone) => zone.points));
  const margin = Math.max(PLAN_WIDTH, PLAN_HEIGHT) * 0.02;
  const outside = zoneBox[0] < baseBox[0] - margin || zoneBox[1] < baseBox[1] - margin
    || zoneBox[2] > baseBox[2] + margin || zoneBox[3] > baseBox[3] + margin;
  if (outside) {
    warnings.push(`紅框範圍 ${zoneBox.map(round)} 超出底圖範圍 ${baseBox.map(round)}，兩個來源檔可能不是同一份圖面`);
  }

  if (plan.numbering === 'position') {
    // 編號依圖面位置：先由上而下分排，同一排內由左而右。
    zones.sort((a, b) => {
      const row = Math.floor(a.label[1] / ROW_BAND) - Math.floor(b.label[1] / ROW_BAND);
      return row !== 0 ? row : a.label[0] - b.label[0];
    });
  } else if (zones.some((zone) => !zone.sourceId)) {
    throw new Error(`${floor} 設定為沿用設計稿編號，但有紅框沒有 id`);
  }
  // numbering: 'id' 保持原圖的文件順序，讓產生的檔案讀起來跟設計稿一致。

  const areas = zones.map((zone, index) => {
    const id = plan.numbering === 'id' ? zone.sourceId : `${plan.idPrefix}${String(index + 1).padStart(2, '0')}`;
    const crossings = selfIntersections(zone.points);
    if (crossings.length) warnings.push(`${id} 有 ${crossings.length} 處自我相交，3D 擠出前需確認`);
    if (!zone.points.every(([x, y]) => containsPoint(OUTLINE, x, y))) warnings.push(`${id} 有頂點落在建築外框之外`);
    const box = boundingBox(zone.points);
    const shortSide = Math.min(box[2] - box[0], box[3] - box[1]);
    return {
      id, name: plan.names[id] ?? '', points: zone.points, label: zone.label,
      area: round(zone.area), sqm: round(zone.area / plan.unitsPerSqm), shortSide: round(shortSide),
    };
  });

  const duplicates = areas.map((a) => a.id).filter((id, i, all) => all.indexOf(id) !== i);
  if (duplicates.length) throw new Error(`${floor} 分區編號重複：${[...new Set(duplicates)].join(', ')}`);

  // 沒過濾小框的樓層（B2）：塞不下 FCU 標記的框改用警告提醒，不直接丟掉。
  const tooSmall = areas.filter((a) => a.shortSide < Math.min(...FCU_MARKER) * unitScale);
  if (tooSmall.length) {
    warnings.push(`${tooSmall.length} 個分區短邊小於一台 FCU 標記（${round(Math.min(...FCU_MARKER) * unitScale)} 單位），`
      + `放不進設備：${tooSmall.map((a) => a.id).join(', ')}`);
  }

  // 分區之間不該重疊：抽樣互相檢查，重疊超過 8% 就示警。
  for (let i = 0; i < areas.length; i += 1) {
    for (let j = i + 1; j < areas.length; j += 1) {
      const [a, b] = [areas[i], areas[j]];
      const box = boundingBox([...a.points, ...b.points]);
      let hits = 0;
      let total = 0;
      for (let x = box[0]; x < box[2]; x += 2) {
        for (let y = box[1]; y < box[3]; y += 2) {
          if (!containsPoint(a.points, x, y)) continue;
          total += 1;
          if (containsPoint(b.points, x, y)) hits += 1;
        }
      }
      if (total && hits / total > 0.08) warnings.push(`${a.id} 與 ${b.id} 重疊約 ${Math.round(hits / total * 100)}%`);
    }
  }

  // ---------------------------------------------------------------- 產出底圖

  const composed = `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"`
    + ` viewBox="0 0 ${PLAN_WIDTH} ${PLAN_HEIGHT}" width="${PLAN_WIDTH}" height="${PLAN_HEIGHT}">`
    + `<g id="${lower}-detail" fill="${BASE_TONE}">${baseLayers.map(serialize).join('')}</g></svg>`;
  const optimized = optimize(composed, {
    multipass: true,
    floatPrecision: 1,
    plugins: [{ name: 'preset-default', params: { overrides: { cleanupIds: { minify: true } } } }],
  });
  const baseSvgOut = resolve(ROOT, `public/floor-plans/${lower}-base.svg`);
  mkdirSync(dirname(baseSvgOut), { recursive: true });
  writeFileSync(baseSvgOut, optimized.data);

  // ---------------------------------------------------------------- 產出分區資料

  const numberingNote = plan.numbering === 'id'
    ? '編號沿用設計稿紅框的 id。'
    : '編號依圖面位置自動給。';
  const areasTs = `// 由 scripts/extract-floor-plan.mjs 產生，請勿手改。
// 分區範圍是設計端在 ${plan.zones} 上畫的紅框，${numberingNote}
// 要調整範圍請改那張圖；要補中文名稱請改 scripts/floor-plan/zone-plan.mjs 的 FLOOR_PLANS.${floor}.names。
// 座標是圖面單位不是公尺（1 ㎡ ≈ ${plan.unitsPerSqm} 單位²），sqm 僅供判讀分區大小。
import type { Point, GeneratedArea } from './floor-plan-types';

export const PLAN_WIDTH = ${PLAN_WIDTH};
export const PLAN_HEIGHT = ${PLAN_HEIGHT};
/** 1 ㎡ 的圖面單位²；各樓層圖面縮放不同，設備標記尺寸也依這個值換算 */
export const UNITS_PER_SQM = ${plan.unitsPerSqm};
/** 由底圖輪廓求得的建築外框；放置設備的第一層過濾與 3D 樓地板量體都用它 */
export const OUTLINE: Point[] = [${OUTLINE.map(([x, y]) => `[${x},${y}]`).join(', ')}];

export const GENERATED_AREAS: GeneratedArea[] = [
${areas.map((a) => `  { id: '${a.id}', name: ${quote(a.name)}, points: [${a.points.map(([x, y]) => `[${x},${y}]`).join(', ')}],`
    + ` label: [${a.label[0]},${a.label[1]}], area: ${a.area}, sqm: ${a.sqm} },`).join('\n')}
];
`;
  const areasTsOut = resolve(ROOT, `src/apps/monitoring/floor-plan/${lower}-areas.generated.ts`);
  mkdirSync(dirname(areasTsOut), { recursive: true });
  writeFileSync(areasTsOut, areasTs);

  return { floor, plan, areas, dropped, warnings, OUTLINE, PLAN_WIDTH, PLAN_HEIGHT, baseBox, unitScale, minZoneArea, sizeKb: optimized.data.length / 1024 };
}

// ================================================================ 執行

const only = option('--floor', '');
const floors = only ? [only.toUpperCase()] : FLOOR_IDS;
for (const floor of floors) {
  if (!FLOOR_PLANS[floor]) throw new Error(`未知樓層 ${floor}，可用：${FLOOR_IDS.join(', ')}`);
}
const results = floors.map(extractFloor);

// ---------------------------------------------------------------- 報告

function reportSection(r) {
  const { floor, plan, areas, dropped, warnings } = r;
  const filtered = plan.minZoneSqm > 0;
  return `## ${floor}

- 底圖來源：\`${plan.base}\`（精簡版，沒有紅框）
- 分區來源：\`${plan.zones}\`（${plan.pick.zones.kind === 'group' ? `\`<g id="${plan.pick.zones.group}">\` 底下的紅框` : `\`class="${plan.pick.zones.className}"\` 的紅框`}）
- 圖面尺寸：${r.PLAN_WIDTH} x ${r.PLAN_HEIGHT} 圖面單位；底圖實際範圍 ${r.baseBox.map(round).join(', ')}
- 比例尺：1 ㎡ ≈ ${plan.unitsPerSqm} 圖面單位²（相對 B1 圖面 ${round(r.unitScale, 3)} 倍）；**非施工尺寸**
- 編號方式：${plan.numbering === 'id' ? '沿用設計稿紅框 id' : '依圖面位置自動給（由上而下、由左而右）'}
- 分區數：${areas.length}（已命名 ${areas.filter((a) => a.name).length} 個，其餘顯示編號）
- 總面積：${round(areas.reduce((sum, a) => sum + a.sqm, 0))} ㎡
- 過濾條件：${filtered
    ? `面積小於 ${plan.minZoneSqm} ㎡、或短邊小於 ${plan.minZoneWidth} 單位的紅框不列入，共濾掉 ${dropped.length} 個`
    : '不過濾（紅框都是設計端逐一編號的，全數保留）'}
- 建築外框：${r.OUTLINE.length} 個角點
- 底圖輸出：\`public/floor-plans/${floor.toLowerCase()}-base.svg\`（${r.sizeKb.toFixed(0)} KB）
- 警告：${warnings.length ? '\n' + warnings.map((w) => `  - ${w}`).join('\n') : '無'}

### ${floor} 分區清單

| 編號 | 名稱 | 面積(㎡) | 短邊(單位) | 標籤位置(x,y) |
| --- | --- | ---: | ---: | --- |
${areas.map((a) => `| ${a.id} | ${a.name || '（未命名）'} | ${a.sqm} | ${a.shortSide} | ${a.label.join(', ')} |`).join('\n')}
${filtered ? `
### ${floor} 因面積過小未列入的紅框

這些框放不下一台 FCU 標記。若其中有需要保留的，調低 \`zone-plan.mjs\` 的 \`minZoneSqm\` 或 \`minZoneWidth\` 再重跑。

| 面積(㎡) | 短邊(單位) | 原因 | 圖形 | 範圍 |
| ---: | ---: | --- | --- | --- |
${dropped.sort((a, b) => a.area - b.area).map((d) => `| ${round(d.area / plan.unitsPerSqm)} | ${round(d.shortSide)} | ${d.reason} | ${d.tag} | ${d.box.map(round).join(', ')} |`).join('\n') || '| — | — | — | — | 無 |'}
` : ''}`;
}

const report = `# 樓層分區抽取報告

由 \`npm run extract:floor-plan\` 產生。分區範圍是設計端在 CAD 圖上畫的紅框，不是程式推測的。
面積換算僅供判讀分區大小，**不是施工尺寸**。

${results.map(reportSection).join('\n')}`;
mkdirSync(dirname(REPORT_OUT), { recursive: true });
writeFileSync(REPORT_OUT, report);

for (const r of results) {
  console.log(`${r.floor}: ${r.areas.length} 區、外框 ${r.OUTLINE.length} 點、底圖 ${r.sizeKb.toFixed(0)} KB`
    + `${r.dropped.length ? `、濾掉 ${r.dropped.length} 個小框` : ''}${r.warnings.length ? `、${r.warnings.length} 則警告` : ''}`);
}
console.log(`報告：docs/floor-plan-extract-report.md`);
