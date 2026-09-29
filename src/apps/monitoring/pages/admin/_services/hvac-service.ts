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

export type DeviceStatus = 'RUNNING' | 'STOPPED' | 'ABNORMAL' | 'OFFLINE';

/**
 * 「告警門檻設定」頁可設定的冰水主機量測指標，ruleCode 的第一段就是這些名稱。
 * 刻意不含 AccumulatedRunningHours：那是保養提醒，不是異常（「待保養」狀態另案處理）。
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
 */
export function deriveChillerStatus(chiller: ChillerRow, activeAlarms: readonly AlarmRow[]): DeviceStatus {
  if (!chiller.dataQuality || !isDataQualityOnline(chiller.dataQuality)) return 'OFFLINE';
  if (chiller.value?.isAlarm) return 'ABNORMAL';
  const exceeded = activeAlarms.some((a) =>
    a.deviceType === AlarmDeviceType.Chiller && a.deviceId === chiller.id
    && CHILLER_THRESHOLD_METRICS.has(a.ruleCode.split('.')[0]));
  if (exceeded) return 'ABNORMAL';
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
