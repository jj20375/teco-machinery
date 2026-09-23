<script setup lang="ts">
/**
 * @file AdminStatusReferenceModal.vue
 * 即時告警狀態對照表 — 對齊 Figma node 549-9430（置中 modal）
 */
defineProps<{ open: boolean }>();
const emit = defineEmits<{ (e: 'close'): void }>();

interface Row {
  state: string;
  kind: '正常' | '告警';
  chiller: boolean;
  fcu: boolean;
  scope: '共用' | '專屬';
}

const ROWS: Row[] = [
  { state: '運轉中', kind: '正常', chiller: true, fcu: true, scope: '共用' },
  { state: '停止', kind: '告警', chiller: true, fcu: true, scope: '共用' },
  { state: '異常：溫度差過高/低', kind: '告警', chiller: true, fcu: true, scope: '共用' },
  { state: '離線', kind: '告警', chiller: true, fcu: true, scope: '共用' },
  { state: '異常：出水溫度過高/低', kind: '告警', chiller: true, fcu: false, scope: '專屬' },
  { state: '異常：回水溫度過高/低', kind: '告警', chiller: true, fcu: false, scope: '專屬' },
  { state: '異常：水流量過高/低', kind: '告警', chiller: true, fcu: false, scope: '專屬' },
  { state: '待保養', kind: '告警', chiller: true, fcu: false, scope: '專屬' },
];
</script>

<template>
    <div
      v-if="open"
      class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/40"
      @click.self="emit('close')"
    >
      <div class="w-full max-w-[732px] bg-white rounded-xl border border-[#E0E0E5] shadow-2xl overflow-hidden">
        <!-- Header -->
        <div class="px-6 pt-6 pb-4">
          <div class="flex items-start justify-between">
            <div>
              <h3 class="text-xl font-bold text-[#1A202C]">即時告警狀態對照表</h3>
              <p class="text-sm text-[#94A3B8] mt-1">冰水主機 vs FCU — 供工程師參考</p>
            </div>
            <button type="button" class="text-[#94A3B8] hover:text-[#334155] -mr-1" @click="emit('close')">
              <svg class="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          </div>
          <div class="flex gap-2 mt-3">
            <span class="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md bg-[#EFF6FF] text-[#2563EB] text-xs font-medium">
              <span class="w-1.5 h-1.5 rounded-full bg-[#2563EB]" />共用
            </span>
            <span class="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md bg-[#FFF7ED] text-[#C2410C] text-xs font-medium">
              <span class="w-1.5 h-1.5 rounded-full bg-[#F97316]" />僅冰水主機
            </span>
          </div>
        </div>

        <!-- Table -->
        <table class="w-full text-left text-sm">
          <thead class="bg-[#1E293B] text-white">
            <tr>
              <th class="px-6 py-3 font-semibold">告警狀態</th>
              <th class="px-4 py-3 font-semibold">類型</th>
              <th class="px-4 py-3 font-semibold">冰水主機</th>
              <th class="px-4 py-3 font-semibold">FCU</th>
              <th class="px-4 py-3 font-semibold">適用範圍</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(r, i) in ROWS" :key="i" class="border-b border-[#F1F5F9]">
              <td class="px-6 py-3 text-[#334155]">{{ r.state }}</td>
              <td class="px-4 py-3">
                <span
                  class="px-2 py-0.5 rounded text-xs font-semibold"
                  :class="r.kind === '正常' ? 'bg-[#E6FBF7] text-[#10B981]' : 'bg-[#FFF5F5] text-[#FF4757]'"
                >
                  {{ r.kind }}
                </span>
              </td>
              <td class="px-4 py-3">
                <svg class="w-4 h-4 text-[#10B981]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="3" d="M5 13l4 4L19 7" />
                </svg>
              </td>
              <td class="px-4 py-3">
                <svg v-if="r.fcu" class="w-4 h-4 text-[#10B981]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="3" d="M5 13l4 4L19 7" />
                </svg>
                <span v-else class="inline-block w-3 h-0.5 bg-[#CBD5E1]" />
              </td>
              <td class="px-4 py-3">
                <span
                  class="px-2 py-0.5 rounded text-xs font-medium"
                  :class="r.scope === '共用' ? 'bg-[#EFF6FF] text-[#2563EB]' : 'bg-[#FFF7ED] text-[#C2410C]'"
                >
                  {{ r.scope }}
                </span>
              </td>
            </tr>
          </tbody>
        </table>

        <!-- Footer -->
        <div class="px-6 py-4 bg-[#F8FAFC] flex flex-wrap gap-x-6 gap-y-1 text-xs text-[#64748B]">
          <span>共用狀態：4 個</span>
          <span>冰水主機專屬：4 個</span>
          <span>FCU 專屬：0 個</span>
          <span class="font-semibold text-[#334155]">總計：8 種狀態</span>
        </div>
      </div>
    </div>
</template>
