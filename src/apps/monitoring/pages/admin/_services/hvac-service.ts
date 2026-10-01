/**
 * @file hvac-service.ts
 * 東元電機智慧環境監控 - 冰水主機/FCU/告警即時資料服務
 * 嚴格遵守專案規範：所有對外呼叫函式一律以 *Api 結尾
 *
 * 打的是 backend/ 的 /api/v1/{chillers,fcus,alarms}（見
 * backend/src/Teco.Hvac.Api/Endpoints/{Chiller,Fcu,Alarm}Endpoints.cs）。
 * 這些端點横跨監控中心總覽、冰水主機管理、FCU管理等多個頁面共用，故放在共用的 _services，
 * 不歸在單一頁面的 _services 下。
 *
 * 後端的列舉一律以整數序列化（沒有掛 JsonStringEnumConverter），這裡對應定義同樣的數值常數，
 * 不要在前端另外猜字串值。
 */

import { authorizedJsonApi, publicJsonApi } from './auth-service';

export const ReadStatus = { NotRead: 0, Success: 1, Failed: 2, Disconnected: 3 } as const;
export const FcuSwitchStatus = { Off: 0, On: 1, Unknown: -1 } as const;
export const AlarmDeviceType = { Chiller: 0, Fcu: 1 } as const;
/** 對應後端 FcuOperationMode／FcuFanSpeed（backend/src/Teco.Hvac.Contracts/Enums.cs），皆為供應商 SDK 的真實列舉值。 */
export const FcuOperationMode = { Cooling: 1, Heating: 2, Ventilation: 3, Unknown: -1 } as const;
export const FcuFanSpeedCode = { High: 0, Medium: 1, Low: 2, Auto: 3, Unknown: -1 } as const;

const FCU_MODE_LABELS: Record<number, string> = { 1: '冷氣', 2: '暖氣', 3: '送風' };
const FCU_FAN_SPEED_LABELS: Record<number, string> = { 0: '高', 1: '中', 2: '低', 3: '自動' };

export function fcuModeLabel(mode: number): string {
  return FCU_MODE_LABELS[mode] ?? '--';
}
export function fcuFanSpeedLabel(fanSpeed: number): string {
  return FCU_FAN_SPEED_LABELS[fanSpeed] ?? '--';
}

export interface DataQuality {
  channel: number;
  isConnected: boolean;
  readStatus: number;
  lastSuccessAtUtc: string | null;
  staleSeconds: number | null;
}

/**
 * 量測值顯示：沒有值（設備離線、讀取失敗）時顯示 `--`，不要顯示 0——「0.0 °C」看起來像量到 0 度，
 * 現場人員會分不出是真的 0 還是沒讀到。
 */
export function formatMeasure(value: number | null | undefined, digits = 1): string {
  return value === null || value === undefined || !Number.isFinite(value) ? '--' : value.toFixed(digits);
}

/** 判斷這筆 dataQuality 底下的 value 是否為可信資料——離線或讀取失敗時 value 裡的數字都不能信。 */
export function isDataQualityOnline(q: DataQuality): boolean {
  return q.isConnected && q.readStatus === ReadStatus.Success;
}

export interface ChillerSnapshotValue {
  updateTimeUtc: string;
  chilledWaterOutletTemperature: number;
  chilledWaterInletTemperature: number;
  chilledWaterTemperatureDifference: number;
  loadPercentage: number;
  accumulatedRunningHours: number;
  isAlarm: boolean;
}

export interface ChillerRow {
  id: number;
  code: string;
  modbusId: number;
  displayName: string;
  ratedCapacityRt: number | null;
  dataQuality: DataQuality;
  value: ChillerSnapshotValue | null;
}

export interface FcuSnapshotValue {
  id: string;
  address: number;
  switchStatus: number;
  mode: number;
  fanSpeed: number;
  temperature: number;
}

export interface FcuRow {
  id: number;
  /** 1＝DDC1（B1F）、2＝DDC2（B2F）。 */
  channel: number;
  stationId: number;
  position: number;
  /** 4X Holding Register 文件位址（說明書表 18），例如 40051。 */
  address: number;
  /** 供應商程式裡的 FCU 編號，例如 FC_MC1_01。DDC1、DDC2 之間會重複，顯示時用 fcuVendorLabel()。 */
  vendorCode: string;
  floor: string;
  zoneCode: string | null;
  displayName: string | null;
  dataQuality: DataQuality;
  value: FcuSnapshotValue | null;
}

