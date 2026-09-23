<script setup lang="ts">
/**
 * @file AdminDatePicker.vue
 * 後台單日日期選擇器 — 對齊 Figma「datepicker-calendar(日)」
 * 觸發欄（含日曆 icon）→ 白色 popover 月曆 + 取消 / 確認
 *
 * modelValue 格式：'YYYY-MM-DD'
 * 日期區間場景請放兩個此元件（起 / 迄）。
 */
import { ref, computed, watch, onMounted, onUnmounted } from 'vue';

const props = withDefaults(
  defineProps<{
    modelValue: string;
    /** 觸發欄前綴文字，例如「日期區間：」「日期區間(起)：」 */
    label?: string;
    /** 可選日期下限 / 上限（YYYY-MM-DD） */
    min?: string;
    max?: string;
    disabled?: boolean;
  }>(),
  { label: '', min: '', max: '', disabled: false }
);

const emit = defineEmits<{
  (e: 'update:modelValue', v: string): void;
}>();

const WEEKDAYS = ['日', '一', '二', '三', '四', '五', '六'];

const open = ref(false);
const rootEl = ref<HTMLElement | null>(null);

function parse(s: string): Date {
  const [y, m, d] = (s || '').split('-').map(Number);
  if (!y) return new Date();
  return new Date(y, (m || 1) - 1, d || 1);
}
function fmtIso(dt: Date): string {
  const y = dt.getFullYear();
  const m = String(dt.getMonth() + 1).padStart(2, '0');
  const d = String(dt.getDate()).padStart(2, '0');
  return `${y}-${m}-${d}`;
}
function fmtDisplay(s: string): string {
  return s ? s.replace(/-/g, '/') : '請選擇日期';
}

const today = new Date();
const todayIso = fmtIso(today);

/** popover 內的暫存選擇與檢視月份 */
const draft = ref(props.modelValue);
const viewYear = ref(parse(props.modelValue).getFullYear());
const viewMonth = ref(parse(props.modelValue).getMonth()); // 0-11

watch(
  () => props.modelValue,
  (v) => {
    if (!open.value) {
      draft.value = v;
      viewYear.value = parse(v).getFullYear();
      viewMonth.value = parse(v).getMonth();
    }
  }
);

interface Cell {
  date: Date;
  iso: string;
  day: number;
  inMonth: boolean;
  weekday: number;
  disabled: boolean;
}

const cells = computed<Cell[]>(() => {
  const first = new Date(viewYear.value, viewMonth.value, 1);
  const startOffset = first.getDay(); // 0=日
  const gridStart = new Date(viewYear.value, viewMonth.value, 1 - startOffset);
  const out: Cell[] = [];
  for (let i = 0; i < 42; i++) {
    const d = new Date(gridStart);
    d.setDate(gridStart.getDate() + i);
    const iso = fmtIso(d);
    out.push({
      date: d,
      iso,
      day: d.getDate(),
      inMonth: d.getMonth() === viewMonth.value,
      weekday: d.getDay(),
      disabled: (props.min && iso < props.min) || (props.max && iso > props.max) ? true : false,
    });
  }
  return out;
});

const monthTitle = computed(() => `${viewYear.value}年 ${viewMonth.value + 1}月`);

function prevMonth() {
  if (viewMonth.value === 0) {
    viewMonth.value = 11;
    viewYear.value -= 1;
  } else {
    viewMonth.value -= 1;
  }
}
function nextMonth() {
  if (viewMonth.value === 11) {
    viewMonth.value = 0;
    viewYear.value += 1;
  } else {
    viewMonth.value += 1;
  }
}

function openPanel() {
  if (props.disabled) return;
  draft.value = props.modelValue;
  viewYear.value = parse(props.modelValue).getFullYear();
  viewMonth.value = parse(props.modelValue).getMonth();
  open.value = true;
}
function pick(c: Cell) {
  if (c.disabled) return;
  draft.value = c.iso;
  if (!c.inMonth) {
    viewYear.value = c.date.getFullYear();
    viewMonth.value = c.date.getMonth();
  }
}
function confirm() {
  emit('update:modelValue', draft.value);
  open.value = false;
}
function cancel() {
  open.value = false;
}

