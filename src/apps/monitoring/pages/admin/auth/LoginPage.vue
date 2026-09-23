<script setup lang="ts">
import { ref, onMounted } from 'vue';
import AdminButton from '../_components/AdminButton.vue';
import { loginApi, getSessionApi, isSessionValidApi } from '../_services/auth-service';

const username = ref('');
const password = ref('');
const showPassword = ref(false);
const rememberMe = ref(false);
const errorMessage = ref('');
const isLoggingIn = ref(false);
const forgotOpen = ref(false);

/** 已經有有效工作階段就不用再看登入表單，直接跳回去（或跳到被導來這裡之前想去的頁面）。 */
onMounted(() => {
  if (isSessionValidApi(getSessionApi())) {
    window.location.replace(redirectTarget());
  }
});

function redirectTarget(): string {
  const params = new URLSearchParams(window.location.search);
  const redirect = params.get('redirect');
  return redirect && redirect.startsWith('/') ? redirect : '/admin';
}

async function handleLogin() {
  errorMessage.value = '';
  if (!username.value.trim() || !password.value.trim()) {
    errorMessage.value = '請輸入帳號與密碼';
    return;
  }
  isLoggingIn.value = true;
  try {
    const result = await loginApi(username.value.trim(), password.value);
    if (!result.ok) {
      errorMessage.value = result.message;
      return;
    }
    window.location.href = redirectTarget();
  } finally {
    isLoggingIn.value = false;
  }
}
</script>

<template>
  <div class="min-h-screen bg-[#F4F6F8] flex items-center justify-center px-4">
    <div class="w-full max-w-[420px] bg-white rounded-xl border border-[#E2E8F0] shadow-sm p-8">
      <h1 class="text-2xl font-bold text-[#1A202C]">東元電機智慧環境監控</h1>
      <p class="text-sm text-[#94A3B8] mt-1">請輸入帳號與密碼登入系統</p>

      <div v-if="errorMessage" class="mt-4 p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">
        {{ errorMessage }}
      </div>

      <form class="mt-6 flex flex-col gap-4" @submit.prevent="handleLogin">
        <div>
          <label class="block text-xs font-bold text-[#334155] mb-1.5">帳號</label>
          <div class="relative">
            <svg class="w-4 h-4 text-[#94A3B8] absolute left-3 top-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M16 7a4 4 0 11-8 0 4 4 0 018 0zM12 14a7 7 0 00-7 7h14a7 7 0 00-7-7z" />
            </svg>
            <input
              v-model="username"
              type="text"
              placeholder="請輸入帳號"
              class="w-full pl-9 pr-3 py-2.5 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]"
            />
          </div>
        </div>

        <div>
          <label class="block text-xs font-bold text-[#334155] mb-1.5">密碼</label>
          <div class="relative">
            <svg class="w-4 h-4 text-[#94A3B8] absolute left-3 top-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z" />
            </svg>
            <input
              v-model="password"
              :type="showPassword ? 'text' : 'password'"
              placeholder="請輸入密碼"
              class="w-full pl-9 pr-10 py-2.5 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]"
            />
            <button type="button" class="absolute right-3 top-2.5 text-[#94A3B8] hover:text-[#64748B]" @click="showPassword = !showPassword">
              <svg v-if="!showPassword" class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M2.458 12C3.732 7.943 7.523 5 12 5c4.478 0 8.268 2.943 9.542 7-1.274 4.057-5.064 7-9.542 7-4.477 0-8.268-2.943-9.542-7z" />
              </svg>
              <svg v-else class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13.875 18.825A10.05 10.05 0 0112 19c-4.478 0-8.268-2.943-9.543-7a9.97 9.97 0 011.563-3.029m5.858.908a3 3 0 114.243 4.243M9.878 9.878l4.242 4.242M9.88 9.88l-3.29-3.29m7.532 7.532l3.29 3.29M3 3l18 18" />
              </svg>
            </button>
          </div>
        </div>

        <div class="flex items-center justify-between text-xs">
          <label class="flex items-center gap-2 text-[#64748B] cursor-pointer">
            <input v-model="rememberMe" type="checkbox" class="w-4 h-4 rounded accent-[#00D1B2]" />
            記住我
          </label>
          <button type="button" class="text-[#00D1B2] font-semibold hover:underline" @click="forgotOpen = true">忘記密碼</button>
        </div>

        <AdminButton type="submit" variant="primary" class="w-full py-3 mt-1" :disabled="isLoggingIn">
          {{ isLoggingIn ? '登入中…' : '登入' }}
        </AdminButton>
      </form>
    </div>

    <!-- 忘記密碼 -->
      <div v-if="forgotOpen" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/40" @click.self="forgotOpen = false">
        <div class="bg-white rounded-xl shadow-2xl border border-[#E2E8F0] w-full max-w-[400px] overflow-hidden">
          <div class="flex items-center justify-between px-5 pt-5 pb-3">
            <h3 class="text-base font-bold text-[#1A202C]">忘記密碼</h3>
            <button type="button" class="text-[#94A3B8] hover:text-[#334155]" @click="forgotOpen = false">
              <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12" /></svg>
            </button>
          </div>
          <p class="px-5 text-sm text-[#334155]">請洽系統管理人員</p>
          <div class="px-5 py-4 flex justify-end">
            <AdminButton variant="tertiary" @click="forgotOpen = false">關閉</AdminButton>
          </div>
        </div>
      </div>
  </div>
</template>
