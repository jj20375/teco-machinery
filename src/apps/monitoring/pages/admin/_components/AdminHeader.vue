<script setup lang="ts">
/**
 * @file AdminHeader.vue
 * 後台頂部列 — 嚴格對齊 Figma
 * 左：頁面標題 + pill「後台管理中心」
 * 右：瀏覽前台 / 告警通知 / 管理員 Anna Chen ▾（中南經銷處）+ 頭像
 */
import { ref, onMounted, onUnmounted } from 'vue';
import { clearSessionApi } from '../_services/auth-service';

defineProps<{ pageTitle: string }>();

const emit = defineEmits<{
  (e: 'open-change-password'): void;
}>();

const menuOpen = ref(false);

function onDocClick(e: MouseEvent) {
  if (!(e.target as HTMLElement).closest('[data-admin-usermenu]')) menuOpen.value = false;
}
onMounted(() => document.addEventListener('click', onDocClick));
onUnmounted(() => document.removeEventListener('click', onDocClick));

function handleLogout() {
  clearSessionApi();
  window.location.href = '/login';
}
</script>

<template>
  <header class="h-16 px-6 bg-white border-b border-[#E2E8F0] flex items-center justify-between select-none z-20 flex-shrink-0">
    <!-- 左：標題 + pill -->
    <div class="flex items-center gap-3">
      <h1 class="text-lg font-bold text-[#1A202C]">{{ pageTitle }}</h1>
      <span class="px-2.5 py-1 rounded-md bg-[#F1F5F9] text-[#64748B] text-xs font-medium">
        後台管理中心
      </span>
    </div>

    <!-- 右：功能 -->
    <div class="flex items-center gap-5 text-sm text-[#475569]">
      <a href="/" target="_blank" class="flex items-center gap-1.5 hover:text-[#00D1B2] transition-colors">
        <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14" />
        </svg>
        <span>瀏覽前台</span>
      </a>

      <a href="/admin/reports/alarms" class="flex items-center gap-1.5 hover:text-[#00D1B2] transition-colors">
        <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9" />
        </svg>
        <span>告警通知</span>
      </a>

      <!-- 使用者選單 -->
      <div class="relative" data-admin-usermenu>
        <button
          type="button"
          class="flex items-center gap-2.5 pl-3 border-l border-[#E2E8F0] cursor-pointer"
          @click="menuOpen = !menuOpen"
        >
          <div class="text-right leading-tight">
            <div class="text-sm font-bold text-[#1A202C]">管理員 Anna Chen</div>
            <div class="text-[11px] text-[#94A3B8]">中南經銷處</div>
          </div>
          <div class="w-9 h-9 rounded-full bg-[#E6FBF7] text-[#00A88E] font-bold flex items-center justify-center text-sm">
            A
          </div>
          <svg class="w-3.5 h-3.5 text-[#94A3B8]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 9l-7 7-7-7" />
          </svg>
        </button>

        <div
          v-if="menuOpen"
          class="absolute right-0 top-full mt-2 w-44 bg-white rounded-lg border border-[#E2E8F0] shadow-lg py-1 z-30"
        >
          <button
            type="button"
            class="w-full flex items-center gap-2 px-3 py-2 text-sm text-[#334155] hover:bg-slate-50 text-left"
            @click="menuOpen = false; emit('open-change-password')"
          >
            <svg class="w-4 h-4 text-[#64748B]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M15 7a2 2 0 012 2m4 0a6 6 0 01-7.743 5.743L11 17H9v2H7v2H4a1 1 0 01-1-1v-2.586a1 1 0 01.293-.707l5.964-5.964A6 6 0 1121 9z" />
            </svg>
            修改密碼
          </button>
          <button
            type="button"
            class="w-full flex items-center gap-2 px-3 py-2 text-sm text-[#334155] hover:bg-slate-50 text-left"
            @click="handleLogout"
          >
            <svg class="w-4 h-4 text-[#64748B]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M17 16l4-4m0 0l-4-4m4 4H7m6 4v1a3 3 0 01-3 3H6a3 3 0 01-3-3V7a3 3 0 013-3h4a3 3 0 013 3v1" />
            </svg>
            登出
          </button>
        </div>
      </div>
    </div>
  </header>
</template>
