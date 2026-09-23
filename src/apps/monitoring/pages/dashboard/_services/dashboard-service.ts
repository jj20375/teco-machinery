/**
 * @file dashboard-service.ts
 * 東元電機智慧環境監控 - 前台戰情室真實 API 服務
 * 嚴格遵守 Metat 規範：所有對外呼叫函式一律以 *Api 結尾
 *
 * 前台頁面（`/`）刻意設計成不用登入的大廳螢幕，打的是 backend/ 的 /api/v1/public/*
 * （不掛 JWT 驗證，見 PublicEndpoints.cs）——不是後台監控中心總覽頁用的 /api/v1/{chillers,fcus,alarms}。
 * 這裡把 hvac-service.ts 的 listPublicXxxApi 用 import...as 重新命名回 listXxxApi，這樣底下的
 * 狀態判定邏輯（deriveChillerStatus 等）可以跟後台共用同一份程式碼，不用另外重寫一次。
 */

import type { FloorId, ChillerData, FloorHeatmapData, AlarmItem, FcuItem, HourlyUsageTrend, HourlyTempTrend, DashboardOverview } from '../_types/dashboard-types';
import {
  listPublicChillersApi as listChillersApi, listPublicFcusApi as listFcusApi, listPublicAlarmsApi as listAlarmsApi,
  getPublicHourlyFcuStatsApi,
  deriveChillerStatus, deriveFcuStatus, alarmingFcuIdsOf, isDataQualityOnline,
  AlarmDeviceType, type AlarmRow,
} from '../../admin/_services/hvac-service';
import { chillerExceededFlags } from '../../admin/_services/threshold-service';

/** 三個超標旗標改從「目前有效告警」反查（見 threshold-service.ts 的 chillerExceededFlags），
 * 跟後台監控中心總覽頁用同一套邏輯，門檻資料現在是真的了（告警門檻設定頁），不用再全部回 false。 */
export async function getChillersDataApi(): Promise<ChillerData[]> {
  const [chillers, alarms] = await Promise.all([listChillersApi(), listAlarmsApi('active')]);
  return chillers.map((c) => ({
    id: String(c.id),
    name: c.displayName,
    code: c.code,
    status: deriveChillerStatus(c),
    loadRate: c.value?.loadPercentage ?? 0,
    supplyTemp: isDataQualityOnline(c.dataQuality) ? (c.value?.chilledWaterOutletTemperature ?? 0) : 0,
    returnTemp: isDataQualityOnline(c.dataQuality) ? (c.value?.chilledWaterInletTemperature ?? 0) : 0,
    tempDiff: isDataQualityOnline(c.dataQuality) ? (c.value?.chilledWaterTemperatureDifference ?? 0) : 0,
    cumulativeHours: c.value?.accumulatedRunningHours ?? 0,
    ...(isDataQualityOnline(c.dataQuality)
      ? chillerExceededFlags(c.id, alarms)
      : { isSupplyTempExceeded: false, isReturnTempExceeded: false, isTempDiffExceeded: false }),
  }));
}

async function loadFcusWithStatus(floor?: FloorId) {
  const [fcus, alarms] = await Promise.all([listFcusApi(floor), listAlarmsApi('active')]);
  const alarmingFcus = alarmingFcuIdsOf(alarms);
  return fcus.map((f) => ({ f, status: deriveFcuStatus(f, alarmingFcus) }));
}

function toFcuItem(f: Awaited<ReturnType<typeof loadFcusWithStatus>>[number]['f'], status: FcuItem['status']): FcuItem {
  const online = isDataQualityOnline(f.dataQuality);
  const roomTemp = online ? (f.value?.temperature ?? NaN) : NaN;
  return {
    id: String(f.id),
    code: f.zoneCode ?? `${f.floor}-${f.id}`,
    floor: f.floor as FloorId,
    roomTemp,
    // 供應商 SDK 沒有 FCU 設定溫度這個欄位，一律回 NaN，畫面已改用 -- 顯示（見 FcuMatrixSection.vue）。
    setTemp: NaN,
    tempDiff: NaN,
    mode: '冷氣',
    fanSpeed: '自動',
    status,
    isExceeded: status === 'ABNORMAL',
  };
}

export async function getFcuStatusMatrixApi(): Promise<Record<FloorId, FcuItem[]>> {
  const withStatus = await loadFcusWithStatus();
  const result: Record<FloorId, FcuItem[]> = { B1: [], B2: [] };
  for (const { f, status } of withStatus) {
    result[f.floor as FloorId].push(toFcuItem(f, status));
  }
  return result;
}

export async function listFcuMatrixApi(floor: FloorId): Promise<FcuItem[]> {
  const withStatus = await loadFcusWithStatus(floor);
  return withStatus.map(({ f, status }) => toFcuItem(f, status));
}

export async function getFloorHeatmapApi(floor: FloorId): Promise<FloorHeatmapData> {
  const withStatus = await loadFcusWithStatus(floor);
  const total = withStatus.length;
  const running = withStatus.filter((x) => x.status === 'RUNNING').length;
  return {
    floorId: floor,
    fcuTotal: total,
    fcuRunning: running,
    runRate: total === 0 ? 0 : Number(((running / total) * 100).toFixed(1)),
  };
}

