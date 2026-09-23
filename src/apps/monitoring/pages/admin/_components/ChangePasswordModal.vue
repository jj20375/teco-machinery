<script setup lang="ts">
/**
 * @file ChangePasswordModal.vue
 * 修改密碼 — 置中 modal（Figma node 250-9778）
 * 打 backend/ 的 POST /api/v1/auth/change-password（見 auth-service.ts changePasswordApi）。
 */
import { ref } from 'vue';
import AdminButton from './AdminButton.vue';
import { changePasswordApi, clearSessionApi, ApiError } from '../_services/auth-service';

defineProps<{ open: boolean }>();

const emit = defineEmits<{
  (e: 'update:open', v: boolean): void;
  (e: 'success'): void;
}>();

const currentPassword = ref('');
const newPassword = ref('');
const confirmPassword = ref('');
const showCurrent = ref(false);
const showNew = ref(false);
const showConfirm = ref(false);
const errorMsg = ref('');
const isSuccess = ref(false);
const isSubmitting = ref(false);

const PWD_RULE = /^(?=.*[A-Za-z])(?=.*\d).{8,16}$/;

function close() {
  if (isSubmitting.value) return;
  emit('update:open', false);
}

function reset() {
  currentPassword.value = '';
  newPassword.value = '';
  confirmPassword.value = '';
  showCurrent.value = false;
  showNew.value = false;
  showConfirm.value = false;
  errorMsg.value = '';
  isSuccess.value = false;
}

async function handleSubmit() {
  errorMsg.value = '';
  if (!currentPassword.value || !newPassword.value || !confirmPassword.value) {
    errorMsg.value = '請完整填寫所有密碼欄位';
    return;
  }
  if (!PWD_RULE.test(newPassword.value)) {
    errorMsg.value = '新密碼長度須為 8~16 個字元，且包含英文字母及數字';
    return;
  }
  if (newPassword.value !== confirmPassword.value) {
    errorMsg.value = '新密碼與確認新密碼不相符';
    return;
  }

  isSubmitting.value = true;
  try {
    await changePasswordApi(currentPassword.value, newPassword.value);
    isSuccess.value = true;
    // 修改成功後，後端已經讓目前這顆 token 失效，這裡不假裝還能繼續用同一個 session，
    // 直接清掉工作階段、導去登入頁，請使用者用新密碼重新登入。
    setTimeout(() => {
      reset();
      emit('update:open', false);
      emit('success');
      clearSessionApi();
      window.location.href = '/login';
    }, 1200);
  } catch (err) {
    errorMsg.value = err instanceof ApiError ? err.message : '修改密碼失敗，請稍後再試。';
  } finally {
    isSubmitting.value = false;
  }
}
</script>

