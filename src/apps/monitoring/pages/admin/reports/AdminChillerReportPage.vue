<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick, watch } from 'vue';
import * as echarts from 'echarts';
import { useQuery } from '@tanstack/vue-query';
import AdminLayout from '../_components/AdminLayout.vue';
import AdminButton from '../_components/AdminButton.vue';
import AdminDatePicker from '../_components/AdminDatePicker.vue';
import AdminStatusBadge from '../_components/AdminStatusBadge.vue';
import AdminPagination from '../_components/AdminPagination.vue';
import EmptyState from '../_components/EmptyState.vue';
import { ApiError } from '../_services/auth-service';
import { listChillersApi, listAlarmsApi, getChillerHistoryApi } from '../_services/hvac-service';
import { chillerExceededFlagsInWindow } from '../_services/threshold-service';
import type { ChillerReportRow } from '../_types/admin-types';

const period = ref<'day' | 'week'>('day');
/** 預設看今天，不能寫死日期字串——理由跟 AdminFcuReportPage.vue 同一個。 */
const selectedDate = ref(new Date().toISOString().slice(0, 10));
const view = ref<'table' | 'chart'>('table');
const page = ref(1);
const pageSize = ref(20);

const chillersQuery = useQuery({ queryKey: ['chiller-report-devices'], queryFn: listChillersApi });
const chillerList = computed(() => chillersQuery.data.value ?? []);
const selectedCode = ref('');
watch(chillerList, (list) => {
  if (!selectedCode.value && list.length > 0) selectedCode.value = list[0].code;
});
const selectedChiller = computed(() => chillerList.value.find((c) => c.code === selectedCode.value) ?? null);

/** 本地日曆日的起訖（瀏覽器跑在 Asia/Taipei），轉成 UTC ISO 字串給後端查詢用。 */
function rangeUtc(dateStr: string, days: number): [string, string] {
  const start = new Date(`${dateStr}T00:00:00`);
  const end = new Date(start.getTime() + days * 24 * 3600_000);
  return [start.toISOString(), end.toISOString()];
}
const [fromIso, toIso] = [computed(() => rangeUtc(selectedDate.value, period.value === 'day' ? 1 : 7)[0]),
  computed(() => rangeUtc(selectedDate.value, period.value === 'day' ? 1 : 7)[1])];

const historyQuery = useQuery({
  queryKey: ['chiller-report-history', selectedCode, fromIso, toIso],
  queryFn: () => getChillerHistoryApi(selectedCode.value, fromIso.value, toIso.value, '1h'),
  enabled: computed(() => !!selectedCode.value),
});
const alarmsQuery = useQuery({
  queryKey: ['chiller-report-alarms', fromIso, toIso],
  queryFn: () => listAlarmsApi('all', fromIso.value, toIso.value),
  enabled: computed(() => !!selectedCode.value),
});

const loading = computed(() => historyQuery.isPending.value || chillersQuery.isPending.value);
const loadError = computed(() => {
  const err = historyQuery.error.value ?? chillersQuery.error.value;
  if (!err) return '';
  return err instanceof ApiError && err.status === 403
    ? '目前登入的帳號沒有「冰水主機管理」查看權限，無法查看報表。'
    : err instanceof Error
      ? err.message
      : '載入報表失敗，請稍後再試。';
});
const alarmWarning = computed(() => {
  const err = alarmsQuery.error.value;
  if (!err) return '';
  return err instanceof ApiError && err.status === 403
    ? '目前登入的帳號沒有「告警」查看權限，下表的告警門檻超標標記可能不準確。'
    : '有效告警清單載入失敗，下表的告警門檻超標標記可能不準確。';
});

function pad(n: number): string {
  return String(n).padStart(2, '0');
}
/** UTC ISO → 本地「YYYY/MM/DD HH:00」，跟原本折線圖的 label 拆解邏輯（split(' ')）對齊。 */
function formatLocal(ts: string): string {
  const d = new Date(ts);
  return `${d.getFullYear()}/${pad(d.getMonth() + 1)}/${pad(d.getDate())} ${pad(d.getHours())}:00`;
}

