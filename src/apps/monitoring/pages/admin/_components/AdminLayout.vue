<script setup lang="ts">
import { ref, onMounted } from 'vue';
import AdminSidebar from './AdminSidebar.vue';
import AdminHeader from './AdminHeader.vue';
import ChangePasswordModal from './ChangePasswordModal.vue';
import { getSessionApi } from '../_services/auth-service';

defineProps<{
  pageTitle: string;
  currentPath: string;
}>();

const isChangePasswordOpen = ref(false);

// 對稱於 PlatformLayout.vue 的 isPlatformScopeApi() 守衛：平台帳號本來就沒有 hvac 或
// merchant 開頭的權限，誤入這裡（例如登入前的 ?redirect= 指到這裡）只會看到側邊選單全開、
// 內容整頁 403 的破碎畫面——這是 2026-09-24 修過的真實 bug，直接導去平台帳號真正該去的
// 首頁，不要讓人卡在這種畫面上。純 UX 優化，真正的防線仍是後端 JWT 檢查。
onMounted(() => {
  if (getSessionApi()?.user.scopeKind === 'platform') window.location.replace('/platform/merchants');
});
</script>

<template>
  <div class="flex h-screen bg-[#F4F6F8] text-[#334155] antialiased overflow-hidden select-none font-sans">
    <AdminSidebar :current-path="currentPath" />

    <div class="flex-1 flex flex-col min-w-0 h-screen overflow-hidden">
      <AdminHeader :page-title="pageTitle" @open-change-password="isChangePasswordOpen = true" />

      <main class="flex-1 p-6 overflow-y-auto bg-[#F4F6F8]">
        <slot />
      </main>
    </div>

    <ChangePasswordModal
      v-model:open="isChangePasswordOpen"
      @success="isChangePasswordOpen = false"
    />
  </div>
</template>
