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
import { ApiError } from '../_services/auth-service';
import {
  listChillersApi, listAlarmsApi, deriveChillerStatus, isDataQualityOnline,
  updateChillerDisplayNameApi,
} from '../_services/hvac-service';
import {
  getChillerThresholdApi, saveChillerThresholdApi, chillerExceededFlags,
  type ChillerThresholdConfig,
} from '../_services/threshold-service';
import { chillerThresholdSchema } from '../_services/threshold-validation';

const queryClient = useQueryClient();
const chillersQuery = useQuery({ queryKey: ['chiller-page-list'], queryFn: listChillersApi, refetchInterval: 10_000 });
const alarmsQuery = useQuery({ queryKey: ['chiller-page-alarms'], queryFn: () => listAlarmsApi('active'), refetchInterval: 10_000 });

const loading = computed(() => chillersQuery.isPending.value || alarmsQuery.isPending.value);

/**
 * 清單本身（hvac.chillers）跟告警清單（hvac.alarms）是兩個獨立權限，一個帳號可能只有其中一個
 * ——實測 merchant_editor 對應的自訂角色就是只有 chillers/fcus，沒有 alarms/thresholds（新權限
 * 加進權限目錄後，既有的自訂角色不會自動補上，要角色管理頁手動勾選）。分開處理：清單失敗才擋
 * 整頁，告警清單失敗只顯示提示，表格照樣看得到（超標旗標會保守顯示未超標）。
 */
const loadError = computed(() => {
  const err = chillersQuery.error.value;
  if (!err) return '';
  return err instanceof ApiError && err.status === 403
    ? '目前登入的帳號沒有「冰水主機管理」權限，無法查看清單。'
    : err instanceof Error
      ? err.message
      : '載入冰水主機清單失敗，請稍後再試。';
});
const alarmWarning = computed(() => {
  const err = alarmsQuery.error.value;
  if (!err) return '';
  return err instanceof ApiError && err.status === 403
    ? '目前登入的帳號沒有「告警」查看權限，下表的告警門檻超標標記可能不準確。'
    : '目前有效告警清單載入失敗，下表的告警門檻超標標記可能不準確。';
});

/**
 * 表格列不直接用後端原始形狀，先攤平成畫面需要的欄位——供水/回水/溫差/累積時數是即時值，
 * 三個超標旗標改從「目前有效告警」反查（見 chillerExceededFlags 的說明），不是自己拿門檻
 * 跟即時值比大小，這樣才會跟 AlarmEngine 的 debounce 判斷一致。離線或讀取失敗時數值不可信，
 * 顯示 0 但不做超標判斷（isDataQualityOnline 為 false 時 exceeded 一律為 false）。
 */
const rows = computed(() => {
  const alarms = alarmsQuery.data.value ?? [];
  return (chillersQuery.data.value ?? []).map((c) => {
    const online = isDataQualityOnline(c.dataQuality);
    const exceeded = online ? chillerExceededFlags(c.id, alarms) : { isSupplyTempExceeded: false, isReturnTempExceeded: false, isTempDiffExceeded: false };
    return {
      id: c.id,
      code: c.code,
      name: c.displayName,
      status: deriveChillerStatus(c),
      supplyTemp: online ? (c.value?.chilledWaterOutletTemperature ?? 0) : 0,
      returnTemp: online ? (c.value?.chilledWaterInletTemperature ?? 0) : 0,
      tempDiff: online ? (c.value?.chilledWaterTemperatureDifference ?? 0) : 0,
      cumulativeHours: c.value?.accumulatedRunningHours ?? 0,
      ...exceeded,
    };
  });
});

const page = ref(1);
const pageSize = ref(20);
const paged = computed(() => rows.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value));

// ── 自訂名稱編輯 ──────────────────────────────
// 跟 FCU 的差別：device_chiller.display_name 是 NOT NULL，所以這裡不接受留空。
const editOpen = ref(false);
const editSaving = ref(false);
const editError = ref('');
const editTarget = ref<{ id: number; code: string; name: string } | null>(null);
const editValue = ref('');

function openEdit(row: { id: number; code: string; name: string }) {
  editTarget.value = row;
  editValue.value = row.name;
  editError.value = '';
  editOpen.value = true;
}

