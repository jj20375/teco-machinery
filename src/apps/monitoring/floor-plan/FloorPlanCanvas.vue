<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import {
  AREA_COLOR, planOf, pointsAttribute,
  areaHeat, equipmentColor, equipmentStatus, equipmentSize,
  type Placement, type Equipment, type Point,
} from './floor-plan';
import type { FloorId } from '../pages/dashboard/_types/dashboard-types';

const props = withDefaults(defineProps<{
  floor?: FloorId;
  placements: Placement[];
  equipment: Equipment[];
  editable?: boolean;
  selectedDeviceId?: string;
  selectedAreaId?: string;
  compact?: boolean;
  /** 開啟滾輪平移／縮放；未開啟時 viewBox 固定為整張圖 */
  zoomable?: boolean;
}>(), { floor: 'B1', editable: false, compact: false, zoomable: false });
const emit = defineEmits<{
  'select-area': [id: string, point: Point];
  'select-device': [id: string];
  move: [id: string, point: Point];
  'view-change': [state: { scale: number; atDefault: boolean }];
}>();
const svg = ref<SVGSVGElement | null>(null);

// ---- 平移／縮放：直接改 viewBox，不用 CSS transform。
// 這樣 getScreenCTM() 會自動把縮放算進去，底下拖曳設備的座標換算完全不用改。
const MAX_SCALE = 8;
// 兩層的圖面尺寸與分區都不同，全部從當前樓層的 plan 取。
const plan = computed(() => planOf(props.floor));
const areas = computed(() => plan.value.areas);
// SVG 裡的絕對尺寸（邊框粗細、字級、光暈半徑）都是以 B1 圖面單位寫死的。
// B2 圖面大 1.334 倍卻畫進同一個框，不換算的話線會細 25%、字會小 25%，
// 看起來就像顏色變淡。u 讓這些常數跟著樓層縮放，兩層的視覺重量才一致。
const u = computed(() => plan.value.unitScale);
/** 設備標記在 B1 圖面單位下的原始尺寸；整組用 scale(u) 放大，內部常數就不用逐一換算 */
function markerSize(kind: EquipmentKind): Point {
  const [width, height] = equipmentSize(kind, props.floor);
  return [width / u.value, height / u.value];
}
const view = ref({ x: 0, y: 0, width: plan.value.width, height: plan.value.height });
const scale = computed(() => plan.value.width / view.value.width);
const atDefault = computed(() => view.value.width >= plan.value.width - .01 && !view.value.x && !view.value.y);

