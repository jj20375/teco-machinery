/**
 * @file threshold-service.ts
 * 東元電機智慧環境監控 - 告警門檻設定服務
 * 嚴格遵守專案規範：所有對外呼叫函式一律以 *Api 結尾
 *
 * 打的是 backend/ 的 /api/v1/thresholds/*（見
 * backend/src/Teco.Hvac.Api/Endpoints/ThresholdEndpoints.cs）。
 *
 * 沒有水流量門檻：供應商 SDK 沒有水流量量測值。FCU 是「室溫上下限」不是「溫差」：溫差＝室溫－設定溫度，
 * 但 Collector 沒有 FCU 設定溫度，這點在最早的規劃就決定用絕對室溫上下限
 * （docs/BACKEND_INTEGRATION_PLAN.md §2.1）。
 */

import { authorizedJsonApi } from './auth-service';
import { AlarmDeviceType, type AlarmRow } from './hvac-service';

/**
 * 冰水主機門檻設定資料結構
 */
export interface ChillerThresholdConfig {
  /** 冰水主機代碼（例如 CH-1） */
  chillerCode: string;
  /** 冰水出水溫度下限（℃，null 表示不設定門檻） */
  supplyTempMin: number | null;
  /** 冰水出水溫度上限（℃，null 表示不設定門檻） */
  supplyTempMax: number | null;
  /** 冰水回水溫度下限（℃，null 表示不設定門檻） */
  returnTempMin: number | null;
  /** 冰水回水溫度上限（℃，null 表示不設定門檻） */
  returnTempMax: number | null;
  /** 冰水溫差下限（℃，出回水絕對溫差，null 表示不設定門檻） */
  tempDiffMin: number | null;
  /** 冰水溫差上限（℃，出回水絕對溫差，null 表示不設定門檻） */
  tempDiffMax: number | null;
  /** 保養運轉時數上限（小時，null 表示不設定保養時數門檻） */
  maintenanceHoursLimit: number | null;
}

/**
 * FCU 冰水送風機門檻設定資料結構（全廠共用統一門檻）
 */
export interface FcuThresholdConfig {
  /** 室內環境溫度下限（℃，null 表示不設定門檻） */
  roomTempMin: number | null;
  /** 室內環境溫度上限（℃，null 表示不設定門檻） */
  roomTempMax: number | null;
}

/**
 * 取得指定冰水主機的告警門檻設定
 *
 * @param code 冰水主機設備代碼（例如 CH-1）
 * @returns 傳回該冰水主機的門檻設定物件
 */
export function getChillerThresholdApi(code: string): Promise<ChillerThresholdConfig> {
  return authorizedJsonApi(`/api/v1/thresholds/chillers/${encodeURIComponent(code)}`);
}

/**
 * 清理表單中的空值與無效數字，將其正規化為 null
 *
 * `v-model.number` 清空數字輸入框時，Vue 給的是空字串 `''`，不是 `null`——`parseFloat('')`
 * 是 `NaN`，Vue 的 `looseToNumber` 遇到 NaN 會原樣傳回輸入字串，不會幫忙轉成 null。這裡送出前
 * 統一清成 null，不然空字串序列化成 JSON 字串會讓後端的 `double?` 反序列化直接 400，
 * 「留空代表不設定該門檻」這個畫面上寫的行為就會失效。
 *
 * @template T 物件型別
 * @param config 包含輸入欄位的原始設定物件
 * @returns 清理完成、空字串與 NaN 皆被置換為 null 的設定物件
 */
function nullifyEmpty<T extends object>(config: T): T {
  const cleaned = { ...config } as Record<string, unknown>;
  for (const key of Object.keys(cleaned)) {
    const value = cleaned[key];
    if (value === '' || (typeof value === 'number' && Number.isNaN(value))) {
      cleaned[key] = null;
    }
  }
  return cleaned as T;
}

/**
 * 儲存指定冰水主機的告警門檻設定
 *
 * @param code 冰水主機設備代碼
 * @param config 門檻設定物件（不包含 chillerCode）
 * @returns 傳回更新後的冰水主機門檻設定
 */
export function saveChillerThresholdApi(
  code: string,
  config: Omit<ChillerThresholdConfig, 'chillerCode'>,
): Promise<ChillerThresholdConfig> {
  return authorizedJsonApi(`/api/v1/thresholds/chillers/${encodeURIComponent(code)}`, {
    method: 'PUT',
    body: JSON.stringify(nullifyEmpty(config)),
  });
}

/**
 * 取得全廠 FCU 冰水送風機的告警門檻設定
 *
 * @returns 傳回 FCU 門檻設定物件
 */
export function getFcuThresholdApi(): Promise<FcuThresholdConfig> {
  return authorizedJsonApi('/api/v1/thresholds/fcus');
}

/**
 * 儲存全廠 FCU 冰水送風機的告警門檻設定
 *
 * @param config FCU 門檻設定物件
 * @returns 傳回更新後的 FCU 門檻設定物件
 */