export interface AlarmRow {
  id: number;
  deviceType: number;
  deviceId: number;
  ruleCode: string;
  severity: number;
  startedAt: string;
  endedAt: string | null;
  peakValue: number | null;
  ackByUserId: number | null;
  ackAt: string | null;
  memo: string | null;
  deviceName: string;
  deviceCode: string;
  location: string;
  ruleLabel: string;
}

/**
 * 保養提醒告警的 ruleCode（後端 ChillerMaintenanceRepository.MaintenanceRuleCode）。
 * 2026-10-01 以前的舊格式帶門檻後綴（AccumulatedRunningHours.0.5000），歷史報表裡仍查得到，一併認得。
 */
const MAINTENANCE_RULE_CODE = 'AccumulatedRunningHours';

export function isMaintenanceAlarm(alarm: Pick<AlarmRow, 'ruleCode'>): boolean {
  return alarm.ruleCode === MAINTENANCE_RULE_CODE || alarm.ruleCode.startsWith(`${MAINTENANCE_RULE_CODE}.`);
}

/** 保養提醒要顯示成「待保養」，不是「異常」。 */
export function alarmBadgeStatus(alarm: Pick<AlarmRow, 'ruleCode'>): 'ABNORMAL' | 'MAINTENANCE' {
  return isMaintenanceAlarm(alarm) ? 'MAINTENANCE' : 'ABNORMAL';
}