/** 一個螢幕像素等於多少圖面單位；用 CTM 取才會把 preserveAspectRatio 的留白算進去 */
function unitsPerPixel(): number {
  const matrix = svg.value?.getScreenCTM();
  return matrix && matrix.a ? 1 / matrix.a : view.value.width / plan.value.width;
}
/** 縮放後不讓畫面跑出圖面外，也不讓使用者縮到看不到東西 */
function clampView(next: { x: number; y: number; width: number }) {
  const width = Math.min(plan.value.width, Math.max(plan.value.width / MAX_SCALE, next.width));
  const height = width * plan.value.height / plan.value.width;
  return {
    width,
    height,
    x: Math.min(Math.max(next.x, 0), plan.value.width - width),
    y: Math.min(Math.max(next.y, 0), plan.value.height - height),
  };
}
/** 以某個螢幕座標為錨點縮放，讓游標底下的位置維持不動 */
function zoomAt(factor: number, clientX: number, clientY: number) {
  const matrix = svg.value?.getScreenCTM();
  if (!matrix) return;
  const anchor = new DOMPoint(clientX, clientY).matrixTransform(matrix.inverse());
  const width = view.value.width / factor;
  const ratioX = (anchor.x - view.value.x) / view.value.width;
  const ratioY = (anchor.y - view.value.y) / view.value.height;
  view.value = clampView({
    width,
    x: anchor.x - ratioX * width,
    y: anchor.y - ratioY * (width * plan.value.height / plan.value.width),
  });
}
/** 給外層按鈕用：以畫面中心縮放 */
function zoomBy(factor: number) {
  const rect = svg.value?.getBoundingClientRect();
  if (!rect) return;
  zoomAt(factor, rect.left + rect.width / 2, rect.top + rect.height / 2);
}
function resetView() {
  view.value = { x: 0, y: 0, width: plan.value.width, height: plan.value.height };
}
function onWheel(event: WheelEvent) {
  if (!props.zoomable) return;
  // 不 preventDefault 的話整個後台頁面會跟著捲動。
  event.preventDefault();
  // 觸控板捏合與 Ctrl+滾輪都會帶 ctrlKey，視為縮放；其餘一律當成平移。
  if (event.ctrlKey || event.metaKey) {
    zoomAt(Math.exp(-event.deltaY * 0.01), event.clientX, event.clientY);
    return;
  }
  const perPixel = unitsPerPixel();
  view.value = clampView({
    width: view.value.width,
    x: view.value.x + event.deltaX * perPixel,
    y: view.value.y + event.deltaY * perPixel,
  });
}
/** 中鍵拖曳平移；滑鼠使用者不必按 Ctrl 也能移動畫面 */
const panning = ref<{ pointer: number; clientX: number; clientY: number } | null>(null);
function startPan(event: PointerEvent) {
  if (!props.zoomable || event.button !== 1) return;
  panning.value = { pointer: event.pointerId, clientX: event.clientX, clientY: event.clientY };
  svg.value?.setPointerCapture(event.pointerId);
  event.preventDefault();
}
function panTo(event: PointerEvent) {
  if (!panning.value || panning.value.pointer !== event.pointerId) return;
  const perPixel = unitsPerPixel();
  view.value = clampView({
    width: view.value.width,
    x: view.value.x - (event.clientX - panning.value.clientX) * perPixel,
    y: view.value.y - (event.clientY - panning.value.clientY) * perPixel,
  });
  panning.value = { ...panning.value, clientX: event.clientX, clientY: event.clientY };
}
function endPan() {
  if (panning.value && svg.value?.hasPointerCapture(panning.value.pointer)) svg.value.releasePointerCapture(panning.value.pointer);
  panning.value = null;
}
watch([scale, atDefault], () => emit('view-change', { scale: scale.value, atDefault: atDefault.value }), { immediate: true });
watch(() => props.floor, resetView);
onMounted(() => {
  // 手動掛 wheel 是為了指定 passive:false；Vue 的 @wheel 在部分瀏覽器會被當成 passive，
  // 那樣就 preventDefault 不了，頁面會跟著捲。
  svg.value?.addEventListener('wheel', onWheel, { passive: false });
});
onBeforeUnmount(() => svg.value?.removeEventListener('wheel', onWheel));
defineExpose({ zoomBy, resetView });
const catalog = computed(() => new Map(props.equipment.map((e) => [e.id, e])));
/** 圖面標籤：自訂代碼優先，沒設定才退回系統編號。 */
function deviceLabel(id: string): string | undefined {
  const device = catalog.value.get(id);
  return device ? (device.name ?? device.code) : undefined;
}
const heatByArea = computed(() => new Map(areas.value.map((area) => [area.id, areaHeat(area.id, props.placements, props.equipment)])));
const heatPlacements = computed(() => new Map(areas.value.map((area) => [area.id, props.placements.filter((placement) => placement.areaId === area.id)])));
const drag = ref<{ id: string; pointer: number; offset: Point } | null>(null);

function coordinates(event: PointerEvent | MouseEvent): Point | undefined {
  const matrix = svg.value?.getScreenCTM();
  if (!matrix) return;
  const point = new DOMPoint(event.clientX, event.clientY).matrixTransform(matrix.inverse());
  return [Math.round(point.x), Math.round(point.y)];
}
function selectArea(id: string, event?: MouseEvent) {
  if (!props.editable) return;
  const area = areas.value.find((a) => a.id === id)!;
  emit('select-area', id, event ? coordinates(event) ?? area.label : area.label);
}
function startDrag(event: PointerEvent, placement: Placement) {
  if (event.button !== 0) return;
  emit('select-device', placement.deviceId);
  if (!props.editable) return;
  const point = coordinates(event);
  if (!point) return;
  drag.value = { id: placement.deviceId, pointer: event.pointerId, offset: [placement.x - point[0], placement.y - point[1]] };
  svg.value?.setPointerCapture(event.pointerId);
  event.preventDefault();
}
function move(event: PointerEvent) {
  panTo(event);
  if (!drag.value || drag.value.pointer !== event.pointerId) return;
  const point = coordinates(event);
  if (point) emit('move', drag.value.id, [point[0] + drag.value.offset[0], point[1] + drag.value.offset[1]]);
}
function endDrag() {
  endPan();
  if (drag.value && svg.value?.hasPointerCapture(drag.value.pointer)) svg.value.releasePointerCapture(drag.value.pointer);
  drag.value = null;
}
function onKey(event: KeyboardEvent, p: Placement) {
  if (event.key === 'Enter' || event.key === ' ') {
    event.preventDefault();
    emit('select-device', p.deviceId);
    return;
  }
  if (!props.editable) return;
  const directions: Record<string, Point> = { ArrowUp: [0,-1], ArrowDown: [0,1], ArrowLeft: [-1,0], ArrowRight: [1,0] };
  const delta = directions[event.key];
  if (!delta) return;
  event.preventDefault();
  const step = event.shiftKey ? 5 : 1;
  emit('move', p.deviceId, [p.x + delta[0] * step, p.y + delta[1] * step]);
}
</script>

