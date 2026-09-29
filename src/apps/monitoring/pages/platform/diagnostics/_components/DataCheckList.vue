<script setup lang="ts">
import LevelBadge from './LevelBadge.vue';
import type { DiagnosticCheck } from '../../_services/diagnostics-service';

defineProps<{ checks: DiagnosticCheck[] }>();
</script>

<template>
  <table class="w-full text-left text-[13px] text-[#334155]">
    <thead class="text-[#64748B] border-b border-[#E2E8F0]">
      <tr>
        <th class="py-2 pr-3 font-medium w-[90px]">類別</th>
        <th class="py-2 pr-3 font-medium w-[260px]">檢查項目</th>
        <th class="py-2 pr-3 font-medium w-[90px]">結果</th>
        <th class="py-2 font-medium">說明</th>
      </tr>
    </thead>
    <tbody>
      <tr v-for="c in checks" :key="c.key" class="border-b border-[#F1F5F9] align-top">
        <td class="py-2 pr-3 text-[#64748B]">{{ c.category }}</td>
        <td class="py-2 pr-3 font-medium">{{ c.label }}</td>
        <td class="py-2 pr-3"><LevelBadge :level="c.level" /></td>
        <td class="py-2 text-[#4A5568] break-all">{{ c.detail }}</td>
      </tr>
    </tbody>
  </table>
</template>
