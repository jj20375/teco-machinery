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

/** 通訊讀取狀態列舉常數 */
export const ReadStatus = {
  /** 尚未執行讀取 */
  NotRead: 0,
  /** 讀取成功 */
  Success: 1,
  /** 讀取失敗（例如校驗碼錯誤或逾時） */
  Failed: 2,
  /** 網路中斷或斷線 */
  Disconnected: 3,
} as const;

/** FCU 運轉開關狀態常數 */
export const FcuSwitchStatus = {
  /** 關機停止 */
  Off: 0,
  /** 開機運轉 */
  On: 1,
  /** 未知狀態 */
  Unknown: -1,
} as const;

/**
 * 告警所屬設備類型常數
 * Channel 是通訊通道（deviceId＝0 漢鐘 Gateway、1 DDC1、2 DDC2），給「離線」告警用，不是實體設備。
 */
export const AlarmDeviceType = {
  /** 冰水主機 */
  Chiller: 0,
  /** 小型送風機 (FCU) */
  Fcu: 1,
  /** Modbus 通訊通道 */
  Channel: 2,
} as const;

/** 對應後端 FcuOperationMode（backend/src/Teco.Hvac.Contracts/Enums.cs），皆為供應商 SDK 的真實列舉值。 */
export const FcuOperationMode = {
  /** 冷氣模式 */
  Cooling: 1,
  /** 暖氣模式 */
  Heating: 2,
  /** 送風模式 */
  Ventilation: 3,
  /** 未知模式 */
  Unknown: -1,
} as const;

/** 對應後端 FcuFanSpeed（backend/src/Teco.Hvac.Contracts/Enums.cs）風速列舉常數 */
export const FcuFanSpeedCode = {
  /** 高風速 */
  High: 0,
  /** 中風速 */
  Medium: 1,
  /** 低風速 */
  Low: 2,
  /** 自動風速 */
  Auto: 3,
  /** 未知風速 */
  Unknown: -1,
} as const;

/** FCU 運轉模式繁體中文標籤對照表 */
const FCU_MODE_LABELS: Record<number, string> = { 1: '冷氣', 2: '暖氣', 3: '送風' };
/** FCU 風速設定繁體中文標籤對照表 */
const FCU_FAN_SPEED_LABELS: Record<number, string> = { 0: '高', 1: '中', 2: '低', 3: '自動' };

/**
 * 取得 FCU 運轉模式之中文顯示名稱。
 * @param mode 運轉模式代碼
 * @returns 中文標籤字串（例如 "冷氣"、"送風" 或 "--"）
 */
export function fcuModeLabel(mode: number): string {
  return FCU_MODE_LABELS[mode] ?? '--';
}

/**
 * 取得 FCU 風速之中文顯示名稱。
 * @param fanSpeed 風速代碼
 * @returns 中文風速標籤（例如 "高"、"中"、"自動" 或 "--"）
 */
export function fcuFanSpeedLabel(fanSpeed: number): string {
  return FCU_FAN_SPEED_LABELS[fanSpeed] ?? '--';
}

/**
 * 資料品質指標介面（反映最後通訊狀況）
 */
export interface DataQuality {
  /** 採集通道編號 (0: Gateway, 1: DDC1, 2: DDC2) */
  channel: number;
  /** 實體或 TCP 連線是否在線 */
  isConnected: boolean;
  /** 最近一次讀取之狀態代碼 (0: 未讀, 1: 成功, 2: 失敗, 3: 斷線) */
  readStatus: number;
  /** 最後一次讀取成功之 UTC 時間戳記 */
  lastSuccessAtUtc: string | null;
  /** 資料陳舊秒數（離最後成功採集之時間差） */
  staleSeconds: number | null;
}

/**
 * 量測值顯示：沒有值（設備離線、讀取失敗）時顯示 `--`，不要顯示 0——「0.0 °C」看起來像量到 0 度，
 * 現場人員會分不出是真的 0 還是沒讀到。
 * @param value 數值
 * @param digits 保留之小數位數（預設 1 位）
 * @returns 格式化之字串
 */
export function formatMeasure(value: number | null | undefined, digits = 1): string {
  return value === null || value === undefined || !Number.isFinite(value) ? '--' : value.toFixed(digits);
}

