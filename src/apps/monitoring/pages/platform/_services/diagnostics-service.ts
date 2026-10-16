/**
 * @file diagnostics-service.ts
 * 平台「系統診斷」（/platform/diagnostics）：現場 IoT 接通驗證用。
 * 打 backend 的 /api/v1/platform/diagnostics（DiagnosticsEndpoints.cs），需要 platform scope
 * 且持有 platform.diagnostics:read。後端已經把每一項判斷成 ok/warn/error/unknown，
 * 前端只負責顯示，不要在這裡重做一套判斷邏輯（合理範圍集中在 DataValidationRules.cs）。
 */

import { authorizedJsonApi } from '../../admin/_services/auth-service';

/** 診斷健康等級評級（正常、警告、錯誤、未知） */
export type DiagnosticLevel = 'ok' | 'warn' | 'error' | 'unknown';

/**
 * 採集器主機健康狀態
 */
export interface CollectorStatus {
  /** 診斷評級 */
  level: DiagnosticLevel;
  /** 採集器主機是否可連通 */
  reachable: boolean;
  /** 採集程式狀態字串 */
  status: string | null;
  /** 詳細診斷訊息 */
  detail: string;
}

/**
 * 數據接收管線狀態
 */
export interface IngestStatus {
  /** 診斷評級 */
  level: DiagnosticLevel;
  /** 最後一次接收數據的時間（UTC ISO 字串） */
  lastIngestAtUtc: string | null;
  /** 距離最後一次接收數據經過的秒數 */
  secondsSinceLastIngest: number | null;
  /** 自 API 啟動以來的累積接收次數 */
  ingestCountSinceApiStart: number;
  /** 供應商程式建立事件的時間（Collector 主機時鐘），不是設備量測時間。 */
  eventTimeUtc: string | null;
  /** 採集器接收到底層設備原始數據的時間（UTC ISO 字串） */
  collectorReceivedAtUtc: string | null;
}

/**
 * 通訊通道連線健康狀態
 */
export interface ChannelStatus {
  /** 通道編號（1=DDC-1, 2=DDC-2, 3=Chiller） */
  channel: number;
  /** 通道顯示名稱 */
  name: string;
  /** 診斷評級 */
  level: DiagnosticLevel;
  /** 目標設備 IP 位址 */
  ip: string | null;
  /** 目標設備 Port 埠號 */
  port: string | null;
  /** 底層連線狀態代碼 */
  connectionState: number | null;
  /** 是否已建立連線 */
  isConnected: boolean;
  /** 數據讀取狀態代碼（0=未讀取, 1=成功, 2=失敗, 3=未連線） */
  readStatus: number | null;
  /** 最後一次成功讀取數據的時間（UTC ISO 字串） */
  lastSuccessAtUtc: string | null;
  /** 數據陳舊（未更新）秒數 */
  staleSeconds: number | null;
  /** 最後一次連線狀態變更時間（UTC ISO 字串） */
  lastConnectionChangeAtUtc: string | null;
}

/**
 * 單項診斷規則檢查結果
 */
export interface DiagnosticCheck {
  /** 檢查項識別碼 */
  key: string;
  /** 檢查分類（Collector, Ingest, Channel, Chiller, Fcu, Persistence 等） */
  category: string;
  /** 檢查項目顯示名稱 */
  label: string;
  /** 檢查結果評級 */
  level: DiagnosticLevel;
  /** 詳細結果說明或異常原因 */
  detail: string;
}

/**
 * FCU 通道點位涵蓋率與異常診斷資訊
 */
export interface FcuCoverage {
  /** 通道編號（1 或 2） */
  channel: number;
  /** 通道名稱 */
  name: string;
  /** 診斷評級 */
  level: DiagnosticLevel;
  /** 採集收到的 FCU 總筆數 */
  received: number;
  /** 成功對應至資料庫實體編號的筆數 */
  mapped: number;
  /** 成功比對並更新狀態的筆數 */
  matched: number;
  /** 逾時無回應或未連線的筆數 */
  unresponsive: number;
  /** 量測室溫超出合理物理範圍的筆數 */
  temperatureOutOfRange: number;
  /** 未被資料庫定義之未知點位鍵值陣列 */
  unmappedKeys: string[];
  /** 資料庫有定義但未收到採集數據之遺失點位鍵值陣列 */
  missingKeys: string[];
}

/**
 * 冰水主機單一量測欄位診斷數值
 */
export interface ChillerFieldValue {
  /** 欄位識別鍵（例如 supplyTemp） */
  key: string;
  /** 欄位顯示名稱 */
  label: string;
  /** 量測單位（例如 ℃、A、kW 等） */
  unit: string;
  /** 當前量測數值 */
  value: number;
  /** 前一次量測數值（用於判定變化） */
  previousValue: number | null;
  /** 合理物理最小值 */
  min: number | null;
  /** 合理物理最大值 */
  max: number | null;
  /** 該數值診斷評級 */
  level: DiagnosticLevel;
  /** 數值備註或異常說明 */
  note: string | null;
}

