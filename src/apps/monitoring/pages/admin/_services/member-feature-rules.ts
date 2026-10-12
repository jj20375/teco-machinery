/**
 * @file member-feature-rules.ts
 * 使用者功能核取方塊之間的依賴關係（對照每個頁面實際打的 API 與後端要求的權限整理出來的）。
 * 後端只檢查「這支 API 要什麼權限」，不知道頁面需要哪幾支 API 湊在一起，所以依賴只存在於前端；
 * 這裡只負責「勾選時自動帶入／提示」，不是安全邊界。
 *
 * - 硬依賴（會自動勾選）：監控中心頁本身只看冰水主機＋FCU 兩個權限的資料，缺任一個整頁顯示沒有權限。
 * - 報表只提示不自動勾：三張報表各自需要不同資源，有人可能只想讓某人看其中一張。
 * - 告警／告警門檻沒有獨立頁面，只說明用途。
 */

import { MEMBER_FEATURE_OPTIONS } from './user-service';

const NAME = Object.fromEntries(MEMBER_FEATURE_OPTIONS.map((o) => [o.code, o.name])) as Record<string, string>;
const ORDER = MEMBER_FEATURE_OPTIONS.map((o) => o.code as string);

/** 勾選左邊的功能時，右邊這些也必須勾選，否則頁面整頁沒有資料。 */
const REQUIRES: Record<string, string[]> = {
  'hvac.overview': ['hvac.chillers', 'hvac.fcus'],
};

/** 統計報表的三張報表各自需要的資源（只提示，不自動勾）。 */
const REPORT_NEEDS = [
  { code: 'hvac.chillers', report: '冰水主機運轉報表' },
  { code: 'hvac.fcus', report: 'FCU運轉報表' },
  { code: 'hvac.alarms', report: '異常告警報表' },
];

/** 有側邊欄選單的功能；一個都沒勾的話，使用者登入後看不到任何頁面。 */
const MENU_FEATURES = [
  'hvac.overview', 'hvac.floor_plan', 'hvac.chillers', 'hvac.fcus', 'hvac.reports', 'merchant.users', 'merchant.operation_log',
];

/** 永遠顯示的小字說明（灰色）。 */
export const FEATURE_NOTES: Record<string, string> = {
  'hvac.overview': '勾選時會自動帶入冰水主機管理與 FCU管理（監控中心的資料來自這兩項）',
  'hvac.alarms': '沒有獨立頁面：用於右上角告警通知，以及各頁面的異常判斷與「異常告警報表」',
  'hvac.thresholds': '沒有獨立頁面：用於冰水主機的「告警門檻」「保養」，以及 FCU 的室溫上下限',
};

function ordered(codes: Iterable<string>): string[] {
  const set = new Set(codes);
  return ORDER.filter((c) => set.has(c));
}

/** 切換一個功能，回傳新的勾選結果；有連動變動時 notice 說明原因。 */
export function toggleFeature(current: readonly string[], code: string, checked: boolean): { features: string[]; notice: string } {
  const next = new Set(current);
  if (checked) {
    next.add(code);
    const added = (REQUIRES[code] ?? []).filter((c) => !next.has(c));
    added.forEach((c) => next.add(c));
    const notice = added.length > 0 ? `${NAME[code]}需要${added.map((c) => NAME[c]).join('、')}，已一併勾選。` : '';
    return { features: ordered(next), notice };
  }

  next.delete(code);
  const dropped = Object.entries(REQUIRES)
    .filter(([dependent, needs]) => next.has(dependent) && needs.includes(code))
    .map(([dependent]) => dependent);
  dropped.forEach((c) => next.delete(c));
  const notice = dropped.length > 0 ? `${dropped.map((c) => NAME[c]).join('、')}需要${NAME[code]}，已一併取消。` : '';
  return { features: ordered(next), notice };
}

export interface FeatureHints {
  /** 各功能旁邊的橘色提醒（目前勾選狀態下會出問題的地方）。 */
  warnings: Record<string, string>;
  /** 整體提醒，例如沒有任何有選單的功能。 */
  general: string;
}

export function featureHints(features: readonly string[]): FeatureHints {
  const selected = new Set(features);
  const warnings: Record<string, string> = {};

  if (selected.has('hvac.overview')) {
    const missing = REQUIRES['hvac.overview'].filter((c) => !selected.has(c));
    if (missing.length > 0) warnings['hvac.overview'] = `缺少${missing.map((c) => NAME[c]).join('、')}，監控中心會整頁顯示沒有權限。`;
  }
  if (selected.has('hvac.reports')) {
    const missing = REPORT_NEEDS.filter((n) => !selected.has(n.code));
    if (missing.length > 0) {
      warnings['hvac.reports'] = `要看到報表資料還需要：${missing.map((n) => `${NAME[n.code]}（${n.report}）`).join('、')}。`;
    }
  }

  const general = features.length > 0 && !features.some((c) => MENU_FEATURES.includes(c))
    ? '目前沒有勾選任何有選單的功能（監控中心、空間設備配置、冰水主機管理、FCU管理、報表統計匯出、使用者管理、操作紀錄），這個使用者登入後會看不到任何頁面。'
    : '';
  return { warnings, general };
}
