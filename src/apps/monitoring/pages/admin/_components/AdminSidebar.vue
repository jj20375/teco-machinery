<script setup lang="ts">
/**
 * @file AdminSidebar.vue
 * 後台側邊欄 — 對齊 Figma（node 58-6 等）
 * 頂部品牌 + 選單（統計報表可展開 3 子項）+ 底部版本文字
 */
import { ref, computed } from 'vue';
import { getSessionApi } from '../_services/auth-service';

const props = defineProps<{ currentPath: string }>();

type IconType = 'dashboard' | 'floor' | 'chiller' | 'fcu' | 'report' | 'user' | 'role' | 'log';

interface Child {
  title: string;
  path: string;
}
interface MenuItem {
  title: string;
  path: string;
  iconType: IconType;
  /** 對應後端 MerchantEndpoints.MemberFeatureCodes 的資源代碼，跟側邊欄七個項目 1:1 對應
   *（見該陣列上的註解）；沒有這個代碼的帳號看不到這個項目，不是只擋 API 卻選單照樣顯示。 */
  permissionCode: string;
  children?: Child[];
}

const ALL_MENU_ITEMS: MenuItem[] = [
  { title: '監控中心', path: '/admin', iconType: 'dashboard', permissionCode: 'hvac.overview' },
  { title: '空間設備配置', path: '/admin/floor-plan', iconType: 'floor', permissionCode: 'hvac.floor_plan' },
  { title: '冰水主機管理', path: '/admin/chiller', iconType: 'chiller', permissionCode: 'hvac.chillers' },
  { title: 'FCU管理', path: '/admin/fcu', iconType: 'fcu', permissionCode: 'hvac.fcus' },
  {
    title: '統計報表',
    path: '/admin/reports',
    iconType: 'report',
    permissionCode: 'hvac.reports',
    children: [
      { title: '冰水主機運轉報表', path: '/admin/reports/chiller' },
      { title: 'FCU運轉報表', path: '/admin/reports/fcu' },
      { title: '異常告警報表', path: '/admin/reports/alarms' },
    ],
  },
  { title: '使用者管理', path: '/admin/users', iconType: 'user', permissionCode: 'merchant.users' },
  { title: '角色管理', path: '/admin/roles', iconType: 'role', permissionCode: 'merchant.roles' },
  { title: '操作紀錄', path: '/admin/operation-log', iconType: 'log', permissionCode: 'merchant.operation_log' },
];

/**
 * 場館範圍的帳號才看權限清單過濾選單；平台範圍（目前沒有對應的前台頁面，理論上不會走到這裡）
 * 一律放行，避免權限代碼語意不同（hvac 開頭／merchant 開頭都是場館概念）卻被誤判成「什麼都沒有」。
 * 用 grants（不是單純 permissions 字串陣列）判斷是否至少有 read，跟後端 scope.Has(code,'read')
 * 的語意一致——理論上可能存在「有這個權限代碼但四個 CRUD 旗標都是 0」的邊界情況
 * （例如場館管理員手動把某個資源的全部權限都關掉，但列還留著），純看代碼是否出現在陣列裡
 * 會誤判成「看得到」。
 */
const visibleCodes = computed<Set<string> | null>(() => {
  const session = getSessionApi();
  if (!session || session.user.scopeKind !== 'merchant') return null; // null = 不過濾，全部顯示
  const codes = new Set(
    session.user.grants.filter((g) => g.actions.includes('read')).map((g) => g.code),
  );
  return codes;
});

const MENU_ITEMS = computed<MenuItem[]>(() => {
  const codes = visibleCodes.value;
  if (codes === null) return ALL_MENU_ITEMS;
  return ALL_MENU_ITEMS.filter((item) => codes.has(item.permissionCode));
});

function isExact(path: string) {
  return props.currentPath === path || props.currentPath === `${path}/`;
}
function isUnder(path: string) {
  return props.currentPath === path || props.currentPath.startsWith(`${path}/`);
}
function isItemActive(item: MenuItem) {
  if (item.path === '/admin') return isExact('/admin');
  return isUnder(item.path);
}

/** 統計報表展開狀態：位於報表頁時預設展開 */
const reportsOpen = ref(props.currentPath.startsWith('/admin/reports'));
</script>

