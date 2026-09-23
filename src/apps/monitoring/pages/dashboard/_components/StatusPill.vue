<script setup lang="ts">
import { computed } from 'vue';
import type { StatusType } from '@/ui/components/UiBadge.vue';

// 前台看板專用的實心狀態膠囊（對齊 Figma「FCU狀態樣式」元件）。
// 與後台共用的 UiBadge（外框+圓點）刻意不同，所以獨立成一個元件。
const props = withDefaults(defineProps<{ status: StatusType | string; text?: string }>(), { text: '' });

const CONFIG: Record<string, { label: string; cls: string }> = {
  RUNNING: { label: '運轉', cls: 'bg-[#5EEAD4] text-[#0F2A26]' },
  ABNORMAL: { label: '異常', cls: 'bg-[#F87171] text-white' },
  OFFLINE: { label: '離線', cls: 'bg-[#3B5BDB] text-white' },
  STOPPED: { label: '停止', cls: 'bg-[#9CA3AF] text-[#1F2937]' },
  MAINTENANCE: { label: '待保養', cls: 'bg-[#FBBF24] text-[#3F2D00]' },
  TEMP_DIFF_HIGH: { label: '溫差過高', cls: 'bg-[#F87171] text-white' },
  TEMP_DIFF_LOW: { label: '溫差過低', cls: 'bg-[#F87171] text-white' },
  FLOW_ABNORMAL: { label: '流量異常', cls: 'bg-[#F87171] text-white' },
};

const conf = computed(() => CONFIG[props.status] ?? { label: props.status, cls: 'bg-[#9CA3AF] text-[#1F2937]' });
</script>

<template>
  <span class="inline-flex items-center justify-center rounded-md px-2 py-0.5 text-xs font-bold whitespace-nowrap" :class="conf.cls">
    {{ text || conf.label }}
  </span>
</template>