/**
 * 單台冰水主機欄位診斷報告
 */
export interface ChillerFieldReport {
  /** Modbus 站號 ID */
  modbusId: number;
  /** 設備實體代碼（如 CH-1） */
  code: string | null;
  /** 設備顯示名稱 */
  name: string | null;
  /** 診斷評級 */
  level: DiagnosticLevel;
  /** Modbus 讀取狀態代碼 */
  readStatus: number;
  /** 是否連線成功 */
  isConnected: boolean;
  /** 各項實體量測欄位數值清單 */
  fields: ChillerFieldValue[];
}

/**
 * 資料庫資料持久化與排程聚合健康狀態
 */
export interface PersistenceStatus {
  /** 診斷評級 */
  level: DiagnosticLevel;
  /** 檢查的最近時間視窗（分鐘） */
  recentWindowMinutes: number;
  /** 冰水主機最近一次寫入時序表的時間（UTC ISO 字串） */
  chillerLatestTsUtc: string | null;
  /** 最近時間視窗內冰水主機寫入列數 */
  chillerRecentRows: number;
  /** 最近時間視窗內有寫入的冰水主機台數 */
  chillerRecentDevices: number;
  /** 系統註冊的冰水主機總台數 */
  chillerActiveDevices: number;
  /** FCU 最近一次寫入時序表的時間（UTC ISO 字串） */
  fcuLatestTsUtc: string | null;
  /** 最近時間視窗內 FCU 寫入列數 */
  fcuRecentRows: number;
  /** 最近時間視窗內有寫入的 FCU 台數 */
  fcuRecentDevices: number;
  /** 系統註冊的 FCU 總台數 */
  fcuActiveDevices: number;
  /** 冰水主機 1 小時聚合最新時間桶（UTC ISO 字串） */
  rollupChillerLatestBucketUtc: string | null;
  /** FCU 1 小時聚合最新時間桶（UTC ISO 字串） */
  rollupFcuLatestBucketUtc: string | null;
  /** 錯誤訊息（無錯誤為 null） */
  error: string | null;
}

/**
 * 背景排程工作執行紀錄
 */
export interface JobRun {
  /** 工作開始執行時間（UTC ISO 字串） */
  startedAtUtc: string;
  /** 工作結束執行時間（UTC ISO 字串） */
  finishedAtUtc: string | null;
  /** 是否執行成功 */
  succeeded: boolean | null;
  /** 執行成果摘要說明 */
  summary: string | null;
  /** 執行失敗錯誤訊息 */
  error: string | null;
}

/**
 * 排程工作健康診斷報告
 */
export interface JobReport {
  /** 工作識別鍵（如 rollup、partition） */
  key: string;
  /** 工作名稱 */
  name: string;
  /** 診斷評級 */
  level: DiagnosticLevel;
  /** 排程週期秒數 */
  intervalSeconds: number;
  /** 最近一次執行紀錄 */
  lastRun: JobRun | null;
  /** 預計下一次執行時間（UTC ISO 字串） */
  nextRunAtUtc: string | null;
  /** 佐證指標標籤 */
  evidenceLabel: string | null;
  /** 佐證指標更新時間（UTC ISO 字串） */
  evidenceUtc: string | null;
  /** 近期歷史執行紀錄 */
  history: JobRun[];
}

/**
 * 通道連線歷史異動歷程項目
 */
export interface ConnectionHistoryItem {
  /** 通道編號 */
  channel: number;
  /** 通道名稱 */
  name: string;
  /** 連線狀態代碼 */
  connectionState: number;
  /** 讀取狀態代碼 */
  readStatus: number;
  /** 狀態變更時間（UTC ISO 字串） */
  changedAtUtc: string;
  /** 這段狀態持續的秒數，下一段開始時才回填；null＝仍在持續中，或回填機制上線（2026-10-15）前的舊資料。 */
  durationSeconds: number | null;
}

/**
 * 完整系統診斷報告總成
 */
export interface DiagnosticsReport {
  /** 診斷報告產生時間（UTC ISO 字串） */
  generatedAtUtc: string;
  /** 系統整體綜合健康評級 */
  overall: DiagnosticLevel;
  /** 採集器狀態 */
  collector: CollectorStatus;
  /** 數據接收管線狀態 */
  ingest: IngestStatus;
  /** 各通訊通道健康狀態清單 */
  channels: ChannelStatus[];
  /** 各項規則診斷檢查清單 */
  checks: DiagnosticCheck[];
  /** FCU 涵蓋率診斷資訊 */
  fcuCoverage: FcuCoverage[];
  /** 冰水主機詳細欄位診斷清單 */
  chillers: ChillerFieldReport[];
  /** 資料庫時序持久化與聚合健康狀態 */
  persistence: PersistenceStatus | null;
  /** 背景排程工作狀態清單 */
  jobs: JobReport[];
  /** 通道連線歷史歷程清單 */
  connectionHistory: ConnectionHistoryItem[];
}

/**
 * 底層原始 FCU 量測點位資料結構
 */
