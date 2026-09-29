<script setup lang="ts">
import type { DiagnosticLevel } from '../../_services/diagnostics-service';

defineProps<{ level: DiagnosticLevel }>();

const STYLES: Record<DiagnosticLevel, { label: string; cls: string }> = {
  ok: { label: '正常', cls: 'bg-[#E6FBF7] text-[#00A38C]' },
  warn: { label: '需確認', cls: 'bg-[#FFF7E6] text-[#D97706]' },
  error: { label: '異常', cls: 'bg-[#FFF5F5] text-[#FF4757]' },
  unknown: { label: '無法判斷', cls: 'bg-[#F1F5F9] text-[#64748B]' },
};
</script>

<template>
  <span :class="['inline-flex items-center px-2 py-0.5 rounded text-[12px] font-bold whitespace-nowrap', STYLES[level].cls]">
    {{ STYLES[level].label }}
  </span>
</template>
