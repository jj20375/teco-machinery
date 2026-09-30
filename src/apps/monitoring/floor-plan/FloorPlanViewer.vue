<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import FloorPlanCanvas from './FloorPlanCanvas.vue';
import {
  areasOf, SAVE_EVENT, getFloorEquipmentApi, placementFromDto,
  equipmentColor, equipmentStatus, type Equipment, type Placement,
} from './floor-plan';
import { getFloorPlacementsApi, getPublicFloorPlacementsApi } from '../pages/admin/_services/floor-plan-service';
import type { FloorId } from '../pages/dashboard/_types/dashboard-types';

// embedded：嵌在前台戰情室裡，只讀不導向後台編輯。前台頁面（`/`）刻意設計成不用登入的
// 大廳螢幕（DashboardLayout.astro 沒有登入檢查），所以 embedded=true 時一併切換成打
// /api/v1/public/* 的公開唯讀端點，不然沒登入的訪客會被 authorizedJsonApi 的 401 處理
// 導去登入頁——這正是先前踩過的真實 bug（見 backend/README.md）。
// zoomable：開放滾輪平移／Ctrl+滾輪或雙指縮放，跟後台空間設備配置頁用同一套
// FloorPlanCanvas 縮放邏輯；控制項自帶在元件內，呼叫端不用另外接線。
const props = withDefaults(
  defineProps<{ floor: FloorId; compact?: boolean; embedded?: boolean; zoomable?: boolean }>(),
  { compact: false, embedded: false, zoomable: false },
);
const equipment = ref<Equipment[]>([]);
const placements = ref<Placement[]>([]);
const error = ref('');
const loading = ref(true);
const selectedId = ref('');
const selected = computed(() => equipment.value.find((e) => e.id === selectedId.value));
const selectedArea = computed(() => areasOf(props.floor).find((a) => a.id === placements.value.find((p) => p.deviceId === selectedId.value)?.areaId)?.id);

const canvasRef = ref<InstanceType<typeof FloorPlanCanvas> | null>(null);
const zoomView = ref({ scale: 1, atDefault: true });

async function refreshLayout() {
  try {
    const saved = props.embedded
      ? await getPublicFloorPlacementsApi(props.floor)
      : await getFloorPlacementsApi(props.floor);
    // 設備被停用或刪除後，舊配置會指到不存在的設備；濾掉就好，不要整張圖不顯示。
    const known = new Set(equipment.value.map((e) => e.id));
    placements.value = saved.placements.map(placementFromDto).filter((p) => known.has(p.deviceId));
    if (!placements.value.some((p) => p.deviceId === selectedId.value)) selectedId.value = '';
    error.value = '';
  } catch { placements.value = []; selectedId.value = ''; error.value = '配置讀取失敗，請至空間設備配置頁面確認。'; }
}
async function load() {
  loading.value = true;
  selectedId.value = '';
  try {
    equipment.value = await getFloorEquipmentApi(props.floor, props.embedded);
    await refreshLayout();
  } catch { error.value = '設備資料讀取失敗。'; }
  finally { loading.value = false; }
}
// 設備狀態（運轉、異常、離線）每 5 秒靜默更新：不設 loading、不清選取，避免地圖閃爍。
// 失敗就保留上一次的資料。沒有這個更新的話，開著不關的畫面上狀態顏色會一直停在載入當下。
async function refreshEquipment() {
  try { equipment.value = await getFloorEquipmentApi(props.floor, props.embedded); } catch { /* 沿用舊資料 */ }
}
let equipmentTimer: number | null = null;
// SAVE_EVENT 只在同一個分頁內有效（後台存檔後讓同頁的唯讀檢視立即更新）。
// 換成後端保存之後，跨分頁/跨裝置的同步靠重新載入頁面或下次進頁時重讀，不再監聽 storage 事件。
onMounted(() => {
  window.addEventListener(SAVE_EVENT, refreshLayout);
  load();
  equipmentTimer = window.setInterval(refreshEquipment, 5000);
});
watch(() => props.floor, load);
onUnmounted(() => {
  window.removeEventListener(SAVE_EVENT, refreshLayout);
  if (equipmentTimer) clearInterval(equipmentTimer);
});
</script>