<template>
    <div
      v-if="open"
      class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/40 select-none"
      @click.self="close"
    >
      <div class="bg-white rounded-xl shadow-2xl border border-[#E2E8F0] w-full max-w-[420px] overflow-hidden">
        <div class="px-6 pt-6 pb-4 flex items-center gap-2 border-b border-[#E2E8F0]">
          <svg class="w-5 h-5 text-[#334155]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z" />
          </svg>
          <h3 class="text-base font-bold text-[#1A202C]">修改密碼</h3>
        </div>

        <form class="px-6 py-5 flex flex-col gap-4" @submit.prevent="handleSubmit">
          <div v-if="errorMsg" class="p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">
            {{ errorMsg }}
          </div>
          <div v-if="isSuccess" class="p-2.5 rounded-lg bg-[#E6FBF7] text-[#10B981] text-xs">
            密碼修改成功！即將導回登入頁，請用新密碼重新登入…
          </div>

          <div>
            <label class="block text-xs font-bold text-[#334155] mb-1.5">目前密碼</label>
            <div class="relative">
              <input
                v-model="currentPassword"
                :type="showCurrent ? 'text' : 'password'"
                class="w-full px-3 pr-10 py-2.5 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2]"
              />
              <button type="button" class="absolute right-3 top-2.5 text-[#94A3B8] hover:text-[#64748B]" @click="showCurrent = !showCurrent">
                <svg v-if="!showCurrent" class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M2.458 12C3.732 7.943 7.523 5 12 5c4.478 0 8.268 2.943 9.542 7-1.274 4.057-5.064 7-9.542 7-4.477 0-8.268-2.943-9.542-7z" />
                </svg>
                <svg v-else class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13.875 18.825A10.05 10.05 0 0112 19c-4.478 0-8.268-2.943-9.543-7a9.97 9.97 0 011.563-3.029m5.858.908a3 3 0 114.243 4.243M9.878 9.878l4.242 4.242M9.88 9.88l-3.29-3.29m7.532 7.532l3.29 3.29M3 3l18 18" />
                </svg>
              </button>
            </div>
          </div>
          <div>
            <label class="block text-xs font-bold text-[#334155] mb-1.5">新密碼</label>
            <div class="relative">
              <input
                v-model="newPassword"
                :type="showNew ? 'text' : 'password'"
                placeholder="請輸入新設定的密碼"
                class="w-full px-3 pr-10 py-2.5 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]"
              />
              <button type="button" class="absolute right-3 top-2.5 text-[#94A3B8] hover:text-[#64748B]" @click="showNew = !showNew">
                <svg v-if="!showNew" class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M2.458 12C3.732 7.943 7.523 5 12 5c4.478 0 8.268 2.943 9.542 7-1.274 4.057-5.064 7-9.542 7-4.477 0-8.268-2.943-9.542-7z" />
                </svg>
                <svg v-else class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13.875 18.825A10.05 10.05 0 0112 19c-4.478 0-8.268-2.943-9.543-7a9.97 9.97 0 011.563-3.029m5.858.908a3 3 0 114.243 4.243M9.878 9.878l4.242 4.242M9.88 9.88l-3.29-3.29m7.532 7.532l3.29 3.29M3 3l18 18" />
                </svg>
              </button>
            </div>
          </div>
          <div>
            <label class="block text-xs font-bold text-[#334155] mb-1.5">確認新密碼</label>
            <div class="relative">
              <input
                v-model="confirmPassword"
                :type="showConfirm ? 'text' : 'password'"
                placeholder="請再次輸入新密碼以進行確認"
                class="w-full px-3 pr-10 py-2.5 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]"
              />
              <button type="button" class="absolute right-3 top-2.5 text-[#94A3B8] hover:text-[#64748B]" @click="showConfirm = !showConfirm">
                <svg v-if="!showConfirm" class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M2.458 12C3.732 7.943 7.523 5 12 5c4.478 0 8.268 2.943 9.542 7-1.274 4.057-5.064 7-9.542 7-4.477 0-8.268-2.943-9.542-7z" />
                </svg>
                <svg v-else class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13.875 18.825A10.05 10.05 0 0112 19c-4.478 0-8.268-2.943-9.543-7a9.97 9.97 0 011.563-3.029m5.858.908a3 3 0 114.243 4.243M9.878 9.878l4.242 4.242M9.88 9.88l-3.29-3.29m7.532 7.532l3.29 3.29M3 3l18 18" />
                </svg>
              </button>
            </div>
          </div>

          <div class="flex items-start gap-2 p-3 rounded-lg bg-[#F1F5F9] text-xs text-[#64748B]">
            <svg class="w-4 h-4 flex-shrink-0 mt-px" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            <span>密碼長度至少 8~16 個字元，需包含英文字母及數字</span>
          </div>

          <div class="flex items-center justify-end gap-3 pt-1">
            <AdminButton type="button" variant="tertiary" :disabled="isSubmitting" @click="close">取消</AdminButton>
            <AdminButton type="submit" variant="primary" :disabled="isSubmitting">
              {{ isSubmitting ? '送出中…' : '確認修改' }}
            </AdminButton>
          </div>
        </form>
      </div>
    </div>
</template>
