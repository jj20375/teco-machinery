<script setup lang="ts">
/**
 * @file AdminButton.vue
 * 後台按鈕 — Figma component node 134-3875
 * primary：#00D1B2 底白字 / secondary：#00D1B2 外框 / tertiary：白底深字灰框
 */
import { computed } from 'vue';

const props = withDefaults(
  defineProps<{
    variant?: 'primary' | 'secondary' | 'tertiary';
    size?: 'sm' | 'md';
    disabled?: boolean;
    type?: 'button' | 'submit';
  }>(),
  { variant: 'primary', size: 'md', disabled: false, type: 'button' }
);

defineEmits<{ (e: 'click', ev: MouseEvent): void }>();

const variantClass = computed(() => {
  switch (props.variant) {
    case 'secondary':
      return 'bg-white text-[#00D1B2] border border-[#00D1B2] hover:bg-[#E6FBF7]';
    case 'tertiary':
      return 'bg-white text-[#334155] border border-[#CBD5E1] hover:bg-slate-50';
    default:
      return 'bg-[#00D1B2] text-white border border-[#00D1B2] hover:bg-[#00BBA0]';
  }
});

const sizeClass = computed(() =>
  props.size === 'sm' ? 'px-3 py-1.5 text-xs' : 'px-4 py-2 text-sm'
);
</script>

<template>
  <button
    :type="type"
    :disabled="disabled"
    :class="[
      'inline-flex items-center justify-center gap-1.5 rounded-lg font-semibold transition-colors cursor-pointer select-none disabled:cursor-not-allowed disabled:opacity-100 disabled:bg-slate-100 disabled:text-slate-400 disabled:border-slate-100',
      variantClass,
      sizeClass,
    ]"
    @click="$emit('click', $event)"
  >
    <slot />
  </button>
</template>
