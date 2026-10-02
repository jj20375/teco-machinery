/**
 * @file admin-types.ts
 * 東元電機智慧環境監控 - 後台管理系統型別定義
 */

import type { StatusType } from '@/ui/components/UiBadge.vue';
import type { FloorId } from '../../dashboard/_types/dashboard-types';

/** 冰水主機運轉日/週報表資料列 */
/** 沒有水流量欄位：供應商 SDK 沒有這個量測值，畫面也不顯示。 */
export interface ChillerReportRow {
  timestamp: string;
  status: StatusType;
  supplyTemp: number;
  returnTemp: number;
  tempDiff: number;
  /** 該小時平均負載率（rollup_chiller_1h.avg_load_pct）。 */
  loadRate: number | null;
  cumulativeHours: number | null;
  isSupplyTempExceeded: boolean;
  isReturnTempExceeded: boolean;
  isTempDiffExceeded: boolean;
}

/** FCU 運轉日/週報表資料列 */
/**
 * 沒有 setTemp/tempDiff：供應商 SDK 沒有 FCU 設定溫度，做不出「溫差」，畫面也不顯示；
 * 告警改用「室內溫度上下限」的絕對溫度模型（見 threshold-service.ts 的說明）。
 * mode/fanSpeed 在每小時聚合（rollup_fcu_1h）下沒有明確的聚合意義，固定是 null，
 * 顯示 `--`，不是漏資料。
 */
export interface FcuReportRow {
  timestamp: string;
  floor: FloorId;
  deviceName: string;
  deviceCode: string;
  location: string;
  roomTemp: number | null;
  fanSpeed: string | null;
  mode: string | null;
  status: StatusType;
  isExceeded: boolean;
}

/**
 * 系統操作紀錄日誌。actionType 是後端算好的分類標籤（字串，不是窄 union）——
 * 原本的設計稿類別包含「開啟/關閉冰水主機」「修改FCU溫差」，但供應商的 Collector SDK
 * 完全沒有寫入/控制能力，這幾種操作在目前的硬體條件下永遠不會發生，故不再寫死這個列舉，
 * 交由後端依實際存在的操作決定分類（見 backend MerchantEndpoints.ActionCategoryLabels）。
 */
export interface OperationLogRow {
  id: number;
  timestamp: string;
  username: string;
  userFullName: string;
  actionType: string;
  content: string;
  ipAddress: string | null;
  isSuccess: boolean;
}
