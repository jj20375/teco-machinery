<script setup lang="ts">
import { computed, ref, onMounted, onUnmounted } from 'vue';
import { useQuery } from '@tanstack/vue-query';
import AdminLayout from '../_components/AdminLayout.vue';
import AdminStatusBadge from '../_components/AdminStatusBadge.vue';
import AdminHeatmapCard from '../_components/AdminHeatmapCard.vue';
import { ApiError } from '../_services/auth-service';
import {
  listChillersApi,
  listFcusApi,
  deriveChillerStatus,
  deriveFcuStatus,
  isDataQualityOnline,
  alarmingFcuIdsOf,
  alarmBadgeStatus,
  formatAlarmTime,
  type AlarmRow,
  type DeviceStatus,
} from '../_services/hvac-service';
import { chillerExceededFlags } from '../_services/threshold-service';
import { ACTIVE_ALARMS_QUERY, openAllAlarms } from '../_services/alarm-notice';

const chillersQuery = useQuery({ queryKey: ['overview-chillers'], queryFn: listChillersApi, refetchInterval: 10_000 });
const fcusQuery = useQuery({ queryKey: ['overview-fcus'], queryFn: () => listFcusApi(), refetchInterval: 10_000 });
const alarmsQuery = useQuery(ACTIVE_ALARMS_QUERY);

const loading = computed(() => chillersQuery.isPending.value || fcusQuery.isPending.value || alarmsQuery.isPending.value);

/**
 * hvac.chillers/hvac.fcus 是頁面主體資料，缺任一個就整頁擋掉；hvac.alarms 是獨立權限，只影響
 * 異常判斷與告警清單那一小塊，缺了只顯示提示、不擋整頁——實測某些自訂角色（新權限加進權限目錄
 * 後，既有角色不會自動補上）就是只有 chillers/fcus 沒有 alarms，這種帳號應該還能看到基本監控畫面。
 */
const loadError = computed(() => {
  const err = chillersQuery.error.value ?? fcusQuery.error.value;
  if (!err) return '';
  return err instanceof ApiError && err.status === 403
    ? '目前登入的帳號沒有監控資料的查看權限。'
    : err instanceof Error
      ? err.message
      : '載入監控資料失敗，請稍後再試。';
});
const alarmWarning = computed(() => {
  const err = alarmsQuery.error.value;
  if (!err) return '';
  return err instanceof ApiError && err.status === 403
    ? '目前登入的帳號沒有「告警」查看權限，異常判斷與告警清單可能不準確。'
    : '目前有效告警清單載入失敗，異常判斷與告警清單可能不準確。';
});

// 狀態判斷一律用 hvac-service.ts 的共用函式：這頁原本自己複製了一份，
// 共用函式修正後（例如冰水主機門檻超標要判為異常）這頁就不會跟著變。
function summarize(statuses: DeviceStatus[]) {
  const total = statuses.length;
  const running = statuses.filter((s) => s === 'RUNNING').length;
  const abnormal = statuses.filter((s) => s === 'ABNORMAL').length;
  const offline = statuses.filter((s) => s === 'OFFLINE').length;
  const stopped = total - running - abnormal - offline;
  return { total, running, stopped, abnormal, offline, runRate: total === 0 ? 0 : Math.round((running / total) * 100) };
}

const alarmingFcuIds = computed(() => alarmingFcuIdsOf(alarmsQuery.data.value ?? []));

const fcuSummary = computed(() => summarize((fcusQuery.data.value ?? []).map((f) => deriveFcuStatus(f, alarmingFcuIds.value))));
const chillerSummary = computed(() =>
  summarize((chillersQuery.data.value ?? []).map((c) => deriveChillerStatus(c, alarmsQuery.data.value ?? []))));

/** 每張卡片顯示的冰水主機明細。三個門檻超標旗標改從「目前有效告警」反查（見
 * threshold-service.ts 的 chillerExceededFlags），不是自己拿門檻跟即時值比大小，這樣才會跟
 * AlarmEngine 的 debounce 判斷一致。「水流量」供應商 SDK 沒有量測值，模板裡固定顯示 --。
 */