/**
 * 判斷這筆 dataQuality 底下的 value 是否為可信資料——離線或讀取失敗時 value 裡的數字都不能信。
 * @param q 資料品質物件
 * @returns 是否在線且讀取成功
 */
export function isDataQualityOnline(q: DataQuality): boolean {
  return q.isConnected && q.readStatus === ReadStatus.Success;
}

/**
 * 冰水主機最新快照量測數值介面
 */
export interface ChillerSnapshotValue {
  /** 快照採集之 UTC 時間戳記 */
  updateTimeUtc: string;
  /** 冰水出水溫度 (°C) */
  chilledWaterOutletTemperature: number;
  /** 冰水回水溫度 (°C) */
  chilledWaterInletTemperature: number;
  /** 冰水溫差 (回水 - 出水, °C) */
  chilledWaterTemperatureDifference: number;
  /** 負載百分比 (%) */
  loadPercentage: number;
  /** 累積運轉時數 (小時) */
  accumulatedRunningHours: number;
  /** 設備本身硬體警報旗標 */
  isAlarm: boolean;
}

/**
 * 冰水主機前端顯示資料列介面
 */
export interface ChillerRow {
  /** 系統內部主鍵 ID */
  id: number;
  /** 設備代碼（例如 CH-01） */
  code: string;
  /** Modbus 站號 ID */
  modbusId: number;
  /** 前台自訂顯示名稱 */
  displayName: string;
  /** 額定冷凍噸位 (RT) */
  ratedCapacityRt: number | null;
  /** 通訊與資料品質狀態 */
  dataQuality: DataQuality;
  /** 即時感測讀值快照 */
  value: ChillerSnapshotValue | null;
}

/**
 * FCU 最新快照感測讀值介面
 */
export interface FcuSnapshotValue {
  /** 設備識別字串 */
  id: string;
  /** Modbus 暫存器位址 */
  address: number;
  /** 開關機狀態 (0: 關, 1: 開) */
  switchStatus: number;
  /** 運轉模式 (1: 冷氣, 2: 暖氣, 3: 送風) */
  mode: number;
  /** 風速 (0: 高, 1: 中, 2: 低, 3: 自動) */
  fanSpeed: number;
  /** 室內環境目前溫度 (°C) */
  temperature: number;
}

/**
 * FCU 前端顯示資料列介面
 */
export interface FcuRow {
  /** 系統內部主鍵 ID */
  id: number;
  /** 通道編號：1＝DDC1（B1F）、2＝DDC2（B2F） */
  channel: number;
  /** DDC 站號 ID */
  stationId: number;
  /** 站點內相對位置序號 */
  position: number;
  /** 4X Holding Register 文件位址（說明書表 18），例如 40051 */
  address: number;
  /** 供應商程式裡的 FCU 編號，例如 FC_MC1_01。DDC1、DDC2 之間會重複，顯示時用 fcuVendorLabel() */
  vendorCode: string;
  /** 所屬樓層代碼 (B1F / B2F) */
  floor: string;
  /** 所屬區域代碼 (Zone Code) */
  zoneCode: string | null;
  /** 前台自訂顯示名稱 */
  displayName: string | null;
  /** 通訊資料品質狀態 */
  dataQuality: DataQuality;
  /** 即時狀態感測快照 */
  value: FcuSnapshotValue | null;
}


/**
 * 警報事件資料列介面
 */
export interface AlarmRow {
  /** 警報主鍵 ID */
  id: number;
  /** 設備類型代碼 (0: Chiller, 1: Fcu, 2: Channel) */
  deviceType: number;
  /** 設備 ID */
  deviceId: number;
  /** 觸發之警報規則代碼 */
  ruleCode: string;
  /** 嚴重程度等級 (1: Critical, 2: Warning, 3: Info) */
  severity: number;
  /** 警報發生之 UTC 時間戳記 */
  startedAt: string;
  /** 警報解除之 UTC 時間戳記（若仍持續中則為 null） */
  endedAt: string | null;
  /** 觸發時之峰值或超出門檻數值 */
  peakValue: number | null;
  /** 確認此警報之使用者 ID（若尚未確認則為 null） */
  ackByUserId: number | null;
  /** 確認之 UTC 時間戳記 */
  ackAt: string | null;
  /** 處理備註內容 */
  memo: string | null;
  /** 設備名稱 */
  deviceName: string;
  /** 設備代碼 */
  deviceCode: string;
  /** 設備所在位置（例如樓層或區域） */
  location: string;
  /** 只有 FCU 有：供應商編號搭配 DDC，例如「DDC1 · FC_MC1_03」；冰水主機為 null */
  vendorLabel: string | null;
  /** 警報規則繁體中文標籤（例如 "出水溫度過高"） */
  ruleLabel: string;
}

