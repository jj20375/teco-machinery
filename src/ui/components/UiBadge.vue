<script setup lang="ts">
import { computed } from 'vue';

export type StatusType = 
  | 'RUNNING' 
  | 'STOPPED' 
  | 'ABNORMAL' 
  | 'OFFLINE' 
  | 'MAINTENANCE' 
  | 'TEMP_DIFF_HIGH' 
  | 'TEMP_DIFF_LOW' 
  | 'FLOW_ABNORMAL';

const props = withDefaults(
  defineProps<{
    status: StatusType | string;
    text?: string;
    size?: 'sm' | 'md' | 'lg';
    pulse?: boolean;
  }>(),
  {
    text: '',
    size: 'md',
    pulse: false,
  }
);

interface StatusConfig {
  label: string;
  bgClass: string;
  textClass: string;
  dotClass: string;
  borderClass: string;
}

const STATUS_MAP: Record<string, StatusConfig> = {
  RUNNING: {
    label: '運轉中',
    bgClass: 'bg-emerald-950/70',
    textClass: 'text-emerald-400',
    dotClass: 'bg-emerald-400',
    borderClass: 'border-emerald-500/30',
  },
  STOPPED: {
    label: '停止',
    bgClass: 'bg-zinc-800/80',
    textClass: 'text-zinc-400',
    dotClass: 'bg-zinc-400',
    borderClass: 'border-zinc-700/50',
  },
  ABNORMAL: {
    label: '異常',
    bgClass: 'bg-red-950/70',
    textClass: 'text-red-400',
    dotClass: 'bg-red-500',
    borderClass: 'border-red-500/40',
  },
  OFFLINE: {
    label: '離線',
    bgClass: 'bg-neutral-900',
    textClass: 'text-neutral-500',
    dotClass: 'bg-neutral-600',
    borderClass: 'border-neutral-800',
  },
  MAINTENANCE: {
    label: '待保養',
    bgClass: 'bg-amber-950/70',
    textClass: 'text-amber-400',
    dotClass: 'bg-amber-400',
    borderClass: 'border-amber-500/40',
  },
  TEMP_DIFF_HIGH: {
    label: '溫差過高',
    bgClass: 'bg-red-950/70',
    textClass: 'text-red-400',
    dotClass: 'bg-red-500',
    borderClass: 'border-red-500/40',
  },
  TEMP_DIFF_LOW: {
    label: '溫差過低',
    bgClass: 'bg-red-950/70',
    textClass: 'text-red-400',
    dotClass: 'bg-red-500',
    borderClass: 'border-red-500/40',
  },
  FLOW_ABNORMAL: {
    label: '流量異常',
    bgClass: 'bg-red-950/70',
    textClass: 'text-red-400',
    dotClass: 'bg-red-500',
    borderClass: 'border-red-500/40',
  },
};

const currentConfig = computed<StatusConfig>(() => {
  return STATUS_MAP[props.status] || {
    label: props.status,
    bgClass: 'bg-zinc-800',
    textClass: 'text-zinc-300',
    dotClass: 'bg-zinc-400',
    borderClass: 'border-zinc-700',
  };
});

const displayText = computed(() => props.text || currentConfig.value.label);

const sizeClasses = computed(() => {
  switch (props.size) {
    case 'sm':
      return 'text-xs px-2 py-0.5 gap-1.5';
    case 'lg':
      return 'text-sm px-3 py-1.5 gap-2 font-medium';
    default:
      return 'text-xs px-2.5 py-1 gap-1.5 font-medium';
  }
});
</script>

<template>
  <span
    :class="[
      'inline-flex items-center rounded-full border transition-all duration-200',
      currentConfig.bgClass,
      currentConfig.textClass,
      currentConfig.borderClass,
      sizeClasses,
    ]"
  >
    <span
      :class="[
        'w-1.5 h-1.5 rounded-full flex-shrink-0',
        currentConfig.dotClass,
        pulse || props.status === 'ABNORMAL' || props.status.startsWith('TEMP_DIFF') ? 'animate-pulse-dot' : '',
      ]"
    />
    <span>{{ displayText }}</span>
  </span>
</template>
