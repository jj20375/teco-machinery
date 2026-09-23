<script setup lang="ts">
/**
 * @file AdminPagination.vue
 * 後台表格底部分頁列 — 統一格式（Figma 各報表 frame）
 * 「顯示 X - Y 筆，共 N 筆資料 ｜ 每頁顯示 [20 ▾] 筆 ｜ 上一頁 [頁碼…] 下一頁」
 */
import { computed } from 'vue';

const props = withDefaults(
  defineProps<{
    total: number;
    page: number;
    pageSize: number;
    pageSizeOptions?: number[];
  }>(),
  { pageSizeOptions: () => [20, 50, 100] }
);

const emit = defineEmits<{
  (e: 'update:page', v: number): void;
  (e: 'update:pageSize', v: number): void;
}>();

const totalPages = computed(() => Math.max(1, Math.ceil(props.total / props.pageSize)));
const rangeStart = computed(() => (props.total === 0 ? 0 : (props.page - 1) * props.pageSize + 1));
const rangeEnd = computed(() => Math.min(props.page * props.pageSize, props.total));

/** 顯示的頁碼（最多 4 個，對齊 Figma） */
const pageNumbers = computed(() => {
  const max = Math.min(4, totalPages.value);
  let start = Math.max(1, props.page - 1);
  if (start + max - 1 > totalPages.value) start = Math.max(1, totalPages.value - max + 1);
  return Array.from({ length: max }, (_, i) => start + i);
});

function go(p: number) {
  if (p < 1 || p > totalPages.value || p === props.page) return;
  emit('update:page', p);
}

function onPageSize(e: Event) {
  emit('update:pageSize', Number((e.target as HTMLSelectElement).value));
  emit('update:page', 1);
}
</script>

<template>
  <div class="flex flex-col sm:flex-row items-center justify-between gap-3 px-4 py-3 border-t border-[#E2E8F0] text-xs text-[#64748B]">
    <div class="flex items-center gap-4">
      <span>顯示 {{ rangeStart }} - {{ rangeEnd }} 筆，共 {{ total }} 筆資料</span>
      <span class="flex items-center gap-1.5">
        每頁顯示
        <select
          :value="pageSize"
          class="border border-[#CBD5E1] rounded px-1.5 py-0.5 text-xs text-[#334155] focus:outline-none focus:border-[#00D1B2]"
          @change="onPageSize"
        >
          <option v-for="opt in pageSizeOptions" :key="opt" :value="opt">{{ opt }}</option>
        </select>
        筆
      </span>
    </div>

    <div class="flex items-center gap-1.5">
      <button
        type="button"
        class="px-2.5 py-1 rounded border border-[#CBD5E1] bg-white hover:bg-slate-50 disabled:opacity-40 disabled:cursor-not-allowed"
        :disabled="page <= 1"
        @click="go(page - 1)"
      >
        上一頁
      </button>
      <button
        v-for="p in pageNumbers"
        :key="p"
        type="button"
        :class="[
          'min-w-[28px] h-[28px] px-1.5 rounded text-xs font-semibold transition-colors',
          p === page
            ? 'bg-[#00D1B2] text-white'
            : 'border border-[#CBD5E1] bg-white text-[#334155] hover:bg-slate-50',
        ]"
        @click="go(p)"
      >
        {{ p }}
      </button>
      <button
        type="button"
        class="px-2.5 py-1 rounded border border-[#CBD5E1] bg-white hover:bg-slate-50 disabled:opacity-40 disabled:cursor-not-allowed"
        :disabled="page >= totalPages"
        @click="go(page + 1)"
      >
        下一頁
      </button>
    </div>
  </div>
</template>
