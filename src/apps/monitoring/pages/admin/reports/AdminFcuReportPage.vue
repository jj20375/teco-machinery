<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick, watch } from 'vue';
import * as echarts from 'echarts';
import { useQuery } from '@tanstack/vue-query';
import AdminLayout from '../_components/AdminLayout.vue';
import AdminButton from '../_components/AdminButton.vue';
import AdminDatePicker from '../_components/AdminDatePicker.vue';
import AdminStatusBadge from '../_components/AdminStatusBadge.vue';
import AdminPagination from '../_components/AdminPagination.vue';
import AdminMultiSelect from '../_components/AdminMultiSelect.vue';
import EmptyState from '../_components/EmptyState.vue';
import { ApiError } from '../_services/auth-service';
import { listFcusApi, listAlarmsApi, getFcuHistoryApi, fcuFanSpeedLabel, fcuModeLabel } from '../_services/hvac-service';
import { fcuExceededInWindow } from '../_services/threshold-service';
import type { FcuReportRow } from '../_types/admin-types';

const floor = ref<'ALL' | 'B1' | 'B2'>('ALL');
const selectedId = ref<number | null>(null);
const period = ref<'day' | 'week'>('day');
/** 預設看今天，不能寫死日期字串——寫死的日期一過就會讓使用者一打開頁面就看到「查無資料」，
 * 以為系統壞了，其實只是預設日期已經跟今天脫節。 */
const selectedDate = ref(new Date().toISOString().slice(0, 10));
const view = ref<'table' | 'chart'>('table');

const STATUS_OPTS = [
  { value: 'ALL', label: '全部' },
  { value: 'RUNNING', label: '運轉中' },
  { value: 'STOPPED', label: '停止' },
  { value: 'ABNORMAL', label: '異常' },
];
const statusFilter = ref<string[]>(['ALL']);

const fcusQuery = useQuery({ queryKey: ['fcu-report-devices'], queryFn: () => listFcusApi() });
const deviceOptions = computed(() => {
  const list = fcusQuery.data.value ?? [];
  const filtered = floor.value === 'ALL' ? list : list.filter((f) => f.floor === floor.value);
  return filtered.map((f) => ({
    id: f.id,
    code: f.zoneCode ?? `${f.floor}-${f.id}`,
    name: f.displayName ?? f.zoneCode ?? `FCU-${f.id}`,
    floor: f.floor,
  }));
});
watch(deviceOptions, (opts) => {
  if (opts.length > 0 && !opts.some((o) => o.id === selectedId.value)) selectedId.value = opts[0].id;
});
const selectedDevice = computed(() => deviceOptions.value.find((o) => o.id === selectedId.value) ?? null);

const page = ref(1);
const pageSize = ref(20);

function rangeUtc(dateStr: string, days: number): [string, string] {
  const start = new Date(`${dateStr}T00:00:00`);
  const end = new Date(start.getTime() + days * 24 * 3600_000);
  return [start.toISOString(), end.toISOString()];
}
const fromIso = computed(() => rangeUtc(selectedDate.value, period.value === 'day' ? 1 : 7)[0]);
const toIso = computed(() => rangeUtc(selectedDate.value, period.value === 'day' ? 1 : 7)[1]);

const historyQuery = useQuery({
  queryKey: ['fcu-report-history', selectedId, fromIso, toIso],
  queryFn: () => getFcuHistoryApi(selectedId.value as number, fromIso.value, toIso.value, '1h'),
  enabled: computed(() => selectedId.value !== null),
});
const alarmsQuery = useQuery({
  queryKey: ['fcu-report-alarms', fromIso, toIso],
  queryFn: () => listAlarmsApi('all', fromIso.value, toIso.value),
  enabled: computed(() => selectedId.value !== null),
});

const loading = computed(() => historyQuery.isPending.value || fcusQuery.isPending.value);
const loadError = computed(() => {
  const err = historyQuery.error.value ?? fcusQuery.error.value;
  if (!err) return '';
  return err instanceof ApiError && err.status === 403
    ? '目前登入的帳號沒有「FCU管理」查看權限，無法查看報表。'
    : err instanceof Error
      ? err.message
      : '載入報表失敗，請稍後再試。';
});
const alarmWarning = computed(() => {
  const err = alarmsQuery.error.value;
  if (!err) return '';
  return err instanceof ApiError && err.status === 403
    ? '目前登入的帳號沒有「告警」查看權限，下表的異常判斷可能不準確。'
    : '有效告警清單載入失敗，下表的異常判斷可能不準確。';
});

