// Run: node tests/floor-plan.test.mjs
import assert from 'node:assert/strict';
import { buildSync } from 'esbuild';
const { outputFiles } = buildSync({ entryPoints: ['src/apps/monitoring/floor-plan/floor-plan.ts'], bundle: true, write: false, platform: 'node', format: 'esm' });
const {
  planOf, areasOf, FLOORS, containsPoint, placementArea, canPlace,
  parseLayout, serializeLayout, equipmentColor, equipmentStatus, equipmentSize, areaHeat,
  getFloorEquipmentApi, storageKey, basePlanImage, findFreeSpot,
} = await import(`data:text/javascript;base64,${Buffer.from(outputFiles[0].text).toString('base64')}`);

// ---- 兩層共通的分區不變式（設計端在 CAD 圖上畫的紅框，見 scripts/extract-floor-plan.mjs）----
const EXPECTED = {
  B1: { count: 72, idPattern: /^B1-Z\d{2}$/, image: '/floor-plans/b1-base.svg', key: 'teco-floor-plan-b1-v3' },
  B2: { count: 41, idPattern: /^B2-[ENWCS]\d{2}$/, image: '/floor-plans/b2-base.svg', key: 'teco-floor-plan-b2-v4' },
};
assert.deepEqual(FLOORS, ['B1', 'B2']);

for (const floor of FLOORS) {
  const expected = EXPECTED[floor];
  const plan = planOf(floor);
  const areas = areasOf(floor);
  assert.equal(areas.length, expected.count, `${floor} 紅框分區應為 ${expected.count} 個`);
  assert.equal(new Set(areas.map((a) => a.id)).size, areas.length, `${floor} 分區編號不得重複`);
  assert.equal(plan.baseImage, expected.image, `${floor} 應載入自己的底圖`);
  assert.equal(storageKey(floor), expected.key, `${floor} 儲存鍵值不符`);
  assert.equal(basePlanImage(floor), expected.image);
  for (const area of areas) {
    assert.match(area.id, expected.idPattern, `${area.id} 編號格式不符`);
    assert.ok(area.name, `${area.id} 應有名稱或退回顯示編號`);
    assert.equal(area.unnamed, area.name === area.id, `${area.id} 的 unnamed 標記與名稱不一致`);
    assert.ok(area.sqm > 0, `${area.id} 面積應為正值`);
    assert.ok(area.points.every(([x, y]) => containsPoint(plan.outline, x, y)), `${area.id} 分區不能超出建築外框`);
  }
  assert.equal(containsPoint(plan.outline, plan.width * 2, plan.height * 2), false, `${floor} 建築外框外不得放置`);
  // 分區互不重疊：findFreeSpot 找到的位置只會歸屬自己。
  for (const area of areas) {
    const spot = findFreeSpot('fcu', area, [], floor);
    if (!spot) continue;   // 放不下的小分區另外檢查
    assert.equal(placementArea('fcu', spot[0], spot[1], 0, floor)?.id, area.id, `${area.id} 找到的位置必須歸屬自己`);
  }
}

// ---- B1：過濾門檻保證每個分區都放得下一台 FCU ----
const b1Areas = areasOf('B1');
for (const area of b1Areas) {
  assert.ok(area.sqm >= 4, `${area.id} 面積 ${area.sqm} ㎡ 小於過濾門檻，不該出現`);
  assert.ok(findFreeSpot('fcu', area, [], 'B1'), `${area.id}（${area.sqm} ㎡）應放得下一台 FCU`);
}
// 紅框沒框到的地方（大梯廳、挑空）不是分區，設備放不進去。
assert.equal(placementArea('fcu', 300, 150, 0, 'B1'), undefined, '紅框未涵蓋的大梯廳不得放置');
assert.equal(placementArea('chiller', NaN, 100, 0, 'B1'), undefined, '非數值座標必須被拒絕');

