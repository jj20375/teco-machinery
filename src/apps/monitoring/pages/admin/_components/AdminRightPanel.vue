<script setup lang="ts">
/**
 * @file AdminRightPanel.vue
 * 後台右側設定面板 — Figma 統一樣式（冰水主機溫度設定 / FCU 設定溫度差 / 角色權限設定）
 * 右側白色面板 ~400 寬滿高；底部滿版「儲存設定」綠鈕（預設 disabled，有變更才 enable）
 */
import AdminButton from './AdminButton.vue';

withDefaults(
  defineProps<{
    open: boolean;
    title: string;
    /** 是否可儲存（表單有變更） */
    canSave?: boolean;
    saving?: boolean;
    /** 是否顯示底部儲存列 */
    showFooter?: boolean;
  }>(),
  { canSave: false, saving: false, showFooter: true }
);

const emit = defineEmits<{
  (e: 'close'): void;
  (e: 'save'): void;
}>();
</script>

<template>
      <div v-if="open" class="fixed inset-0 z-50">
        <div class="absolute inset-0 bg-slate-900/30" @click="emit('close')" />
        <transition
          enter-active-class="transition-transform duration-200 ease-out"
          enter-from-class="translate-x-full"
          leave-active-class="transition-transform duration-150 ease-in"
          leave-to-class="translate-x-full"
        >
          <aside
            v-if="open"
            class="absolute inset-y-0 right-0 w-full max-w-[400px] bg-white shadow-2xl flex flex-col"
          >
            <!-- Header -->
            <div class="flex items-center justify-between px-5 h-16 border-b border-[#E2E8F0] flex-shrink-0">
              <div class="flex items-center gap-2 text-[#1A202C] font-bold text-base">
                <svg class="w-4 h-4 text-[#64748B]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                </svg>
                {{ title }}
              </div>
              <button
                type="button"
                class="text-[#94A3B8] hover:text-[#334155] p-1 -mr-1 rounded transition-colors"
                @click="emit('close')"
              >
                <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12" />
                </svg>
              </button>
            </div>

            <!-- Body -->
            <div class="flex-1 overflow-y-auto px-5 py-6">
              <slot />
            </div>

            <!-- Footer -->
            <div v-if="showFooter" class="px-5 py-4 border-t border-[#E2E8F0] flex-shrink-0">
              <AdminButton
                variant="primary"
                class="w-full py-2.5"
                :disabled="!canSave || saving"
                @click="emit('save')"
              >
                {{ saving ? '儲存中…' : '儲存設定' }}
              </AdminButton>
            </div>
          </aside>
        </transition>
      </div>
</template>