<template>
  <svg ref="svg" :viewBox="`${view.x} ${view.y} ${view.width} ${view.height}`" class="floor-svg" :style="{ '--u': u }"
    :class="{ editable, compact, zoomable, panning: !!panning }"
    preserveAspectRatio="xMidYMid meet" role="group" :aria-label="`${floor} 空間配置圖；藍色光暈為正常設備區域，紅色光暈為異常設備區域`"
    @pointerdown="startPan" @pointermove="move" @pointerup="endDrag" @pointercancel="endDrag"
    @lostpointercapture="drag = null; panning = null">
    <title>{{ floor }} 空間分區；藍色光暈為正常設備區域，紅色光暈為異常設備區域</title>
    <defs>
      <filter id="heat-blur" x="-60%" y="-60%" width="220%" height="220%"><feGaussianBlur :stdDeviation="20 * u" /></filter>
      <filter id="heat-edge" x="-20%" y="-20%" width="140%" height="140%"><feDropShadow dx="0" dy="0" :stdDeviation="7 * u" flood-color="#ffffff" flood-opacity=".35" /></filter>
      <clipPath v-for="area in areas" :id="`area-clip-${area.id}`" :key="`clip-${area.id}`"><polygon :points="pointsAttribute(area.points)" /></clipPath>
    </defs>
    <rect x="0" y="0" :width="plan.width" :height="plan.height" fill="#0f2531" />
    <!-- CAD 原圖底圖；分區與設備是疊在上面的互動層，底圖本身不可互動。 -->
    <image :href="plan.baseImage" x="0" y="0" :width="plan.width" :height="plan.height" :opacity="plan.baseOpacity" style="pointer-events:none" />
    <g v-for="area in areas" :key="area.id" class="area" :class="{ selectable: editable, selected: selectedAreaId === area.id }"
      :tabindex="editable ? 0 : undefined" :role="editable ? 'button' : undefined"
      :aria-label="`${area.id} ${area.name}${editable ? '，選擇此區域新增設備' : ''}`"
      @click="selectArea(area.id, $event)" @keydown.enter.prevent="selectArea(area.id)" @keydown.space.prevent="selectArea(area.id)">
      <title>{{ area.id }}{{ area.unnamed ? '' : ` · ${area.name}` }} · 約 {{ area.sqm }} ㎡</title>
      <polygon :points="pointsAttribute(area.points)" :fill="AREA_COLOR" stroke="#476071" :stroke-width="u" />
      <polygon v-if="heatByArea.get(area.id)?.color" :points="pointsAttribute(area.points)"
        class="area-heat" :fill="heatByArea.get(area.id)?.color" :fill-opacity=".12 + heatByArea.get(area.id)!.intensity * .2" />
      <g v-if="heatByArea.get(area.id)?.color" :clip-path="`url(#area-clip-${area.id})`" class="heat-spots">
        <circle v-for="placement in heatPlacements.get(area.id)" :key="`heat-${placement.deviceId}`"
          :cx="placement.x" :cy="placement.y" :r="(placement.kind === 'chiller' ? 87 : 60) * u"
          :fill="heatByArea.get(area.id)?.color" :fill-opacity="heatByArea.get(area.id)!.intensity" filter="url(#heat-blur)" />
      </g>
      <text :x="area.label[0]" :y="area.label[1] - 4 * u" text-anchor="middle" class="area-code">{{ area.id.replace(`${floor}-`, '') }}</text>
      <text v-if="!compact && !area.unnamed && area.area > 2500 * u * u" :x="area.label[0]" :y="area.label[1] + 5 * u" text-anchor="middle" class="area-name">{{ area.name }}</text>
    </g>
    <g v-for="p in placements" :key="p.deviceId" :transform="`translate(${p.x} ${p.y})`"
      class="device" :class="{ selected: selectedDeviceId === p.deviceId, dragging: drag?.id === p.deviceId }"
      tabindex="0" role="button" :data-device-id="p.deviceId"
      :aria-label="`${deviceLabel(p.deviceId)}，${equipmentStatus(catalog.get(p.deviceId)?.status)}${editable ? '，方向鍵移動' : ''}`"
      @pointerdown.stop="startDrag($event, p)" @click.stop="emit('select-device', p.deviceId)" @keydown="onKey($event, p)">
      <title>{{ deviceLabel(p.deviceId) }} · {{ equipmentStatus(catalog.get(p.deviceId)?.status) }}</title>
      <g :transform="`rotate(${p.rotation}) scale(${u})`">
        <rect :x="-markerSize(p.kind)[0]/2 - 3" :y="-markerSize(p.kind)[1]/2 - 3"
          :width="markerSize(p.kind)[0]+6" :height="markerSize(p.kind)[1]+6" rx="5"
          fill="none" :stroke="selectedDeviceId === p.deviceId ? '#f8fafc' : 'transparent'" stroke-width="1" />
        <rect :x="-markerSize(p.kind)[0]/2" :y="-markerSize(p.kind)[1]/2" :width="markerSize(p.kind)[0]" :height="markerSize(p.kind)[1]"
          rx="2" :fill="equipmentColor(catalog.get(p.deviceId)?.status)" stroke="#e2e8f0" stroke-width="0.8" />
        <g v-if="p.kind === 'chiller'" fill="none" stroke="#102538" stroke-width="1.2">
          <circle cx="-7" cy="0" r="5" /><circle cx="7" cy="0" r="5" />
        </g>
        <path v-else d="M-5 -3h10M-5 0h10M-5 3h10" stroke="#102538" stroke-width="1.2" />
      </g>
      <text x="0" :y="21 * u" text-anchor="middle" class="device-label">{{ deviceLabel(p.deviceId) }}</text>
    </g>
    <text :x="12 * u" :y="plan.height - 8 * u" class="footer-label">{{ floor }} / 空間示意 · 非施工尺寸</text>
  </svg>
