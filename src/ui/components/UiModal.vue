<script setup lang="ts">
import { computed } from 'vue';

const props = withDefaults(
  defineProps<{
    open: boolean;
    title?: string;
    width?: string | number;
  }>(),
  {
    title: '',
    width: '640px',
  }
);

const emit = defineEmits<{
  (e: 'update:open', val: boolean): void;
  (e: 'close'): void;
}>();

function handleClose() {
  emit('update:open', false);
  emit('close');
}

const modalWidth = computed(() => {
  return typeof props.width === 'number' ? `${props.width}px` : props.width;
});
</script>

<template>
  <Teleport to="body">
    <div
      v-if="open"
      class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/75 backdrop-blur-xs transition-opacity duration-200"
      @click.self="handleClose"
    >
      <div
        :style="{ maxWidth: modalWidth, width: '100%' }"
        class="bg-[#161B22] border border-[#30363D] rounded-xl shadow-2xl flex flex-col max-h-[90vh] overflow-hidden animate-in fade-in zoom-in-95 duration-150"
      >
        <!-- Modal Header -->
        <div class="flex items-center justify-between px-5 py-4 border-b border-[#30363D]">
          <div class="flex items-center gap-2.5">
            <slot name="title">
              <h3 class="text-base font-semibold text-[#F0F6FC] tracking-wide">
                {{ title }}
              </h3>
            </slot>
          </div>
          <button
            type="button"
            class="text-[#8B949E] hover:text-[#F0F6FC] transition-colors p-1 rounded-md hover:bg-slate-800"
            @click="handleClose"
          >
            <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

        <!-- Modal Body -->
        <div class="p-5 overflow-y-auto flex-1 text-sm text-[#F0F6FC]">
          <slot />
        </div>

        <!-- Modal Footer -->
        <div v-if="$slots.footer" class="px-5 py-3.5 bg-[#0D1117]/60 border-t border-[#30363D] flex items-center justify-end gap-3">
          <slot name="footer" />
        </div>
      </div>
    </div>
  </Teleport>
</template>