const rows = computed<ChillerReportRow[]>(() => {
  if (!selectedChiller.value) return [];
  const chillerId = selectedChiller.value.id;
  const alarms = alarmsQuery.data.value ?? [];
  return (historyQuery.data.value ?? []).map((p) => {
    const windowEnd = new Date(new Date(p.ts).getTime() + 3600_000).toISOString();
    const flags = chillerExceededFlagsInWindow(chillerId, alarms, p.ts, windowEnd);
    const isAbnormal = flags.isSupplyTempExceeded || flags.isReturnTempExceeded || flags.isTempDiffExceeded;
    return {
      timestamp: formatLocal(p.ts),
      status: isAbnormal ? 'ABNORMAL' : (p.loadPercentage ?? 0) > 0 ? 'RUNNING' : 'STOPPED',
      supplyTemp: p.chilledWaterOut ?? 0,
      returnTemp: p.chilledWaterIn ?? 0,
      tempDiff: p.chilledWaterDelta ?? 0,
      loadRate: p.loadPercentage,
      cumulativeHours: p.runningHours,
      ...flags,
    };
  });
});

const dateRangeText = computed(() => {
  const start = selectedDate.value;
  if (period.value === 'day') return start;
  const [, endIso] = rangeUtc(selectedDate.value, 7);
  const end = new Date(new Date(endIso).getTime() - 24 * 3600_000);
  return `${start} - ${end.getFullYear()}-${pad(end.getMonth() + 1)}-${pad(end.getDate())}`;
});

const paged = computed(() => rows.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value));
const chartRows = computed(() => rows.value);

/** 依超標欄位推導異常原因文字——不猜過高/過低方向（沒有一起帶入目前的門檻設定值），只標出是哪個量測異常。 */
function abnormalReason(r: ChillerReportRow): string {
  if (r.isReturnTempExceeded) return '回水溫度異常';
  if (r.isSupplyTempExceeded) return '出水溫度異常';
  if (r.isTempDiffExceeded) return '溫度差異常';
  return '';
}

const chartRef = ref<HTMLDivElement | null>(null);
let chart: echarts.ECharts | null = null;

function renderChart() {
  if (!chartRef.value || chartRows.value.length === 0) return;
  // view 在報表／折線圖間切換時，chartRef 的 DOM 節點會被 v-if/v-else 整個換掉（不是同一個
  // element），但 echarts 實例還是舊的、綁著已經被移除的舊節點——不重新 init 的話 setOption
  // 會畫在看不到的舊節點上，畫面就一直停在第一次切換時的狀態。
  if (chart && chart.getDom() !== chartRef.value) {
    chart.dispose();
    chart = null;
  }
  if (!chart) chart = echarts.init(chartRef.value);
  const isWeek = period.value === 'week';
  const labels = chartRows.value.map((r) => {
    const [datePart, hourPart] = r.timestamp.split(' ');
    if (!isWeek) return hourPart ?? r.timestamp;
    return hourPart === '00:00' ? datePart.slice(5) : '';
  });
  const showPointLabel = !isWeek;
  const supply = chartRows.value.map((r) => r.supplyTemp);
  const ret = chartRows.value.map((r) => r.returnTemp);
  const all = [...supply, ...ret];
  const yMin = Math.floor(Math.min(...all) - 2);
  const yMax = Math.ceil(Math.max(...all) + 2);

  const supplyMarks = chartRows.value
    .map((r, i) => (r.isSupplyTempExceeded ? { coord: [i, r.supplyTemp], value: r.supplyTemp } : null))
    .filter(Boolean) as echarts.MarkPointComponentOption['data'];
  const returnMarks = chartRows.value
    .map((r, i) => (r.isReturnTempExceeded ? { coord: [i, r.returnTemp], value: r.returnTemp } : null))
    .filter(Boolean) as echarts.MarkPointComponentOption['data'];

  chart.setOption(
    {
      grid: { left: 44, right: 24, top: 24, bottom: 36 },
      tooltip: { trigger: 'axis' },
      legend: { data: ['出水溫度', '回水溫度'], right: 0, top: 0, textStyle: { color: '#64748B', fontSize: 12 } },
      xAxis: {
        type: 'category',
        data: labels,
        name: isWeek ? '日期' : '時',
        nameLocation: 'end',
        axisLine: { lineStyle: { color: '#CBD5E1' } },
        axisTick: { alignWithLabel: true },
        axisLabel: { color: '#94A3B8', fontSize: 11, interval: isWeek ? 0 : 'auto' },
      },
      yAxis: {
        type: 'value',
        name: '°C',
        min: yMin,
        max: yMax,
        axisLine: { show: false },
        splitLine: { lineStyle: { color: '#F1F5F9' } },
        axisLabel: { color: '#94A3B8', fontSize: 11 },
      },
      series: [
        {
          name: '出水溫度',
          type: 'line',
          smooth: true,
          data: supply,
          symbolSize: 5,
          lineStyle: { color: '#4C7DF0', width: 2 },
          itemStyle: { color: '#4C7DF0' },
          label: { show: showPointLabel, fontSize: 10, color: '#4C7DF0', formatter: '{c}°C' },
          markPoint: {
            symbol: 'triangle',
            symbolSize: 12,
            itemStyle: { color: '#FF4757' },
            label: { show: showPointLabel, color: '#FF4757', fontSize: 10, formatter: '{c}°C', position: 'bottom' },
            data: supplyMarks,
          },
        },
        {
          name: '回水溫度',
          type: 'line',
          smooth: true,
          data: ret,
          symbolSize: 5,
          lineStyle: { color: '#F5A623', width: 2 },
          itemStyle: { color: '#F5A623' },
          label: { show: showPointLabel, fontSize: 10, color: '#F5A623', formatter: '{c}°C' },
          markPoint: {
            symbol: 'triangle',
            symbolSize: 12,
            itemStyle: { color: '#FF4757' },
            label: { show: showPointLabel, color: '#FF4757', fontSize: 10, formatter: '{c}°C', position: 'top' },
            data: returnMarks,
          },
        },
      ],
    },
    true,
  );
}

