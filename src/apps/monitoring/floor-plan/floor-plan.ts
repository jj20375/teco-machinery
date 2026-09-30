import {
  listChillersApi, listFcusApi, listAlarmsApi,
  listPublicChillersApi, listPublicFcusApi, listPublicAlarmsApi,
  deriveChillerStatus, deriveFcuStatus, alarmingFcuIdsOf, isDataQualityOnline, fcuVendorLabel,
} from '../pages/admin/_services/hvac-service';
import { PlacementDeviceType, type FloorPlacementDto } from '../pages/admin/_services/floor-plan-service';
import type { FloorId } from '../pages/dashboard/_types/dashboard-types';
import type { Point, GeneratedArea } from './floor-plan-types';
import * as B1 from './b1-areas.generated';
import * as B2 from './b2-areas.generated';

export type { Point, GeneratedArea };
export type EquipmentKind = 'chiller' | 'fcu';
export interface Equipment {
  id: string;
  code: string;
  /** 客戶自訂代碼（display_name）；未設定時為 null，UI 要顯示「未設定」，不要退回顯示系統編號。 */
  name: string | null;
  kind: EquipmentKind;
  status: string;
  temperature?: number;
  /** FCU 才有：供應商編號（含 DDC），現場技術人員與供應商溝通時用的名稱。 */
  vendorLabel?: string;
}
export interface Placement {
  deviceId: string;
  kind: EquipmentKind;
  areaId: string;
  x: number;
  y: number;
  rotation: number;
}
export interface Area {
  id: string;
  /** 中文名稱；尚未命名時等於編號 */
  name: string;
  points: Point[];
  label: Point;
  /** 圖面單位平方；巢狀分區取「最小候選」與標籤密度都用它 */
  area: number;
  /** 換算面積（㎡），僅供判讀分區大小，非施工尺寸 */
  sqm: number;
  /** 尚未命名的分區；UI 用來決定要不要多畫一行名稱 */
  unnamed: boolean;
}
export interface AreaHeat {
  tone: 'normal' | 'alert' | 'none';
  color?: '#60a5fa' | '#f87171';
  intensity: number;
  equipmentCount: number;
}

export const SAVE_EVENT = 'teco-floor-plan-saved';
export const FLOORS: FloorId[] = ['B1', 'B2'];
export const AREA_COLOR = '#20465a';

export interface FloorPlan {
  floor: FloorId;
  /** 圖面尺寸（圖面單位，不是公尺） */
  width: number;
  height: number;
  /** 建築外框；放置設備的第一層過濾與 3D 樓地板量體都用它 */
  outline: Point[];
  areas: Area[];
  baseImage: string;
  /** 底圖線稿的不透明度；各樓層圖面的線條密度差很多，需要分別壓 */
  baseOpacity: number;
  /** 1 ㎡ 的圖面單位²；僅供判讀分區大小，非施工尺寸 */
  unitsPerSqm: number;
  /** 相對 B1 圖面的線性比例；設備標記尺寸依此縮放，兩層看起來才一樣大 */
  unitScale: number;
}

// 分區範圍是設計端在 CAD 圖上畫的紅框（見 scripts/extract-floor-plan.mjs）。
// 紅框沒有涵蓋的地方（B1 的大梯廳挑空、B2 的停車場車道）就沒有分區，設備自然放不進去，
// 不需要額外的「禁止配置」類別。
/** *-areas.generated.ts 的模組介面；各檔的常數是字面量型別，要用介面收斂才能共用 */
interface GeneratedPlanModule {
  PLAN_WIDTH: number;
  PLAN_HEIGHT: number;
  UNITS_PER_SQM: number;
  OUTLINE: Point[];
  GENERATED_AREAS: GeneratedArea[];
}

/**
 * 底圖線稿的不透明度，兩層一致（使用者已確認 B2 用 .42 線條更清楚，不需要像先前
 * 壓到 .22 那樣特別調低）。B2 幾乎整層是停車場、車位斜線鋪滿整張圖，原本擔心密度
 * 太高會讓分區糊進背景，但實測 .42 反而讓分區與線稿的對比更好。
 */
const BASE_OPACITY: Record<FloorId, number> = { B1: 0.42, B2: 0.42 };

