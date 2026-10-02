<script setup lang="ts">
/**
 * @file PlatformSystemUsersPage.vue
 * 平台管理 - 系統帳號（/platform/system-users）
 * 管理「平台帳號」本身（system_role_id 非空或 is_platform_admin=1），跟場館成員帳號是
 * 完全不同的一批人——見 backend/README.md「雙軌帳號」。這裡故意沒有做「停用/刪除」，
 * 後端目前也還沒有對應端點（見 PlatformEndpoints 只有 List/Create），先滿足「能新增」
 * 這個最急迫的雞生蛋問題（沒有平台帳號就沒辦法用平台功能）。
 */
import { ref, reactive, computed } from 'vue';
import { useQuery, useMutation, useQueryClient } from '@tanstack/vue-query';
import PlatformLayout from '../_components/PlatformLayout.vue';
import AdminButton from '../../admin/_components/AdminButton.vue';
import { ApiError } from '../../admin/_services/auth-service';
import { listSystemUsersApi, createSystemUserApi, listPlatformRolesApi } from '../_services/platform-service';

const queryClient = useQueryClient();
const usersQuery = useQuery({ queryKey: ['platform-system-users'], queryFn: listSystemUsersApi });
const rolesQuery = useQuery({ queryKey: ['platform-roles-for-users'], queryFn: listPlatformRolesApi });
const users = computed(() => usersQuery.data.value ?? []);
const roles = computed(() => rolesQuery.data.value ?? []);

const loadError = computed(() => {
  const err = usersQuery.error.value;
  if (!err) return '';
  if (err instanceof ApiError && err.status === 403) return '目前登入的帳號沒有「系統帳號」管理權限。';
  return err instanceof Error ? err.message : '載入系統帳號清單失敗，請稍後再試。';
});

function formatDate(value: string | null): string {
  if (!value) return '—';
  return new Date(value).toLocaleString('zh-TW', { hour12: false });
}

// ── 新增系統帳號 ──────────────────────
const addOpen = ref(false);
const addError = ref('');
const addForm = reactive({ username: '', displayName: '', password: '', systemRoleId: null as number | null });
const createMutation = useMutation({
  mutationFn: () => createSystemUserApi({
    username: addForm.username.trim(), displayName: addForm.displayName.trim(),
    password: addForm.password, systemRoleId: addForm.systemRoleId!,
  }),
  onSuccess: () => queryClient.invalidateQueries({ queryKey: ['platform-system-users'] }),
});
function openAdd() {
  addForm.username = ''; addForm.displayName = ''; addForm.password = ''; addForm.systemRoleId = null;
  addError.value = '';
  addOpen.value = true;
}
async function submitAdd() {
  if (createMutation.isPending.value) return;
  if (!addForm.username.trim() || !addForm.displayName.trim() || !addForm.password || !addForm.systemRoleId) {
    addError.value = '帳號、使用者名稱、密碼、角色都是必填。';
    return;
  }
  if (addForm.password.length < 6) {
    addError.value = '初始密碼至少要 6 個字元。';
    return;
  }
  addError.value = '';
  try {
    await createMutation.mutateAsync();
    addOpen.value = false;
  } catch (err) {
    addError.value = err instanceof Error ? err.message : '新增失敗，請稍後再試。';
  }
}
</script>