const chillerCards = computed(() => {
  const alarms = alarmsQuery.data.value ?? [];
  return (chillersQuery.data.value ?? []).map((c) => {
    const online = isDataQualityOnline(c.dataQuality);
    return {
      id: c.id,
      name: c.displayName,
      code: c.code,
      status: deriveChillerStatus(c, alarms),
      // 離線時舊快照的數字不可信，用 null 讓畫面顯示 --，不要顯示 0.0 °C；超標紅字也一併不顯示。
      loadRate: online ? (c.value?.loadPercentage ?? null) : null,
      supplyTemp: online ? (c.value?.chilledWaterOutletTemperature ?? null) : null,
      returnTemp: online ? (c.value?.chilledWaterInletTemperature ?? null) : null,
      tempDiff: online ? (c.value?.chilledWaterTemperatureDifference ?? null) : null,
      cumulativeHours: online ? (c.value?.accumulatedRunningHours ?? null) : null,
      ...(online
        ? chillerExceededFlags(c.id, alarms)
        : { isSupplyTempExceeded: false, isReturnTempExceeded: false, isTempDiffExceeded: false }),
    };
  });
});

/** 樓層熱感圖卡片：依 FCU 的 floor 欄位分組算運轉率，取代原本寫死的 66.7%/40/60。 */
function floorSummary(floor: string) {
  const fcus = (fcusQuery.data.value ?? []).filter((f) => f.floor === floor);
  const running = fcus.filter((f) => deriveFcuStatus(f, alarmingFcuIds.value) === 'RUNNING').length;
  const total = fcus.length;
  return { runRate: total === 0 ? 0 : Math.round((running / total) * 100), running, total };
}
const b1 = computed(() => floorSummary('B1'));
const b2 = computed(() => floorSummary('B2'));

// 設計稿：超過兩筆告警要輪播。比照前台戰情室 FloorHeatmapSection.vue，一次兩列、每 4 秒往下捲一筆；
// 完整清單在「全部告警」彈窗。
const ALARM_VISIBLE_ROWS = 2;
const alarmIdx = ref(0);
let alarmTimer: number | null = null;
const alarmCount = computed(() => alarmsQuery.data.value?.length ?? 0);
const alarmRows = computed<AlarmRow[]>(() => {
  const all = alarmsQuery.data.value ?? [];
  if (all.length <= ALARM_VISIBLE_ROWS) return all;
  return Array.from({ length: ALARM_VISIBLE_ROWS }, (_, k) => all[(alarmIdx.value + k) % all.length]);
});
onMounted(() => {
  alarmTimer = window.setInterval(() => {
    if (alarmCount.value > ALARM_VISIBLE_ROWS) alarmIdx.value = (alarmIdx.value + 1) % alarmCount.value;
  }, 4000);
});
onUnmounted(() => { if (alarmTimer) clearInterval(alarmTimer); });

function dashArray(pct: number) {
  return `${pct} ${100 - pct}`;
}
</script>

