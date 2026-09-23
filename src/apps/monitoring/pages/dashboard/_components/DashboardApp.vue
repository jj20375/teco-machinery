<script setup lang="ts">
import { defineAsyncComponent, ref, onMounted, onUnmounted } from 'vue';
import DashboardHeader from './DashboardHeader.vue';
// 背景動畫延後載入：three.js 不該擋看板資料的初始渲染，載入失敗也不影響監控功能。
const RetroGridBackground = defineAsyncComponent(() => import('./RetroGridBackground.vue'));
import ChillerMonitorSection from './ChillerMonitorSection.vue';
import FloorHeatmapSection from './FloorHeatmapSection.vue';
import FcuMatrixSection from './FcuMatrixSection.vue';
import AlarmModal from './AlarmModal.vue';

import type {
  FloorId, DashboardOverview, ChillerData, HourlyUsageTrend, HourlyTempTrend,
  FloorHeatmapData, AlarmItem, FcuItem,
} from '../_types/dashboard-types';

import {
  getDashboardOverviewApi, getChillersDataApi, getHourlyFcuUsageTrendApi, getHourlyTempTrendApi,
  getFloorHeatmapApi, listRealtimeAlarmsApi, getFcuStatusMatrixApi,
} from '../_services/dashboard-service';

const ROTATE_SECONDS = 30;

// 戰情室設計稿基準為 1920×1080；小螢幕（筆電、平板）改用「整版縮放」而非裁切，
// 確保下方圖表在任何視窗尺寸都看得到，不會被 overflow-hidden 吃掉。
const DESIGN_WIDTH = 1920;
const DESIGN_HEIGHT = 1080;
const scale = ref(1);
// 置中用的位移：不靠 flex 置中，因為未縮放的 1920px 版面比視窗寬時，
// flex 對溢出項目的對齊行為會把整塊往左推。自己算 offset 才是確定的。
const offsetX = ref(0);
const offsetY = ref(0);
function updateScale() {
  const { innerWidth: vw, innerHeight: vh } = window;
  scale.value = Math.min(vw / DESIGN_WIDTH, vh / DESIGN_HEIGHT);
  offsetX.value = (vw - DESIGN_WIDTH * scale.value) / 2;
  offsetY.value = (vh - DESIGN_HEIGHT * scale.value) / 2;
}

const currentFloor = ref<FloorId>('B1');
const floorLocked = ref(false);   // 手動點選均溫膠囊後鎖定，暫停輪播
let rotationInterval: number | null = null;
const countdown = ref(ROTATE_SECONDS);   // 距下次自動切換樓層的秒數，顯示給使用者看輪詢節奏

const overview = ref<DashboardOverview | null>(null);
const chillers = ref<ChillerData[]>([]);
const usageTrend = ref<HourlyUsageTrend | null>(null);
const tempTrend = ref<HourlyTempTrend | null>(null);
const heatmapData = ref<FloorHeatmapData | null>(null);
const alarms = ref<AlarmItem[]>([]);
const fcuByFloor = ref<Record<FloorId, FcuItem[]>>({ B1: [], B2: [] });

const isAlarmModalOpen = ref(false);

async function loadDashboardData() {
  const [overviewData, chillersData, usageData, tempData, heatmap, alarmList, fcuMatrix] = await Promise.all([
    getDashboardOverviewApi(),
    getChillersDataApi(),
    getHourlyFcuUsageTrendApi(),
    getHourlyTempTrendApi(),
    getFloorHeatmapApi(currentFloor.value),
    listRealtimeAlarmsApi(),
    getFcuStatusMatrixApi(),
  ]);
  overview.value = overviewData;
  chillers.value = chillersData;
  usageTrend.value = usageData;
  tempTrend.value = tempData;
  heatmapData.value = heatmap;
  alarms.value = alarmList;
  fcuByFloor.value = fcuMatrix;
}

async function setFloor(next: FloorId) {
  currentFloor.value = next;
  countdown.value = ROTATE_SECONDS;
  heatmapData.value = await getFloorHeatmapApi(next);
}