/**
 * 總體指標。真正有畫面在用的只有 b1AvgTemp/b2AvgTemp（樓層均溫膠囊）跟
 * totalFcu/overallRunRate（FCU 設備總覽卡片在沒有清單資料時的備援值），其餘欄位
 * （totalChillers/runningChillers/runningFcu/abnormalFcu/offlineFcu/stoppedFcu）
 * 翻過所有戰情室元件都沒有被實際渲染，但型別还留著，就用真實統計值填，不留假數字。
 */
export async function getDashboardOverviewApi(): Promise<DashboardOverview> {
  const [chillers, fcus] = await Promise.all([listChillersApi(), listFcusApi()]);
  const alarms = await listAlarmsApi('active');
  const alarmingFcus = alarmingFcuIdsOf(alarms);

  const chillerStatuses = chillers.map(deriveChillerStatus);
  const fcuStatuses = fcus.map((f) => ({ f, status: deriveFcuStatus(f, alarmingFcus) }));

  const avgTemp = (floor: FloorId) => {
    const temps = fcuStatuses
      .filter(({ f }) => f.floor === floor && isDataQualityOnline(f.dataQuality))
      .map(({ f }) => f.value?.temperature)
      .filter((t): t is number => Number.isFinite(t));
    return temps.length === 0 ? 0 : Number((temps.reduce((a, b) => a + b, 0) / temps.length).toFixed(1));
  };

  const running = fcuStatuses.filter((x) => x.status === 'RUNNING').length;
  return {
    totalChillers: chillers.length,
    runningChillers: chillerStatuses.filter((s) => s === 'RUNNING').length,
    totalFcu: fcus.length,
    runningFcu: running,
    abnormalFcu: fcuStatuses.filter((x) => x.status === 'ABNORMAL').length,
    offlineFcu: fcuStatuses.filter((x) => x.status === 'OFFLINE').length,
    stoppedFcu: fcuStatuses.filter((x) => x.status === 'STOPPED').length,
    overallRunRate: fcus.length === 0 ? 0 : Math.round((running / fcus.length) * 100),
    b1AvgTemp: avgTemp('B1'),
    b2AvgTemp: avgTemp('B2'),
  };
}

/**
 * FCU 的 ruleCode 是 "{Metric}.{Operator}.{Threshold}"（例："Temperature.0.28"）；
 * 從中還原出「門檻 28°C」這種人話字串，跟後端 AlarmEndpoints.DescribeFcuRule 是同一份邏輯的
 * 前端版本——這裡只是為了組出 thresholdValue 顯示字串，不是重新判斷告警。
 */
function fcuThresholdText(ruleCode: string): string {
  const parts = ruleCode.split('.');
  if (parts.length < 3 || parts[0] !== 'Temperature') return '—';
  const threshold = Number(parts.slice(2).join('.'));
  return Number.isFinite(threshold) ? `${threshold.toFixed(1)} °C` : '—';
}

function toAlarmItem(a: AlarmRow): AlarmItem {
  const isFcu = a.deviceType === AlarmDeviceType.Fcu;
  return {
    id: String(a.id),
    time: new Date(a.startedAt).toLocaleTimeString('zh-TW', { hour12: false }),
    deviceName: a.deviceName,
    deviceCode: a.deviceCode,
    location: a.location,
    // 冰水主機沒有樓層欄位（機台實體皆位於 B1 機房，跟 floor-plan.ts 的既有假設一致）。
    floor: (isFcu ? a.location : 'B1') as FloorId,
    statusText: `異常：${a.ruleLabel}`,
    triggerValue: isFcu && a.peakValue !== null ? `${a.peakValue.toFixed(1)} °C` : '已觸發',
    thresholdValue: isFcu ? fcuThresholdText(a.ruleCode) : '—',
    isCritical: a.severity === 2,
  };
}

export async function listRealtimeAlarmsApi(): Promise<AlarmItem[]> {
  const alarms = await listAlarmsApi('active');
  return alarms.map(toAlarmItem);
}

// 兩張趨勢圖背後是同一支後端查詢（GET /fcus/hourly-stats），這裡各自只取需要的欄位；
// DashboardApp.vue 用 Promise.all 平行呼叫，所以會打兩次同一支端點——資料量小、
// 一天一次的聚合查詢，不值得為了省一次請求把兩個獨立函式的介面攪在一起。

export async function getHourlyFcuUsageTrendApi(): Promise<HourlyUsageTrend> {
  const stats = await getPublicHourlyFcuStatsApi();
  return { hours: stats.hours, b1Count: stats.b1Count, b2Count: stats.b2Count };
}

export async function getHourlyTempTrendApi(): Promise<HourlyTempTrend> {
  const stats = await getPublicHourlyFcuStatsApi();
  return { hours: stats.hours, b1Temp: stats.b1Temp, b2Temp: stats.b2Temp };
}
