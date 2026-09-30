/**
 * @file alarm-notice.ts
 * 後台「告警通知」紅點與「全部告警」彈窗的共用狀態（設計稿：出現新的異常項目就亮紅點，
 * 點擊後跳出「全部告警」，查看後紅點消失）。
 *
 * 彈窗只有一個實例，放在 AdminHeader.vue；監控中心的「查看全部告警」呼叫 openAllAlarms()
 * 打開同一個彈窗，所以不管從哪裡打開，看過就算已檢視。
 *
 * 「已檢視」只記在這個瀏覽器的 localStorage：這是個人的提示，不是告警確認（ack）紀錄，
 * 換瀏覽器或清掉資料只會讓紅點再亮一次，不影響告警本身。
 */
import { ref } from 'vue';
import { listAlarmsApi } from './hvac-service';

const STORAGE_KEY = 'teco-admin-seen-alarm-ids';

/** 監控中心與頂部列共用同一個 query key，同一頁不會重複打 API。 */
export const ACTIVE_ALARMS_QUERY = {
  queryKey: ['active-alarms'],
  queryFn: () => listAlarmsApi('active'),
  refetchInterval: 10_000,
} as const;

export const allAlarmsOpen = ref(false);
export const seenAlarmIds = ref<Set<number>>(new Set());

export function openAllAlarms(): void {
  allAlarmsOpen.value = true;
}

/** 只能在瀏覽器端（onMounted 之後）呼叫，SSR 階段沒有 localStorage。 */
export function loadSeenAlarmIds(): void {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    const ids = raw ? (JSON.parse(raw) as unknown) : [];
    seenAlarmIds.value = new Set(Array.isArray(ids) ? ids.filter((x): x is number => typeof x === 'number') : []);
  } catch {
    seenAlarmIds.value = new Set();
  }
}

/** 只保留目前仍有效的告警 id：已解除的告警不會再出現，留著只會讓清單越存越大。 */
export function markAlarmsSeen(activeIds: readonly number[]): void {
  seenAlarmIds.value = new Set(activeIds);
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(activeIds));
  } catch {
    // 私密瀏覽等情況寫不進去就算了，最多紅點下次重新整理時再亮一次。
  }
}
