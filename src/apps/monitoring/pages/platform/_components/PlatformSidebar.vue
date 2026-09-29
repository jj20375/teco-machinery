<script setup lang="ts">
/**
 * @file PlatformSidebar.vue
 * 平台管理側邊欄——比照 admin/_components/AdminSidebar.vue 的視覺樣式，但選單內容是
 * 平台範圍的資源（場館／系統帳號／角色），跟場館後台的 hvac.*／merchant.* 選單完全不同組。
 * 平台範圍目前只有一個角色在用（platform-admin），沒有做像場館那樣依權限過濾選單——
 * 能登入這裡的帳號一定是平台範圍，且平台角色數量少，之後真的有 platform-operator 這種
 * 縮權角色要上線時再補過濾邏輯，見 backend/README.md「已知缺口」。
 */
const props = defineProps<{ currentPath: string }>();

interface MenuItem {
  title: string;
  path: string;
}

const MENU_ITEMS: MenuItem[] = [
  { title: '場館管理', path: '/platform/merchants' },
  { title: '系統帳號', path: '/platform/system-users' },
  { title: '角色管理', path: '/platform/roles' },
  { title: '系統診斷', path: '/platform/diagnostics' },
];

function isActive(path: string) {
  return props.currentPath === path || props.currentPath.startsWith(`${path}/`);
}
</script>

<template>
  <aside class="w-[240px] bg-white border-r border-[#E2E8F0] flex flex-col select-none h-screen flex-shrink-0 z-30">
    <!-- 頂部品牌 -->
    <div class="h-16 px-4 flex items-center gap-3 border-b border-[#E2E8F0]">
      <div class="w-10 h-6 border border-[#0066CC] rounded flex items-center justify-center text-[#0066CC] font-extrabold text-[11px] tracking-tighter shrink-0">
        TECO
      </div>
      <div class="leading-tight">
        <h2 class="text-base font-bold text-[#1A202C]">平台管理</h2>
        <span class="text-[13px] text-[#00D1B2] font-bold tracking-wide block">TECO PLATFORM</span>
      </div>
    </div>

    <!-- 選單 -->
    <nav class="flex-1 py-4 px-3 overflow-y-auto flex flex-col gap-1">
      <a
        v-for="item in MENU_ITEMS"
        :key="item.path"
        :href="item.path"
        :class="[
          'flex items-center gap-3 px-4 py-3 rounded-lg text-[15px] transition-colors',
          isActive(item.path) ? 'bg-[#E6FBF7] text-[#00D1B2] font-bold' : 'text-[#4A5568] hover:bg-slate-50 font-medium',
        ]"
      >
        <span>{{ item.title }}</span>
      </a>
    </nav>

    <!-- 底部版本資訊 -->
    <div class="px-5 py-4 text-[11px] text-[#94A3B8] leading-relaxed border-t border-[#E2E8F0]">
      <div>系統版本 v1.0.0</div>
      <div>© 2026 TECO Group</div>
    </div>
  </aside>
</template>