function pad(n: number): string {
  return String(n).padStart(2, '0');
}
function formatLocal(ts: string): string {
  const d = new Date(ts);
  return `${d.getFullYear()}/${pad(d.getMonth() + 1)}/${pad(d.getDate())} ${pad(d.getHours())}:00`;
}

const rows = computed<FcuReportRow[]>(() => {
  if (!selectedDevice.value) return [];
  const fcuId = selectedDevice.value.id;
  const alarms = alarmsQuery.data.value ?? [];
  return (historyQuery.data.value ?? []).map((p) => {
    const windowEnd = new Date(new Date(p.ts).getTime() + 3600_000).toISOString();
    const isExceeded = fcuExceededInWindow(fcuId, alarms, p.ts, windowEnd);
    const isRunning = (p.onMinutes ?? 0) > 0;
    return {
      timestamp: formatLocal(p.ts),
      floor: selectedDevice.value!.floor as FcuReportRow['floor'],
      deviceName: selectedDevice.value!.name,
      deviceCode: selectedDevice.value!.code,
      location: selectedDevice.value!.floor,
      roomTemp: p.temperature,
      fanSpeed: p.fanSpeed !== null ? fcuFanSpeedLabel(p.fanSpeed) : null,
      mode: p.mode !== null ? fcuModeLabel(p.mode) : null,
      status: isExceeded ? 'ABNORMAL' : isRunning ? 'RUNNING' : 'STOPPED',
      isExceeded,
    };
  });
});

const filteredRows = computed(() =>
  statusFilter.value.includes('ALL') ? rows.value : rows.value.filter((r) => statusFilter.value.includes(r.status)),
);
const paged = computed(() => filteredRows.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value));
const chartRows = computed(() => rows.value);

const dateRangeText = computed(() => {
  const start = selectedDate.value;
  if (period.value === 'day') return start;
  const [, endIso] = rangeUtc(selectedDate.value, 7);
  const end = new Date(new Date(endIso).getTime() - 24 * 3600_000);
  return `${start} - ${end.getFullYear()}-${pad(end.getMonth() + 1)}-${pad(end.getDate())}`;
});

const chartRef = ref<HTMLDivElement | null>(null);
let chart: echarts.ECharts | null = null;