export interface RawFcu {
  /** 通道編號 */
  channel: number;
  /** 站號 ID */
  stationId: number;
  /** 點位位置流水號 */
  position: number;
  /** FCU 代碼識別字串 */
  id: string;
  /** Modbus 暫存器位址 */
  address: number;
  /** 開關狀態代碼（0=關, 1=開） */
  switchStatus: number;
  /** 運轉模式代碼（1=冷氣, 2=暖氣, 3=送風） */
  mode: number;
  /** 風速代碼（0=高, 1=中, 2=低, 3=自動） */
  fanSpeed: number;
  /** 量測室內溫度（℃） */
  temperature: number;
}

/**
 * 底層原始 DDC 通道數據結構
 */
export interface RawDdc {
  /** 通道編號 */
  channel: number;
  /** 數據更新時間（UTC ISO 字串） */
  updateTimeUtc: string;
  /** 讀取狀態代碼 */
  readStatus: number;
  /** 是否成功連線 */
  isConnected: boolean;
  /** 該通道包含的所有原始 FCU 點位清單 */
  fcuList: RawFcu[];
}

/**
 * 採集器最新底層原始快照結構
 */
export interface RawSnapshot {
  /** 快照數據更新時間（UTC ISO 字串） */
  updateTimeUtc: string;
  /** API 接收到快照的時間（UTC ISO 字串） */
  receivedAtUtc: string;
  /** DDC-1 通道原始數據 */
  ddc1: RawDdc;
  /** DDC-2 通道原始數據 */
  ddc2: RawDdc;
}

/**
 * 取得完整系統健康診斷報告
 *
 * @returns 傳回診斷報告資料結構
 */
export function getDiagnosticsApi(): Promise<DiagnosticsReport> {
  return authorizedJsonApi('/api/v1/platform/diagnostics');
}

/**
 * 取得採集器最新底層原始快照
 *
 * 還沒收到任何資料時後端回 `{ message }`，這裡統一轉成 null。
 *
 * @returns 傳回原始快照物件；若尚未接收到數據則傳回 null
 */
export async function getRawSnapshotApi(): Promise<RawSnapshot | null> {
  const body = await authorizedJsonApi<RawSnapshot | { message: string }>('/api/v1/platform/diagnostics/raw');
  return 'ddc1' in body ? body : null;
}

/** 連線狀態代碼對應中文標籤（對應後端 Teco.Hvac.Contracts.Enums.ConnectionState） */
export const CONNECTION_STATE_LABEL: Record<number, string> = {
  [-1]: '未知', 0: '已停止', 1: '連線中', 2: '已連線', 3: '連線失敗', 4: '已斷線',
};

/** 數據讀取狀態代碼對應中文標籤（對應後端 Teco.Hvac.Contracts.Enums.ReadStatus） */
export const READ_STATUS_LABEL: Record<number, string> = {
  0: '尚未讀取', 1: '讀取成功', 2: '讀取失敗', 3: '未連線',
};

/** FCU 開關狀態代碼對應標籤 */
export const FCU_SWITCH_LABEL: Record<number, string> = { [-1]: '未知', 0: '關', 1: '開' };

/** FCU 模式代碼對應標籤 */
export const FCU_MODE_LABEL: Record<number, string> = { [-1]: '未知', 1: '冷氣', 2: '暖氣', 3: '送風' };

/** FCU 風速代碼對應標籤 */
export const FCU_FAN_LABEL: Record<number, string> = { [-1]: '未知', 0: '高', 1: '中', 2: '低', 3: '自動' };

/**
 * 格式化 ISO 日期時間字串為台灣在地化格式
 *
 * @param value ISO 日期時間字串、null 或 undefined
 * @returns 傳回格式化之日期時間字串（24 小時制），若無值則傳回 '—'
 */
export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '—';
  return new Date(value).toLocaleString('zh-TW', { hour12: false });
}

/**
 * 將秒數換算為容易閱讀的持續時間字串（如「X 小時 Y 分」）
 *
 * @param seconds 持續秒數
 * @returns 傳回中文持續時間字串
 */
export function formatDuration(seconds: number): string {
  if (seconds < 60) return `${seconds} 秒`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)} 分鐘`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} 小時 ${Math.floor((seconds % 3600) / 60)} 分`;
  return `${Math.floor(seconds / 86400)} 天 ${Math.floor((seconds % 86400) / 3600)} 小時`;
}

/**
 * 計算指定時間與當前基準時間相差多久並格式化為相對時間字串（如「X 分鐘前」）
 *
 * @param value 過去的 ISO 日期時間字串
 * @param nowMs 當前基準時間（毫秒時間戳記）
 * @returns 傳回「X 秒前 / X 分鐘前 / X 小時前 / X 天前」字串，若無值傳回 '—'
 */
export function formatAgo(value: string | null | undefined, nowMs: number): string {
  if (!value) return '—';
  const seconds = Math.max(0, Math.round((nowMs - new Date(value).getTime()) / 1000));
  if (seconds < 60) return `${seconds} 秒前`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)} 分鐘前`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} 小時前`;
  return `${Math.floor(seconds / 86400)} 天前`;
}
