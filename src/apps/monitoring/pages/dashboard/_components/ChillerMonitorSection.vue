<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch, nextTick } from 'vue';
import * as echarts from 'echarts';
import DeviceThumbnail from './DeviceThumbnail.vue';
import type { ChillerData, HourlyUsageTrend, HourlyTempTrend } from '../_types/dashboard-types';
import { formatMeasure } from '../../admin/_services/hvac-service';

const props = defineProps<{
  chillers: ChillerData[];
  usageTrend: HourlyUsageTrend | null;
  tempTrend: HourlyTempTrend | null;
}>();

const barChartRef = ref<HTMLDivElement | null>(null);
const lineChartRef = ref<HTMLDivElement | null>(null);
let barChart: echarts.ECharts | null = null;
let lineChart: echarts.ECharts | null = null;

// 負載率環：低=青綠、中=橘、高=紅
function loadColor(rate: number): string {
  if (rate <= 60) return '#00D1B2';
  if (rate <= 85) return '#F5A623';
  return '#FF4D4F';
}

const METRICS = [
  { key: 'supplyTemp', label: '出水溫度', unit: '°C', digits: 1, flag: 'isSupplyTempExceeded' },
  { key: 'returnTemp', label: '回水溫度', unit: '°C', digits: 1, flag: 'isReturnTempExceeded' },
  { key: 'tempDiff', label: '溫度差', unit: '°C', digits: 1, flag: 'isTempDiffExceeded' },
] as const;

function initBar() {
  if (!barChartRef.value || !props.usageTrend) return;
  barChart?.dispose();
  barChart = echarts.init(barChartRef.value);
  barChart.setOption({
    backgroundColor: 'transparent',
    tooltip: { trigger: 'axis', backgroundColor: '#161B22', borderColor: '#30363D', textStyle: { color: '#F0F6FC', fontSize: 12 }, axisPointer: { type: 'shadow' } },
    legend: { data: ['B1 樓', 'B2 樓'], textStyle: { color: '#8B949E', fontSize: 11 }, top: 0, right: 0, itemWidth: 10, itemHeight: 10 },
    grid: { left: 2, right: 8, bottom: 2, top: 26, containLabel: true },
    xAxis: {
      type: 'category',
      data: props.usageTrend.hours.map((h, i) => (i % 3 === 0 ? h.slice(0, 2) : '')),
      axisLine: { lineStyle: { color: '#30363D' } }, axisTick: { show: false },
      axisLabel: { color: '#8B949E', fontSize: 10 },
    },
    yAxis: { type: 'value', max: 90, interval: 45, splitLine: { lineStyle: { color: '#21262D', type: 'dashed' } }, axisLabel: { color: '#8B949E', fontSize: 10 } },
    series: [
      { name: 'B1 樓', type: 'bar', stack: 't', data: props.usageTrend.b1Count, itemStyle: { color: '#00D1B2' }, barWidth: '55%' },
      { name: 'B2 樓', type: 'bar', stack: 't', data: props.usageTrend.b2Count, itemStyle: { color: '#38BDF8', borderRadius: [2, 2, 0, 0] }, barWidth: '55%' },
    ],
  });
}

function initLine() {
  if (!lineChartRef.value || !props.tempTrend) return;
  lineChart?.dispose();
  lineChart = echarts.init(lineChartRef.value);
  const area = (c: string) => ({
    color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
      { offset: 0, color: `${c}40` }, { offset: 1, color: `${c}00` },
    ]),
  });
  lineChart.setOption({
    backgroundColor: 'transparent',
    tooltip: { trigger: 'axis', backgroundColor: '#161B22', borderColor: '#30363D', textStyle: { color: '#F0F6FC', fontSize: 12 }, valueFormatter: (v) => `${v} °C` },
    legend: { data: ['B1 溫度', 'B2 溫度'], textStyle: { color: '#8B949E', fontSize: 11 }, top: 0, right: 0, itemWidth: 12, itemHeight: 3 },
    grid: { left: 2, right: 8, bottom: 2, top: 26, containLabel: true },
    xAxis: {
      type: 'category', boundaryGap: false,
      data: props.tempTrend.hours.map((h, i) => (i % 3 === 0 ? h.slice(0, 2) : '')),
      axisLine: { lineStyle: { color: '#30363D' } }, axisTick: { show: false },
      axisLabel: { color: '#8B949E', fontSize: 10 },
    },
    yAxis: { type: 'value', min: 20, max: 28, interval: 4, splitLine: { lineStyle: { color: '#21262D', type: 'dashed' } }, axisLabel: { color: '#8B949E', fontSize: 10, formatter: '{value}°C' } },
    series: [
      { name: 'B1 溫度', type: 'line', smooth: true, showSymbol: false, data: props.tempTrend.b1Temp, lineStyle: { color: '#00D1B2', width: 2 }, areaStyle: area('#00D1B2') },
      { name: 'B2 溫度', type: 'line', smooth: true, showSymbol: false, data: props.tempTrend.b2Temp, lineStyle: { color: '#38BDF8', width: 2 }, areaStyle: area('#38BDF8') },
    ],
  });
}

function handleResize() { barChart?.resize(); lineChart?.resize(); }
watch(() => props.usageTrend, () => nextTick(initBar), { deep: true });
watch(() => props.tempTrend, () => nextTick(initLine), { deep: true });
onMounted(() => { nextTick(() => { initBar(); initLine(); }); window.addEventListener('resize', handleResize); });
onUnmounted(() => { window.removeEventListener('resize', handleResize); barChart?.dispose(); lineChart?.dispose(); });
</script>