async function submitEdit() {
  if (!editTarget.value || editSaving.value) return;
  const value = editValue.value.trim();
  if (!value) {
    editError.value = '顯示名稱不能留空。';
    return;
  }
  if (value.length > 64) {
    editError.value = '顯示名稱最多 64 個字。';
    return;
  }
  // 沒改就直接關掉，不要打 API——不然操作紀錄會留下「冰水主機 1 → 冰水主機 1」這種空紀錄。
  if (value === editTarget.value.name) {
    editOpen.value = false;
    return;
  }
  editSaving.value = true;
  editError.value = '';
  try {
    await updateChillerDisplayNameApi(editTarget.value.id, value);
    await queryClient.invalidateQueries({ queryKey: ['chiller-page-list'] });
    editOpen.value = false;
  } catch (err) {
    editError.value = err instanceof ApiError && err.status === 403
      ? '目前登入的帳號沒有「冰水主機管理」的編輯權限。'
      : err instanceof Error ? err.message : '儲存失敗，請稍後再試。';
  } finally {
    editSaving.value = false;
  }
}

// ── 告警門檻設定 面板 ──────────────────────────
const panelOpen = ref(false);
const panelLoading = ref(false);
const saving = ref(false);
const saveError = ref('');
const activeChiller = ref<{ code: string; name: string } | null>(null);

const EMPTY_BOUNDS: Omit<ChillerThresholdConfig, 'chillerCode'> = {
  supplyTempMin: null, supplyTempMax: null,
  returnTempMin: null, returnTempMax: null,
  tempDiffMin: null, tempDiffMax: null,
  maintenanceHoursLimit: null,
};

// vee-validate 的 useForm 同時取代了原本手寫的 reactive(form) + original ref 做 dirty-check
// 那套邏輯——meta.dirty 就是拿目前值跟最後一次 resetForm() 的值比較，語意完全一樣，
// 不用自己再維護一份 original。
const { defineField, errors, meta, handleSubmit, resetForm } = useForm<Omit<ChillerThresholdConfig, 'chillerCode'>>({
  validationSchema: toTypedSchema(chillerThresholdSchema),
  initialValues: EMPTY_BOUNDS,
});
const [supplyTempMin, supplyTempMinAttrs] = defineField('supplyTempMin');
const [supplyTempMax, supplyTempMaxAttrs] = defineField('supplyTempMax');
const [returnTempMin, returnTempMinAttrs] = defineField('returnTempMin');
const [returnTempMax, returnTempMaxAttrs] = defineField('returnTempMax');
const [tempDiffMin, tempDiffMinAttrs] = defineField('tempDiffMin');
const [tempDiffMax, tempDiffMaxAttrs] = defineField('tempDiffMax');
const [maintenanceHoursLimit, maintenanceHoursLimitAttrs] = defineField('maintenanceHoursLimit');

async function openPanel(row: { code: string; name: string }) {
  activeChiller.value = row;
  saveError.value = '';
  panelOpen.value = true;
  panelLoading.value = true;
  try {
    const cfg = await getChillerThresholdApi(row.code);
    const { chillerCode: _chillerCode, ...bounds } = cfg;
    resetForm({ values: bounds });
  } catch (err) {
    saveError.value = err instanceof ApiError && err.status === 403
      ? '目前登入的帳號沒有「告警門檻」的查看權限。'
      : err instanceof Error ? err.message : '讀取門檻設定失敗，請稍後再試。';
  } finally {
    panelLoading.value = false;
  }
}

