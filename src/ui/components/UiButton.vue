<script setup lang="ts">
import { computed } from 'vue';

const props = withDefaults(
  defineProps<{
    variant?: 'primary' | 'secondary' | 'danger' | 'ghost';
    size?: 'sm' | 'md' | 'lg';
    disabled?: boolean;
    loading?: boolean;
    block?: boolean;
  }>(),
  {
    variant: 'secondary',
    size: 'md',
    disabled: false,
    loading: false,
    block: false,
  }
);

const emit = defineEmits<{
  (e: 'click', event: MouseEvent): void;
}>();

const variantClasses = computed(() => {
  switch (props.variant) {
    case 'primary':
      return 'bg-teal-500 hover:bg-teal-400 text-slate-950 font-semibold border-teal-400/50 shadow-sm shadow-teal-500/20';
    case 'danger':
      return 'bg-red-900/40 hover:bg-red-900/60 text-red-300 border-red-700/60 shadow-sm shadow-red-900/20';
    case 'ghost':
      return 'bg-transparent hover:bg-slate-800/60 text-slate-300 border-transparent';
    default:
      return 'bg-slate-800/80 hover:bg-slate-700/90 text-slate-200 border-slate-700/60 shadow-sm';
  }
});

const sizeClasses = computed(() => {
  switch (props.size) {
    case 'sm':
      return 'px-2.5 py-1 text-xs rounded-md gap-1.5';
    case 'lg':
      return 'px-5 py-2.5 text-base rounded-lg gap-2.5 font-medium';
    default:
      return 'px-3.5 py-1.5 text-sm rounded-md gap-2';
  }
});
</script>

<template>
  <button
    type="button"
    :disabled="disabled || loading"
    :class="[
      'inline-flex items-center justify-center border transition-all duration-150 cursor-pointer select-none focus:outline-none focus:ring-2 focus:ring-teal-500/40 disabled:opacity-40 disabled:cursor-not-allowed',
      variantClasses,
      sizeClasses,
      block ? 'w-full' : '',
    ]"
    @click="(e) => emit('click', e)"
  >
    <svg
      v-if="loading"
      class="animate-spin -ml-0.5 mr-2 h-4 w-4"
      xmlns="http://www.w3.org/2000/svg"
      fill="none"
      viewBox="0 0 24 24"
    >
      <circle class="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" stroke-width="4" />
      <path class="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z" />
    </svg>
    <slot />
  </button>
</template>