/** 告警時間，對齊設計稿格式「2026/08/18 10:23」。 */
export function formatAlarmTime(iso: string): string {
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}/${pad(d.getMonth() + 1)}/${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** 供應商編號加上 DDC，例如「DDC1 · FC_MC1_01」——編號本身在兩台 DDC 之間會重複（說明書 6.1）。 */
export function fcuVendorLabel(fcu: Pick<FcuRow, 'channel' | 'vendorCode'>): string {
  return `DDC${fcu.channel} · ${fcu.vendorCode}`;
}

/** MAINTENANCE（待保養）只會出現在冰水主機，FCU 沒有運轉時數（2026-09-30 依設計稿確認）。 */
export type DeviceStatus = 'RUNNING' | 'STOPPED' | 'ABNORMAL' | 'OFFLINE' | 'MAINTENANCE';

/**
 * 「告警門檻設定」頁可設定的冰水主機量測指標，ruleCode 的第一段就是這些名稱。
 * 刻意不含 AccumulatedRunningHours：那是保養提醒，不是異常，判成「待保養」。
 */
const CHILLER_THRESHOLD_METRICS: ReadonlySet<string> = new Set([
  'ChilledWaterOutletTemperature',
  'ChilledWaterInletTemperature',
  'ChilledWaterTemperatureDifference',
]);

/**
 * 異常有兩個來源：主機自己回報的 14 個硬體警報（value.isAlarm），以及後台設定的門檻被超過
 * （對照目前有效告警）。原本只看硬體警報，超過門檻時數字變紅、狀態卻還是「運轉中」，
 * 跟規格書「超標時狀態切換為異常」不符，也跟 FCU 超標就顯示異常的行為不一致。
 *
 * 優先順序：離線 > 異常 > 待保養 > 運轉／停止（docs/IOT_資料對照與缺口清單.md 2.3）。
 * 待保養排在異常後面，是因為異常要立刻處理，保養可以排時間。
 */
export function deriveChillerStatus(chiller: ChillerRow, activeAlarms: readonly AlarmRow[]): DeviceStatus {
  if (!chiller.dataQuality || !isDataQualityOnline(chiller.dataQuality)) return 'OFFLINE';
  if (chiller.value?.isAlarm) return 'ABNORMAL';
  const own = activeAlarms.filter((a) => a.deviceType === AlarmDeviceType.Chiller && a.deviceId === chiller.id);
  if (own.some((a) => CHILLER_THRESHOLD_METRICS.has(a.ruleCode.split('.')[0]))) return 'ABNORMAL';
  if (own.some(isMaintenanceAlarm)) return 'MAINTENANCE';
  return (chiller.value?.loadPercentage ?? 0) > 0 ? 'RUNNING' : 'STOPPED';
}

/**
 * 給「運轉中幾台」這類統計用：待保養只是提醒，主機可能還在運轉，要依負載歸回運轉／停止，
 * 不然會被算進「停止」。顯示徽章時仍用 deriveChillerStatus 的「待保養」。
 */
export function chillerOperatingStatus(chiller: ChillerRow, activeAlarms: readonly AlarmRow[]): DeviceStatus {
  const status = deriveChillerStatus(chiller, activeAlarms);
  if (status !== 'MAINTENANCE') return status;
  return (chiller.value?.loadPercentage ?? 0) > 0 ? 'RUNNING' : 'STOPPED';
}

/** FCU 沒有像冰水主機那樣現成的 alarm 旗標，異常與否要對照目前有效告警清單的 deviceId。 */
export function deriveFcuStatus(fcu: FcuRow, alarmingFcuIds: ReadonlySet<number>): DeviceStatus {
  if (!fcu.dataQuality || !isDataQualityOnline(fcu.dataQuality)) return 'OFFLINE';
  if (alarmingFcuIds.has(fcu.id)) return 'ABNORMAL';
  return fcu.value?.switchStatus === FcuSwitchStatus.On ? 'RUNNING' : 'STOPPED';
}

/** 從有效告警清單取出正在告警的 FCU deviceId 集合，供 deriveFcuStatus 使用。 */
export function alarmingFcuIdsOf(alarms: readonly AlarmRow[]): Set<number> {
  return new Set(alarms.filter((a) => a.deviceType === AlarmDeviceType.Fcu).map((a) => a.deviceId));
}

export function listChillersApi(): Promise<ChillerRow[]> {
  return authorizedJsonApi('/api/v1/chillers');
}

export function listFcusApi(floor?: string): Promise<FcuRow[]> {
  const query = floor ? `?floor=${encodeURIComponent(floor)}` : '';
  return authorizedJsonApi(`/api/v1/fcus${query}`);
}

/**
 * 場館自己維護的顯示名稱／自訂代碼。系統自己的編號（FCU 的 zoneCode、冰水主機的 code）
 * 是實體接線／Modbus 位址決定的，不開放修改，兩者在畫面上並存顯示。
 * FCU 傳空字串代表清回「未設定」；冰水主機的欄位是 NOT NULL，後端不接受留空。
 */
export function updateFcuDisplayNameApi(id: number, displayName: string): Promise<void> {
  return authorizedJsonApi(`/api/v1/fcus/${id}`, {
    method: 'PATCH',
    body: JSON.stringify({ displayName }),
  });
}

export function updateChillerDisplayNameApi(id: number, displayName: string): Promise<void> {
  return authorizedJsonApi(`/api/v1/chillers/${id}`, {
    method: 'PATCH',
    body: JSON.stringify({ displayName }),
  });
}

export interface ChillerMaintenanceLog {
  id: number;
  performedAt: string;
  performedByName: string | null;
  hoursAtReset: number;
  hoursSincePrevious: number | null;
  memo: string | null;
}

/**
 * 保養提醒（「機車換機油」模式）：hoursSinceService＝目前累積時數－上次保養時的累積時數。
 * baselineHours 為 null 代表還沒開始計算（剛設定保養間隔、主機尚未回報過時數）。
 */
export interface ChillerMaintenanceStatus {
  chillerId: number;
  intervalHours: number | null;
  baselineHours: number | null;
  baselineAt: string | null;
  currentHours: number | null;
  hoursSinceService: number | null;
  remainingHours: number | null;
  isDue: boolean;
  history: ChillerMaintenanceLog[];
}

export function getChillerMaintenanceApi(chillerId: number): Promise<ChillerMaintenanceStatus> {
  return authorizedJsonApi(`/api/v1/chillers/${chillerId}/maintenance`);
}

/** 「保養完成」：熄掉保養提醒，並以當下的累積時數重新起算。需要 hvac.thresholds:update。 */
export function resetChillerMaintenanceApi(chillerId: number, memo?: string): Promise<{ hoursAtReset: number; hoursSincePrevious: number | null }> {
  return authorizedJsonApi(`/api/v1/chillers/${chillerId}/maintenance/reset`, {
    method: 'POST',
    body: JSON.stringify({ memo: memo?.trim() || null }),
  });
}

/**
 * status='active'（預設）只回傳目前尚未結束的告警；'all' 回傳含已結束的歷史紀錄。
 * `from`/`to`（ISO 日期字串）給告警歷史報表用——有給的話後端會放寬回傳筆數上限
 * （見 AlarmEndpoints.BuildListAsync），不是每次都只回最近 200 筆。
 */
export function listAlarmsApi(status: 'active' | 'all' = 'active', from?: string, to?: string): Promise<AlarmRow[]> {
  const params = new URLSearchParams({ status });
  if (from) params.set('from', from);
  if (to) params.set('to', to);
  return authorizedJsonApi(`/api/v1/alarms?${params.toString()}`);
}

export interface ChillerHistoryPoint {
  ts: string;
  chilledWaterOut: number | null;
  chilledWaterIn: number | null;
  chilledWaterDelta: number | null;
  inputPowerKw: number | null;
  accumulatedKwh: number | null;
  loadPercentage: number | null;
  runningHours: number | null;
  readStatus: number;
}

/**
 * interval='1h'（預設，報表用）查 rollup_chiller_1h；'raw' 查節流後落地的原始讀值
 * （見 backend 的 ChillerRepository.GetHistoryAsync）。時間全部是 UTC，畫面顯示前要自己轉。
 */
export function getChillerHistoryApi(
  code: string, fromIso: string, toIso: string, interval: 'raw' | '1h' = '1h',
): Promise<ChillerHistoryPoint[]> {
  const params = new URLSearchParams({ from: fromIso, to: toIso, interval });
  return authorizedJsonApi(`/api/v1/chillers/${encodeURIComponent(code)}/history?${params.toString()}`);
}

export interface FcuHistoryPoint {
  ts: string;
  switchStatus: number | null;
  mode: number | null;
  fanSpeed: number | null;
  temperature: number | null;
  readStatus: number;
  onMinutes: number | null;
}

/** interval='1h'（預設，報表用）查 rollup_fcu_1h；'raw' 查原始讀值。 */
export function getFcuHistoryApi(
  id: number, fromIso: string, toIso: string, interval: 'raw' | '1h' = '1h',
): Promise<FcuHistoryPoint[]> {
  const params = new URLSearchParams({ from: fromIso, to: toIso, interval });
  return authorizedJsonApi(`/api/v1/fcus/${id}/history?${params.toString()}`);
}

export interface FcuHourlyStats {
  date: string;
  hours: string[];
  b1Count: number[];
  b2Count: number[];
  b1Temp: (number | null)[];
  b2Temp: (number | null)[];
}

/** date 省略時預設容器當地日期（TZ=Asia/Taipei）；格式 YYYY-MM-DD。 */
export function getHourlyFcuStatsApi(date?: string): Promise<FcuHourlyStats> {
  const query = date ? `?date=${encodeURIComponent(date)}` : '';
  return authorizedJsonApi(`/api/v1/fcus/hourly-stats${query}`);
}

// ── 公開唯讀版本：給前台戰情室（`/`）用，打 /api/v1/public/*，不需要登入 ──
// 理由見 auth-service.ts 的 publicJsonApi 說明；資料形狀跟上面三支完全一樣，只是不掛 JWT。

export function listPublicChillersApi(): Promise<ChillerRow[]> {
  return publicJsonApi('/api/v1/public/chillers');
}

export function listPublicFcusApi(floor?: string): Promise<FcuRow[]> {
  const query = floor ? `?floor=${encodeURIComponent(floor)}` : '';
  return publicJsonApi(`/api/v1/public/fcus${query}`);
}

export function listPublicAlarmsApi(status: 'active' | 'all' = 'active'): Promise<AlarmRow[]> {
  return publicJsonApi(`/api/v1/public/alarms?status=${status}`);
}

export function getPublicHourlyFcuStatsApi(date?: string): Promise<FcuHourlyStats> {
  const query = date ? `?date=${encodeURIComponent(date)}` : '';
  return publicJsonApi(`/api/v1/public/fcus/hourly-stats${query}`);
}