export function saveFcuThresholdApi(config: FcuThresholdConfig): Promise<FcuThresholdConfig> {
  return authorizedJsonApi('/api/v1/thresholds/fcus', { method: 'PUT', body: JSON.stringify(nullifyEmpty(config)) });
}

/**
 * 冰水主機的供水/回水/溫差三個超標旗標，改從「目前有效告警」反查，不是自己拿門檻跟即時值
 * 比大小——AlarmEngine 有 debounce，瞬間超標不代表真的告警中，用真正的告警狀態才不會跟
 * 後端顯示的告警清單打架。ruleCode 格式是 "{Metric}.{Operator}.{Threshold}"（見
 * AlarmEngine.RuleCode），比對開頭的 Metric 名稱即可。
 *
 * @param chillerId 冰水主機內部 ID（流水號）
 * @param alarms 目前系統中的即時活躍告警清單
 * @returns 包含出水溫、回水溫與溫差是否超標的旗標物件
 */
export function chillerExceededFlags(chillerId: number, alarms: readonly AlarmRow[]) {
  const activeMetrics = new Set(
    alarms
      .filter((a) => a.deviceType === AlarmDeviceType.Chiller && a.deviceId === chillerId)
      .map((a) => a.ruleCode.split('.')[0]),
  );
  return {
    isSupplyTempExceeded: activeMetrics.has('ChilledWaterOutletTemperature'),
    isReturnTempExceeded: activeMetrics.has('ChilledWaterInletTemperature'),
    isTempDiffExceeded: activeMetrics.has('ChilledWaterTemperatureDifference'),
  };
}

/**
 * 冰水主機運轉報表用：判斷某個時間區間（例如某小時的 rollup bucket）內是否有對應規則的告警
 * 跟這段時間重疊，不是查「現在還在告警中」——歷史報表要看的是「那個時候曾經告警過」，
 * 邏輯跟 chillerExceededFlags 一致（比對 alarm_event，不是前端自己拿門檻跟歷史值比大小），
 * 只是判斷式從「目前有效」換成「時間區間重疊」。
 *
 * @param chillerId 冰水主機內部 ID
 * @param alarms 查詢區間內的歷史告警事件清單
 * @param windowStartIso 時間視窗起始時間（ISO-8601 字串）
 * @param windowEndIso 時間視窗結束時間（ISO-8601 字串）
 * @returns 包含在該時間區間內出水溫、回水溫與溫差是否曾超標的旗標物件
 */
export function chillerExceededFlagsInWindow(
  chillerId: number, alarms: readonly AlarmRow[], windowStartIso: string, windowEndIso: string,
) {
  const windowStart = new Date(windowStartIso).getTime();
  const windowEnd = new Date(windowEndIso).getTime();
  const overlapsMetric = (metric: string) =>
    alarms.some((a) => {
      if (a.deviceType !== AlarmDeviceType.Chiller || a.deviceId !== chillerId) return false;
      if (a.ruleCode.split('.')[0] !== metric) return false;
      const alarmStart = new Date(a.startedAt).getTime();
      const alarmEnd = a.endedAt ? new Date(a.endedAt).getTime() : Infinity;
      return alarmStart < windowEnd && alarmEnd > windowStart;
    });
  return {
    isSupplyTempExceeded: overlapsMetric('ChilledWaterOutletTemperature'),
    isReturnTempExceeded: overlapsMetric('ChilledWaterInletTemperature'),
    isTempDiffExceeded: overlapsMetric('ChilledWaterTemperatureDifference'),
  };
}

/**
 * FCU 運轉報表用：跟 chillerExceededFlagsInWindow 同樣的「時間區間重疊」邏輯，只是 FCU
 * 只有一種可設定門檻（室內溫度上下限，scope 是 "*" 全廠共用），不用分供水/回水/溫差三種，
 * 直接回傳單一布林值。
 *
 * @param fcuId FCU 設備內部 ID
 * @param alarms 查詢區間內的歷史告警事件清單
 * @param windowStartIso 時間視窗起始時間（ISO-8601 字串）
 * @param windowEndIso 時間視窗結束時間（ISO-8601 字串）
 * @returns 若在該時間區間內曾發生溫度超標告警則傳回 true，否則傳回 false
 */
export function fcuExceededInWindow(
  fcuId: number, alarms: readonly AlarmRow[], windowStartIso: string, windowEndIso: string,
): boolean {
  const windowStart = new Date(windowStartIso).getTime();
  const windowEnd = new Date(windowEndIso).getTime();
  return alarms.some((a) => {
    if (a.deviceType !== AlarmDeviceType.Fcu || a.deviceId !== fcuId) return false;
    const alarmStart = new Date(a.startedAt).getTime();
    const alarmEnd = a.endedAt ? new Date(a.endedAt).getTime() : Infinity;
    return alarmStart < windowEnd && alarmEnd > windowStart;
  });
}