// ---- B2：編號沿用設計稿（E東／N北／W西／C中／S南），不重編也不過濾 ----
const b2Areas = areasOf('B2');
assert.deepEqual(b2Areas.slice(0, 3).map((a) => a.id), ['B2-E01', 'B2-E02', 'B2-E03'], 'B2 應保持設計稿的文件順序');
for (const [prefix, count] of [['E', 8], ['N', 10], ['W', 6], ['C', 6], ['S', 11]]) {
  assert.equal(b2Areas.filter((a) => a.id.startsWith(`B2-${prefix}`)).length, count, `B2-${prefix} 應有 ${count} 區`);
}
// 不過濾表示會保留幾個小到放不下 FCU 標記（14.7 x 10.7 單位）的框。這是設計端的決定，
// 保留是為了不讓編號跳號，但要確保數量不會無聲擴散：
// - B2-N07（2.8 ㎡）、B2-S11（1.6 ㎡）是直角三角形，內接矩形只有約半個邊長
// - B2-W06（2.1 ㎡）寬 14.3 單位，比標記的 14.7 還窄
const b2TooSmall = b2Areas.filter((a) => !findFreeSpot('fcu', a, [], 'B2')).map((a) => a.id);
assert.deepEqual(b2TooSmall, ['B2-N07', 'B2-W06', 'B2-S11'], 'B2 放不下 FCU 的分區應只有已知的那三個');
// B2 停車場車道沒有紅框，設備放不進去。
assert.equal(placementArea('fcu', 400, 250, 0, 'B2'), undefined, 'B2 停車場區域未框選，不得放置');

// ---- 設備尺寸：B2 圖面較大，標記等比放大，兩層看起來才一樣大 ----
assert.deepEqual(equipmentSize('chiller', 'B1'), [32, 18]);
assert.deepEqual(equipmentSize('fcu', 'B1'), [11, 8]);
assert.equal(planOf('B1').unitScale, 1, 'B1 是比例尺基準');
const b2Scale = planOf('B2').unitScale;
assert.ok(b2Scale > 1.3 && b2Scale < 1.35, `B2 相對 B1 應約 1.334 倍，實得 ${b2Scale}`);
assert.deepEqual(equipmentSize('fcu', 'B2').map((v) => Math.round(v * 10) / 10), [14.7, 10.7]);

// ---- 重疊判定 ----
// 取面積最大的分區當測試場地，才放得下間距組合。位置一律用 findFreeSpot 求，
// 免得寫死的偏移量在分區重畫後失效。
const biggest = b1Areas.reduce((max, a) => (a.area > max.area ? a : max));
const spot = findFreeSpot('chiller', biggest, [], 'B1');
const chiller = { deviceId: 'chiller-1', kind: 'chiller', areaId: biggest.id, x: spot[0], y: spot[1], rotation: 0 };
assert.equal(canPlace(chiller, [], 'B1'), true);

const nextSpot = findFreeSpot('fcu', biggest, [chiller], 'B1');
assert.ok(nextSpot, '最大的分區應同時放得下冰水主機與 FCU');
const fcu = { deviceId: 'b1-fcu-01', kind: 'fcu', areaId: biggest.id, x: nextSpot[0], y: nextSpot[1], rotation: 90 };
assert.equal(canPlace(fcu, [chiller], 'B1'), true, '同分區內間距足夠的設備可並存');

const fcuSameRoom = { deviceId: 'b1-fcu-02', kind: 'fcu', areaId: biggest.id, x: spot[0], y: spot[1], rotation: 0 };
const fcuNear = { deviceId: 'b1-fcu-03', kind: 'fcu', areaId: biggest.id, x: spot[0] + 3, y: spot[1] + 3, rotation: 0 };
assert.equal(canPlace(fcuNear, [fcuSameRoom], 'B1'), false, '設備間距過近必須拒絕');
const fcuFarSpot = findFreeSpot('fcu', biggest, [fcuSameRoom], 'B1');
const fcuFar = { deviceId: 'b1-fcu-03', kind: 'fcu', areaId: biggest.id, x: fcuFarSpot[0], y: fcuFarSpot[1], rotation: 0 };
assert.equal(canPlace(fcuFar, [fcuSameRoom], 'B1'), true, '間距足夠應可放置');
// 座標系是分層的：B1 的位置套到 B2 不會剛好落在同名分區裡。
assert.equal(canPlace(chiller, [], 'B2'), false, 'B1 的配置不得直接套用到 B2');

// ---- 冰水主機比較大，小分區放不下是正確結果，不是缺陷 ----
const smallest = b1Areas.reduce((min, a) => (a.area < min.area ? a : min));
assert.equal(findFreeSpot('chiller', smallest, [], 'B1'), undefined,
  `最小分區（${smallest.sqm} ㎡）不該塞得下冰水主機`);
