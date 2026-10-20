// 把 scripts/floor-plan/ppt-layout.json（npm run build:ppt-layout 的產出）寫進後端：
//   1. 每個樓層整層覆寫空間配置（PUT /api/v1/floor-plan/{floor}/placements，會順便回寫 FCU 的 zone_code、留操作紀錄）
//   2. 95 台 FCU 的自訂名稱（PATCH /api/v1/fcus/{id}）
// 打的是正式 API，所以權限、樂觀鎖、操作紀錄都照原本的流程走；不直接動資料庫。
//
// 用法（預設只預覽，不會寫入）：
//   TECO_USERNAME=<有「空間設備配置」「FCU管理」編輯權限的帳號> TECO_PASSWORD=<密碼> \
//     node scripts/apply-ppt-layout.mjs [--apply] [--api http://127.0.0.1:8082]
// 帳號密碼一律從環境變數讀，不寫進檔案；操作紀錄會記在這個帳號底下。
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const apply = args.includes('--apply');
const optionValue = (name) => { const i = args.indexOf(name); return i === -1 ? undefined : args[i + 1]; };
const API = (optionValue('--api') ?? process.env.TECO_API ?? 'http://127.0.0.1:8082').replace(/\/$/, '');
const { TECO_USERNAME: username, TECO_PASSWORD: password } = process.env;
if (!username || !password) {
  console.error('請用環境變數 TECO_USERNAME、TECO_PASSWORD 提供有權限的帳號（見檔頭說明）。');
  process.exit(1);
}

const layout = JSON.parse(readFileSync(resolve(ROOT, 'scripts/floor-plan/ppt-layout.json'), 'utf8'));
const FLOOR_PLACEMENT = { chiller: 0, fcu: 1 };

async function call(method, path, token, body) {
  const response = await fetch(`${API}${path}`, {
    method,
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  if (!response.ok) throw new Error(`${method} ${path} → HTTP ${response.status} ${text.slice(0, 300)}`);
  return text ? JSON.parse(text) : null;
}

const login = await call('POST', '/api/v1/auth/login', null, { username, password });
const token = login.accessToken;
if (!token) throw new Error('登入沒有取得 accessToken，帳號可能需要先選擇場館身分。');

const [fcus, chillers] = await Promise.all([call('GET', '/api/v1/fcus', token), call('GET', '/api/v1/chillers', token)]);
const fcuId = new Map(fcus.map((f) => [`${f.channel}/${f.stationId}/${f.position}`, f]));
const chillerId = new Map(chillers.map((c) => [c.code, c]));

const placementsByFloor = {};
const renames = [];
const missing = [];
for (const row of layout.rows) {
  const placement = { deviceType: FLOOR_PLACEMENT[row.kind], areaId: row.areaId, x: row.x, y: row.y, rotation: row.rotation };
  if (row.kind === 'chiller') {
    const device = chillerId.get(row.code);
    if (!device) { missing.push(`冰水主機 ${row.code}`); continue; }
    (placementsByFloor[row.floor] ??= []).push({ ...placement, deviceId: device.id });
  } else {
    const device = fcuId.get(`${row.channel}/${row.station}/${row.position}`);
    if (!device) { missing.push(`FCU ${row.vendorCode}（DDC${row.channel}）`); continue; }
    (placementsByFloor[row.floor] ??= []).push({ ...placement, deviceId: device.id });
    if ((device.displayName ?? null) !== row.name) renames.push({ id: device.id, vendorCode: row.vendorCode, from: device.displayName, to: row.name });
  }
}
if (missing.length) throw new Error(`後端找不到這些設備：${missing.join('、')}`);

console.log(`API：${API}　帳號：${username}`);
for (const [floor, list] of Object.entries(placementsByFloor)) {
  console.log(`${floor}：覆寫 ${list.length} 筆配置（FCU ${list.filter((p) => p.deviceType === 1).length}、冰水主機 ${list.filter((p) => p.deviceType === 0).length}）`);
}
console.log(`自訂名稱：${renames.length} 台需要更新（其餘已經一致）`);
if (!apply) {
  console.log('\n以上只是預覽。確認沒問題後加 --apply 才會真的寫入。');
  process.exit(0);
}

for (const [floor, placements] of Object.entries(placementsByFloor)) {
  const current = await call('GET', `/api/v1/floor-plan/${floor}/placements`, token);
  const result = await call('PUT', `/api/v1/floor-plan/${floor}/placements`, token, { placements, expectedVersion: current.version });
  console.log(`${floor} 配置已儲存：${result.count} 筆`);
}
for (const rename of renames) {
  await call('PATCH', `/api/v1/fcus/${rename.id}`, token, { displayName: rename.to });
}
console.log(`已更新 ${renames.length} 台 FCU 的自訂名稱`);
