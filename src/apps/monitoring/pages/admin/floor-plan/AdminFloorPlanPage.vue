<script setup lang="ts">
import { computed, defineAsyncComponent, h, onMounted, onUnmounted, ref, watch } from 'vue';
import AdminLayout from '../_components/AdminLayout.vue';
import AdminButton from '../_components/AdminButton.vue';
import FloorPlanCanvas from '../../../floor-plan/FloorPlanCanvas.vue';
import {
  areasOf, FLOORS, SAVE_EVENT,
  getFloorEquipmentApi, serializeLayout, placementArea, canPlace, findFreeSpot, equipmentColor, equipmentStatus,
  placementToDto, placementFromDto,
  type Equipment, type EquipmentKind, type Placement, type Point,
} from '../../../floor-plan/floor-plan';
import { getFloorPlacementsApi, saveFloorPlacementsApi } from '../_services/floor-plan-service';
import { ApiError } from '../_services/auth-service';
import type { FloorId } from '../../dashboard/_types/dashboard-types';

// 3D 檢視連同 three.js 一起動態載入，平面檢視就不用背這包。
// 載入失敗時一定要有東西可看：沒有 errorComponent 的話 defineAsyncComponent 會靜靜地
// 什麼都不渲染，整個地圖區塌成空白，使用者只看得到「3D 出不來」卻沒有任何線索。
const Plan3DError = () => h('div', { class: 'plan-3d-error', role: 'alert' }, [
  h('p', '3D 檢視載入失敗。'),
  h('button', { type: 'button', onClick: () => window.location.reload() }, '重新載入頁面'),
  h('p', { class: 'hint' }, '若重新載入後仍然失敗，請改用 2D 平面檢視。'),
]);
const FloorPlan3D = defineAsyncComponent({
  loader: () => import('../../../floor-plan/FloorPlan3D.vue'),
  errorComponent: Plan3DError,
  timeout: 15000,
});
const FLOOR_LABELS: Record<FloorId, string> = { B1: 'B1 地下一樓', B2: 'B2 地下二樓' };

function floorFromUrl(): FloorId {
  if (typeof window === 'undefined') return 'B1';
  return new URLSearchParams(window.location.search).get('floor') === 'B2' ? 'B2' : 'B1';
}

const floor = ref<FloorId>(floorFromUrl());
const view = ref<'2d' | '3d'>('2d');
const equipment = ref<Equipment[]>([]);
const placements = ref<Placement[]>([]);
const loading = ref(true);
const loadError = ref('');
const feedback = ref('');
const feedbackError = ref(false);
const selectedAreaId = ref('');
const selectedDeviceId = ref('');
const kind = ref<EquipmentKind>('chiller');
const pendingDeviceId = ref('');
const clickedPoint = ref<Point | null>(null);
const canvasRef = ref<InstanceType<typeof FloorPlanCanvas> | null>(null);
const zoomView = ref({ scale: 1, atDefault: true });
let loadedVersion: string | null = null;
const savedSnapshot = ref('[]');
const saving = ref(false);
const dirty = computed(() => JSON.stringify(placements.value) !== savedSnapshot.value);
// 兩層的分區各自獨立（B1 72 區 B1-Zxx、B2 41 區 B2-E/N/W/C/Sxx）
const areas = computed(() => areasOf(floor.value));
const selectedArea = computed(() => areas.value.find((a) => a.id === selectedAreaId.value));
const selectedPlacement = computed(() => placements.value.find((p) => p.deviceId === selectedDeviceId.value));
const selectedEquipment = computed(() => equipment.value.find((e) => e.id === selectedDeviceId.value));
const available = computed(() => equipment.value.filter((e) => e.kind === kind.value && !placements.value.some((p) => p.deviceId === e.id)));
const areaPlacements = computed(() => placements.value.filter((p) => p.areaId === selectedAreaId.value));
const abnormalCount = computed(() => placements.value.filter((p) => equipmentColor(equipment.value.find((e) => e.id === p.deviceId)?.status) === '#f87171').length);

watch(available, (list) => {
  if (!list.some((e) => e.id === pendingDeviceId.value)) pendingDeviceId.value = list[0]?.id ?? '';
});

