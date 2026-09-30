<script setup lang="ts">
/**
 * @file AdminHeader.vue
 * 後台頂部列 — 嚴格對齊 Figma
 * 左：頁面標題 + pill「後台管理中心」
 * 右：瀏覽前台 / 告警通知（新告警亮紅點，點擊開「全部告警」）/ 管理員 {顯示姓名} ▾（{場館名稱}）+ 頭像
 * 姓名/場館名稱/頭像字母都讀自 getSessionApi()，不是寫死的 Figma 稿文字。
 */
import { ref, computed, watch, onMounted, onUnmounted } from 'vue';
import { useQuery } from '@tanstack/vue-query';
import {
  ApiError, clearSessionApi, getSessionApi, listScopesApi, selectScopeApi, defaultHomePathApi, type ScopeOption,
} from '../_services/auth-service';
import {
  ACTIVE_ALARMS_QUERY, allAlarmsOpen, seenAlarmIds, loadSeenAlarmIds, markAlarmsSeen, openAllAlarms,
} from '../_services/alarm-notice';
import AdminAllAlarmsModal from './AdminAllAlarmsModal.vue';

withDefaults(defineProps<{ pageTitle: string; badgeLabel?: string }>(), { badgeLabel: '後台管理中心' });

const emit = defineEmits<{
  (e: 'open-change-password'): void;
}>();

const menuOpen = ref(false);

// 只有雙軌身分（同時是平台帳號又是場館成員，或身兼多個場館）的帳號才需要「切換身分」——
// 單一身分的帳號（TECO 目前絕大多數）這支查詢會回傳 <= 1 筆，選單直接不顯示這個項目，
// 不會讓人以為點了會有作用。
const scopeOptions = ref<ScopeOption[]>([]);
onMounted(() => {
  listScopesApi().then((options) => { scopeOptions.value = options; }).catch(() => {});
});
const canSwitchScope = computed(() => scopeOptions.value.length > 1);

const switchScopeOpen = ref(false);
const switchingScope = ref(false);
const switchScopeError = ref('');
async function chooseScope(option: ScopeOption) {
  if (switchingScope.value) return;
  switchingScope.value = true;
  switchScopeError.value = '';
  try {
    await selectScopeApi(option);
    window.location.href = defaultHomePathApi(option);
  } catch (err) {
    switchScopeError.value = err instanceof Error ? err.message : '切換身分失敗，請稍後再試。';
    switchingScope.value = false;
  }
}

// SSR 階段 getSessionApi() 一律回傳 null（見該函式上的註解），這裡沿用 AdminSidebar.vue 同一套
// 「computed 直接呼叫，hydrate 後在瀏覽器重新執行 setup 自然算出正確值」的作法，不用另外處理。
const currentUser = computed(() => getSessionApi()?.user ?? null);
const displayName = computed(() => currentUser.value?.displayName ?? '使用者');
const scopeLabel = computed(() => {
  const user = currentUser.value;
  if (!user) return '';
  return user.scopeKind === 'platform' || user.isPlatformAdmin ? '平台管理' : (user.merchantName ?? '');
});
const avatarInitial = computed(() => displayName.value.trim().charAt(0).toUpperCase() || 'U');

// hvac.alarms 是獨立權限，沒有的帳號（403）就不亮紅點，彈窗裡顯示原因，不擋整個頂部列。
const alarmsQuery = useQuery({ ...ACTIVE_ALARMS_QUERY, retry: false });
const activeAlarms = computed(() => alarmsQuery.data.value ?? []);
const alarmsError = computed(() => {
  const err = alarmsQuery.error.value;
  if (!err) return '';
  return err instanceof ApiError && err.status === 403
    ? '目前登入的帳號沒有「告警」查看權限。'
    : '告警清單載入失敗，請稍後再試。';
});
onMounted(loadSeenAlarmIds);
const hasUnseenAlarm = computed(() => activeAlarms.value.some((a) => !seenAlarmIds.value.has(a.id)));
// 彈窗開著時新進來的告警也算已經看過，關掉後不會馬上又亮紅點。
watch([allAlarmsOpen, activeAlarms], () => {
  if (allAlarmsOpen.value) markAlarmsSeen(activeAlarms.value.map((a) => a.id));
});

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
        {{ badgeLabel }}
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

      <button type="button" class="flex items-center gap-1.5 hover:text-[#00D1B2] transition-colors" @click="openAllAlarms">
        <span class="relative">
          <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9" />
          </svg>
          <span
            v-if="hasUnseenAlarm"
            class="absolute -top-0.5 -right-0.5 w-2 h-2 rounded-full bg-[#FF4757] ring-2 ring-white"
            aria-label="有新的告警"
          />
        </span>
        <span>告警通知</span>
      </button>

      <!-- 使用者選單 -->
      <div class="relative" data-admin-usermenu>
        <button
          type="button"
          class="flex items-center gap-2.5 pl-3 border-l border-[#E2E8F0] cursor-pointer"
          @click="menuOpen = !menuOpen"
        >
          <div class="text-right leading-tight">
            <div class="text-sm font-bold text-[#1A202C]">管理員 {{ displayName }}</div>
            <div class="text-[11px] text-[#94A3B8]">{{ scopeLabel }}</div>
          </div>
          <div class="w-9 h-9 rounded-full bg-[#E6FBF7] text-[#00A88E] font-bold flex items-center justify-center text-sm">
            {{ avatarInitial }}
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
            v-if="canSwitchScope"
            type="button"
            class="w-full flex items-center gap-2 px-3 py-2 text-sm text-[#334155] hover:bg-slate-50 text-left"
            @click="menuOpen = false; switchScopeOpen = true"
          >
            <svg class="w-4 h-4 text-[#64748B]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M8 7h12m0 0l-4-4m4 4l-4 4M16 17H4m0 0l4 4m-4-4l4-4" />
            </svg>
            切換身分
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

    <AdminAllAlarmsModal :open="allAlarmsOpen" :alarms="activeAlarms" :error="alarmsError" @close="allAlarmsOpen = false" />

    <!-- 切換身分 -->
    <div v-if="switchScopeOpen" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/40" @click.self="switchScopeOpen = false">
      <div class="bg-white rounded-xl shadow-2xl border border-[#E2E8F0] w-full max-w-[420px] overflow-hidden">
        <div class="px-6 pt-6 pb-4 border-b border-[#E2E8F0]"><h3 class="text-base font-bold text-[#1A202C]">切換身分</h3></div>
        <div class="px-6 py-5 flex flex-col gap-4">
          <div v-if="switchScopeError" class="p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ switchScopeError }}</div>
          <button
            v-for="option in scopeOptions"
            :key="`${option.scopeKind}-${option.merchantId ?? 'platform'}`"
            type="button"
            class="w-full flex items-center justify-between px-4 py-3 rounded-lg border border-[#E2E8F0] hover:border-[#00D1B2] hover:bg-[#F0FDFB] text-left transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
            :disabled="switchingScope"
            @click="chooseScope(option)"
          >
            <div>
              <div class="text-sm font-bold text-[#1A202C]">{{ option.name }}</div>
              <div class="text-xs text-[#94A3B8] mt-0.5">{{ option.scopeKind === 'platform' ? '平台管理' : '場館帳號' }}</div>
            </div>
            <span
              v-if="option.scopeKind === currentUser?.scopeKind && option.merchantId === currentUser?.merchantId"
              class="text-xs font-semibold text-[#00A88E]"
            >目前使用中</span>
          </button>
        </div>
      </div>
    </div>
  </header>
</template>
