/**
 * @file admin-types.ts
 * 東元電機智慧環境監控 - 後台管理系統型別定義
 */

import type { StatusType } from '@/ui/components/UiBadge.vue';
import type { FloorId } from '../../dashboard/_types/dashboard-types';

/**
 * 冰水主機運轉日/週報表資料列
 * 沒有水流量欄位：供應商 SDK 沒有這個量測值，畫面也不顯示。
 */
export interface ChillerReportRow {
  /** 時間戳記（例如 YYYY-MM-DD HH:00 或 ISO 字串） */
  timestamp: string;
  /** 運轉狀態標籤（運轉中、停止、離線、告警中） */
  status: StatusType;
  /** 冰水出水溫度（℃） */
  supplyTemp: number;
  /** 冰水回水溫度（℃） */
  returnTemp: number;
  /** 冰水出回水溫差（℃） */
  tempDiff: number;
  /** 該小時平均負載率（rollup_chiller_1h.avg_load_pct） */
  loadRate: number | null;
  /** 累積運轉時數（小時） */
  cumulativeHours: number | null;
  /** 該時間點/區間是否曾發生出水溫度超標 */
  isSupplyTempExceeded: boolean;
  /** 該時間點/區間是否曾發生回水溫度超標 */
  isReturnTempExceeded: boolean;
  /** 該時間點/區間是否曾發生溫差超標 */
  isTempDiffExceeded: boolean;
}

/**
 * FCU 運轉日/週報表資料列
 *
 * 沒有 setTemp/tempDiff：供應商 SDK 沒有 FCU 設定溫度，做不出「溫差」，畫面也不顯示；
 * 告警改用「室內溫度上下限」的絕對溫度模型（見 threshold-service.ts 的說明）。
 * mode/fanSpeed 在每小時聚合（rollup_fcu_1h）下沒有明確的聚合意義，固定是 null，
 * 顯示 `--`，不是漏資料。
 */
export interface FcuReportRow {
  /** 時間戳記（例如 YYYY-MM-DD HH:00 或 ISO 字串） */
  timestamp: string;
  /** 所在樓層代碼（如 1F, 2F, 3F 等） */
  floor: FloorId;
  /** 設備顯示名稱 */
  deviceName: string;
  /** 設備實體編號（如 FCU-1-01） */
  deviceCode: string;
  /** 安裝位置描述 */
  location: string;
  /** 室內環境溫度（℃） */
  roomTemp: number | null;
  /** 風速設定（每小時聚合下為 null） */
  fanSpeed: string | null;
  /** 運轉模式（每小時聚合下為 null） */
  mode: string | null;
  /** 設備狀態標籤 */
  status: StatusType;
  /** 該時間點/區間是否曾發生室溫超標告警 */
  isExceeded: boolean;
}

/**
 * 系統操作紀錄日誌。actionType 是後端算好的分類標籤（字串，不是窄 union）——
 * 原本的設計稿類別包含「開啟/關閉冰水主機」「修改FCU溫差」，但供應商的 Collector SDK
 * 完全沒有寫入/控制能力，這幾種操作在目前的硬體條件下永遠不會發生，故不再寫死這個列舉，
 * 交由後端依實際存在的操作決定分類（見 backend MerchantEndpoints.ActionCategoryLabels）。
 */
export interface OperationLogRow {
  /** 操作紀錄流水號 ID */
  id: number;
  /** 操作發生時間（ISO 字串） */
  timestamp: string;
  /** 操作者帳號名稱 */
  username: string;
  /** 操作者姓名 */
  userFullName: string;
  /** 操作類型分類標籤 */
  actionType: string;
  /** 詳細操作內容敘述 */
  content: string;
  /** 操作者來源 IP 位址 */
  ipAddress: string | null;
  /** 操作是否執行成功 */
  isSuccess: boolean;
}