function toPlan(floor: FloorId, source: GeneratedPlanModule): FloorPlan {
  return {
    floor,
    width: source.PLAN_WIDTH,
    height: source.PLAN_HEIGHT,
    outline: source.OUTLINE,
    areas: source.GENERATED_AREAS.map((zone) => ({ ...zone, name: zone.name || zone.id, unnamed: !zone.name })),
    baseImage: `/floor-plans/${floor.toLowerCase()}-base.svg`,
    baseOpacity: BASE_OPACITY[floor],
    unitsPerSqm: source.UNITS_PER_SQM,
    unitScale: Math.sqrt(source.UNITS_PER_SQM / B1.UNITS_PER_SQM),
  };
}

const PLANS: Record<FloorId, FloorPlan> = {
  B1: toPlan('B1', B1),
  B2: toPlan('B2', B2),
};

export function planOf(floor: FloorId): FloorPlan {
  return PLANS[floor];
}
export function areasOf(floor: FloorId): Area[] {
  return PLANS[floor].areas;
}
export function basePlanImage(floor: FloorId): string {
  return PLANS[floor].baseImage;
}

// 配置已改存後端（/api/v1/floor-plan/*），不再寫 localStorage。
// 改版前存在瀏覽器裡的舊配置**無法沿用**：那時的設備清單來自 mock service，deviceId 是
// 'b1-fcu-01' 這種樓層流水號，跟真實 device_fcu.id（依 channel/station_id/position 編號）
// 沒有可靠對應關係，硬搬會把真實設備放到當初替假設備擺的位置上。舊資料就讓它留在瀏覽器裡失效。

export function equipmentColor(status?: string): string {
  if (status === 'RUNNING') return '#60a5fa';
  if (status && !['OFFLINE', 'STOPPED'].includes(status)) return '#f87171';
  return '#94a3b8';
}

export function equipmentStatus(status?: string): string {
  if (status === 'RUNNING') return '正常';
  if (status === 'OFFLINE') return '離線';
  if (status === 'STOPPED') return '停止';
  if (status === 'MAINTENANCE') return '待保養';
  return status ? '異常' : '無資料';
}

export function isAlertStatus(status?: string): boolean {
  return status !== undefined && !['RUNNING', 'STOPPED', 'OFFLINE'].includes(status);
}

export function areaHeat(areaId: string, placements: Placement[], equipment: Equipment[]): AreaHeat {
  const devices = placements
    .filter((placement) => placement.areaId === areaId)
    .map((placement) => equipment.find((item) => item.id === placement.deviceId))
    .filter((item): item is Equipment => Boolean(item));
  const temperatures = devices.map((item) => item.temperature).filter((value): value is number => Number.isFinite(value));

  if (devices.some((item) => isAlertStatus(item.status))) {
    // 異常優先顯示，避免正常設備掩蓋同一區域的告警。
    const highest = Math.max(...temperatures, 26);
    return { tone: 'alert', color: '#f87171', intensity: Math.min(.88, .58 + Math.max(0, highest - 26) / 20), equipmentCount: devices.length };
  }
  if (devices.some((item) => item.status === 'RUNNING')) {
    return { tone: 'normal', color: '#60a5fa', intensity: .46, equipmentCount: devices.length };
  }
  return { tone: 'none', intensity: 0, equipmentCount: devices.length };
}

function rect(x: number, y: number, w: number, h: number): Point[] {
  return [[x, y], [x + w, y], [x + w, y + h], [x, y + h]];
}

export function pointsAttribute(points: Point[]): string {
  return points.map((p) => p.join(',')).join(' ');
}

