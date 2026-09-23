<script setup lang="ts">
import UiModal from '@/ui/components/UiModal.vue';
import UiButton from '@/ui/components/UiButton.vue';
import StatusPill from './StatusPill.vue';
import type { AlarmItem } from '../_types/dashboard-types';

function alarmStatus(text: string) {
  if (text.includes('離線')) return 'OFFLINE';
  if (text.includes('停止') || text.includes('排程')) return 'STOPPED';
  if (text.includes('保養')) return 'MAINTENANCE';
  return 'ABNORMAL';
}

defineProps<{
  open: boolean;
  alarms: AlarmItem[];
}>();

const emit = defineEmits<{
  (e: 'update:open', val: boolean): void;
  (e: 'close'): void;
}>();
</script>

<template>
  <UiModal
    :open="open"
    width="840px"
    @update:open="(val) => emit('update:open', val)"
    @close="emit('close')"
  >
    <template #title>
      <div class="flex items-center gap-2.5">
        <span class="w-2.5 h-2.5 rounded-full bg-red-500 animate-pulse" />
        <h3 class="text-base font-bold text-[#F0F6FC]">
          即時未解除告警清單
        </h3>
        <span class="px-2 py-0.5 rounded-full bg-red-950/80 border border-red-500/40 text-red-300 text-xs font-bold font-tabular">
          {{ alarms.length }} 筆告警
        </span>
      </div>
    </template>

    <!-- 告警表格清單 -->
    <div class="flex flex-col gap-3">
      <div class="text-xs text-[#8B949E] flex items-center justify-between pb-1 border-b border-[#30363D]">
        <span>當設備數值回歸正常範圍（運轉中），系統將自動自即時告警清單中移除。歷史紀錄完整保留於報表中心。</span>
      </div>

      <div class="overflow-x-auto">
        <table class="w-full text-left text-xs border-collapse">
          <thead>
            <tr class="border-b border-[#30363D] text-[#8B949E] bg-[#0D1117]/80">
              <th class="py-2.5 px-3">時間</th>
              <th class="py-2.5 px-3">設備名稱</th>
              <th class="py-2.5 px-3">設備編號</th>
              <th class="py-2.5 px-3">安裝位置</th>
              <th class="py-2.5 px-3">異常數值 (門檻)</th>
              <th class="py-2.5 px-3 text-right">告警狀態</th>
            </tr>
          </thead>
          <tbody class="divide-y divide-[#21262D]">
            <tr
              v-for="alarm in alarms"
              :key="alarm.id"
              class="hover:bg-[#0D1117] transition-colors"
            >
              <td class="py-2.5 px-3 font-tabular text-[#8B949E]">{{ alarm.time }}</td>
              <td class="py-2.5 px-3 font-bold text-white">{{ alarm.deviceName }}</td>
              <td class="py-2.5 px-3 font-tabular text-[#8B949E]">{{ alarm.deviceCode }}</td>
              <td class="py-2.5 px-3 text-[#8B949E]">{{ alarm.location }}</td>
              <td class="py-2.5 px-3">
                <span class="text-red-400 font-bold font-tabular">{{ alarm.triggerValue }}</span>
                <span class="text-[#8B949E] text-[11px] ml-1">({{ alarm.thresholdValue }})</span>
              </td>
              <td class="py-2.5 px-3 text-right">
                <StatusPill :status="alarmStatus(alarm.statusText)" :text="alarm.statusText" />
              </td>
            </tr>

            <tr v-if="alarms.length === 0">
              <td colspan="6" class="py-8 text-center text-emerald-400">
                ✓ 目前無任何未解除之異常告警
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>

    <template #footer>
      <UiButton variant="secondary" size="sm" @click="emit('update:open', false)">
        關閉視窗
      </UiButton>
    </template>
  </UiModal>
</template>