</template>

<style scoped>
.floor-svg { display: block; width: 100%; height: 100%; min-height: 200px; user-select: none; }
.editable, .zoomable { touch-action: none; }
.zoomable { cursor: default; }
.zoomable.panning { cursor: grabbing; }
.area-code { fill: #d7e7ef; font-family: ui-monospace, monospace; font-size: calc(7px * var(--u, 1)); font-weight: 600; pointer-events: none; paint-order: stroke; stroke: #0b1926; stroke-width: calc(2.5px * var(--u, 1)); }
.area-name { fill: #b6cfdf; font-size: calc(6px * var(--u, 1)); pointer-events: none; paint-order: stroke; stroke: #0b1926; stroke-width: calc(2px * var(--u, 1)); }
.selectable { cursor: pointer; }
.area > polygon:first-of-type { fill-opacity: .62; }
.selectable:hover polygon:first-of-type, .area.selected polygon:first-of-type { fill: #24556a; fill-opacity: .85; stroke: #70d9ed; stroke-width: calc(1.5px * var(--u, 1)); }
.area:focus { outline: none; }
.area:focus-visible polygon:first-of-type { stroke: white; stroke-width: calc(2.5px * var(--u, 1)); }
.area-heat { pointer-events: none; filter: url(#heat-edge); }
.heat-spots { pointer-events: none; }
.footer-label { fill: #6f8a9a; font-size: calc(8px * var(--u, 1)); pointer-events: none; }
.device { cursor: pointer; outline: none; }
.editable .device { cursor: grab; }
.device.dragging { cursor: grabbing; }
.device:focus-visible > g > rect:first-child { stroke: white; stroke-width: calc(2px * var(--u, 1)); }
.device-label { fill: #f8fafc; font-family: ui-monospace, monospace; font-size: calc(8px * var(--u, 1)); paint-order: stroke; stroke: #0b1926; stroke-width: calc(2.5px * var(--u, 1)); pointer-events: none; }
.compact .area-code { font-size: calc(11px * var(--u, 1)); }
.compact .footer-label, .compact .device-label { display: none; }
</style>
