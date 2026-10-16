/**
 * @file dashboard-types.ts
 * 東元電機智慧環境監控 - 前台戰情室型別定義
 */

import type { StatusType } from '@/ui/components/UiBadge.vue';

/** 樓層識別碼（B1 或 B2） */
export type FloorId = 'B1' | 'B2';

/**
 * 冰水主機資料模型。沒有 flowRate（水流量）：供應商 SDK 沒有這個量測值，畫面也不顯示。
 * 三個 isXxxExceeded 旗標從目前有效告警反查（見 threshold-service.ts 的 chillerExceededFlags）。
 */
export interface ChillerData {
  /** 設備識別字串 ID */
  id: string;
  /** 設備顯示名稱 */
  name: string;
  /** 設備實體代碼（如 CH-1） */
  code: string;
  /** 運轉狀態標籤（運轉中、停止、離線、告警中） */
  status: StatusType;
  /** 設備負載率（%，設備離線或讀取失敗時為 null，畫面顯示 --） */
  loadRate: number | null;
  /** 冰水出水溫度（℃，設備離線或讀取失敗時為 null） */
  supplyTemp: number | null;
  /** 冰水回水溫度（℃，設備離線或讀取失敗時為 null） */
  returnTemp: number | null;
  /** 出回水溫差（℃，設備離線或讀取失敗時為 null） */
  tempDiff: number | null;
  /** 累積運轉時數（小時，設備離線或讀取失敗時為 null） */
  cumulativeHours: number | null;
  /** 是否發生出水溫度超標告警 */
  isSupplyTempExceeded: boolean;
  /** 是否發生回水溫度超標告警 */
  isReturnTempExceeded: boolean;
  /** 是否發生溫差超標告警 */
  isTempDiffExceeded: boolean;
}

/** 每小時 FCU 啟用長條圖資料 */
export interface HourlyUsageTrend {
  /** 24 小時時間軸標籤陣列（如 "00:00", "01:00", ...） */
  hours: string[];
  /** B1 各小時運轉台數陣列 */
  b1Count: number[];
  /** B2 各小時運轉台數陣列 */
  b2Count: number[];
}

/**
 * 每小時溫度變化折線圖資料。溫度陣列允許 null——那個小時完全沒有成功讀值時回 null
 * 讓折線圖畫成斷點，不要用 0°C 冒充「量到 0 度」（跟這個專案其他地方的「沒有真資料就不
 * 假裝有」原則一致）。
 */
export interface HourlyTempTrend {
  /** 24 小時時間軸標籤陣列 */
  hours: string[];
  /** B1 各小時平均室溫陣列（null 代表該小時無讀值斷點） */
  b1Temp: (number | null)[];
  /** B2 各小時平均室溫陣列（null 代表該小時無讀值斷點） */
  b2Temp: (number | null)[];
}

/** 即時告警項目 */
export interface AlarmItem {
  /** 告警唯一識別字串（alarm_event 流水號轉字串） */
  id: string;
  /** 告警發生時間字串（HH:mm:ss 或 YYYY-MM-DD HH:mm） */
  time: string;
  /** 設備名稱 */
  deviceName: string;
  /** 設備編號代碼 */
  deviceCode: string;
  /** 設備安裝位置 */
  location: string;
  /** 所在樓層代碼（B1 或 B2） */
  floor: FloorId;
  /** 告警狀態中文敘述（如「溫度過高」） */
  statusText: string;
  /** 觸發當下的量測值字串（如 "12.5 ℃"） */
  triggerValue: string;
  /** 設定的告警門檻值字串（如 "> 10.0 ℃"） */
  thresholdValue: string;
  /** 是否為重大嚴重告警 */
  isCritical: boolean;
}

/**
 * 樓層摘要。原本還有 avgTemp/sensors 兩個欄位，但熱區圖實際上是用 FloorPlanViewer
 * （接的是 /floor-plan/{floor}/placements 真實配置）畫的，這兩個欄位從沒被畫面用過，故拿掉。
 */
export interface FloorHeatmapData {
  /** 樓層代碼（B1 或 B2） */
  floorId: FloorId;
  /** 該樓層 FCU 總台數 */
  fcuTotal: number;
  /** 該樓層目前運轉中台數 */
  fcuRunning: number;
  /** 該樓層運轉率百分比（0 ~ 100） */
  runRate: number;
}

/** 單台 FCU 資料。沒有設定溫度與溫度差：供應商 SDK 沒有 FCU 設定溫度。 */
export interface FcuItem {
  /** 設備唯一識別代碼 */
  id: string;
  /** 設備實體代碼（如 FCU-1-01） */
  code: string;
  /** 所在樓層代碼（B1 或 B2） */
  floor: FloorId;
  /** 量測室內溫度（℃） */
  roomTemp: number;
  /** 共用 fcuModeLabel()：冷氣／暖氣／送風，離線或未知時為 --。 */
  mode: string;
  /** 共用 fcuFanSpeedLabel()：高／中／低／自動，離線或未知時為 --。 */
  fanSpeed: string;
  /** 設備運轉狀態標籤 */
  status: StatusType;
  /** 是否處於溫度超標告警狀態 */
  isExceeded: boolean;
}

/** 戰情看板總體指標 */
export interface DashboardOverview {
  /** 冰水主機總台數 */
  totalChillers: number;
  /** 運轉中冰水主機台數 */
  runningChillers: number;
  /** FCU 總台數 */
  totalFcu: number;
  /** 運轉中 FCU 台數 */
  runningFcu: number;
  /** 告警異常中 FCU 台數 */
  abnormalFcu: number;
  /** 離線 FCU 台數 */
  offlineFcu: number;
  /** 停止運轉 FCU 台數 */
  stoppedFcu: number;
  /** 全廠整體運轉率百分比（0 ~ 100） */
  overallRunRate: number;
  /** B1 樓層平均室溫（℃） */
  b1AvgTemp: number;
  /** B2 樓層平均室溫（℃） */
  b2AvgTemp: number;
}
