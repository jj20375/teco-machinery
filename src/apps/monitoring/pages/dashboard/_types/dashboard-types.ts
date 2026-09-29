/**
 * @file dashboard-types.ts
 * 東元電機智慧環境監控 - 前台戰情室型別定義
 */

import type { StatusType } from '@/ui/components/UiBadge.vue';

export type FloorId = 'B1' | 'B2';

/**
 * 冰水主機資料模型。沒有 flowRate（水流量）——供應商 SDK 完全沒有這個量測值，
 * Figma 設計稿上的數字是虛構的；四個 isXxxExceeded 門檻旗標目前也還沒有場館可設定的告警門檻
 * （alarm_rule 只有 FCU 溫度規則），沒有真門檻可比對就一律回 false，不假裝有超標。
 */
export interface ChillerData {
  id: string;
  name: string;
  code: string;
  status: StatusType;
  loadRate: number;
  supplyTemp: number;
  returnTemp: number;
  tempDiff: number;
  cumulativeHours: number;
  isSupplyTempExceeded: boolean;
  isReturnTempExceeded: boolean;
  isTempDiffExceeded: boolean;
}

/** 每小時 FCU 啟用長條圖資料 */
export interface HourlyUsageTrend {
  hours: string[];
  b1Count: number[];
  b2Count: number[];
}

/**
 * 每小時溫度變化折線圖資料。溫度陣列允許 null——那個小時完全沒有成功讀值時回 null
 * 讓折線圖畫成斷點，不要用 0°C 冒充「量到 0 度」（跟這個專案其他地方的「沒有真資料就不
 * 假裝有」原則一致）。
 */
export interface HourlyTempTrend {
  hours: string[];
  b1Temp: (number | null)[];
  b2Temp: (number | null)[];
}

/** 即時告警項目 */
export interface AlarmItem {
  id: string;
  time: string;
  deviceName: string;
  deviceCode: string;
  location: string;
  floor: FloorId;
  statusText: string;
  triggerValue: string;
  thresholdValue: string;
  isCritical: boolean;
}

/**
 * 樓層摘要。原本還有 avgTemp/sensors 兩個欄位，但熱區圖實際上是用 FloorPlanViewer
 * （接的是 /floor-plan/{floor}/placements 真實配置）畫的，這兩個欄位從沒被畫面用過，故拿掉。
 */
export interface FloorHeatmapData {
  floorId: FloorId;
  fcuTotal: number;
  fcuRunning: number;
  runRate: number;
}

/** 單台 FCU 資料 */
export interface FcuItem {
  id: string;
  code: string;
  floor: FloorId;
  roomTemp: number;
  setTemp: number;
  tempDiff: number;
  /** 共用 fcuModeLabel()：冷氣／暖氣／送風，離線或未知時為 --。 */
  mode: string;
  /** 共用 fcuFanSpeedLabel()：高／中／低／自動，離線或未知時為 --。 */
  fanSpeed: string;
  status: StatusType;
  isExceeded: boolean;
}

/** 戰情看板總體指標 */
export interface DashboardOverview {
  totalChillers: number;
  runningChillers: number;
  totalFcu: number;
  runningFcu: number;
  abnormalFcu: number;
  offlineFcu: number;
  stoppedFcu: number;
  overallRunRate: number;
  b1AvgTemp: number;
  b2AvgTemp: number;
}