watch([view, rows], async ([v]) => {
  if (v === 'chart') {
    await nextTick();
    renderChart();
  }
});
function onResize() {
  try {
    chart?.resize();
  } catch { /* ignore */ }
}
onMounted(() => window.addEventListener('resize', onResize));
onUnmounted(() => {
  window.removeEventListener('resize', onResize);
  chart?.dispose();
});

function reset() {
  period.value = 'day';
  selectedDate.value = new Date().toISOString().slice(0, 10);
  page.value = 1;
}
function exportExcel() {
  window.alert('已匯出冰水主機運轉報表 (Excel)');
}
</script>

<template>
  <AdminLayout page-title="冰水主機運轉報表" current-path="/admin/reports/chiller">
    <div class="flex flex-col gap-4">
      <!-- 篩選列 -->
      <div class="bg-white rounded-xl border border-[#E2E8F0] p-4 flex flex-wrap items-center gap-3">
        <label class="flex items-center gap-2 text-xs text-[#64748B]">
          設備名稱：
          <select
            v-model="selectedCode"
            class="border border-[#CBD5E1] rounded-lg px-3 py-1.5 text-xs text-[#334155] focus:outline-none focus:border-[#00D1B2]"
          >
            <option v-for="c in chillerList" :key="c.code" :value="c.code">{{ c.displayName }}</option>
          </select>
        </label>

        <div class="flex items-center gap-2 text-xs text-[#64748B]">
          <AdminDatePicker v-model="selectedDate" label="日期區間：" />
          <span v-if="period === 'week'" class="text-[#334155] font-medium whitespace-nowrap">～ {{ dateRangeText.split(' - ')[1] }}</span>
          <select v-model="period" class="border border-[#CBD5E1] rounded-lg px-2 py-1.5 text-xs text-[#334155] focus:outline-none focus:border-[#00D1B2]">
            <option value="day">日</option>
            <option value="week">週</option>
          </select>
        </div>

        <AdminButton variant="secondary" size="sm" @click="reset">重置</AdminButton>

        <div class="ml-auto flex items-center rounded-lg bg-[#F1F5F9] p-0.5">
          <button
            type="button"
            :class="['px-3 py-1.5 rounded-md text-xs font-semibold transition-colors', view === 'table' ? 'bg-white text-[#00D1B2] shadow-sm' : 'text-[#64748B]']"
            @click="view = 'table'"
          >
            報表
          </button>
          <button
            type="button"
            :class="['px-3 py-1.5 rounded-md text-xs font-semibold transition-colors', view === 'chart' ? 'bg-white text-[#00D1B2] shadow-sm' : 'text-[#64748B]']"
            @click="view = 'chart'"
          >
            折線圖
          </button>
        </div>
      </div>

      <!-- 資訊列 -->
      <div v-if="selectedChiller" class="bg-white rounded-xl border border-[#E2E8F0] px-4 py-3 flex flex-wrap items-center gap-x-5 gap-y-1 text-xs text-[#64748B]">
        <span>設備名稱：<strong class="text-[#334155]">{{ selectedChiller.displayName }}</strong></span>
        <span>設備編號：<strong class="text-[#334155]">{{ selectedChiller.code }}</strong></span>
        <span>安裝位置：<strong class="text-[#334155]">機房</strong></span>
        <span>日期區間：<strong class="text-[#334155]">{{ dateRangeText }}</strong></span>
        <AdminButton variant="primary" size="sm" class="ml-auto" @click="exportExcel">
          <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
          </svg>
          匯出 Excel
        </AdminButton>
      </div>

      <div v-if="loadError" class="p-3 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-sm">{{ loadError }}</div>
      <div v-else-if="alarmWarning" class="p-3 rounded-lg bg-[#FFFBEB] text-[#B45309] text-sm">{{ alarmWarning }}</div>

      <!-- 內容 -->
      <div v-if="!loadError" class="bg-white rounded-xl border border-[#E2E8F0] overflow-hidden">
        <div v-if="loading" class="p-6 text-center text-sm text-[#94A3B8]">載入中…</div>
        <template v-else-if="rows.length === 0">
          <EmptyState title="查無資料" description="所選設備與日期區間查無運轉報表資料（可能是這段時間沒有成功的讀值）。" />
        </template>

        <!-- 表格 -->
        <template v-else-if="view === 'table'">
          <div class="overflow-x-auto">
            <table class="w-full text-left text-[13px] text-[#334155]">
              <thead class="bg-[#F8FAFC] text-[#64748B] border-b border-[#E2E8F0]">
                <tr>
                  <th class="px-4 py-3 font-semibold">出水溫度</th>
                  <th class="px-4 py-3 font-semibold">回水溫度</th>
                  <th class="px-4 py-3 font-semibold">溫度差 ΔT (°C)</th>
                  <th class="px-4 py-3 font-semibold">水流量</th>
                  <th class="px-4 py-3 font-semibold">負載率</th>
                  <th class="px-4 py-3 font-semibold">累積運轉時數</th>
                  <th class="px-4 py-3 font-semibold whitespace-nowrap">日期時間</th>
                  <th class="px-4 py-3 font-semibold">運轉狀態</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="(r, i) in paged" :key="i" class="border-b border-[#F1F5F9] hover:bg-slate-50/70">
                  <td class="px-4 py-3 font-tabular" :class="{ 'text-[#FF4757] font-bold': r.isSupplyTempExceeded }">{{ r.supplyTemp.toFixed(1) }} °C</td>
                  <td class="px-4 py-3 font-tabular" :class="{ 'text-[#FF4757] font-bold': r.isReturnTempExceeded }">{{ r.returnTemp.toFixed(1) }} °C</td>
                  <td class="px-4 py-3 font-tabular" :class="{ 'text-[#FF4757] font-bold': r.isTempDiffExceeded }">{{ r.tempDiff.toFixed(1) }} °C</td>
                  <td class="px-4 py-3 font-tabular" title="供應商尚未提供水流量量測值">--</td>
                  <td class="px-4 py-3 font-tabular">{{ r.loadRate !== null ? `${Math.round(r.loadRate)} %` : '--' }}</td>
                  <td class="px-4 py-3 font-tabular text-[#64748B]">{{ r.cumulativeHours !== null ? `${r.cumulativeHours.toLocaleString()} hrs` : '--' }}</td>
                  <td class="px-4 py-3 font-tabular text-[#64748B] whitespace-nowrap">{{ r.timestamp }}</td>
                  <td class="px-4 py-3"><AdminStatusBadge :status="r.status" :reason="abnormalReason(r)" /></td>
                </tr>
              </tbody>
            </table>
          </div>
          <AdminPagination :total="rows.length" v-model:page="page" v-model:page-size="pageSize" />
        </template>

        <!-- 折線圖 -->
        <template v-else>
          <div class="p-5">
            <div ref="chartRef" class="w-full h-[360px]" />
          </div>
        </template>
      </div>
    </div>
  </AdminLayout>
</template>