<template>
  <PlatformLayout page-title="系統帳號" current-path="/platform/system-users">
    <div class="flex flex-col gap-4">
      <div class="flex justify-end">
        <AdminButton variant="primary" size="sm" :disabled="!!loadError" @click="openAdd">＋ 新增系統帳號</AdminButton>
      </div>

      <div v-if="loadError" class="p-3 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-sm">{{ loadError }}</div>

      <div v-else class="bg-white rounded-xl border border-[#E2E8F0] overflow-hidden">
        <div v-if="usersQuery.isPending.value" class="p-6 text-center text-sm text-[#94A3B8]">載入中…</div>
        <div v-else class="overflow-x-auto">
          <table class="w-full text-left text-[13px] text-[#334155]">
            <thead class="bg-[#F8FAFC] text-[#64748B] border-b border-[#E2E8F0]">
              <tr>
                <th class="px-4 py-3 font-semibold">使用者名稱</th>
                <th class="px-4 py-3 font-semibold">帳號</th>
                <th class="px-4 py-3 font-semibold">角色</th>
                <th class="px-4 py-3 font-semibold">狀態</th>
                <th class="px-4 py-3 font-semibold">上次登入</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="u in users" :key="u.id" class="border-b border-[#F1F5F9] hover:bg-slate-50/70">
                <td class="px-4 py-3 font-medium">{{ u.displayName }}</td>
                <td class="px-4 py-3 font-tabular text-[#64748B]">{{ u.username }}</td>
                <td class="px-4 py-3">
                  <span class="px-2 py-0.5 rounded text-xs font-semibold bg-[#F1F5F9] text-[#64748B]">
                    {{ u.isPlatformAdmin && !u.roleName ? '平台超級管理員' : (u.roleName ?? '（未指派）') }}
                  </span>
                </td>
                <td class="px-4 py-3">
                  <span class="px-2 py-0.5 rounded text-xs font-semibold" :class="u.isActive ? 'bg-[#E6FBF7] text-[#10B981]' : 'bg-[#FFF5F5] text-[#FF4757]'">
                    {{ u.isActive ? '啟用中' : '已停用' }}
                  </span>
                </td>
                <td class="px-4 py-3 font-tabular text-[#64748B]">{{ formatDate(u.lastLoginAt) }}</td>
              </tr>
              <tr v-if="users.length === 0">
                <td colspan="5" class="px-4 py-6 text-center text-[#94A3B8]">目前還沒有任何系統帳號。</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </div>

    <!-- 新增系統帳號 -->
    <div v-if="addOpen" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/40" @click.self="addOpen = false">
      <div class="bg-white rounded-xl shadow-2xl border border-[#E2E8F0] w-full max-w-[420px] overflow-hidden">
        <div class="px-6 pt-6 pb-4 border-b border-[#E2E8F0]"><h3 class="text-base font-bold text-[#1A202C]">新增系統帳號</h3></div>
        <form autocomplete="off" class="px-6 py-5 flex flex-col gap-4" @submit.prevent="submitAdd">
          <div v-if="addError" class="p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ addError }}</div>
          <label class="flex flex-col gap-1.5 text-xs text-[#64748B]">
            帳號
            <input v-model="addForm.username" autocomplete="off" name="new-user-account" data-lpignore="true" data-1p-ignore type="text" class="px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2]" />
          </label>
          <label class="flex flex-col gap-1.5 text-xs text-[#64748B]">
            使用者名稱
            <input v-model="addForm.displayName" autocomplete="off" name="new-user-display-name" data-lpignore="true" data-1p-ignore type="text" class="px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2]" />
          </label>
          <label class="flex flex-col gap-1.5 text-xs text-[#64748B]">
            初始密碼
            <input v-model="addForm.password" autocomplete="new-password" name="new-user-initial-password" data-lpignore="true" data-1p-ignore type="text" class="px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2]" />
          </label>
          <label class="flex flex-col gap-1.5 text-xs text-[#64748B]">
            平台角色
            <select v-model.number="addForm.systemRoleId" class="px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2]">
              <option :value="null" disabled>請選擇角色</option>
              <option v-for="r in roles" :key="r.id" :value="r.id">{{ r.name }}</option>
            </select>
          </label>
          <div class="flex justify-end gap-3 pt-1">
            <AdminButton variant="tertiary" :disabled="createMutation.isPending.value" type="button" @click="addOpen = false">取消</AdminButton>
            <AdminButton variant="primary" :disabled="createMutation.isPending.value" type="submit">
              {{ createMutation.isPending.value ? '新增中…' : '新增' }}
            </AdminButton>
          </div>
        </form>
      </div>
    </div>
  </PlatformLayout>
</template>
