<script setup lang="ts">
/**
 * @file AdminAllAlarmsModal.vue
 * 「全部告警」彈窗 — 對齊設計稿「查看全部告警」：標題＋「N 筆告警中」、時間／設備名稱／設備編號／
 * 供應商編號（FCU 才有，現場技術人員跟供應商對照用）／安裝位置／狀態。設備恢復正常後告警會從有效清單移除，歷史紀錄在「異常告警報表」。
 */
import AdminStatusBadge from './AdminStatusBadge.vue';
import { alarmBadgeStatus, formatAlarmTime, type AlarmRow } from '../_services/hvac-service';

defineProps<{ open: boolean; alarms: AlarmRow[]; error: string }>();
const emit = defineEmits<{ (e: 'close'): void }>();
</script>

<template>
  <!-- 彈窗掛在 AdminHeader 裡，header 的 z-20 會形成獨立圖層順序，不移到 body 會被側邊欄（z-30）蓋住。 -->
  <Teleport to="body">
  <div
    v-if="open"
    class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/40"
    @click.self="emit('close')"
  >
    <div class="w-full max-w-[1152px] max-h-[85vh] flex flex-col bg-white rounded-xl border border-[#E0E0E5] shadow-2xl overflow-hidden">
      <div class="px-6 pt-5 pb-4 flex items-center justify-between border-b border-[#E2E8F0]">
        <div class="flex items-center gap-2">
          <h3 class="text-lg font-bold text-[#1A202C]">全部告警</h3>
          <span class="px-2 py-0.5 rounded bg-[#FFF5F5] text-[#FF4757] text-xs font-semibold">{{ alarms.length }} 筆告警中</span>
        </div>
        <button type="button" class="text-[#94A3B8] hover:text-[#334155] -mr-1" aria-label="關閉" @click="emit('close')">
          <svg class="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12" />
          </svg>
        </button>
      </div>

      <div class="p-6 overflow-y-auto">
        <div v-if="error" class="p-3 rounded-lg bg-[#FFFBEB] text-[#B45309] text-sm">{{ error }}</div>
        <div v-else class="rounded-lg border border-[#E2E8F0] overflow-hidden">
          <table class="w-full text-left text-[13px] text-[#334155]">
            <thead class="bg-[#F8FAFC] text-[#64748B] border-b border-[#E2E8F0]">
              <tr>
                <th class="px-4 py-3 font-semibold whitespace-nowrap">時間</th>
                <th class="px-4 py-3 font-semibold">設備名稱</th>
                <th class="px-4 py-3 font-semibold">設備編號</th>
                <th class="px-4 py-3 font-semibold whitespace-nowrap">供應商編號</th>
                <th class="px-4 py-3 font-semibold">安裝位置</th>
                <th class="px-4 py-3 font-semibold">狀態</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="a in alarms" :key="a.id" class="border-b border-[#F1F5F9] last:border-b-0">
                <td class="px-4 py-3 font-tabular text-[#64748B] whitespace-nowrap">{{ formatAlarmTime(a.startedAt) }}</td>
                <td class="px-4 py-3 font-medium" :class="{ 'text-[#94A3B8] font-normal': a.deviceName === '未設定' }">{{ a.deviceName }}</td>
                <td class="px-4 py-3 font-tabular text-[#64748B]">{{ a.deviceCode }}</td>
                <td class="px-4 py-3 font-tabular text-[#64748B] whitespace-nowrap">{{ a.vendorLabel ?? '—' }}</td>
                <td class="px-4 py-3 text-[#64748B]">{{ a.location }}</td>
                <td class="px-4 py-3"><AdminStatusBadge :status="alarmBadgeStatus(a)" :reason="a.ruleLabel" /></td>
              </tr>
              <tr v-if="alarms.length === 0">
                <td colspan="6" class="px-4 py-6 text-center text-[#94A3B8]">目前沒有任何告警中的設備</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </div>
  </div>
  </Teleport>
</template>