<template>
  <div class="flex flex-col gap-3 h-full min-h-0">
    <!-- 冰水主機雙卡 -->
    <div
      v-for="chiller in chillers"
      :key="chiller.id"
      class="bg-[#161B22] border border-[#30363D] rounded-xl px-3.5 pt-2.5 pb-1.5 shrink-0"
    >
      <!-- 名稱 / 代碼 / 狀態 -->
      <div class="flex items-start justify-between">
        <div class="leading-tight">
          <h3 class="text-lg font-bold text-[#F0F6FC]">{{ chiller.name }}</h3>
          <span class="text-xs text-[#8B949E] font-tabular tracking-wider">{{ chiller.code }}</span>
        </div>
        <span
          class="flex items-center gap-1.5 text-sm font-medium"
          :class="chiller.status === 'RUNNING' ? 'text-[#00D1B2]' : chiller.status === 'ABNORMAL' ? 'text-[#FF4D4F]' : 'text-[#8B949E]'"
        >
          <span class="w-1.5 h-1.5 rounded-full bg-current" />
          {{ chiller.status === 'RUNNING' ? '運轉中' : chiller.status === 'ABNORMAL' ? '異常' : chiller.status === 'STOPPED' ? '停止' : chiller.status === 'OFFLINE' ? '離線' : '待保養' }}
        </span>
      </div>

      <!-- 縮圖 + 負載環 -->
      <div class="flex items-center justify-between py-1.5">
        <DeviceThumbnail kind="chiller" :size="60" />
        <div class="relative w-[68px] h-[68px] grid place-items-center">
          <svg class="w-full h-full -rotate-90" viewBox="0 0 36 36">
            <path class="text-[#30363D]" stroke-width="3.5" stroke="currentColor" fill="none"
              d="M18 2.0845 a 15.9155 15.9155 0 0 1 0 31.831 a 15.9155 15.9155 0 0 1 0 -31.831" />
            <path stroke-dasharray="100, 100" :stroke-dashoffset="100 - (chiller.loadRate ?? 0)" stroke-linecap="round"
              stroke-width="3.5" :stroke="loadColor(chiller.loadRate ?? 0)" fill="none"
              d="M18 2.0845 a 15.9155 15.9155 0 0 1 0 31.831 a 15.9155 15.9155 0 0 1 0 -31.831"
              class="transition-all duration-700" />
          </svg>
          <div class="absolute inset-0 flex flex-col items-center justify-center">
            <span class="text-base font-extrabold font-tabular text-[#F0F6FC]">{{ chiller.loadRate === null ? '--' : `${chiller.loadRate}%` }}</span>
            <span class="text-[10px] text-[#8B949E]">負載</span>
          </div>
        </div>
      </div>

      <!-- 指標列（無冒號、3 字標籤分散對齊、超標紅字） -->
      <div class="text-sm">
        <div
          v-for="m in METRICS"
          :key="m.key"
          class="flex items-center justify-between py-1 border-t border-[#21262D]"
        >
          <span class="text-[#C9D1D9] w-[4.5em] text-justify" style="text-align-last: justify;">{{ m.label }}</span>
          <span>
            <b
              class="font-tabular font-bold"
              :class="chiller[m.flag] ? 'text-[#FF4D4F]' : 'text-[#F0F6FC]'"
            >{{ formatMeasure(chiller[m.key], m.digits) }}</b>
            <span class="text-[11px] text-[#8B949E] ml-1">{{ m.unit }}</span>
          </span>
        </div>
        <!-- 設計稿要求的欄位，但供應商 SDK 沒有流量量測值（只有流量異常警報旗標），先保留欄位顯示 --，
             不編數字；單位（LPM 或 m³/h）也等拿到資料再定。 -->
        <div class="flex items-center justify-between py-1 border-t border-[#21262D]" title="供應商尚未提供水流量量測值">
          <span class="text-[#C9D1D9] w-[4.5em] text-justify" style="text-align-last: justify;">水流量</span>
          <b class="font-tabular font-bold text-[#F0F6FC]">--</b>
        </div>
        <div class="flex items-center justify-between py-1 border-t border-[#21262D]">
          <span class="text-[#C9D1D9] w-[4.5em] text-justify" style="text-align-last: justify;">累積運轉</span>
          <span>
            <b class="font-tabular font-bold text-[#F0F6FC]">{{ chiller.cumulativeHours === null ? '--' : chiller.cumulativeHours.toLocaleString() }}</b>
            <span class="text-[11px] text-[#8B949E] ml-1">小時</span>
          </span>
        </div>
      </div>
    </div>

    <!-- 每小時 FCU 啟用數 -->
    <div class="bg-[#161B22] border border-[#30363D] rounded-xl px-3 py-2.5 flex flex-col flex-1 min-h-[120px]">
      <span class="text-sm font-bold text-[#F0F6FC] mb-1">每小時 FCU 啟用數</span>
      <div ref="barChartRef" class="w-full flex-1 min-h-0" />
    </div>

    <!-- 每小時溫度變化 -->
    <div class="bg-[#161B22] border border-[#30363D] rounded-xl px-3 py-2.5 flex flex-col flex-1 min-h-[120px]">
      <span class="text-sm font-bold text-[#F0F6FC] mb-1">每小時溫度變化</span>
      <div ref="lineChartRef" class="w-full flex-1 min-h-0" />
    </div>
  </div>
</template>
