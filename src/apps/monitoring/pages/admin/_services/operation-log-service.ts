/**
 * @file operation-log-service.ts
 * 東元電機智慧環境監控 - 操作紀錄查詢服務
 * 嚴格遵守 Metat 規範：所有對外呼叫函式一律以 *Api 結尾
 *
 * 打的是 backend/ 的 /api/v1/merchant/operation-logs（見
 * backend/src/Teco.Hvac.Api/Endpoints/MerchantEndpoints.cs 的 ListOperationLogs）。
 */

import { authorizedJsonApi } from './auth-service';
import type { OperationLogRow } from '../_types/admin-types';

/**
 * 查詢區間為「日期」（YYYY-MM-DD），這裡補成整天的起訖時間再送出，
 * 否則 dateTo 當天發生的操作會因為只送到 00:00:00 而被排除在外。
 */
export function listOperationLogsApi(dateFrom: string, dateTo: string): Promise<OperationLogRow[]> {
  const params = new URLSearchParams({
    from: `${dateFrom}T00:00:00`,
    to: `${dateTo}T23:59:59`,
  });
  return authorizedJsonApi(`/api/v1/merchant/operation-logs?${params.toString()}`);
}
