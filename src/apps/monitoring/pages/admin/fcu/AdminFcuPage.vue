<script setup lang="ts">
import { ref, computed } from 'vue';
import { useForm } from 'vee-validate';
import { toTypedSchema } from '@vee-validate/yup';
import { useQuery, useQueryClient } from '@tanstack/vue-query';
import AdminLayout from '../_components/AdminLayout.vue';
import AdminButton from '../_components/AdminButton.vue';
import AdminStatusBadge from '../_components/AdminStatusBadge.vue';
import AdminPagination from '../_components/AdminPagination.vue';
import AdminRightPanel from '../_components/AdminRightPanel.vue';
import EmptyState from '../_components/EmptyState.vue';
import { ApiError } from '../_services/auth-service';
import {
  listFcusApi, listAlarmsApi, deriveFcuStatus, alarmingFcuIdsOf, isDataQualityOnline,
  fcuModeLabel, fcuFanSpeedLabel, fcuVendorLabel, updateFcuDisplayNameApi,
} from '../_services/hvac-service';
import { getFcuThresholdApi, saveFcuThresholdApi, type FcuThresholdConfig } from '../_services/threshold-service';
import { fcuThresholdSchema } from '../_services/threshold-validation';

const queryClient = useQueryClient();
const fcusQuery = useQuery({ queryKey: ['fcu-page-list'], queryFn: () => listFcusApi(), refetchInterval: 10_000 });
const alarmsQuery = useQuery({ queryKey: ['fcu-page-alarms'], queryFn: () => listAlarmsApi('active'), refetchInterval: 10_000 });

const loading = computed(() => fcusQuery.isPending.value || alarmsQuery.isPending.value);

/** hvac.fcus 跟 hvac.alarms 是兩個獨立權限，見 AdminChillerPage.vue 同樣邏輯的說明。 */
const loadError = computed(() => {
  const err = fcusQuery.error.value;
  if (!err) return '';
  return err instanceof ApiError && err.status === 403
    ? '目前登入的帳號沒有「FCU管理」權限，無法查看清單。'
    : err instanceof Error
      ? err.message
      : '載入 FCU 清單失敗，請稍後再試。';
});
const alarmWarning = computed(() => {
  const err = alarmsQuery.error.value;
  if (!err) return '';
  return err instanceof ApiError && err.status === 403
    ? '目前登入的帳號沒有「告警」查看權限，下表的異常狀態判斷可能不準確。'
    : '目前有效告警清單載入失敗，下表的異常狀態判斷可能不準確。';
});

/**
 * 沒有 code 這個欄位（供應商 SDK 沒給，後端只有 channel/stationId/position 組出的內部 id），
 * 跟前台戰情室（dashboard-service.ts）用同一套 fallback：zoneCode 優先，沒有就用「樓層-id」。
 * 異常狀態直接沿用 deriveFcuStatus（已對照有效告警清單），不用另外算一次室溫是否超標，
 * 這樣「表格顯示異常」跟「後端真的有開告警」保證一致，不會有前端自己算出來的假異常。
 */
const rows = computed(() => {
  const alarms = alarmsQuery.data.value ?? [];
  const alarmingIds = alarmingFcuIdsOf(alarms);
  return (fcusQuery.data.value ?? []).map((f) => {
    const online = isDataQualityOnline(f.dataQuality);
    const status = deriveFcuStatus(f, alarmingIds);
    return {
      id: f.id,
      // 場館自訂代碼跟系統編號分成兩欄並存：沒設定時這欄留空顯示「未設定」，不要退回顯示
      // 系統編號——兩欄長得一模一樣的話，使用者根本看不出來哪一欄是自己可以改的。
      customCode: f.displayName,
      code: f.zoneCode ?? `${f.floor}-${f.id}`,
      // 現場技術人員與供應商講的是這組名稱（例如 FC_MC3_12），到現場逐台核對時用得到。
      vendorLabel: fcuVendorLabel(f),
      location: f.floor,
      roomTemp: online ? f.value?.temperature ?? null : null,
      mode: online && f.value ? fcuModeLabel(f.value.mode) : '--',
      fanSpeed: online && f.value ? fcuFanSpeedLabel(f.value.fanSpeed) : '--',
      status,
      isExceeded: status === 'ABNORMAL',
    };
  });
});

const page = ref(1);
const pageSize = ref(20);
const paged = computed(() => rows.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value));

// ── 自訂代碼編輯 ──────────────────────────
const editOpen = ref(false);
const editSaving = ref(false);
const editError = ref('');
const editTarget = ref<{ id: number; code: string; vendorLabel: string; customCode: string | null } | null>(null);
const editValue = ref('');

function openEdit(row: { id: number; code: string; vendorLabel: string; customCode: string | null }) {
  editTarget.value = row;
  editValue.value = row.customCode ?? '';
  editError.value = '';
  editOpen.value = true;
}