<template>
  <AdminLayout page-title="監控中心" current-path="/admin">
    <div v-if="loadError" class="p-3 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-sm">{{ loadError }}</div>
    <div v-else-if="loading" class="p-6 text-center text-sm text-[#94A3B8]">載入中…</div>
    <div v-else class="flex flex-col gap-4">
      <div v-if="alarmWarning" class="p-3 rounded-lg bg-[#FFFBEB] text-[#B45309] text-sm">{{ alarmWarning }}</div>
      <!-- 上半：統計卡 + 冰水主機卡 -->
      <div class="grid grid-cols-1 xl:grid-cols-[300px_1fr] gap-4">
        <!-- 左：兩張統計卡 -->
        <div class="flex flex-col gap-4">
          <div class="bg-white rounded-xl border border-[#E2E8F0] p-4">
            <div class="flex items-center justify-between">
              <div>
                <div class="text-xs text-[#64748B]">總 FCU 數量</div>
                <div class="text-3xl font-extrabold text-[#1A202C] font-tabular mt-1">
                  {{ fcuSummary.total }} <span class="text-base font-bold">台</span>
                </div>
              </div>
              <div class="relative w-14 h-14">
                <svg class="w-full h-full -rotate-90" viewBox="0 0 36 36">
                  <circle cx="18" cy="18" r="15.9155" fill="none" stroke="#E2E8F0" stroke-width="3.5" />
                  <circle cx="18" cy="18" r="15.9155" fill="none" stroke="#00D1B2" stroke-width="3.5" stroke-linecap="round" :stroke-dasharray="dashArray(fcuSummary.runRate)" />
                </svg>
                <div class="absolute inset-0 flex flex-col items-center justify-center leading-none">
                  <span class="text-xs font-extrabold text-[#1A202C]">{{ fcuSummary.runRate }}%</span>
                  <span class="text-[8px] text-[#94A3B8]">運轉率</span>
                </div>
              </div>
            </div>
            <div class="flex flex-wrap gap-x-3 gap-y-1 text-xs text-[#64748B] mt-3">
              <span class="flex items-center gap-1"><span class="w-2 h-2 rounded-full bg-[#10B981]" />運轉中 {{ fcuSummary.running }}</span>
              <span class="flex items-center gap-1"><span class="w-2 h-2 rounded-full bg-[#94A3B8]" />停止 {{ fcuSummary.stopped }}</span>
              <span class="flex items-center gap-1"><span class="w-2 h-2 rounded-full bg-[#FF4757]" />異常 {{ fcuSummary.abnormal }}</span>
              <span class="flex items-center gap-1"><span class="w-2 h-2 rounded-full bg-[#41628C]" />離線 {{ fcuSummary.offline }}</span>
            </div>
          </div>

          <div class="bg-white rounded-xl border border-[#E2E8F0] p-4">
            <div class="flex items-center justify-between">
              <div>
                <div class="text-xs text-[#64748B]">總冰水主機數量</div>
                <div class="text-3xl font-extrabold text-[#1A202C] font-tabular mt-1">
                  {{ chillerSummary.total }} <span class="text-base font-bold">台</span>
                </div>
              </div>
              <div class="relative w-14 h-14">
                <svg class="w-full h-full -rotate-90" viewBox="0 0 36 36">
                  <circle cx="18" cy="18" r="15.9155" fill="none" stroke="#E2E8F0" stroke-width="3.5" />
                  <circle cx="18" cy="18" r="15.9155" fill="none" stroke="#00D1B2" stroke-width="3.5" stroke-linecap="round" :stroke-dasharray="dashArray(chillerSummary.runRate)" />
                </svg>
                <div class="absolute inset-0 flex flex-col items-center justify-center leading-none">
                  <span class="text-xs font-extrabold text-[#1A202C]">{{ chillerSummary.runRate }}%</span>
                  <span class="text-[8px] text-[#94A3B8]">運轉率</span>
                </div>
              </div>
            </div>
            <div class="flex flex-wrap gap-x-3 gap-y-1 text-xs text-[#64748B] mt-3">
              <span class="flex items-center gap-1"><span class="w-2 h-2 rounded-full bg-[#10B981]" />運轉中 {{ chillerSummary.running }}</span>
              <span class="flex items-center gap-1"><span class="w-2 h-2 rounded-full bg-[#94A3B8]" />停止 {{ chillerSummary.stopped }}</span>
              <span class="flex items-center gap-1"><span class="w-2 h-2 rounded-full bg-[#FF4757]" />異常 {{ chillerSummary.abnormal }}</span>
              <span class="flex items-center gap-1"><span class="w-2 h-2 rounded-full bg-[#41628C]" />離線 {{ chillerSummary.offline }}</span>
            </div>
          </div>
        </div>

        <!-- 右：冰水主機卡 -->
        <div class="flex flex-col gap-4">
          <div
            v-for="c in chillerCards"
            :key="c.id"
            class="bg-white rounded-xl border border-[#E2E8F0] p-4"
          >
            <div class="flex items-start justify-between">
              <div>
                <div class="text-base font-bold text-[#1A202C]">{{ c.name }}</div>
                <div class="text-xs text-[#94A3B8] font-tabular">{{ c.code }}</div>
              </div>
              <AdminStatusBadge :status="c.status" />
            </div>

            <div class="flex gap-4 mt-3">
              <div
                class="w-24 flex-shrink-0 rounded-lg flex flex-col items-center justify-center py-3"
                :class="(c.loadRate ?? 0) > 60 ? 'bg-[#FFF7ED] text-[#FB923C]' : 'bg-[#E6FBF7] text-[#10B981]'"
              >
                <span class="text-xl font-extrabold font-tabular">{{ c.loadRate === null ? '--' : `${c.loadRate}%` }}</span>
                <span class="text-[10px]">運轉負載</span>
              </div>

              <div class="flex-1 divide-y divide-[#F1F5F9] text-sm">
                <div class="flex justify-between py-1.5"><span class="text-[#64748B]">出水溫度</span><span class="font-tabular" :class="c.isSupplyTempExceeded ? 'text-[#FF4757] font-bold' : 'text-[#334155]'">{{ c.supplyTemp === null ? '--' : `${c.supplyTemp.toFixed(1)} °C` }}</span></div>
                <div class="flex justify-between py-1.5"><span class="text-[#64748B]">回水溫度</span><span class="font-tabular" :class="c.isReturnTempExceeded ? 'text-[#FF4757] font-bold' : 'text-[#334155]'">{{ c.returnTemp === null ? '--' : `${c.returnTemp.toFixed(1)} °C` }}</span></div>
                <div class="flex justify-between py-1.5"><span class="text-[#64748B]">溫度差</span><span class="font-tabular" :class="c.isTempDiffExceeded ? 'text-[#FF4757] font-bold' : 'text-[#334155]'">{{ c.tempDiff === null ? '--' : `${c.tempDiff.toFixed(1)} °C` }}</span></div>
                <div class="flex justify-between py-1.5" title="供應商尚未提供水流量量測值"><span class="text-[#64748B]">水流量</span><span class="font-tabular text-[#334155]">--</span></div>
                <div class="flex justify-between py-1.5"><span class="text-[#64748B]">累積運轉</span><span class="font-tabular text-[#334155]">{{ c.cumulativeHours === null ? '--' : `${c.cumulativeHours.toLocaleString()} 小時` }}</span></div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- 即時告警 -->
      <div class="bg-white rounded-xl border border-[#E2E8F0] overflow-hidden">
        <div class="flex items-center justify-between px-4 py-3 border-b border-[#E2E8F0]">
          <div class="flex items-center gap-2">
            <svg class="w-4 h-4 text-[#FF4757]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
            </svg>
            <span class="text-sm font-bold text-[#1A202C]">即時告警</span>
            <span class="text-xs text-[#FF4757] font-semibold">{{ alarmCount }} 筆告警中</span>
          </div>
          <button type="button" class="text-xs text-[#64748B] hover:text-[#00D1B2] flex items-center gap-1" @click="openAllAlarms">
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" /><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M2.458 12C3.732 7.943 7.523 5 12 5c4.478 0 8.268 2.943 9.542 7-1.274 4.057-5.064 7-9.542 7-4.477 0-8.268-2.943-9.542-7z" /></svg>
            查看全部告警
          </button>
        </div>
        <table class="w-full text-left text-[13px] text-[#334155]">
          <thead class="bg-[#F8FAFC] text-[#64748B] border-b border-[#E2E8F0]">
            <tr>
              <th class="px-4 py-3 font-semibold whitespace-nowrap">時間</th>
              <th class="px-4 py-3 font-semibold">設備名稱</th>
              <th class="px-4 py-3 font-semibold">設備編號</th>
              <th class="px-4 py-3 font-semibold">安裝位置</th>
              <th class="px-4 py-3 font-semibold">狀態</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="a in alarmRows" :key="a.id" class="border-b border-[#F1F5F9]">
              <td class="px-4 py-3 font-tabular text-[#64748B] whitespace-nowrap">{{ formatAlarmTime(a.startedAt) }}</td>
              <td class="px-4 py-3 font-medium">{{ a.deviceName }}</td>
              <td class="px-4 py-3 font-tabular text-[#64748B]">{{ a.deviceCode }}</td>
              <td class="px-4 py-3 text-[#64748B]">{{ a.location }}</td>
              <td class="px-4 py-3"><AdminStatusBadge :status="alarmBadgeStatus(a)" :reason="a.ruleLabel" /></td>
            </tr>
            <tr v-if="alarmRows.length === 0">
              <td colspan="5" class="px-4 py-6 text-center text-[#94A3B8]">目前沒有任何告警中的設備</td>
            </tr>
          </tbody>
        </table>
      </div>

      <!-- 樓層熱感圖 -->
      <div class="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <AdminHeatmapCard floor="B1" :run-rate="b1.runRate" :running-count="b1.running" :total-count="b1.total" />
        <AdminHeatmapCard floor="B2" :run-rate="b2.runRate" :running-count="b2.running" :total-count="b2.total" />
      </div>
    </div>
  </AdminLayout>
</template>