function onDocMouseDown(e: MouseEvent) {
  if (open.value && rootEl.value && !rootEl.value.contains(e.target as Node)) cancel();
}
onMounted(() => document.addEventListener('mousedown', onDocMouseDown));
onUnmounted(() => document.removeEventListener('mousedown', onDocMouseDown));
</script>

<template>
  <div ref="rootEl" class="relative inline-flex items-center gap-2 text-xs text-[#64748B]">
    <span v-if="label">{{ label }}</span>

    <button
      type="button"
      :disabled="disabled"
      class="flex items-center gap-2 border border-[#CBD5E1] rounded-lg px-3 py-1.5 text-[#334155] focus:outline-none focus:border-[#00D1B2] disabled:bg-slate-50 disabled:text-[#94A3B8]"
      @click="open ? cancel() : openPanel()"
    >
      <svg class="w-3.5 h-3.5 text-[#94A3B8]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M8 7V3m8 4V3m-9 8h10M5 21h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z" />
      </svg>
      <span class="font-tabular">{{ fmtDisplay(modelValue) }}</span>
    </button>

    <!-- 月曆 popover -->
    <div
      v-if="open"
      class="absolute left-0 top-full mt-2 z-40 w-[300px] bg-white rounded-2xl border border-[#E2E8F0] shadow-xl p-4"
    >
      <!-- header -->
      <div class="flex items-center justify-between mb-2">
        <button
          type="button"
          class="w-8 h-8 rounded-lg border border-[#E2E8F0] flex items-center justify-center text-[#64748B] hover:bg-slate-50"
          @click="prevMonth"
        >
          <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M15 19l-7-7 7-7" /></svg>
        </button>
        <span class="text-sm font-bold text-[#1A202C]">{{ monthTitle }}</span>
        <button
          type="button"
          class="w-8 h-8 rounded-lg border border-[#E2E8F0] flex items-center justify-center text-[#64748B] hover:bg-slate-50"
          @click="nextMonth"
        >
          <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M9 5l7 7-7 7" /></svg>
        </button>
      </div>

      <!-- weekday -->
      <div class="grid grid-cols-7 text-center text-xs font-semibold mb-1">
        <span
          v-for="(w, i) in WEEKDAYS"
          :key="w"
          :class="i === 0 || i === 6 ? 'text-[#EF4444]' : 'text-[#94A3B8]'"
          class="py-1.5"
        >
          {{ w }}
        </span>
      </div>

      <!-- days -->
      <div class="grid grid-cols-7 text-center gap-y-1">
        <button
          v-for="c in cells"
          :key="c.iso"
          type="button"
          :disabled="c.disabled"
          class="mx-auto w-9 h-9 rounded-full text-sm flex items-center justify-center transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
          :class="[
            c.iso === draft
              ? 'bg-[#00D1B2] text-white font-semibold'
              : c.inMonth
                ? 'text-[#1E293B] hover:bg-slate-100'
                : 'text-[#CBD5E1] hover:bg-slate-50',
            c.iso === todayIso && c.iso !== draft ? 'bg-[#F1F5F9]' : '',
          ]"
          @click="pick(c)"
        >
          {{ c.day }}
        </button>
      </div>

      <!-- footer -->
      <div class="mt-3 pt-3 border-t border-[#E2E8F0] flex items-center justify-end gap-2">
        <button
          type="button"
          class="px-4 py-1.5 rounded-lg text-xs font-semibold bg-white text-[#00D1B2] border border-[#00D1B2] hover:bg-[#E6FBF7]"
          @click="cancel"
        >
          取消
        </button>
        <button
          type="button"
          class="px-4 py-1.5 rounded-lg text-xs font-semibold bg-[#00D1B2] text-white border border-[#00D1B2] hover:bg-[#00BBA0]"
          @click="confirm"
        >
          確認
        </button>
      </div>
    </div>
  </div>
</template>