export function containsPoint(points: Point[], x: number, y: number): boolean {
  let inside = false;
  for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
    const [xi, yi] = points[i];
    const [xj, yj] = points[j];
    if ((yi > y) !== (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

// 依 B1 實測比例（1 m ≈ 8.9 圖面單位）取的示意尺寸：冰水主機約 3.6 x 2 m、FCU 標記約 1.2 x 0.9 m。
// 紅框分區最小只有 4.8 ㎡，FCU 標記必須真的塞得進小房間，所以貼近實際機體尺寸；
// 冰水主機維持實際機體大小，放不進小房間是正確的結果，不是限制。
// B2 的圖面比 B1 大 1.334 倍，標記要跟著放大，否則同一台設備在兩層會畫成不同大小。
export function equipmentSize(kind: EquipmentKind, floor: FloorId): Point {
  const [width, height] = kind === 'chiller' ? [32, 18] : [11, 8];
  const scale = PLANS[floor].unitScale;
  return [width * scale, height * scale];
}

export function placementArea(
  kind: EquipmentKind, x: number, y: number, rotation: number, floor: FloorId,
): Area | undefined {
  if (![x, y, rotation].every(Number.isFinite)) return;
  const plan = PLANS[floor];
  const [width, height] = equipmentSize(kind, floor);
  const angle = rotation * Math.PI / 180;
  const corners = rect(-width / 2, -height / 2, width, height).map(([cx, cy]) =>
    [x + cx * Math.cos(angle) - cy * Math.sin(angle), y + cx * Math.sin(angle) + cy * Math.cos(angle)] as Point);
  if (!corners.every(([cx, cy]) => containsPoint(plan.outline, cx, cy))) return;
  // 目前的紅框互不重疊（產生腳本會檢查），取面積最小的候選只是防呆：
  // 之後若畫成「大區內再框小區」，這裡不必跟著改。
  const candidates = plan.areas.filter((a) => corners.every(([cx, cy]) => containsPoint(a.points, cx, cy)));
  return candidates.length ? candidates.reduce((min, a) => (a.area < min.area ? a : min)) : undefined;
}

/**
 * 在分區內找一個放得下的位置：先試指定點與分區標籤點，都不行才在分區內掃描。
 * L 形或狹長的分區（例如打席走道）標籤點常常貼著牆，沒有這層退路使用者會一直被擋。
 */
export function findFreeSpot(
  kind: EquipmentKind, area: Area, others: Placement[], floor: FloorId, preferred?: Point,
): Point | undefined {
  const fits = ([x, y]: Point) => canPlace({ deviceId: '', kind, areaId: area.id, x, y, rotation: 0 }, others, floor);
  for (const candidate of [preferred, area.label]) if (candidate && fits(candidate)) return candidate;
  const [x0, y0, x1, y1] = [
    Math.min(...area.points.map((p) => p[0])), Math.min(...area.points.map((p) => p[1])),
    Math.max(...area.points.map((p) => p[0])), Math.max(...area.points.map((p) => p[1])),
  ];
  // 由標籤點往外找，讓設備盡量落在分區中央而不是角落。
  const step = 4;
  const candidates: Point[] = [];
  for (let x = x0; x <= x1; x += step) for (let y = y0; y <= y1; y += step) candidates.push([x, y]);
  candidates.sort((a, b) =>
    Math.hypot(a[0] - area.label[0], a[1] - area.label[1]) - Math.hypot(b[0] - area.label[0], b[1] - area.label[1]));
  return candidates.find(fits);
}

export function canPlace(placement: Placement, others: Placement[], floor: FloorId): boolean {
  const area = placementArea(placement.kind, placement.x, placement.y, placement.rotation, floor);
  if (!area || area.id !== placement.areaId) return false;
  // 以外接圓保留標記間距；需要密集設備排布時再改成旋轉矩形相交判斷。
  const gap = 3 * PLANS[floor].unitScale;
  const radius = Math.hypot(...equipmentSize(placement.kind, floor)) / 2;
  return others.every((p) => p.deviceId === placement.deviceId ||
    Math.hypot(p.x - placement.x, p.y - placement.y) >= radius + Math.hypot(...equipmentSize(p.kind, floor)) / 2 + gap);
}

/**
 * 設備在圖面上的識別字串。後端用 (deviceType, deviceId) 兩個欄位當複合鍵，前端的 Placement
 * 模型從一開始就是單一字串 id，用 "kind:id" 組合起來兩邊就能無損互轉，不用改動既有的
 * canPlace / parseLayout 等一整串以字串 id 為前提的邏輯。
 */
export function equipmentKey(kind: EquipmentKind, deviceId: number): string {
  return `${kind}:${deviceId}`;
}

export function placementToDto(placement: Placement): FloorPlacementDto {
  const deviceId = Number(placement.deviceId.split(':')[1]);
  return {
    deviceType: placement.kind === 'chiller' ? PlacementDeviceType.Chiller : PlacementDeviceType.Fcu,
    deviceId,
    areaId: placement.areaId,
    x: placement.x,
    y: placement.y,
    rotation: placement.rotation,
  };
}

export function placementFromDto(dto: FloorPlacementDto): Placement {
  const kind: EquipmentKind = dto.deviceType === PlacementDeviceType.Chiller ? 'chiller' : 'fcu';
  return {
    deviceId: equipmentKey(kind, dto.deviceId),
    kind,
    areaId: dto.areaId,
    x: Number(dto.x),
    y: Number(dto.y),
    rotation: dto.rotation,
  };
}

/**
 * publicApi=true 時打 /api/v1/public/*（不需要登入）——前台戰情室（`/`）本來就設計成不用登入
 * 的大廳螢幕，理由見 auth-service.ts 的 publicJsonApi 說明。後台的空間設備配置頁與監控中心
 * 熱區圖是登入後的頁面，維持 publicApi=false（預設）打有權限檢查的端點。
 */
export async function getFloorEquipmentApi(floor: FloorId, publicApi = false): Promise<Equipment[]> {
  const [chillers, fcus, alarms] = publicApi
    ? await Promise.all([listPublicChillersApi(), listPublicFcusApi(floor), listPublicAlarmsApi('active')])
    : await Promise.all([listChillersApi(), listFcusApi(floor), listAlarmsApi('active')]);
  const alarmingFcus = alarmingFcuIdsOf(alarms);

  return [
    // 冰水主機資料沒有樓層欄位（device_chiller 沒這個概念）；現有機台實際皆位於 B1 機房，
    // B2 尚無主機可配置。
    ...(floor === 'B1'
      ? chillers.map((c) => {
          const status = deriveChillerStatus(c, alarms);
          return {
            id: equipmentKey('chiller', c.id),
            code: c.code,
            name: c.displayName,
            kind: 'chiller' as const,
            status,
            // 離線或讀取失敗時 value 裡的數字不可信，寧可不顯示也不要顯示 0°C。
            temperature: isDataQualityOnline(c.dataQuality) ? c.value?.chilledWaterOutletTemperature : undefined,
          };
        })
      : []),
    ...fcus.map((f) => ({
      id: equipmentKey('fcu', f.id),
      code: f.zoneCode ?? `${f.floor}-${f.id}`,
      name: f.displayName,
      vendorLabel: fcuVendorLabel(f),
      kind: 'fcu' as const,
      status: deriveFcuStatus(f, alarmingFcus),
      temperature: isDataQualityOnline(f.dataQuality) ? f.value?.temperature : undefined,
    })),
  ];
}

export function parseLayout(raw: string | null, equipment: Equipment[], floor: FloorId): Placement[] {
  if (raw === null) return [];
  const data = JSON.parse(raw);
  if (!data || data.version !== 1 || data.floor !== floor || !Array.isArray(data.placements) || data.placements.length > equipment.length) {
    throw new Error('配置格式或版本不符');
  }
  const seen = new Set<string>();
  const result: Placement[] = [];
  for (const item of data.placements) {
    if (!item || !equipment.some((e) => e.id === item.deviceId && e.kind === item.kind) || seen.has(item.deviceId) ||
      ![item.x, item.y, item.rotation].every((n) => typeof n === 'number' && Number.isFinite(n)) ||
      item.rotation < 0 || item.rotation >= 360 || !canPlace(item, result, floor)) {
      throw new Error('配置含有無效設備、重複點位或不合法的位置');
    }
    seen.add(item.deviceId);
    result.push({ deviceId: item.deviceId, kind: item.kind, areaId: item.areaId, x: item.x, y: item.y, rotation: item.rotation });
  }
  return result;
}

export function serializeLayout(placements: Placement[], equipment: Equipment[], floor: FloorId): string {
  const raw = JSON.stringify({ version: 1, floor, placements });
  parseLayout(raw, equipment, floor);
  return raw;
}