async function submitEdit() {
  if (!editTarget.value || editSaving.value) return;
  const value = editValue.value.trim();
  if (value.length > 64) {
    editError.value = '自訂代碼最多 64 個字。';
    return;
  }
  // 沒改就直接關掉，不要打 API，避免操作紀錄留下前後一模一樣的空紀錄。
  if (value === (editTarget.value.customCode ?? '')) {
    editOpen.value = false;
    return;
  }
  editSaving.value = true;
  editError.value = '';
  try {
    await updateFcuDisplayNameApi(editTarget.value.id, value);
    await queryClient.invalidateQueries({ queryKey: ['fcu-page-list'] });
    editOpen.value = false;
  } catch (err) {
    editError.value = err instanceof ApiError && err.status === 403
      ? '目前登入的帳號沒有「FCU管理」的編輯權限。'
      : err instanceof Error ? err.message : '儲存失敗，請稍後再試。';
  } finally {
    editSaving.value = false;
  }
}

// ── 室溫上下限設定 面板 ──────────────────
const panelOpen = ref(false);
const panelLoading = ref(false);
const saving = ref(false);
const saveError = ref('');
// vee-validate 的 useForm 同時取代了原本手寫的 reactive(form) + original ref 做 dirty-check
// 那套邏輯，見 AdminChillerPage.vue 同樣的說明。
const { defineField, errors, meta, handleSubmit, resetForm } = useForm<FcuThresholdConfig>({
  validationSchema: toTypedSchema(fcuThresholdSchema),
  initialValues: { roomTempMin: null, roomTempMax: null },
});
const [roomTempMin, roomTempMinAttrs] = defineField('roomTempMin');
const [roomTempMax, roomTempMaxAttrs] = defineField('roomTempMax');

async function openPanel() {
  saveError.value = '';
  panelOpen.value = true;
  panelLoading.value = true;
  try {
    const cfg = await getFcuThresholdApi();
    resetForm({ values: cfg });
  } catch (err) {
    saveError.value = err instanceof ApiError && err.status === 403
      ? '目前登入的帳號沒有「告警門檻」的查看權限。'
      : err instanceof Error ? err.message : '讀取設定失敗，請稍後再試。';
  } finally {
    panelLoading.value = false;
  }
}

const save = handleSubmit(async (values) => {
  if (saving.value) return;
  saving.value = true;
  saveError.value = '';
  try {
    await saveFcuThresholdApi({ ...values });
    await queryClient.invalidateQueries({ queryKey: ['fcu-page-alarms'] });
    panelOpen.value = false;
  } catch (err) {
    saveError.value = err instanceof ApiError && err.status === 403
      ? '目前登入的帳號沒有「告警門檻」的編輯權限。'
      : err instanceof Error ? err.message : '儲存失敗，請稍後再試。';
  } finally {
    saving.value = false;
  }
});
</script>