// 點均溫膠囊：切到該樓層並鎖定；再點目前樓層則解除鎖定、恢復輪播。
function handleFloorBadge(floor: FloorId) {
  if (floorLocked.value && floor === currentFloor.value) {
    floorLocked.value = false;
    countdown.value = ROTATE_SECONDS;
    return;
  }
  floorLocked.value = true;
  void setFloor(floor);
}

function startRotationTimer() {
  if (rotationInterval) clearInterval(rotationInterval);
  rotationInterval = window.setInterval(() => {
    if (floorLocked.value) return;
    countdown.value -= 1;
    if (countdown.value <= 0) {
      countdown.value = ROTATE_SECONDS;
      void setFloor(currentFloor.value === 'B1' ? 'B2' : 'B1');
    }
  }, 1000);
}

function handleOpenAlarmModal() {
  isAlarmModalOpen.value = true;
}

// 即時數值微幅跳動
let liveTicker: number | null = null;
function startLiveSimulation() {
  liveTicker = window.setInterval(() => {
    chillers.value.forEach((c) => {
      const delta = (Math.random() - 0.5) * 0.2;
      c.supplyTemp = Number((c.supplyTemp + delta).toFixed(1));
      c.tempDiff = Number((c.returnTemp - c.supplyTemp).toFixed(1));
    });
  }, 5000);
}

onMounted(async () => {
  updateScale();
  window.addEventListener('resize', updateScale);
  await loadDashboardData();
  startRotationTimer();
  startLiveSimulation();
});
onUnmounted(() => {
  window.removeEventListener('resize', updateScale);
  if (rotationInterval) clearInterval(rotationInterval);
  if (liveTicker) clearInterval(liveTicker);
});
</script>

<template>
  <!-- 外層：填滿實際視窗，置中容納縮放後的看板；小螢幕整版縮小顯示，不裁切、不需捲動 -->
  <div class="relative w-screen h-screen overflow-hidden bg-[#0D1117]">
    <!-- 復古格線背景：填滿實際視窗；卡片都是不透明底色，只有縫隙、頁首、縮放留白看得到 -->
    <RetroGridBackground />
    <div
      class="absolute top-0 left-0 origin-top-left z-10"
      :style="{
        width: `${DESIGN_WIDTH}px`,
        height: `${DESIGN_HEIGHT}px`,
        transform: `translate(${offsetX}px, ${offsetY}px) scale(${scale})`,
      }"
    >
      <!-- 這層本來鋪滿 1920×1080 的不透明底色，會把背景動畫整個蓋住；
           拿掉之後 header／每張卡片各自的 bg-[#161B22]／[#0D1117] 還在，內容不會變透明，
           只有卡片間的縫隙跟 main 的 padding 會透出背景動畫。 -->
      <div class="w-full h-full text-[#F0F6FC] flex flex-col font-sans select-none overflow-hidden">
        <DashboardHeader />

        <!-- 固定三欄：450 / 中間彈性 / 480（1920×1080 看板設計基準） -->
        <main class="flex-1 min-h-0 p-5 grid gap-4 overflow-hidden" style="grid-template-columns: 450px minmax(0, 1fr) 480px;">
          <section class="h-full min-h-0 overflow-hidden">
            <ChillerMonitorSection :chillers="chillers" :usage-trend="usageTrend" :temp-trend="tempTrend" />
          </section>

          <section class="h-full min-h-0 overflow-hidden">
            <FloorHeatmapSection
              :current-floor="currentFloor"
              :floor-locked="floorLocked"
              :countdown="countdown"
              :heatmap-data="heatmapData"
              :alarms="alarms"
              :b1-avg-temp="overview?.b1AvgTemp ?? 24.3"
              :b2-avg-temp="overview?.b2AvgTemp ?? 24.3"
              @floor-badge="handleFloorBadge"
              @open-alarm-modal="handleOpenAlarmModal"
            />
          </section>

          <section class="h-full min-h-0 overflow-hidden">
            <FcuMatrixSection :overview="overview" :fcu-by-floor="fcuByFloor" />
          </section>
        </main>
      </div>
    </div>

    <AlarmModal v-model:open="isAlarmModalOpen" :alarms="alarms" @close="isAlarmModalOpen = false" />
  </div>
</template>