<template>
  <aside class="w-[240px] bg-white border-r border-[#E2E8F0] flex flex-col select-none h-screen flex-shrink-0 z-30">
    <!-- 頂部品牌 -->
    <div class="h-16 px-4 flex items-center gap-3 border-b border-[#E2E8F0]">
      <div class="w-10 h-6 border border-[#0066CC] rounded flex items-center justify-center text-[#0066CC] font-extrabold text-[11px] tracking-tighter shrink-0">
        TECO
      </div>
      <div class="leading-tight">
        <h2 class="text-base font-bold text-[#1A202C]">東元空調監控</h2>
        <span class="text-[13px] text-[#00D1B2] font-bold tracking-wide block">TECO MACHINERY</span>
      </div>
    </div>

    <!-- 選單 -->
    <nav class="flex-1 py-4 px-3 overflow-y-auto flex flex-col gap-1">
      <template v-for="item in MENU_ITEMS" :key="item.path">
        <!-- 一般項目 -->
        <a
          v-if="!item.children"
          :href="item.path"
          :class="[
            'flex items-center gap-3 px-4 py-3 rounded-lg text-[15px] transition-colors',
            isItemActive(item) ? 'bg-[#E6FBF7] text-[#00D1B2] font-bold' : 'text-[#4A5568] hover:bg-slate-50 font-medium',
          ]"
        >
          <span class="shrink-0" :class="isItemActive(item) ? 'text-[#00D1B2]' : 'text-[#4A5568]'">
            <svg v-if="item.iconType === 'dashboard'" class="w-[18px] h-[18px]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <rect x="3" y="3" width="7" height="7" rx="1.5" stroke-width="1.8" />
              <rect x="14" y="3" width="7" height="7" rx="1.5" stroke-width="1.8" />
              <rect x="3" y="14" width="7" height="7" rx="1.5" stroke-width="1.8" />
              <rect x="14" y="14" width="7" height="7" rx="1.5" stroke-width="1.8" />
            </svg>
            <svg v-else-if="item.iconType === 'floor'" class="w-[18px] h-[18px]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.8" d="M9 20l-5.447-2.724A1 1 0 013 16.382V5.618a1 1 0 011.447-.894L9 7m0 13l6-3m-6 3V7m6 10l4.553 2.276A1 1 0 0021 18.382V7.618a1 1 0 00-.553-.894L15 4m0 13V4m0 0L9 7" />
            </svg>
            <svg v-else-if="item.iconType === 'chiller'" class="w-[18px] h-[18px]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.8" d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.8" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
            </svg>
            <svg v-else-if="item.iconType === 'fcu'" class="w-[18px] h-[18px]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <rect x="3" y="3" width="18" height="18" rx="2" stroke-width="1.8" />
              <path stroke-linecap="round" stroke-width="1.8" d="M3 9h18M3 15h18M9 3v18M15 3v18" />
            </svg>
            <svg v-else-if="item.iconType === 'user'" class="w-[18px] h-[18px]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.8" d="M16 7a4 4 0 11-8 0 4 4 0 018 0zM12 14a7 7 0 00-7 7h14a7 7 0 00-7-7z" />
            </svg>
            <svg v-else-if="item.iconType === 'role'" class="w-[18px] h-[18px]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.8" d="M12 3l7 3v5c0 4.5-2.9 8.4-7 10-4.1-1.6-7-5.5-7-10V6l7-3z" />
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.8" d="M9.5 12l1.8 1.8L14.5 10" />
            </svg>
            <svg v-else class="w-[18px] h-[18px]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.8" d="M4 6h16M4 12h16M4 18h16" />
            </svg>
          </span>
          <span>{{ item.title }}</span>
        </a>

        <!-- 可展開群組（統計報表） -->
        <template v-else>
          <button
            type="button"
            class="flex items-center gap-3 px-4 py-3 rounded-lg text-[15px] w-full transition-colors"
            :class="isItemActive(item) && !reportsOpen ? 'bg-[#E6FBF7] text-[#00D1B2] font-bold' : 'text-[#4A5568] hover:bg-slate-50 font-medium'"
            @click="reportsOpen = !reportsOpen"
          >
            <span class="shrink-0" :class="isItemActive(item) && !reportsOpen ? 'text-[#00D1B2]' : 'text-[#4A5568]'">
              <svg class="w-[18px] h-[18px]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.8" d="M9 17v-2m3 2v-4m3 4v-6m2 10H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
              </svg>
            </span>
            <span class="flex-1 text-left">{{ item.title }}</span>
            <svg
              class="w-3.5 h-3.5 text-[#94A3B8] transition-transform"
              :class="reportsOpen ? 'rotate-180' : ''"
              fill="none" stroke="currentColor" viewBox="0 0 24 24"
            >
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 9l-7 7-7-7" />
            </svg>
          </button>

          <div v-show="reportsOpen" class="flex flex-col gap-1 pl-3">
            <a
              v-for="child in item.children"
              :key="child.path"
              :href="child.path"
              :class="[
                'flex items-center gap-2.5 pl-6 pr-4 py-2.5 rounded-lg text-sm transition-colors',
                isUnder(child.path)
                  ? 'bg-[#E6FBF7] text-[#00D1B2] font-bold'
                  : 'text-[#4A5568] hover:bg-slate-50 font-medium',
              ]"
            >
              <span
                class="w-1.5 h-1.5 rounded-full shrink-0"
                :class="isUnder(child.path) ? 'bg-[#00D1B2]' : 'bg-[#CBD5E1]'"
              />
              <span>{{ child.title }}</span>
            </a>
          </div>
        </template>
      </template>
    </nav>

    <!-- 底部版本資訊 -->
    <div class="px-5 py-4 text-[11px] text-[#94A3B8] leading-relaxed border-t border-[#E2E8F0]">
      <div>系統版本 v1.0.0</div>
      <div>© 2026 TECO Group</div>
    </div>
  </aside>
</template>
