<script setup lang="ts">
/**
 * 冰水主機保養狀態＋「保養完成・重置」，比照機車換機油：距上次保養達到間隔就亮「待保養」，
 * 沒保養時數會繼續累積、燈一直亮著，按下保養完成才熄燈並從當下時數重新計算。
 * 也允許還沒到時間就提前保養（跟提早換機油一樣），一樣會重新起算。
 */
import { ref, computed, watch } from 'vue';
import { useQuery, useQueryClient } from '@tanstack/vue-query';
import AdminButton from '../../_components/AdminButton.vue';
import AdminStatusBadge from '../../_components/AdminStatusBadge.vue';
import { ApiError, getSessionApi } from '../../_services/auth-service';
import { getChillerMaintenanceApi, resetChillerMaintenanceApi, formatAlarmTime } from '../../_services/hvac-service';

const props = defineProps<{
  open: boolean;
  chiller: { id: number; code: string; name: string } | null;
}>();
const emit = defineEmits<{ close: []; reset: [] }>();

const queryClient = useQueryClient();
const chillerId = computed(() => props.chiller?.id ?? 0);
const query = useQuery({
  queryKey: ['chiller-maintenance', chillerId],
  queryFn: () => getChillerMaintenanceApi(chillerId.value),
  enabled: computed(() => props.open && chillerId.value > 0),
  refetchInterval: 10_000,
});
const status = computed(() => query.data.value ?? null);

const loadError = computed(() => {
  const err = query.error.value;
  if (!err) return '';
  return err instanceof ApiError && err.status === 403
    ? '目前登入的帳號沒有「冰水主機管理」的查看權限。'
    : err instanceof Error ? err.message : '讀取保養狀態失敗，請稍後再試。';
});

const confirming = ref(false);
const memo = ref('');
const resetting = ref(false);
const resetError = ref('');

// 按鈕只是顯示優化，真正的權限檢查在後端（hvac.thresholds:update）。
// 平台範圍不帶場館權限清單，一律顯示、交給後端判斷。
const canReset = ref(false);
watch(() => props.open, (open) => {
  if (!open) return;
  confirming.value = false;
  memo.value = '';
  resetError.value = '';
  const session = getSessionApi();
  canReset.value = !!session && (session.user.scopeKind !== 'merchant'
    || session.user.grants.some((g) => g.code === 'hvac.thresholds' && g.actions.includes('update')));
}, { immediate: true });

const progressPct = computed(() => {
  const s = status.value;
  if (!s?.intervalHours || s.hoursSinceService === null) return 0;
  return Math.min(100, Math.round((s.hoursSinceService / s.intervalHours) * 100));
});

async function submitReset() {
  if (!props.chiller || resetting.value) return;
  resetting.value = true;
  resetError.value = '';
  try {
    await resetChillerMaintenanceApi(props.chiller.id, memo.value);
    confirming.value = false;
    memo.value = '';
    await queryClient.invalidateQueries({ queryKey: ['chiller-maintenance'] });
    emit('reset');
  } catch (err) {
    resetError.value = err instanceof ApiError && err.status === 403
      ? '目前登入的帳號沒有「告警門檻」的編輯權限，無法標記保養完成。'
      : err instanceof Error ? err.message : '操作失敗，請稍後再試。';
  } finally {
    resetting.value = false;
  }
}

function hours(n: number | null | undefined): string {
  return n === null || n === undefined ? '--' : `${n.toLocaleString()} h`;
}
</script>