<template>
  <AdminLayout page-title="FCU管理" current-path="/admin/fcu">
    <div class="flex flex-col gap-4">
      <div class="flex justify-end">
        <AdminButton variant="primary" size="sm" @click="openPanel">室溫上下限設定</AdminButton>
      </div>

      <div v-if="loadError" class="p-3 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-sm">{{ loadError }}</div>
      <div v-else-if="alarmWarning" class="p-3 rounded-lg bg-[#FFFBEB] text-[#B45309] text-sm">{{ alarmWarning }}</div>

      <div class="bg-white rounded-xl border border-[#E2E8F0] overflow-hidden">
        <div class="overflow-x-auto">
          <table class="w-full text-left text-[13px] text-[#334155]">
            <thead class="bg-[#F8FAFC] text-[#64748B] border-b border-[#E2E8F0]">
              <tr>
                <th class="px-4 py-3 font-semibold">自訂代碼</th>
                <th class="px-4 py-3 font-semibold">系統編號</th>
                <th class="px-4 py-3 font-semibold whitespace-nowrap">供應商編號</th>
                <th class="px-4 py-3 font-semibold">安裝位置</th>
                <th class="px-4 py-3 font-semibold">室內溫度</th>
                <th class="px-4 py-3 font-semibold">風速</th>
                <th class="px-4 py-3 font-semibold">運轉模式</th>
                <th class="px-4 py-3 font-semibold">狀態</th>
                <th class="px-4 py-3 font-semibold">操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="d in paged" :key="d.id" class="border-b border-[#F1F5F9] hover:bg-slate-50/70">
                <td class="px-4 py-3 font-medium">
                  <span v-if="d.customCode">{{ d.customCode }}</span>
                  <span v-else class="text-[#94A3B8] font-normal">未設定</span>
                </td>
                <td class="px-4 py-3 font-tabular text-[#64748B]">{{ d.code }}</td>
                <td class="px-4 py-3 font-tabular text-[#64748B] whitespace-nowrap">{{ d.vendorLabel }}</td>
                <td class="px-4 py-3 text-[#64748B]">{{ d.location }}</td>
                <td class="px-4 py-3 font-tabular" :class="{ 'text-[#FF4757] font-bold': d.isExceeded }">
                  {{ d.roomTemp !== null ? `${d.roomTemp.toFixed(1)} °C` : '--' }}
                </td>
                <td class="px-4 py-3">{{ d.fanSpeed }}</td>
                <td class="px-4 py-3">{{ d.mode }}</td>
                <td class="px-4 py-3"><AdminStatusBadge :status="d.status" /></td>
                <td class="px-4 py-3">
                  <button type="button" class="link-action" @click="openEdit(d)">編輯</button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <EmptyState v-if="!loadError && rows.length === 0" title="查無 FCU 設備" description="目前沒有任何 FCU 設備資料。" />
        <AdminPagination v-else-if="!loadError" :total="rows.length" v-model:page="page" v-model:page-size="pageSize" />
      </div>
    </div>

    <!-- 室溫上下限設定 面板 -->
    <AdminRightPanel
      :open="panelOpen"
      title="室溫上下限設定"
      :can-save="meta.dirty && meta.valid"
      :saving="saving"
      @close="panelOpen = false"
      @save="save"
    >
      <div v-if="panelLoading" class="text-xs text-[#94A3B8] py-4 text-center">載入中…</div>
      <div v-else class="flex flex-col gap-6">
        <div v-if="saveError" class="p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ saveError }}</div>

        <section>
          <h4 class="text-sm font-bold text-[#1A202C]">室內溫度上下限 (°C)</h4>
          <p class="text-xs text-[#94A3B8] mt-0.5 mb-2">
            當室內溫度不在設定範圍時將觸發異常告警；適用於全廠所有 FCU（供應商 SDK 沒有提供 FCU
            設定溫度，無法用「溫差」判斷，改用絕對室溫上下限）
          </p>
          <div class="flex items-center gap-2">
            <input v-model="roomTempMin" v-bind="roomTempMinAttrs" type="number" step="0.1" placeholder="最低溫度" class="flex-1 min-w-0 px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]" />
            <span class="text-[#94A3B8]">~</span>
            <input v-model="roomTempMax" v-bind="roomTempMaxAttrs" type="number" step="0.1" placeholder="最高溫度" class="flex-1 min-w-0 px-3 py-2 text-sm border rounded-lg focus:outline-none placeholder-[#94A3B8]" :class="errors.roomTempMax ? 'border-[#FF4757] text-[#FF4757] focus:border-[#FF4757]' : 'border-[#CBD5E1] text-black focus:border-[#00D1B2]'" />
          </div>
          <p v-if="errors.roomTempMax" class="text-xs text-[#FF4757] mt-1">{{ errors.roomTempMax }}</p>
        </section>

        <p class="text-xs text-[#94A3B8]">
          門檻改變後，儲存後約數秒內自動套用新設定，不需要重新啟動系統。數值超出範圍需持續 60 秒才會告警，所以存檔後約 1 分鐘才會看到結果；回到範圍內則在下一次讀取（數秒內）解除。
        </p>
      </div>
    </AdminRightPanel>

    <!-- 自訂代碼 -->
    <div v-if="editOpen" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/40" @click.self="editOpen = false">
      <div class="bg-white rounded-xl shadow-2xl border border-[#E2E8F0] w-full max-w-[420px] overflow-hidden">
        <div class="px-6 pt-6 pb-4 border-b border-[#E2E8F0]">
          <h3 class="text-base font-bold text-[#1A202C]">編輯自訂代碼</h3>
          <p class="text-xs text-[#94A3B8] mt-1">系統編號 <span class="font-tabular text-[#64748B]">{{ editTarget?.code }}</span>、供應商編號 <span class="font-tabular text-[#64748B]">{{ editTarget?.vendorLabel }}</span>（由現場接線決定，不可修改）</p>
        </div>
        <form class="px-6 py-5 flex flex-col gap-4" @submit.prevent="submitEdit">
          <div v-if="editError" class="p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ editError }}</div>
          <label class="flex flex-col gap-1.5 text-xs text-[#64748B]">
            自訂代碼
            <input v-model="editValue" type="text" maxlength="64" placeholder="例如現場標籤上的編號 AC-B1-012" class="px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]" />
          </label>
          <p class="text-xs text-[#94A3B8]">留空代表不設定，清單與告警會改回顯示系統編號。</p>
          <div class="flex justify-end gap-3 pt-1">
            <AdminButton variant="tertiary" type="button" :disabled="editSaving" @click="editOpen = false">取消</AdminButton>
            <AdminButton variant="primary" type="submit" :disabled="editSaving">
              {{ editSaving ? '儲存中…' : '儲存' }}
            </AdminButton>
          </div>
        </form>
      </div>
    </div>
  </AdminLayout>
</template>
