<script setup lang="ts">
/**
 * @file AdminStatusBadge.vue
 * 後台淡色狀態膠囊 — 嚴格對齊 Figma component node 313-1103
 * 運轉中 / 停止 / 異常(可帶原因) / 離線 / 待保養
 */
import { computed } from 'vue';

const props = withDefaults(
  defineProps<{
    /** 機台狀態 */
    status: 'RUNNING' | 'STOPPED' | 'ABNORMAL' | 'OFFLINE' | 'MAINTENANCE' | string;
    /** 異常原因（僅 status = ABNORMAL 時顯示，例如「溫度差過高」「出水溫度過高」） */
    reason?: string;
  }>(),
  { reason: '' }
);

interface BadgeStyle {
  label: string;
  bg: string;
  text: string;
}

const STYLE_MAP: Record<string, BadgeStyle> = {
  RUNNING: { label: '運轉中', bg: 'bg-[#E6FBF7]', text: 'text-[#10B981]' },
  STOPPED: { label: '停止', bg: 'bg-[#F7FAFC]', text: 'text-[#64748B]' },
  ABNORMAL: { label: '異常', bg: 'bg-[#FFF5F5]', text: 'text-[#FF4757]' },
  OFFLINE: { label: '離線', bg: 'bg-[#CEDAEC]', text: 'text-[#41628C]' },
  MAINTENANCE: { label: '待保養', bg: 'bg-[#F1F1F1]', text: 'text-[#9A7B1F]' },
};

const style = computed<BadgeStyle>(
  () => STYLE_MAP[props.status] ?? { label: props.status, bg: 'bg-slate-100', text: 'text-slate-600' }
);

const displayText = computed(() => {
  if (props.status === 'ABNORMAL' && props.reason) {
    return `異常：${props.reason}`;
  }
  return style.value.label;
});
</script>

<template>
  <span
    :class="[
      'inline-flex items-center rounded font-bold whitespace-nowrap px-2 py-0.5 text-xs leading-5',
      style.bg,
      style.text,
    ]"
  >
    {{ displayText }}
  </span>
</template>