/**
 * 保養提醒告警的 ruleCode（後端 ChillerMaintenanceRepository.MaintenanceRuleCode）。
 * 2026-10-01 以前的舊格式帶門檻後綴（AccumulatedRunningHours.0.5000），歷史報表裡仍查得到，一併認得。
 */
const MAINTENANCE_RULE_CODE = 'AccumulatedRunningHours';

/**
 * 檢查該告警項目是否屬於冰水主機之運轉時數保養提醒。
 * @param alarm 包含 ruleCode 之物件
 * @returns 若為保養提醒則為 true；否則為 false
 */
export function isMaintenanceAlarm(alarm: Pick<AlarmRow, 'ruleCode'>): boolean {
  return alarm.ruleCode === MAINTENANCE_RULE_CODE || alarm.ruleCode.startsWith(`${MAINTENANCE_RULE_CODE}.`);
}

/**
 * 判斷警報顯示徽章樣式狀態（'ABNORMAL' 異常、'MAINTENANCE' 待保養、'OFFLINE' 離線）。
 * @param alarm 警報列物件
 * @returns 樣式標籤字串
 */
export function alarmBadgeStatus(alarm: Pick<AlarmRow, 'ruleCode'>): 'ABNORMAL' | 'MAINTENANCE' | 'OFFLINE' {
  if (alarm.ruleCode === 'ChannelOffline') return 'OFFLINE';
  return isMaintenanceAlarm(alarm) ? 'MAINTENANCE' : 'ABNORMAL';
}

/**
 * 格式化告警時間字串，對齊設計稿格式「2026/08/18 10:23」。
 * @param iso ISO 8601 時間字串
 * @returns 格式化之日期時間文字
 */
