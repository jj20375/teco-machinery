<script setup lang="ts">
defineProps<{ title: string; hint?: string }>();
</script>

<template>
  <section class="bg-white rounded-xl border border-[#E2E8F0] overflow-hidden">
    <header class="px-5 py-3 border-b border-[#E2E8F0] flex items-baseline gap-3 flex-wrap">
      <h3 class="text-[15px] font-bold text-[#1A202C]">{{ title }}</h3>
      <span v-if="hint" class="text-[12px] text-[#94A3B8]">{{ hint }}</span>
      <div class="ml-auto"><slot name="actions" /></div>
    </header>
    <div class="p-5"><slot /></div>
  </section>
</template>