<template>
  <div class="floor-viewer" :class="{ compact }">
    <div v-if="loading" class="empty-plan" role="status">正在讀取配置…</div>
    <div v-else-if="error" class="empty-plan" role="alert">{{ error }}</div>
    <template v-else>
      <FloorPlanCanvas
        ref="canvasRef" :floor="floor" :placements="placements" :equipment="equipment" :compact="compact"
        :zoomable="zoomable" :selected-device-id="selectedId"
        @select-device="selectedId = $event" @view-change="zoomView = $event"
      />
      <div v-if="!placements.length && !embedded" class="empty-note">尚未配置設備 <a :href="`/admin/floor-plan?floor=${floor}`">前往配置 →</a></div>
      <div v-else-if="placements.length" class="heatmap-note">區域熱力：<i class="blue" />正常 <i class="red" />異常／待保養</div>
      <div v-if="selected" class="equipment-popup" role="status"><button type="button" aria-label="關閉設備資訊" @click="selectedId = ''">×</button><strong>{{ selected.code }}</strong><span>{{ selectedArea }} · <b :style="{ color: equipmentColor(selected.status) }">{{ equipmentStatus(selected.status) }}</b></span><span>{{ selected.kind === 'chiller' ? '出水溫度' : '室內溫度' }} {{ selected.temperature === undefined ? '無資料' : `${selected.temperature.toFixed(1)} °C` }}</span></div>
      <div v-if="zoomable" class="zoom-controls">
        <button type="button" aria-label="縮小平面圖" :disabled="zoomView.atDefault" @click="canvasRef?.zoomBy(1 / 1.4)">−</button>
        <button type="button" aria-label="重設平面圖縮放" :disabled="zoomView.atDefault" @click="canvasRef?.resetView()">{{ Math.round(zoomView.scale * 100) }}%</button>
        <button type="button" aria-label="放大平面圖" :disabled="zoomView.scale >= 8" @click="canvasRef?.zoomBy(1.4)">＋</button>
      </div>
    </template>
  </div>
</template>

<style scoped>
.floor-viewer { position: relative; background: #0c1925; height: 100%; min-height: 250px; border-radius: 9px; overflow: hidden; }.compact { min-height: 220px; }.empty-plan { display: flex; flex-direction: column; justify-content: center; align-items: center; text-align: center; padding: 20px; gap: 9px; min-height: inherit; height: 100%; color: #bdcedb; font-size: 13px; }.empty-plan span { font-size: 11px; color: #839cac; }.empty-note,.heatmap-note,.placeholder-note { position: absolute; left: 50%; transform: translateX(-50%); background: #132c3eef; border: 1px solid #456678; color: #c3d9e5; font-size: 11px; padding: 7px 10px; border-radius: 6px; white-space: nowrap; }.empty-note,.heatmap-note { bottom: 12px; }.placeholder-note { top: 12px; color: #f0c46a; border-color: #6b5a2e; }.empty-note a { color: #8dd5ee; margin-left: 10px; }.heatmap-note { display: flex; align-items: center; gap: 6px; }.heatmap-note i { width: 7px; height: 7px; border-radius: 50%; margin-left: 4px; }.blue { background: #60a5fa; }.red { background: #f87171; }.equipment-popup { position: absolute; top: 12px; right: 12px; display: flex; flex-direction: column; gap: 6px; border: 1px solid #456678; background: #122735f5; border-radius: 8px; padding: 14px 30px 14px 14px; font-size: 12px; color: #c3d9e5; }.equipment-popup strong { color: #f1f5f9; }.equipment-popup button { position: absolute; top: 3px; right: 8px; color: #c3d9e5; font-size: 22px; cursor: pointer; }
.zoom-controls { position: absolute; bottom: 12px; right: 12px; display: flex; align-items: center; gap: 4px; background: #132c3eef; border: 1px solid #456678; border-radius: 7px; padding: 4px; }
.zoom-controls button { min-width: 26px; padding: 4px 6px; border-radius: 4px; color: #c3d9e5; font-size: 12px; font-variant-numeric: tabular-nums; cursor: pointer; }
.zoom-controls button:not(:disabled):hover { background: #24425a; }
.zoom-controls button:disabled { color: #5c7688; cursor: default; }
a:focus-visible,button:focus-visible { outline: 2px solid #60a5fa; outline-offset: 3px; }
</style>
