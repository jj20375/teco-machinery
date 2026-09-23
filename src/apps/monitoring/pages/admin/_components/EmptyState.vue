<script setup lang="ts">
withDefaults(
  defineProps<{
    title?: string;
    description?: string;
  }>(),
  {
    title: '查無資料',
    description: '請調整搜尋條件後再試一次',
  }
);
</script>

<template>
  <div class="flex flex-col items-center justify-center py-16 px-4 text-center select-none">
    <div class="w-20 h-20 mb-4 rounded-full bg-[#F1F5F9] flex items-center justify-center text-[#94A3B8]">
      <svg class="w-9 h-9" fill="none" stroke="currentColor" viewBox="0 0 24 24">
        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.5" d="M20 13V6a2 2 0 00-2-2H6a2 2 0 00-2 2v7m16 0v5a2 2 0 01-2 2H6a2 2 0 01-2-2v-5m16 0h-2.586a1 1 0 00-.707.293l-2.414 2.414a1 1 0 01-.707.293h-3.172a1 1 0 01-.707-.293l-2.414-2.414A1 1 0 006.586 13H4" />
      </svg>
    </div>
    <h4 class="text-sm font-bold text-[#334155] mb-1">{{ title }}</h4>
    <p class="text-xs text-[#94A3B8] max-w-sm">{{ description }}</p>
  </div>
</template>
