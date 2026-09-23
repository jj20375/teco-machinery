<script setup lang="ts">
import { ref } from 'vue';
import AdminSidebar from './AdminSidebar.vue';
import AdminHeader from './AdminHeader.vue';
import ChangePasswordModal from './ChangePasswordModal.vue';

defineProps<{
  pageTitle: string;
  currentPath: string;
}>();

const isChangePasswordOpen = ref(false);
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
