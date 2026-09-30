/**
 * @file diagnostics-service.ts
 * 平台「系統診斷」（/platform/diagnostics）：現場 IoT 接通驗證用。
 * 打 backend 的 /api/v1/platform/diagnostics（DiagnosticsEndpoints.cs），需要 platform scope
 * 且持有 platform.diagnostics:read。後端已經把每一項判斷成 ok/warn/error/unknown，
 * 前端只負責顯示，不要在這裡重做一套判斷邏輯（合理範圍集中在 DataValidationRules.cs）。
 */

import { authorizedJsonApi } from '../../admin/_services/auth-service';

export type DiagnosticLevel = 'ok' | 'warn' | 'error' | 'unknown';

export interface CollectorStatus {
  level: DiagnosticLevel;
  reachable: boolean;
  status: string | null;
  detail: string;
}

export interface IngestStatus {
  level: DiagnosticLevel;
  lastIngestAtUtc: string | null;
  secondsSinceLastIngest: number | null;
  ingestCountSinceApiStart: number;
  /** 供應商程式建立事件的時間（Collector 主機時鐘），不是設備量測時間。 */
  eventTimeUtc: string | null;
  collectorReceivedAtUtc: string | null;
}

export interface ChannelStatus {
  channel: number;
  name: string;
  level: DiagnosticLevel;
  ip: string | null;
  port: string | null;
  connectionState: number | null;
  isConnected: boolean;
  readStatus: number | null;
  lastSuccessAtUtc: string | null;
  staleSeconds: number | null;
  lastConnectionChangeAtUtc: string | null;
}

export interface DiagnosticCheck {
  key: string;
  category: string;
  label: string;
  level: DiagnosticLevel;
  detail: string;
}

export interface FcuCoverage {
  channel: number;
  name: string;
  level: DiagnosticLevel;
  received: number;
  mapped: number;
  matched: number;
  unresponsive: number;
  temperatureOutOfRange: number;
  unmappedKeys: string[];
  missingKeys: string[];
}

export interface ChillerFieldValue {
  key: string;
  label: string;
  unit: string;
  value: number;
  previousValue: number | null;
  min: number | null;
  max: number | null;
  level: DiagnosticLevel;
  note: string | null;
}

export interface ChillerFieldReport {
  modbusId: number;
  code: string | null;
  name: string | null;
  level: DiagnosticLevel;
  readStatus: number;
  isConnected: boolean;
  fields: ChillerFieldValue[];
}

export interface PersistenceStatus {
  level: DiagnosticLevel;
  recentWindowMinutes: number;
  chillerLatestTsUtc: string | null;
  chillerRecentRows: number;
  chillerRecentDevices: number;
  chillerActiveDevices: number;
  fcuLatestTsUtc: string | null;
  fcuRecentRows: number;
  fcuRecentDevices: number;
  fcuActiveDevices: number;
  rollupChillerLatestBucketUtc: string | null;
  rollupFcuLatestBucketUtc: string | null;
  error: string | null;
}

export interface JobRun {
  startedAtUtc: string;
  finishedAtUtc: string | null;
  succeeded: boolean | null;
  summary: string | null;
  error: string | null;
}

export interface JobReport {
  key: string;
  name: string;
  level: DiagnosticLevel;
  intervalSeconds: number;
  lastRun: JobRun | null;
  nextRunAtUtc: string | null;
  evidenceLabel: string | null;
  evidenceUtc: string | null;
  history: JobRun[];
}

export interface ConnectionHistoryItem {
  channel: number;
  name: string;
  connectionState: number;
  readStatus: number;
  changedAtUtc: string;
}

export interface DiagnosticsReport {
  generatedAtUtc: string;
  overall: DiagnosticLevel;
  collector: CollectorStatus;
  ingest: IngestStatus;
  channels: ChannelStatus[];
  checks: DiagnosticCheck[];
  fcuCoverage: FcuCoverage[];
  chillers: ChillerFieldReport[];
  persistence: PersistenceStatus | null;
  jobs: JobReport[];
  connectionHistory: ConnectionHistoryItem[];
}

export interface RawFcu {
  channel: number;
  stationId: number;
  position: number;
  id: string;
  address: number;
  switchStatus: number;
  mode: number;
  fanSpeed: number;
  temperature: number;
}

export interface RawDdc {
  channel: number;
  updateTimeUtc: string;
  readStatus: number;
  isConnected: boolean;
  fcuList: RawFcu[];
}

export interface RawSnapshot {
  updateTimeUtc: string;
  receivedAtUtc: string;
  ddc1: RawDdc;
  ddc2: RawDdc;
}

export function getDiagnosticsApi(): Promise<DiagnosticsReport> {
  return authorizedJsonApi('/api/v1/platform/diagnostics');
}

/** 還沒收到任何資料時後端回 `{ message }`，這裡統一轉成 null。 */
export async function getRawSnapshotApi(): Promise<RawSnapshot | null> {
  const body = await authorizedJsonApi<RawSnapshot | { message: string }>('/api/v1/platform/diagnostics/raw');
  return 'ddc1' in body ? body : null;
}

// 對應後端 Teco.Hvac.Contracts.Enums（JSON 輸出是數字）。
export const CONNECTION_STATE_LABEL: Record<number, string> = {
  [-1]: '未知', 0: '已停止', 1: '連線中', 2: '已連線', 3: '連線失敗', 4: '已斷線',
};
export const READ_STATUS_LABEL: Record<number, string> = {
  0: '尚未讀取', 1: '讀取成功', 2: '讀取失敗', 3: '未連線',
};
export const FCU_SWITCH_LABEL: Record<number, string> = { [-1]: '未知', 0: '關', 1: '開' };
export const FCU_MODE_LABEL: Record<number, string> = { [-1]: '未知', 1: '冷氣', 2: '暖氣', 3: '送風' };
export const FCU_FAN_LABEL: Record<number, string> = { [-1]: '未知', 0: '高', 1: '中', 2: '低', 3: '自動' };

export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '—';
  return new Date(value).toLocaleString('zh-TW', { hour12: false });
}

export function formatAgo(value: string | null | undefined, nowMs: number): string {
  if (!value) return '—';
  const seconds = Math.max(0, Math.round((nowMs - new Date(value).getTime()) / 1000));
  if (seconds < 60) return `${seconds} 秒前`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)} 分鐘前`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} 小時前`;
  return `${Math.floor(seconds / 86400)} 天前`;
}