<template>
  <div v-if="open" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/40" @click.self="emit('close')">
    <div class="bg-white rounded-xl shadow-2xl border border-[#E2E8F0] w-full max-w-[480px] max-h-[90vh] overflow-y-auto">
      <div class="px-6 pt-6 pb-4 border-b border-[#E2E8F0] flex items-start justify-between gap-3">
        <div>
          <h3 class="text-base font-bold text-[#1A202C]">保養狀態</h3>
          <p class="text-xs text-[#94A3B8] mt-1">{{ chiller?.name }}｜<span class="font-tabular">{{ chiller?.code }}</span></p>
        </div>
        <AdminStatusBadge v-if="status?.isDue" status="MAINTENANCE" />
      </div>

      <div class="px-6 py-5 flex flex-col gap-5">
        <div v-if="loadError" class="p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ loadError }}</div>
        <div v-else-if="!status" class="text-xs text-[#94A3B8] py-4 text-center">載入中…</div>

        <template v-else>
          <div v-if="status.intervalHours === null" class="p-3 rounded-lg bg-[#FFFBEB] text-[#B45309] text-xs">
            尚未設定保養間隔，請先到「告警門檻」設定「保養間隔」，系統才會開始計算保養時數。
          </div>

          <template v-else>
            <section>
              <div class="flex items-baseline justify-between">
                <span class="text-xs text-[#64748B]">距上次保養</span>
                <span class="text-xs text-[#94A3B8]">保養間隔 {{ hours(status.intervalHours) }}</span>
              </div>
              <div class="mt-1 text-2xl font-bold font-tabular" :class="status.isDue ? 'text-[#9A7B1F]' : 'text-[#1A202C]'">
                {{ hours(status.hoursSinceService) }}
              </div>
              <div class="mt-2 h-2 rounded-full bg-[#F1F5F9] overflow-hidden">
                <div class="h-full rounded-full transition-all" :class="status.isDue ? 'bg-[#FBBF24]' : 'bg-[#00D1B2]'" :style="{ width: `${progressPct}%` }" />
              </div>
              <p class="mt-1.5 text-xs" :class="status.isDue ? 'text-[#9A7B1F] font-semibold' : 'text-[#64748B]'">
                <template v-if="status.baselineHours === null">尚未開始計算（主機回報累積時數後自動以當下時數起算）</template>
                <template v-else-if="status.isDue">
                  已達保養時數<template v-if="status.remainingHours !== null && status.remainingHours < 0">，超過 {{ hours(-status.remainingHours) }}</template>，請安排保養
                </template>
                <template v-else-if="status.remainingHours !== null">再運轉 {{ hours(Math.max(0, status.remainingHours)) }} 需要保養</template>
              </p>
            </section>

            <dl class="grid grid-cols-2 gap-y-2 text-xs">
              <dt class="text-[#94A3B8]">目前累積運轉</dt>
              <dd class="font-tabular text-[#334155] text-right">{{ hours(status.currentHours) }}</dd>
              <dt class="text-[#94A3B8]">上次保養時累積</dt>
              <dd class="font-tabular text-[#334155] text-right">{{ hours(status.baselineHours) }}</dd>
              <dt class="text-[#94A3B8]">上次保養（或開始計算）</dt>
              <dd class="font-tabular text-[#334155] text-right">{{ status.baselineAt ? formatAlarmTime(status.baselineAt) : '--' }}</dd>
            </dl>
          </template>

          <section v-if="canReset" class="border-t border-[#E2E8F0] pt-4">
            <div v-if="resetError" class="mb-3 p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ resetError }}</div>
            <div v-if="!confirming" class="flex justify-end">
              <AdminButton variant="primary" type="button" :disabled="status.intervalHours === null" @click="confirming = true">
                保養完成・重置
              </AdminButton>
            </div>
            <form v-else class="flex flex-col gap-3" @submit.prevent="submitReset">
              <p class="text-xs text-[#334155]">
                確認這台主機已完成保養？按下後
                <template v-if="status.isDue">「待保養」提醒會解除，</template>
                保養時數會以目前累積的 <span class="font-tabular font-semibold">{{ hours(status.currentHours) }}</span> 重新計算。
              </p>
              <label class="flex flex-col gap-1.5 text-xs text-[#64748B]">
                備註（選填）
                <input v-model="memo" type="text" maxlength="255" placeholder="例如 更換濾網、保養廠商名稱" class="px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]" />
              </label>
              <div class="flex justify-end gap-3">
                <AdminButton variant="tertiary" type="button" :disabled="resetting" @click="confirming = false">取消</AdminButton>
                <AdminButton variant="primary" type="submit" :disabled="resetting">{{ resetting ? '處理中…' : '確認保養完成' }}</AdminButton>
              </div>
            </form>
          </section>

          <section v-if="status.history.length > 0" class="border-t border-[#E2E8F0] pt-4">
            <h4 class="text-xs font-bold text-[#1A202C] mb-2">保養紀錄</h4>
            <ul class="flex flex-col gap-2">
              <li v-for="h in status.history" :key="h.id" class="text-xs text-[#334155]">
                <div class="flex justify-between gap-2">
                  <span class="font-tabular">{{ formatAlarmTime(h.performedAt) }}</span>
                  <span class="text-[#64748B]">{{ h.performedByName ?? '--' }}</span>
                </div>
                <div class="text-[#94A3B8]">
                  累積 {{ hours(h.hoursAtReset) }}<template v-if="h.hoursSincePrevious !== null">，距上次保養 {{ hours(h.hoursSincePrevious) }}</template>
                  <template v-if="h.memo">｜{{ h.memo }}</template>
                </div>
              </li>
            </ul>
          </section>
        </template>

        <div class="flex justify-end">
          <AdminButton variant="tertiary" type="button" @click="emit('close')">關閉</AdminButton>
        </div>
      </div>
    </div>
  </div>
</template>
