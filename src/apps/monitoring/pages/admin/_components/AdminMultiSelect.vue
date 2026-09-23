<script setup lang="ts">
/**
 * @file AdminMultiSelect.vue
 * 後台複選下拉 — 對齊 Figma「Multi-select Dropdown」元件
 * 白色 popover + checkbox 清單 + footer「確定 / 取消」（確定才套用）
 * 「全部」與其他選項互斥。
 */
import { ref, computed, watch, onMounted, onUnmounted } from 'vue';

const props = withDefaults(
  defineProps<{
    label: string;
    modelValue: string[];
    options: { value: string; label: string }[];
    /** 代表「全部」的值 */
    allValue?: string;
  }>(),
  { allValue: 'ALL' }
);

const emit = defineEmits<{
  (e: 'update:modelValue', v: string[]): void;
}>();

const open = ref(false);
const draft = ref<string[]>([...props.modelValue]);
const rootEl = ref<HTMLElement | null>(null);

watch(
  () => props.modelValue,
  (v) => {
    if (!open.value) draft.value = [...v];
  }
);

const summary = computed(() => {
  if (draft.value.length === 0 || draft.value.includes(props.allValue)) {
    return props.options.find((o) => o.value === props.allValue)?.label ?? '全部';
  }
  return props.options
    .filter((o) => draft.value.includes(o.value))
    .map((o) => o.label)
    .join('、');
});

function toggle(v: string) {
  if (v === props.allValue) {
    draft.value = [props.allValue];
    return;
  }
  const next = draft.value.filter((s) => s !== props.allValue);
  const i = next.indexOf(v);
  if (i >= 0) next.splice(i, 1);
  else next.push(v);
  draft.value = next.length ? next : [props.allValue];
}

function openPanel() {
  draft.value = props.modelValue.length ? [...props.modelValue] : [props.allValue];
  open.value = true;
}
function confirm() {
  emit('update:modelValue', [...draft.value]);
  open.value = false;
}
function cancel() {
  draft.value = [...props.modelValue];
  open.value = false;
}

function onDocClick(e: MouseEvent) {
  if (open.value && rootEl.value && !rootEl.value.contains(e.target as Node)) cancel();
}
onMounted(() => document.addEventListener('mousedown', onDocClick));
onUnmounted(() => document.removeEventListener('mousedown', onDocClick));
</script>

<template>
  <div ref="rootEl" class="relative text-xs">
    <button
      type="button"
      class="flex items-center gap-2 border border-[#CBD5E1] rounded-lg px-3 py-1.5 text-[#334155] focus:outline-none focus:border-[#00D1B2]"
      @click="open ? cancel() : openPanel()"
    >
      <span class="text-[#64748B]">{{ label }}：</span>
      <span class="max-w-[160px] truncate">{{ summary }}</span>
      <svg class="w-3 h-3 text-[#94A3B8]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 9l-7 7-7-7" />
      </svg>
    </button>

    <div
      v-if="open"
      class="absolute left-0 top-full mt-1 w-44 bg-white border border-[#E2E8F0] rounded-lg shadow-lg z-30 overflow-hidden"
    >
      <div class="py-1">
        <label
          v-for="o in options"
          :key="o.value"
          class="flex items-center gap-2 px-3 py-1.5 hover:bg-slate-50 cursor-pointer"
        >
          <input
            type="checkbox"
            :checked="draft.includes(o.value)"
            class="w-3.5 h-3.5 rounded accent-[#4C7DF0]"
            @change="toggle(o.value)"
          />
          <span class="text-[#334155]">{{ o.label }}</span>
        </label>
      </div>
      <div class="flex items-center justify-end gap-4 px-3 py-2 border-t border-[#E2E8F0]">
        <button type="button" class="text-[#64748B] hover:underline" @click="cancel">取消</button>
        <button type="button" class="text-[#23A3EE] font-medium hover:underline" @click="confirm">確定</button>
      </div>
    </div>
  </div>
</template>
