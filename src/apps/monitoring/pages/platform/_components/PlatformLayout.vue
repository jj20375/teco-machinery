<script setup lang="ts">
/**
 * @file PlatformLayout.vue
 * 平台管理頁面外殼——結構比照 admin/_components/AdminLayout.vue，重用 AdminHeader／
 * ChangePasswordModal（這兩個雖然放在 admin 模組下，但本質是「登入者資訊列」「改密碼」
 * 這種跟 hvac 業務無關的通用元件，不是 hvac 後台專屬，跨模組重用比複製一份更好維護）。
 *
 * 進頁面時做 scope 守衛：不是 platform scope 的帳號（例如場館帳號手動打 /platform/xxx
 * 網址）導回 /admin，避免看到一堆 API 全部 403 的破碎畫面——真正的防線仍是後端 JWT 檢查，
 * 這裡純粹是 UX 優化（見 CLAUDE.md 原則 2）。
 */
import { ref, onMounted } from 'vue';
import PlatformSidebar from './PlatformSidebar.vue';
import AdminHeader from '../../admin/_components/AdminHeader.vue';
import ChangePasswordModal from '../../admin/_components/ChangePasswordModal.vue';
import { isPlatformScopeApi } from '../_services/platform-service';

defineProps<{
  pageTitle: string;
  currentPath: string;
}>();

const isChangePasswordOpen = ref(false);

onMounted(() => {
  if (!isPlatformScopeApi()) window.location.replace('/admin');
});
</script>

<template>
  <div class="flex h-screen bg-[#F4F6F8] text-[#334155] antialiased overflow-hidden select-none font-sans">
    <PlatformSidebar :current-path="currentPath" />

    <div class="flex-1 flex flex-col min-w-0 h-screen overflow-hidden">
      <AdminHeader :page-title="pageTitle" badge-label="平台管理中心" @open-change-password="isChangePasswordOpen = true" />

      <main class="flex-1 p-6 overflow-y-auto bg-[#F4F6F8]">
        <slot />
      </main>
    </div>

    <ChangePasswordModal v-model:open="isChangePasswordOpen" @success="isChangePasswordOpen = false" />
  </div>
</template>
