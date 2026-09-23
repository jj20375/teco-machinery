<script setup lang="ts">
import FloorPlanViewer from '../../../floor-plan/FloorPlanViewer.vue';
defineProps<{ floor: 'B1' | 'B2'; runRate: number; runningCount: number; totalCount: number }>();
</script>

<template>
  <div class="bg-white rounded-xl border border-[#E2E8F0] p-4 flex flex-col gap-3">
    <div class="flex items-center justify-between gap-2">
      <h3 class="text-sm font-bold text-[#1A202C]">{{ floor }} 空間與設備狀態</h3>
      <a :href="floor === 'B1' ? '/admin/floor-plan' : '/admin/floor-plan?floor=B2'" class="text-xs text-[#157C95] hover:underline">配置設備 →</a>
    </div>
    <p class="text-xs text-[#64748B]">FCU 運轉率：{{ runRate }}%（{{ runningCount }}/{{ totalCount }} 台）</p>
    <div class="h-[290px]"><FloorPlanViewer :floor="floor" compact /></div>
    <div class="flex flex-wrap items-center gap-3 text-[11px] text-[#64748B]">
      <span class="flex items-center gap-1"><i class="w-2 h-2 rounded-full bg-[#60A5FA] shadow-[0_0_6px_#60A5FA]" />正常區域藍光</span>
      <span class="flex items-center gap-1"><i class="w-2 h-2 rounded-full bg-[#F87171] shadow-[0_0_6px_#F87171]" />異常區域紅光</span>
      <span class="flex items-center gap-1"><i class="w-2 h-2 rounded-sm bg-[#94A3B8]" />離線／停止</span>
      <span class="ml-auto">本機配置 · 模擬資料</span>
    </div>
  </div>
</template>