function notify(message: string, error = false) {
  feedback.value = message;
  feedbackError.value = error;
}
function chooseArea(id: string, point: Point) {
  selectedAreaId.value = id;
  selectedDeviceId.value = '';
  clickedPoint.value = point;
  feedback.value = '';
}
function selectDevice(id: string) {
  selectedDeviceId.value = id;
  selectedAreaId.value = placements.value.find((p) => p.deviceId === id)?.areaId ?? '';
  feedback.value = '';
}
function addEquipment() {
  const device = available.value.find((e) => e.id === pendingDeviceId.value);
  const area = selectedArea.value;
  if (!device || !area || loadError.value) return;
  const spot = findFreeSpot(device.kind, area, placements.value, floor.value, clickedPoint.value ?? undefined);
  if (!spot) {
    notify(`${area.name}已放不下${device.kind === 'chiller' ? '冰水主機' : ' FCU'}，請先移除或改放其他區域。`, true);
    return;
  }
  const placement: Placement = { deviceId: device.id, kind: device.kind, areaId: area.id, x: spot[0], y: spot[1], rotation: 0 };
  placements.value.push(placement);
  selectedDeviceId.value = placement.deviceId;
  notify(`已加入 ${device.code}，請儲存配置。`);
}
function moveDevice(id: string, point: Point, rotation?: number) {
  const p = placements.value.find((item) => item.deviceId === id);
  if (!p || loadError.value) return;
  const angle = rotation ?? p.rotation;
  const area = placementArea(p.kind, point[0], point[1], angle, floor.value);
  const updated = { ...p, x: point[0], y: point[1], rotation: angle, areaId: area?.id ?? '' };
  if (!canPlace(updated, placements.value, floor.value)) {
    notify('請將設備完整放在可配置區域內，並避開其他設備。', true);
    return;
  }
  Object.assign(p, updated);
  selectedAreaId.value = p.areaId;
  notify('位置已調整，尚未儲存。');
}
function rotateDevice() {
  const p = selectedPlacement.value;
  if (p) moveDevice(p.deviceId, [p.x, p.y], (p.rotation + 90) % 360);
}
function removeDevice() {
  placements.value = placements.value.filter((p) => p.deviceId !== selectedDeviceId.value);
  selectedDeviceId.value = '';
  notify('已移除圖面位置，設備仍保留在設備清單中。');
}
async function save() {
  if (loading.value || loadError.value || saving.value) return;
  saving.value = true;
  try {
    // 先跑一次 serializeLayout：它內含完整驗證（重複設備、非法座標、放不進分區），
    // 有問題就不要送出去讓後端擋，錯誤訊息也比較貼近使用者剛剛的操作。
    serializeLayout(placements.value, equipment.value, floor.value);
    const result = await saveFloorPlacementsApi(
      floor.value, placements.value.map(placementToDto), loadedVersion);
    loadedVersion = result.version;
    savedSnapshot.value = JSON.stringify(placements.value);
    window.dispatchEvent(new Event(SAVE_EVENT));
    notify('配置已儲存，前台與監控中心會一起更新。');
  } catch (err) {
    if (err instanceof ApiError && err.status === 409) {
      notify('另一個人已更新這層的配置。請先匯出目前草稿，再重新載入本頁，避免覆蓋對方的修改。', true);
    } else if (err instanceof ApiError && err.status === 403) {
      notify('目前登入的帳號沒有「空間設備配置」的編輯權限。', true);
    } else {
      notify(err instanceof Error ? err.message : '儲存失敗，請稍後再試或先匯出草稿保留修改。', true);
    }
  } finally {
    saving.value = false;
  }
}
function exportDraft() {
  const blob = new Blob([serializeLayout(placements.value, equipment.value, floor.value)], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `${floor.value}-設備配置.json`;
  anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
function beforeLeave(event: BeforeUnloadEvent) {
  if (dirty.value) { event.preventDefault(); event.returnValue = ''; }
}
async function loadFloor(next: FloorId) {
  loading.value = true;
  loadError.value = '';
  selectedAreaId.value = '';
  selectedDeviceId.value = '';
  clickedPoint.value = null;
  feedback.value = '';
  try {
    const [devices, saved] = await Promise.all([getFloorEquipmentApi(next), getFloorPlacementsApi(next)]);
    equipment.value = devices;
    loadedVersion = saved.version;
    // 設備被停用或刪除後，舊配置會指到不存在的設備；靜靜濾掉即可，不要讓整頁載不出來。
    const known = new Set(devices.map((d) => d.id));
    placements.value = saved.placements.map(placementFromDto).filter((p) => known.has(p.deviceId));
    savedSnapshot.value = JSON.stringify(placements.value);
  } catch (err) {
    loadError.value = err instanceof ApiError && err.status === 403
      ? '目前登入的帳號沒有「空間設備配置」的查看權限。'
      : '無法讀取設備或已儲存的配置，已暫停編輯以保留原始資料。請稍後重新載入。';
  } finally {
    loading.value = false;
  }
}
function switchFloor(next: FloorId) {
  if (next === floor.value) return;
  if (dirty.value && typeof window !== 'undefined' &&
    !window.confirm(`${FLOOR_LABELS[floor.value]}尚有未儲存的變更，切換樓層將捨棄這些變更，確定要切換嗎？`)) return;
  floor.value = next;
  if (typeof window !== 'undefined') {
    const url = new URL(window.location.href);
    url.searchParams.set('floor', next);
    window.history.replaceState({}, '', url);
  }
  void loadFloor(next);
}
onMounted(async () => {
  window.addEventListener('beforeunload', beforeLeave);
  await loadFloor(floor.value);
});
onUnmounted(() => window.removeEventListener('beforeunload', beforeLeave));
</script>

<template>
  <AdminLayout page-title="空間設備配置" current-path="/admin/floor-plan">
    <div class="plan-page">
      <div class="page-heading">
        <div><div class="eyebrow">SPACE & EQUIPMENT</div><h2>{{ floor }} 空間配置</h2><p>點選區域，加入冰水主機或 FCU，再調整設備位置。</p></div>
        <div class="actions"><span class="save-state" :class="{ dirty }">{{ dirty ? '● 尚未儲存' : '已同步' }}</span>
          <AdminButton variant="tertiary" :disabled="loading || !!loadError" @click="exportDraft">匯出配置</AdminButton>
          <AdminButton :disabled="loading || !!loadError || !dirty || saving" @click="save">
            {{ saving ? '儲存中…' : '儲存配置' }}
          </AdminButton>
        </div>
      </div>
      <div v-if="loadError" role="alert" class="error-banner">{{ loadError }}</div>
      <div class="workspace">
        <section class="map-panel" aria-label="空間配置畫布">
          <div class="map-toolbar">
            <div class="floor-tabs" role="tablist" aria-label="樓層">
              <button v-for="f in FLOORS" :key="f" type="button" :aria-pressed="floor === f" @click="switchFloor(f)">{{ FLOOR_LABELS[f] }}</button>
            </div>
            <div class="view-tabs" aria-label="檢視模式">
              <button type="button" :aria-pressed="view === '2d'" @click="view = '2d'">2D 平面</button>
              <button type="button" :aria-pressed="view === '3d'" @click="view = '3d'">3D 立體</button>
            </div>
          </div>
          <div class="map-caption"><span>CAD 分區圖 <span class="divider">/</span> {{ areas.length }} 個分區</span><span>{{ placements.length }} 台已配置 <span class="divider">·</span> {{ abnormalCount }} 台異常／待保養</span></div>
          <div v-if="loading" class="loading" role="status">正在讀取配置…</div>
          <div v-else-if="view === '2d'" class="map-stage">
            <FloorPlanCanvas ref="canvasRef" :floor="floor" :placements="placements" :equipment="equipment" :editable="!loadError"
              zoomable :selected-area-id="selectedAreaId" :selected-device-id="selectedDeviceId"
              @select-area="chooseArea" @select-device="selectDevice" @move="moveDevice" @view-change="zoomView = $event" />
          </div>
          <!-- :key：3D 場景是掛載時一次建好的，換樓層要整個重建才會換成該層的圖面與分區 -->
          <FloorPlan3D v-else :key="floor" :floor="floor" :placements="placements" :equipment="equipment" :selected-device-id="selectedDeviceId" @fallback="view = '2d'" />
          <div class="map-bottom"><div class="legend"><span><i class="blue" />正常</span><span><i class="red" />異常／待保養</span><span><i class="gray" />離線／停止</span></div>
            <div v-if="view === '2d'" class="zoom-controls">
              <span class="zoom-hint">滾輪平移 · Ctrl+滾輪或雙指縮放</span>
              <button type="button" aria-label="縮小平面圖" :disabled="zoomView.atDefault" @click="canvasRef?.zoomBy(1 / 1.4)">−</button>
              <button type="button" aria-label="重設平面圖縮放" :disabled="zoomView.atDefault" @click="canvasRef?.resetView()">{{ Math.round(zoomView.scale * 100) }}%</button>
              <button type="button" aria-label="放大平面圖" :disabled="zoomView.scale >= 8" @click="canvasRef?.zoomBy(1.4)">＋</button>
            </div>
          </div>
        </section>

        <aside class="inspector" aria-label="區域與設備設定">
          <div class="inspector-heading"><span class="step">01</span><h3>選擇配置區域</h3></div>
          <label class="field-label" for="area-select">目前區域</label>
          <select id="area-select" :value="selectedAreaId" :disabled="loading || !!loadError" @change="chooseArea(($event.target as HTMLSelectElement).value, areas.find(a => a.id === ($event.target as HTMLSelectElement).value)?.label ?? [0,0])">
            <option value="" disabled>點選圖面或選擇區域</option>
            <option v-for="a in areas" :key="a.id" :value="a.id">{{ a.id.replace(`${floor}-`, '') }}{{ a.unnamed ? '' : ` · ${a.name}` }} · {{ a.sqm }} ㎡</option>
          </select>
          <div v-if="!selectedArea" class="empty-selection"><span class="empty-icon">⌖</span><strong>先選一個空間</strong><p>點選圖面上的分區，即可在此加入設備。</p></div>
          <template v-else>
            <div class="area-summary">
              <span>{{ selectedArea.id }}</span>
              <strong v-if="!selectedArea.unnamed">{{ selectedArea.name }}</strong>
              <small>約 {{ selectedArea.sqm }} ㎡ · {{ areaPlacements.length }} 台設備</small>
              <p v-if="selectedArea.unnamed" class="area-note">此分區尚未命名，目前以編號顯示。</p>
            </div>
            <div class="inspector-heading"><span class="step">02</span><h3>新增設備到此區域</h3></div>
            <div class="kind-tabs"><button type="button" :aria-pressed="kind === 'chiller'" @click="kind = 'chiller'">冰水主機</button><button type="button" :aria-pressed="kind === 'fcu'" @click="kind = 'fcu'">FCU</button></div>
            <label class="field-label" for="equipment-select">未配置的設備</label>
            <select id="equipment-select" v-model="pendingDeviceId" :disabled="!available.length || !!loadError"><option v-if="!available.length" value="">此類設備已全部配置</option><option v-for="e in available" :key="e.id" :value="e.id">{{ e.name ?? '未設定' }} · {{ e.code }} · {{ equipmentStatus(e.status) }}</option></select>
            <AdminButton class="add-button" :disabled="!pendingDeviceId || !!loadError" @click="addEquipment">＋ 加入此區域</AdminButton>
            <p class="hint">從系統設備清單加入，不會改變設備的啟停設定。</p>
          </template>

          <div v-if="selectedEquipment && selectedPlacement" class="device-detail">
            <div class="detail-heading"><h3>{{ selectedEquipment.kind === 'chiller' ? '冰水主機' : 'FCU' }} 設備位置</h3><span :style="{ color: equipmentColor(selectedEquipment.status) }">● {{ equipmentStatus(selectedEquipment.status) }}</span></div>
            <strong class="device-code">{{ selectedEquipment.name ?? '未設定' }}</strong>
            <small class="device-system-code">{{ selectedEquipment.code }}</small>
            <div class="device-metric"><span>{{ selectedEquipment.kind === 'chiller' ? '出水溫度' : '室內溫度' }}</span><b>{{ selectedEquipment.temperature === undefined ? '無資料' : `${selectedEquipment.temperature.toFixed(1)} °C` }}</b></div>
            <div class="device-metric"><span>方向</span><b>{{ selectedPlacement.rotation }}°</b></div>
            <div class="device-actions"><AdminButton variant="tertiary" size="sm" @click="rotateDevice">旋轉 90°</AdminButton><button class="remove" type="button" @click="removeDevice">移除位置</button></div>
            <p class="hint">{{ view === '2d' ? '拖曳設備可調整位置；也可聚焦設備後使用方向鍵微調。' : '切換到 2D 平面即可調整設備位置。' }}</p>
          </div>

          <div v-if="areaPlacements.length" class="placed-list"><h3>此區域設備 <span>{{ areaPlacements.length }}</span></h3><button v-for="p in areaPlacements" :key="p.deviceId" type="button" :class="{ active: selectedDeviceId === p.deviceId }" @click="selectDevice(p.deviceId)"><i :style="{ background: equipmentColor(equipment.find(e => e.id === p.deviceId)?.status) }" /><span>{{ equipment.find(e => e.id === p.deviceId)?.name ?? '未設定' }}</span><small>{{ equipment.find(e => e.id === p.deviceId)?.code }} · {{ equipmentStatus(equipment.find(e => e.id === p.deviceId)?.status) }}</small></button></div>
          <p v-if="feedback" role="status" aria-live="polite" class="feedback" :class="{ error: feedbackError }">{{ feedback }}</p>
          <div class="scope-note"><strong>配置說明</strong><p>底圖取自 CLUB-M {{ floor }} CAD 圖；分區範圍是設計端在 CAD 圖上標的紅框，共 {{ areas.length }} 區。沒有框到的地方（{{ floor === 'B1' ? '大梯廳、挑空、共構核心' : '停車場車道、坡道' }}）不是分區，設備放不進去。</p><p>面積為換算值僅供判讀，非施工尺寸。設備狀態取自即時監控資料；配置儲存在伺服器，所有人與前台看到的都是同一份，B1／B2 各自獨立。</p></div>
        </aside>
      </div>
    </div>
  </AdminLayout>
</template>

<style scoped>
.plan-3d-error { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 10px; min-height: 360px; padding: 24px; text-align: center; color: #c3d9e5; font-size: 13px; }
.plan-3d-error button { padding: 7px 14px; border: 1px solid #456678; border-radius: 6px; background: #132c3e; color: #8dd5ee; cursor: pointer; }
.plan-3d-error .hint { font-size: 11px; color: #839cac; }
.plan-page { max-width: 1700px; margin: 0 auto; }
.page-heading,.actions,.map-toolbar,.map-caption,.map-bottom,.legend,.legend span,.inspector-heading,.detail-heading,.device-metric,.device-actions { display: flex; align-items: center; }
.page-heading { justify-content: space-between; gap: 20px; margin-bottom: 22px; flex-wrap: wrap; }
.eyebrow { font-size: 10px; letter-spacing: .18em; color: #64808d; font-weight: 700; }
h2 { font-size: 25px; font-weight: 750; color: #142b3b; margin: 4px 0; }
.page-heading p { color: #64748b; font-size: 13px; margin: 0; }
.actions { gap: 10px; }
.save-state { font-size: 12px; color: #64748b; margin-right: 4px; }.save-state.dirty { color: #a16207; }
.workspace { display: grid; grid-template-columns: minmax(0,1fr) 300px; gap: 20px; align-items: start; }
.map-panel { background: #0c1925; border: 1px solid #263b4b; border-radius: 14px; overflow: hidden; color: #d8e7ef; }
.map-toolbar { justify-content: space-between; padding: 15px 18px; gap: 12px; flex-wrap: wrap; border-bottom: 1px solid #253847; }
.floor-tabs { display: flex; background: #142937; border-radius: 7px; padding: 3px; gap: 3px; }.floor-tabs button { font-size: 12px; padding: 6px 12px; border-radius: 5px; color: #9eb3c2; }.floor-tabs button[aria-pressed=true] { background: #315268; color: white; font-weight: 700; }
.view-tabs { display: flex; background: #142937; border-radius: 7px; padding: 3px; gap: 3px; }.view-tabs button { font-size: 12px; padding: 6px 12px; border-radius: 5px; color: #9eb3c2; }.view-tabs button[aria-pressed=true] { background: #315268; color: white; }
.map-caption { justify-content: space-between; padding: 13px 18px 0; font-size: 11px; color: #94adbd; gap: 8px; }.divider { margin: 0 7px; color: #506c7e; }
.map-stage { height: clamp(440px, 65vh, 850px); padding: 8px 12px; background-image: radial-gradient(#284050 1px, transparent 1px); background-size: 22px 22px; }
.map-bottom { justify-content: space-between; flex-wrap: wrap; gap: 12px; padding: 12px 18px; border-top: 1px solid #253847; }
.legend { gap: 14px; font-size: 11px; color: #b3c6d3; }.legend span { gap: 6px; }i { display: inline-block; width: 8px; height: 8px; border-radius: 3px; flex-shrink: 0; }.blue { background: #60a5fa; }.red { background: #f87171; }.gray { background: #94a3b8; }
.zoom-controls { display: flex; align-items: center; gap: 8px; }.zoom-hint { font-size: 10px; color: #6c8598; }.zoom-controls button { font-size: 12px; padding: 5px 8px; background: #1c303f; border-radius: 4px; }.zoom-controls button:not(:disabled):hover { background: #24425a; }
.inspector { background: white; border: 1px solid #e2e8f0; border-radius: 14px; padding: 20px; color: #334155; }
.inspector-heading { gap: 9px; margin-bottom: 15px; }h3 { font-size: 13px; font-weight: 700; margin: 0; }.step { border: 1px solid #cbe3e7; color: #32828e; border-radius: 6px; padding: 3px 5px; font-size: 10px; font-weight: 700; }
.field-label { display: block; font-size: 11px; color: #64748b; margin: 12px 0 7px; }select { width: 100%; background: #fff; border: 1px solid #cbd5e1; border-radius: 7px; padding: 10px 8px; font-size: 12px; color: #334155; }
.empty-selection { min-height: 235px; display: flex; flex-direction: column; justify-content: center; align-items: center; text-align: center; }.empty-icon { font-size: 40px; color: #a0bdc7; }.empty-selection strong { font-size: 14px; margin-top: 12px; }.empty-selection p { font-size: 12px; color: #64748b; line-height: 1.8; max-width: 200px; }
.area-summary { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; padding: 16px 0 22px; }.area-summary span { font-size: 11px; color: #2d7d8a; background: #e9f6f6; padding: 3px 6px; border-radius: 4px; }.area-summary small { width: 100%; color: #64748b; }.area-note { width: 100%; font-size: 11px; color: #7c8fa0; line-height: 1.7; margin: 4px 0 0; }
.kind-tabs { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }.kind-tabs button { border: 1px solid #dce4ec; padding: 10px; border-radius: 7px; font-size: 12px; }.kind-tabs button[aria-pressed=true] { color: #166c85; border-color: #409fb8; background: #eff9fb; }.add-button { width: 100%; margin-top: 12px; }
.hint { font-size: 11px; line-height: 1.7; color: #64748b; margin: 10px 0 18px; }.device-detail { border-top: 1px solid #e2e8f0; padding-top: 18px; }.detail-heading { justify-content: space-between; gap: 5px; }.detail-heading span { font-size: 11px; font-weight: 700; }.device-code { display: block; font: 16px ui-monospace, monospace; margin: 12px 0 2px; color: #163344; }.device-system-code { display: block; font: 11px ui-monospace, monospace; margin: 0 0 12px; color: #94a3b8; }.device-metric { justify-content: space-between; font-size: 12px; margin: 8px 0; color: #64748b; }.device-metric b { color: #334155; }.device-actions { gap: 14px; margin-top: 14px; }.remove { font-size: 12px; color: #c2414a; }
.placed-list { border-top: 1px solid #e2e8f0; padding-top: 15px; margin-top: 16px; }.placed-list h3 span { color: #94a3b8; margin-left: 6px; }.placed-list button { display: flex; align-items: center; gap: 8px; width: 100%; margin-top: 8px; padding: 10px 6px; border-radius: 6px; font-size: 11px; }.placed-list button.active { background: #edf7fa; }.placed-list small { margin-left: auto; color: #64748b; }
.scope-note { margin-top: 20px; padding-top: 16px; border-top: 1px solid #e2e8f0; font-size: 11px; line-height: 1.8; color: #64748b; }.scope-note strong { color: #475569; }.scope-note p { margin: 6px 0 0; }
.region-notice,.feedback { font-size: 12px; line-height: 1.8; padding: 12px; background: #edf8f8; color: #17626d; border-radius: 7px; margin-top: 14px; }.region-notice { background: #f1f5f9; color: #64748b; }.feedback.error,.error-banner { background: #fff1f2; color: #be123c; }.error-banner { padding: 14px; font-size: 13px; border-radius: 8px; margin-bottom: 16px; }.loading { min-height: 480px; display: grid; place-items: center; color: #b7cbd9; }
button { cursor: pointer; }button:disabled { cursor: not-allowed; opacity: .45; }button:focus-visible,select:focus-visible { outline: 2px solid #258ca6; outline-offset: 3px; }
@media (max-width: 1200px) { .workspace { grid-template-columns: minmax(0,1fr); }.inspector { max-width: none; }.map-stage { height: 65vh; } }
</style>