// 已被占滿時要誠實回報找不到，而不是硬塞
const occupied = findFreeSpot('fcu', smallest, [], 'B1');
assert.equal(findFreeSpot('fcu', smallest, [{ deviceId: 'x', kind: 'fcu', areaId: smallest.id, x: occupied[0], y: occupied[1], rotation: 0 }], 'B1'),
  undefined, '最小分區放滿後不應再找到位置');

// ---- 儲存格式：版本、樓層、重複點位、越界數值 ----
const catalog = [{ id: 'chiller-1', kind: 'chiller' }, { id: 'b1-fcu-01', kind: 'fcu' }];
assert.deepEqual(parseLayout(serializeLayout([chiller, fcu], catalog, 'B1'), catalog, 'B1'), [chiller, fcu]);
assert.deepEqual(parseLayout(null, catalog, 'B1'), []);
assert.throws(() => parseLayout('{broken', catalog, 'B1'));
assert.throws(() => parseLayout(serializeLayout([chiller], catalog, 'B1'), catalog, 'B2'), '樓層不符必須拒絕');
for (const placements of [
  [chiller, chiller], [{ ...chiller, x: Infinity }], [{ ...chiller, areaId: 'B1-Z99' }],
  [{ ...chiller, deviceId: 'unknown' }], [{ ...chiller, kind: 'fcu' }], [{ ...chiller, rotation: -90 }],
]) {
  assert.throws(() => serializeLayout(placements, catalog, 'B1'));
}
assert.throws(() => parseLayout(JSON.stringify({ version: 2, floor: 'B1', placements: [] }), catalog, 'B1'));

// ---- 樓層設備清單各自獨立（chiller 目前僅歸屬 B1）----
const b1Equipment = await getFloorEquipmentApi('B1');
const b2Equipment = await getFloorEquipmentApi('B2');
assert.equal(b1Equipment.filter((e) => e.kind === 'chiller').length, 2, 'B1 應有 2 台冰水主機');
assert.equal(b2Equipment.filter((e) => e.kind === 'chiller').length, 0, 'B2 尚無冰水主機資料，不應出現');
assert.equal(b1Equipment.filter((e) => e.kind === 'fcu').length, 61);
assert.equal(b2Equipment.filter((e) => e.kind === 'fcu').length, 30);
assert.equal(b1Equipment.find((e) => e.id === 'chiller-1').status, 'ABNORMAL', '運轉中但超標的冰水主機仍顯示異常');

// ---- 狀態顏色與區域熱力（與座標無關，沿用既有規則）----
assert.equal(equipmentColor('RUNNING'), '#60a5fa', '正常顯示藍色');
assert.equal(equipmentColor('ABNORMAL'), '#f87171', '異常顯示紅色');
assert.equal(equipmentColor('OFFLINE'), '#94a3b8');
assert.equal(equipmentStatus('STOPPED'), '停止');
const heatEquipment = [
  { id: 'normal', kind: 'fcu', status: 'RUNNING', temperature: 24 },
  { id: 'alert', kind: 'fcu', status: 'ABNORMAL', temperature: 29 },
  { id: 'stopped', kind: 'fcu', status: 'STOPPED' },
];
const heatId = biggest.id;
assert.deepEqual(areaHeat(heatId, [{ ...fcuSameRoom, deviceId: 'normal' }], heatEquipment), { tone: 'normal', color: '#60a5fa', intensity: .46, equipmentCount: 1 });
assert.equal(areaHeat(heatId, [{ ...fcuSameRoom, deviceId: 'normal' }, { ...fcuSameRoom, deviceId: 'alert' }], heatEquipment).tone, 'alert', '異常設備優先讓區域變紅');
assert.equal(areaHeat(heatId, [{ ...fcuSameRoom, deviceId: 'normal' }, { ...fcuSameRoom, deviceId: 'alert' }], heatEquipment).color, '#f87171');
assert.deepEqual(areaHeat('B1-Z01', [{ ...fcuSameRoom, deviceId: 'stopped', areaId: 'B1-Z01' }], heatEquipment), { tone: 'none', intensity: 0, equipmentCount: 1 });

console.log('Floor plan checks passed: B1 + B2 red-box zones, boundaries, fit, overlap, storage and floor isolation.');