export function formatAlarmTime(iso: string): string {
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}/${pad(d.getMonth() + 1)}/${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/**
 * 供應商編號加上 DDC，例如「DDC1 · FC_MC1_01」——編號本身在兩台 DDC 之間會重複（說明書 6.1）。
 * @param fcu 包含 channel 與 vendorCode 之 FCU 項目
 * @returns 組合後之完整編號文字
 */
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
 * 計算冰水主機綜合顯示狀態。
 * 異常有兩個來源：主機自己回報的 14 個硬體警報（value.isAlarm），以及後台設定的門檻被超過
 * （對照目前有效告警）。原本只看硬體警報，超過門檻時數字變紅、狀態卻還是「運轉中」，
 * 跟規格書「超標時狀態切換為異常」不符，也跟 FCU 超標就顯示異常的行為不一致。
 *
 * 優先順序：離線 > 異常 > 待保養 > 運轉／停止（docs/IOT_資料對照與缺口清單.md 2.3）。
 * 待保養排在異常後面，是因為異常要立刻處理，保養可以排時間。
 * @param chiller 冰水主機資料列
 * @param activeAlarms 當前未解除的有效告警集合
 * @returns 設備狀態 (OFFLINE / ABNORMAL / MAINTENANCE / RUNNING / STOPPED)
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
 * @param chiller 冰水主機資料列
 * @param activeAlarms 當前未解除的有效告警集合
 * @returns 實質運轉狀態 (OFFLINE / ABNORMAL / RUNNING / STOPPED)
 */
export function chillerOperatingStatus(chiller: ChillerRow, activeAlarms: readonly AlarmRow[]): DeviceStatus {
  const status = deriveChillerStatus(chiller, activeAlarms);
  if (status !== 'MAINTENANCE') return status;
  return (chiller.value?.loadPercentage ?? 0) > 0 ? 'RUNNING' : 'STOPPED';
}

/**
 * 計算 FCU 綜合運轉狀態。
 * FCU 沒有像冰水主機那樣現成的 alarm 旗標，異常與否要對照目前有效告警清單的 deviceId。
 * @param fcu FCU 資料列
 * @param alarmingFcuIds 正在告警中的 FCU ID 集合
 * @returns 設備狀態 (OFFLINE / ABNORMAL / RUNNING / STOPPED)
 */
export function deriveFcuStatus(fcu: FcuRow, alarmingFcuIds: ReadonlySet<number>): DeviceStatus {
  if (!fcu.dataQuality || !isDataQualityOnline(fcu.dataQuality)) return 'OFFLINE';
  if (alarmingFcuIds.has(fcu.id)) return 'ABNORMAL';
  return fcu.value?.switchStatus === FcuSwitchStatus.On ? 'RUNNING' : 'STOPPED';
}

/**
 * 從有效告警清單取出正在告警的 FCU deviceId 集合，供 deriveFcuStatus 使用。
 * @param alarms 告警清單
 * @returns 正在告警的 FCU ID 集合
 */
export function alarmingFcuIdsOf(alarms: readonly AlarmRow[]): Set<number> {
  return new Set(alarms.filter((a) => a.deviceType === AlarmDeviceType.Fcu).map((a) => a.deviceId));
}

/** 取得後台冰水主機即時清單（需 JWT 驗證） */
export function listChillersApi(): Promise<ChillerRow[]> {
  return authorizedJsonApi('/api/v1/chillers');
}

/**
 * 取得後台 FCU 即時清單（可選樓層篩選，需 JWT 驗證）
 * @param floor 樓層字串（例如 '1F', '2F'）
 * @returns FCU 清單 Promise
 */
export function listFcusApi(floor?: string): Promise<FcuRow[]> {
  const query = floor ? `?floor=${encodeURIComponent(floor)}` : '';
  return authorizedJsonApi(`/api/v1/fcus${query}`);
}

/**
 * 場館自己維護的顯示名稱／自訂代碼。系統自己的編號（FCU 的 zoneCode、冰水主機的 code）
 * 是實體接線／Modbus 位址決定的，不開放修改，兩者在畫面上並存顯示。
 * FCU 傳空字串代表清回「未設定」；冰水主機的欄位是 NOT NULL，後端不接受留空。
 * @param id FCU 設備主鍵 ID
 * @param displayName 自訂顯示名稱
 */
export function updateFcuDisplayNameApi(id: number, displayName: string): Promise<void> {
  return authorizedJsonApi(`/api/v1/fcus/${id}`, {
    method: 'PATCH',
    body: JSON.stringify({ displayName }),
  });
}

/**
 * 更新冰水主機前台自訂顯示名稱
 * @param id 冰水主機主鍵 ID
 * @param displayName 自訂顯示名稱
 */
export function updateChillerDisplayNameApi(id: number, displayName: string): Promise<void> {
  return authorizedJsonApi(`/api/v1/chillers/${id}`, {
    method: 'PATCH',
    body: JSON.stringify({ displayName }),
  });
}

/**
 * 冰水主機保養歷史紀錄項目介面
 */
export interface ChillerMaintenanceLog {
  /** 保養日誌主鍵 ID */
  id: number;
  /** 執行保養完成之時間戳記 */
  performedAt: string;
  /** 執行保養人員姓名 */
  performedByName: string | null;
  /** 重設當下的累積運轉時數 */
  hoursAtReset: number;
  /** 距離上一次保養累積運轉之時數差距 */
  hoursSincePrevious: number | null;
  /** 備註說明文字 */
  memo: string | null;
}

/**
 * 冰水主機保養狀態介面（「機車換機油」模式）：
 * hoursSinceService＝目前累積時數－上次保養時的累積時數。
 * baselineHours 為 null 代表還沒開始計算（剛設定保養間隔、主機尚未回報過時數）。
 */
export interface ChillerMaintenanceStatus {
  /** 冰水主機 ID */
  chillerId: number;
  /** 保養間隔時數 (小時) */
  intervalHours: number | null;
  /** 基準累積運轉時數 */
  baselineHours: number | null;
  /** 基準時間戳記 */
  baselineAt: string | null;
  /** 目前即時累積運轉時數 */
  currentHours: number | null;
  /** 本期已運轉時數 */
  hoursSinceService: number | null;
  /** 剩餘可運轉時數 */
  remainingHours: number | null;
  /** 是否已屆期需保養 */
  isDue: boolean;
  /** 歷史保養紀錄清單 */
  history: ChillerMaintenanceLog[];
}

/**
 * 取得指定冰水主機之保養狀態與紀錄
 * @param chillerId 冰水主機 ID
 * @returns 保養狀態物件 Promise
 */
export function getChillerMaintenanceApi(chillerId: number): Promise<ChillerMaintenanceStatus> {
  return authorizedJsonApi(`/api/v1/chillers/${chillerId}/maintenance`);
}

/**
 * 「保養完成」：熄掉保養提醒，並以當下的累積時數重新起算。需要 hvac.thresholds:update 權限。
 * @param chillerId 冰水主機 ID
 * @param memo 可選之保養備註文字
 * @returns 重設時數回傳結果
 */
export function resetChillerMaintenanceApi(chillerId: number, memo?: string): Promise<{ hoursAtReset: number; hoursSincePrevious: number | null }> {
  return authorizedJsonApi(`/api/v1/chillers/${chillerId}/maintenance/reset`, {
    method: 'POST',
    body: JSON.stringify({ memo: memo?.trim() || null }),
  });
}

/**
 * 取得告警清單。
 * status='active'（預設）只回傳目前尚未結束的告警；'all' 回傳含已結束的歷史紀錄。
 * `from`/`to`（ISO 日期字串）給告警歷史報表用——有給的話後端會放寬回傳筆數上限
 * （見 AlarmEndpoints.BuildListAsync），不是每次都只回最近 200 筆。
 * @param status 狀態篩選 ('active' 或 'all')
 * @param from 起始時間 ISO 字串
 * @param to 結束時間 ISO 字串
 * @returns 告警列陣列 Promise
 */
export function listAlarmsApi(status: 'active' | 'all' = 'active', from?: string, to?: string): Promise<AlarmRow[]> {
  const params = new URLSearchParams({ status });
  if (from) params.set('from', from);
  if (to) params.set('to', to);
  return authorizedJsonApi(`/api/v1/alarms?${params.toString()}`);
}


/**
 * 冰水主機歷史時序資料點介面
 */
export interface ChillerHistoryPoint {
  /** 時間戳記 (UTC) */
  ts: string;
  /** 冰水出水溫度 (°C) */
  chilledWaterOut: number | null;
  /** 冰水回水溫度 (°C) */
  chilledWaterIn: number | null;
  /** 冰水溫差 (°C) */
  chilledWaterDelta: number | null;
  /** 輸入功率 (kW) */
  inputPowerKw: number | null;
  /** 累積電量 (kWh) */
  accumulatedKwh: number | null;
  /** 負載百分比 (%) */
  loadPercentage: number | null;
  /** 運轉時數 (小時) */
  runningHours: number | null;
  /** 通訊讀取狀態代碼 */
  readStatus: number;
}

/**
 * 查詢冰水主機歷史時序趨勢資料。
 * interval='1h'（預設，報表用）查 rollup_chiller_1h；'raw' 查節流後落地的原始讀值
 * （見 backend 的 ChillerRepository.GetHistoryAsync）。時間全部是 UTC，畫面顯示前要自己轉。
 * @param code 冰水主機代碼（例如 CH-01）
 * @param fromIso 起始時間 ISO 字串
 * @param toIso 結束時間 ISO 字串
 * @param interval 聚合區間 ('raw' 原始點 或 '1h' 小時平均)
 * @returns 歷史資料點陣列 Promise
 */
export function getChillerHistoryApi(
  code: string, fromIso: string, toIso: string, interval: 'raw' | '1h' = '1h',
): Promise<ChillerHistoryPoint[]> {
  const params = new URLSearchParams({ from: fromIso, to: toIso, interval });
  return authorizedJsonApi(`/api/v1/chillers/${encodeURIComponent(code)}/history?${params.toString()}`);
}

/**
 * FCU 歷史時序資料點介面
 */
export interface FcuHistoryPoint {
  /** 時間戳記 (UTC) */
  ts: string;
  /** 開關機狀態 (0: 關, 1: 開) */
  switchStatus: number | null;
  /** 運轉模式代碼 */
  mode: number | null;
  /** 風速代碼 */
  fanSpeed: number | null;
  /** 環境溫度 (°C) */
  temperature: number | null;
  /** 通訊讀取狀態代碼 */
  readStatus: number;
  /** 該時段累積運轉分鐘數 */
  onMinutes: number | null;
}

/**
 * 查詢 FCU 歷史運轉趨勢資料。
 * interval='1h'（預設，報表用）查 rollup_fcu_1h；'raw' 查原始讀值。
 * @param id FCU 設備主鍵 ID
 * @param fromIso 起始時間 ISO 字串
 * @param toIso 結束時間 ISO 字串
 * @param interval 聚合區間 ('raw' 或 '1h')
 * @returns 歷史資料點陣列 Promise
 */
export function getFcuHistoryApi(
  id: number, fromIso: string, toIso: string, interval: 'raw' | '1h' = '1h',
): Promise<FcuHistoryPoint[]> {
  const params = new URLSearchParams({ from: fromIso, to: toIso, interval });
  return authorizedJsonApi(`/api/v1/fcus/${id}/history?${params.toString()}`);
}

/**
 * FCU 逐時運轉統計資料結構介面
 */
export interface FcuHourlyStats {
  /** 統計日期字串 (YYYY-MM-DD) */
  date: string;
  /** 24 小時時段標籤陣列 */
  hours: string[];
  /** B1F 各時段開機台數統計陣列 */
  b1Count: number[];
  /** B2F 各時段開機台數統計陣列 */
  b2Count: number[];
  /** B1F 各時段平均室溫陣列 */
  b1Temp: (number | null)[];
  /** B2F 各時段平均室溫陣列 */
  b2Temp: (number | null)[];
}

/**
 * 取得指定日期之 24 小時 FCU 運轉與室溫統計。
 * date 省略時預設容器當地日期（TZ=Asia/Taipei）；格式 YYYY-MM-DD。
 * @param date 查詢日期字串 (YYYY-MM-DD)
 * @returns 統計資料物件 Promise
 */
export function getHourlyFcuStatsApi(date?: string): Promise<FcuHourlyStats> {
  const query = date ? `?date=${encodeURIComponent(date)}` : '';
  return authorizedJsonApi(`/api/v1/fcus/hourly-stats${query}`);
}

// ── 公開唯讀版本：給前台戰情室（`/`）用，打 /api/v1/public/*，不需要登入 ──
// 理由見 auth-service.ts 的 publicJsonApi 說明；資料形狀跟上面三支完全一樣，只是不掛 JWT。

/** 前台戰情室公開取得冰水主機即時清單（無需 JWT） */
export function listPublicChillersApi(): Promise<ChillerRow[]> {
  return publicJsonApi('/api/v1/public/chillers');
}

/**
 * 前台戰情室公開取得 FCU 即時清單（可選樓層篩選，無需 JWT）
 * @param floor 樓層字串
 * @returns FCU 清單 Promise
 */
export function listPublicFcusApi(floor?: string): Promise<FcuRow[]> {
  const query = floor ? `?floor=${encodeURIComponent(floor)}` : '';
  return publicJsonApi(`/api/v1/public/fcus${query}`);
}

/**
 * 前台戰情室公開取得即時告警清單（無需 JWT）
 * @param status 狀態篩選 ('active' 或 'all')
 * @returns 告警列陣列 Promise
 */
export function listPublicAlarmsApi(status: 'active' | 'all' = 'active'): Promise<AlarmRow[]> {
  return publicJsonApi(`/api/v1/public/alarms?status=${status}`);
}

/**
 * 前台戰情室公開取得逐時 FCU 統計（無需 JWT）
 * @param date 查詢日期字串 (YYYY-MM-DD)
 * @returns 統計資料物件 Promise
 */
export function getPublicHourlyFcuStatsApi(date?: string): Promise<FcuHourlyStats> {
  const query = date ? `?date=${encodeURIComponent(date)}` : '';
  return publicJsonApi(`/api/v1/public/fcus/hourly-stats${query}`);
}