// handleSubmit 包起來的函式會先跑 validationSchema，驗證沒過就不會呼叫下面這個 callback，
// 所以不用再另外檢查一次「有沒有下限大於上限」——防呆邏輯只放在 threshold-validation.ts 一處。
const save = handleSubmit(async (values) => {
  if (saving.value || !activeChiller.value) return;
  saving.value = true;
  saveError.value = '';
  try {
    await saveChillerThresholdApi(activeChiller.value.code, { ...values });
    // 門檻改變後，超標旗標要重新算，靠重打有效告警清單（AlarmEngine 約 1 分鐘內套用新門檻，
    // 這裡先只刷新畫面資料，不代表告警立刻反應——這是熱重載機制本身的延遲，不是前端的 bug）。
    await queryClient.invalidateQueries({ queryKey: ['chiller-page-alarms'] });
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
  <AdminLayout page-title="冰水主機管理" current-path="/admin/chiller">
    <div class="flex flex-col gap-4">
      <div v-if="loadError" class="p-3 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-sm">{{ loadError }}</div>
      <div v-else-if="alarmWarning" class="p-3 rounded-lg bg-[#FFFBEB] text-[#B45309] text-sm">{{ alarmWarning }}</div>

      <div class="bg-white rounded-xl border border-[#E2E8F0] overflow-hidden">
        <div class="overflow-x-auto">
          <table class="w-full text-left text-[13px] text-[#334155]">
            <thead class="bg-[#F8FAFC] text-[#64748B] border-b border-[#E2E8F0]">
              <tr>
                <th class="px-4 py-3 font-semibold">自訂名稱</th>
                <th class="px-4 py-3 font-semibold">系統編號</th>
                <th class="px-4 py-3 font-semibold">安裝位置</th>
                <th class="px-4 py-3 font-semibold">出水溫度</th>
                <th class="px-4 py-3 font-semibold">回水溫度</th>
                <th class="px-4 py-3 font-semibold">溫度差 ΔT (°C)</th>
                <th class="px-4 py-3 font-semibold">累積運轉</th>
                <th class="px-4 py-3 font-semibold">運轉狀態</th>
                <th class="px-4 py-3 font-semibold">操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="c in paged" :key="c.id" class="border-b border-[#F1F5F9] hover:bg-slate-50/70">
                <td class="px-4 py-3 font-medium">{{ c.name }}</td>
                <td class="px-4 py-3 font-tabular text-[#64748B]">{{ c.code }}</td>
                <td class="px-4 py-3 text-[#64748B]">機房</td>
                <td class="px-4 py-3 font-tabular" :class="c.isSupplyTempExceeded ? 'text-[#FF4757] font-bold' : ''">{{ c.supplyTemp.toFixed(1) }} °C</td>
                <td class="px-4 py-3 font-tabular" :class="c.isReturnTempExceeded ? 'text-[#FF4757] font-bold' : ''">{{ c.returnTemp.toFixed(1) }} °C</td>
                <td class="px-4 py-3 font-tabular" :class="c.isTempDiffExceeded ? 'text-[#FF4757] font-bold' : ''">{{ c.tempDiff.toFixed(1) }} °C</td>
                <td class="px-4 py-3 font-tabular text-[#64748B]">{{ c.cumulativeHours.toLocaleString() }} hrs</td>
                <td class="px-4 py-3"><AdminStatusBadge :status="c.status" /></td>
                <td class="px-4 py-3">
                  <div class="flex items-center gap-3">
                    <button type="button" class="link-action" @click="openEdit(c)">編輯名稱</button>
                    <button type="button" class="link-action" @click="openPanel(c)">告警門檻</button>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <AdminPagination v-if="!loadError" :total="rows.length" v-model:page="page" v-model:page-size="pageSize" />
      </div>
    </div>

    <!-- 告警門檻設定 面板 -->
    <AdminRightPanel
      :open="panelOpen"
      :title="activeChiller ? `${activeChiller.name}｜${activeChiller.code}` : ''"
      :can-save="meta.dirty && meta.valid"
      :saving="saving"
      @close="panelOpen = false"
      @save="save"
    >
      <div v-if="panelLoading" class="text-xs text-[#94A3B8] py-4 text-center">載入中…</div>
      <div v-else class="flex flex-col gap-6">
        <div v-if="saveError" class="p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ saveError }}</div>

        <section>
          <h4 class="text-sm font-bold text-[#1A202C]">出水溫度 (°C)</h4>
          <p class="text-xs text-[#94A3B8] mt-0.5 mb-2">當出水溫度不在設定範圍時將觸發出水異常告警；留空代表不設定該側門檻</p>
          <div class="flex items-center gap-2">
            <input v-model="supplyTempMin" v-bind="supplyTempMinAttrs" type="number" step="0.1" placeholder="最低溫度" class="flex-1 min-w-0 px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]" />
            <span class="text-[#94A3B8]">~</span>
            <input v-model="supplyTempMax" v-bind="supplyTempMaxAttrs" type="number" step="0.1" placeholder="最高溫度" class="flex-1 min-w-0 px-3 py-2 text-sm text-black border rounded-lg focus:outline-none placeholder-[#94A3B8]" :class="errors.supplyTempMax ? 'border-[#FF4757] text-[#FF4757] focus:border-[#FF4757]' : 'border-[#CBD5E1] text-black focus:border-[#00D1B2]'" />
          </div>
          <p v-if="errors.supplyTempMax" class="text-xs text-[#FF4757] mt-1">{{ errors.supplyTempMax }}</p>
        </section>

        <section>
          <h4 class="text-sm font-bold text-[#1A202C]">回水溫度 (°C)</h4>
          <p class="text-xs text-[#94A3B8] mt-0.5 mb-2">當回水溫度不在設定範圍時將觸發回水異常告警</p>
          <div class="flex items-center gap-2">
            <input v-model="returnTempMin" v-bind="returnTempMinAttrs" type="number" step="0.1" placeholder="最低溫度" class="flex-1 min-w-0 px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]" />
            <span class="text-[#94A3B8]">~</span>
            <input v-model="returnTempMax" v-bind="returnTempMaxAttrs" type="number" step="0.1" placeholder="最高溫度" class="flex-1 min-w-0 px-3 py-2 text-sm border rounded-lg focus:outline-none placeholder-[#94A3B8]" :class="errors.returnTempMax ? 'border-[#FF4757] text-[#FF4757] focus:border-[#FF4757]' : 'border-[#CBD5E1] text-black focus:border-[#00D1B2]'" />
          </div>
          <p v-if="errors.returnTempMax" class="text-xs text-[#FF4757] mt-1">{{ errors.returnTempMax }}</p>
        </section>

        <section>
          <h4 class="text-sm font-bold text-[#1A202C]">溫度差 ΔT (°C)</h4>
          <p class="text-xs text-[#94A3B8] mt-0.5 mb-2">當溫度差不在設定範圍時將觸發溫度差異常告警</p>
          <div class="flex items-center gap-2">
            <input v-model="tempDiffMin" v-bind="tempDiffMinAttrs" type="number" step="0.1" placeholder="最低溫度差" class="flex-1 min-w-0 px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]" />
            <span class="text-[#94A3B8]">~</span>
            <input v-model="tempDiffMax" v-bind="tempDiffMaxAttrs" type="number" step="0.1" placeholder="最高溫度差" class="flex-1 min-w-0 px-3 py-2 text-sm border rounded-lg focus:outline-none placeholder-[#94A3B8]" :class="errors.tempDiffMax ? 'border-[#FF4757] text-[#FF4757] focus:border-[#FF4757]' : 'border-[#CBD5E1] text-black focus:border-[#00D1B2]'" />
          </div>
          <p v-if="errors.tempDiffMax" class="text-xs text-[#FF4757] mt-1">{{ errors.tempDiffMax }}</p>
        </section>

        <section>
          <h4 class="text-sm font-bold text-[#1A202C]">運轉保護值 (小時)</h4>
          <p class="text-xs text-[#94A3B8] mt-0.5 mb-2">累積運轉時數達到此值，系統會記一筆保養提醒告警；留空代表不設定</p>
          <input v-model="maintenanceHoursLimit" v-bind="maintenanceHoursLimitAttrs" type="number" step="100" placeholder="不設定" class="w-full px-3 py-2 text-sm border rounded-lg focus:outline-none placeholder-[#94A3B8]" :class="errors.maintenanceHoursLimit ? 'border-[#FF4757] text-[#FF4757] focus:border-[#FF4757]' : 'border-[#CBD5E1] text-black focus:border-[#00D1B2]'" />
          <p v-if="errors.maintenanceHoursLimit" class="text-xs text-[#FF4757] mt-1">{{ errors.maintenanceHoursLimit }}</p>
        </section>

        <p class="text-xs text-[#94A3B8]">
          門檻改變後，Collector 會在約一分鐘內自動套用新設定，不需要重新啟動系統。
        </p>
      </div>
    </AdminRightPanel>

    <!-- 自訂名稱 -->
    <div v-if="editOpen" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/40" @click.self="editOpen = false">
      <div class="bg-white rounded-xl shadow-2xl border border-[#E2E8F0] w-full max-w-[420px] overflow-hidden">
        <div class="px-6 pt-6 pb-4 border-b border-[#E2E8F0]">
          <h3 class="text-base font-bold text-[#1A202C]">編輯自訂名稱</h3>
          <p class="text-xs text-[#94A3B8] mt-1">系統編號 <span class="font-tabular text-[#64748B]">{{ editTarget?.code }}</span>（對應 Modbus 位址，不可修改）</p>
        </div>
        <form class="px-6 py-5 flex flex-col gap-4" @submit.prevent="submitEdit">
          <div v-if="editError" class="p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ editError }}</div>
          <label class="flex flex-col gap-1.5 text-xs text-[#64748B]">
            自訂名稱
            <input v-model="editValue" type="text" maxlength="64" placeholder="例如 冰水主機 1 或現場標籤編號" class="px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]" />
          </label>
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