function renderChart() {
  if (!chartRef.value || chartRows.value.length === 0) return;
  if (!chart) chart = echarts.init(chartRef.value);
  const isWeek = period.value === 'week';
  const labels = chartRows.value.map((r) => {
    const [datePart, hourPart] = r.timestamp.split(' ');
    if (!isWeek) return hourPart ?? r.timestamp;
    return hourPart === '00:00' ? datePart.slice(5) : '';
  });
  const showPointLabel = !isWeek;
  const room = chartRows.value.map((r) => r.roomTemp);
  const roomVals = room.filter((v): v is number => v !== null);
  const yMin = roomVals.length ? Math.floor(Math.min(...roomVals) - 2) : 0;
  const yMax = roomVals.length ? Math.ceil(Math.max(...roomVals) + 2) : 30;
  const markPoints = chartRows.value
    .map((r, i) => (r.isExceeded && r.roomTemp !== null ? { coord: [i, r.roomTemp], value: r.roomTemp } : null))
    .filter(Boolean) as echarts.MarkPointComponentOption['data'];

  chart.setOption(
    {
      grid: { left: 44, right: 24, top: 24, bottom: 36 },
      tooltip: { trigger: 'axis' },
      legend: { data: ['室內溫度'], right: 0, top: 0, textStyle: { color: '#64748B', fontSize: 12 } },
      xAxis: {
        type: 'category',
        data: labels,
        name: isWeek ? '日期' : '時',
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
          name: '室內溫度',
          type: 'line',
          smooth: true,
          data: room,
          connectNulls: true,
          symbolSize: 5,
          lineStyle: { color: '#4C7DF0', width: 2 },
          itemStyle: { color: '#4C7DF0' },
          label: { show: showPointLabel, fontSize: 10, color: '#4C7DF0', formatter: '{c}°C' },
          markPoint: {
            symbol: 'triangle',
            symbolSize: 12,
            itemStyle: { color: '#FF4757' },
            label: { show: showPointLabel, color: '#FF4757', fontSize: 10, formatter: '{c}°C', position: 'top' },
            data: markPoints,
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
  floor.value = 'ALL';
  period.value = 'day';
  selectedDate.value = new Date().toISOString().slice(0, 10);
  statusFilter.value = ['ALL'];
  page.value = 1;
}
function exportExcel() {
  window.alert('已匯出 FCU 運轉報表 (Excel)');
}
</script>

<template>
  <AdminLayout page-title="FCU運轉報表" current-path="/admin/reports/fcu">
    <div class="flex flex-col gap-4">
      <!-- 篩選列 -->
      <div class="bg-white rounded-xl border border-[#E2E8F0] p-4 flex flex-wrap items-center gap-3">
        <label class="flex items-center gap-2 text-xs text-[#64748B]">
          樓層：
          <select v-model="floor" class="border border-[#CBD5E1] rounded-lg px-3 py-1.5 text-xs text-[#334155] focus:outline-none focus:border-[#00D1B2]">
            <option value="ALL">全部</option>
            <option value="B1">B1</option>
            <option value="B2">B2</option>
          </select>
        </label>

        <label class="flex items-center gap-2 text-xs text-[#64748B]">
          設備名稱：
          <select v-model.number="selectedId" class="border border-[#CBD5E1] rounded-lg px-3 py-1.5 text-xs text-[#334155] focus:outline-none focus:border-[#00D1B2]">
            <option v-for="d in deviceOptions" :key="d.id" :value="d.id">{{ d.name }}（{{ d.code }}）</option>
          </select>
        </label>

        <AdminMultiSelect label="設備狀態" :options="STATUS_OPTS" v-model="statusFilter" />

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
          <button type="button" :class="['px-3 py-1.5 rounded-md text-xs font-semibold', view === 'table' ? 'bg-white text-[#00D1B2] shadow-sm' : 'text-[#64748B]']" @click="view = 'table'">報表</button>
          <button type="button" :class="['px-3 py-1.5 rounded-md text-xs font-semibold', view === 'chart' ? 'bg-white text-[#00D1B2] shadow-sm' : 'text-[#64748B]']" @click="view = 'chart'">折線圖</button>
        </div>
      </div>

      <!-- 資訊列 -->
      <div v-if="selectedDevice" class="bg-white rounded-xl border border-[#E2E8F0] px-4 py-3 flex flex-wrap items-center gap-x-5 gap-y-1 text-xs text-[#64748B]">
        <span>設備名稱：<strong class="text-[#334155]">{{ selectedDevice.name }}</strong></span>
        <span>設備編號：<strong class="text-[#334155]">{{ selectedDevice.code }}</strong></span>
        <span>安裝位置：<strong class="text-[#334155]">{{ selectedDevice.floor }}</strong></span>
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
        <template v-else-if="filteredRows.length === 0">
          <EmptyState title="查無資料" description="所選設備與日期區間查無 FCU 運轉報表資料（可能是這段時間沒有成功的讀值）。" />
        </template>

        <template v-else-if="view === 'table'">
          <div class="overflow-x-auto">
            <table class="w-full text-left text-[13px] text-[#334155]">
              <thead class="bg-[#F8FAFC] text-[#64748B] border-b border-[#E2E8F0]">
                <tr>
                  <th class="px-4 py-3 font-semibold">室內溫度</th>
                  <th class="px-4 py-3 font-semibold">風速</th>
                  <th class="px-4 py-3 font-semibold">運轉模式</th>
                  <th class="px-4 py-3 font-semibold whitespace-nowrap">日期時間</th>
                  <th class="px-4 py-3 font-semibold">狀態</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="(r, i) in paged" :key="i" class="border-b border-[#F1F5F9] hover:bg-slate-50/70">
                  <td class="px-4 py-3 font-tabular" :class="{ 'text-[#FF4757] font-bold': r.isExceeded }">
                    {{ r.roomTemp !== null ? `${r.roomTemp.toFixed(1)} °C` : '--' }}
                  </td>
                  <td class="px-4 py-3">{{ r.fanSpeed ?? '--' }}</td>
                  <td class="px-4 py-3">{{ r.mode ?? '--' }}</td>
                  <td class="px-4 py-3 font-tabular text-[#64748B] whitespace-nowrap">{{ r.timestamp }}</td>
                  <td class="px-4 py-3"><AdminStatusBadge :status="r.status" :reason="r.isExceeded ? '室內溫度異常' : ''" /></td>
                </tr>
              </tbody>
            </table>
          </div>
          <AdminPagination :total="filteredRows.length" v-model:page="page" v-model:page-size="pageSize" />
        </template>

        <template v-else>
          <div class="p-5">
            <div ref="chartRef" class="w-full h-[360px]" />
          </div>
        </template>
      </div>
    </div>
  </AdminLayout>
</template>
