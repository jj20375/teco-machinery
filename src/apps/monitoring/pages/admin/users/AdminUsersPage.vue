<script setup lang="ts">
import { ref, reactive, computed } from 'vue';
import { useQuery, useMutation, useQueryClient } from '@tanstack/vue-query';
import AdminLayout from '../_components/AdminLayout.vue';
import AdminButton from '../_components/AdminButton.vue';
import AdminPagination from '../_components/AdminPagination.vue';
import AdminRightPanel from '../_components/AdminRightPanel.vue';
import EmptyState from '../_components/EmptyState.vue';
import { ApiError, getSessionApi } from '../_services/auth-service';
import {
  listMerchantUsersApi,
  listMerchantRolesApi,
  createMerchantUserApi,
  updateMerchantUserApi,
  resetMerchantUserPasswordApi,
  deleteMerchantUserApi,
  getMemberFeaturesApi,
  setMemberFeaturesApi,
  MEMBER_FEATURE_OPTIONS,
  type MerchantUserRow,
  type MerchantRoleOption,
} from '../_services/user-service';

const queryClient = useQueryClient();

// ── 清單：使用者 + 角色（TanStack Query 取代原本手寫的 ref+onMounted+try/catch） ──
const usersQuery = useQuery({ queryKey: ['merchant-users'], queryFn: listMerchantUsersApi });
const rolesQuery = useQuery({ queryKey: ['merchant-roles'], queryFn: listMerchantRolesApi });

const users = computed<MerchantUserRow[]>(() => usersQuery.data.value ?? []);
const roles = computed<MerchantRoleOption[]>(() => rolesQuery.data.value ?? []);
const loading = computed(() => usersQuery.isPending.value || rolesQuery.isPending.value);

// 兩個查詢分開報錯：使用者清單失敗才算「整頁都看不到」，角色清單失敗只影響角色名稱顯示與
// 新增使用者的角色下拉選單——之前把兩者合併成同一個 loadError，會出現「清單明明有資料，
// 卻整頁顯示紅字說看不到清單」這種矛盾畫面（角色清單 403，使用者清單其實是 200）。
function describeError(err: unknown, forbiddenMessage: string, fallbackMessage: string): string {
  if (!err) return '';
  if (err instanceof ApiError && err.status === 403) return forbiddenMessage;
  return err instanceof Error ? err.message : fallbackMessage;
}
const loadError = computed(() =>
  describeError(
    usersQuery.error.value,
    '目前登入的帳號沒有「場館帳號管理」權限，無法查看或編輯使用者清單。',
    '載入使用者清單失敗，請稍後再試。',
  ),
);
const rolesLoadError = computed(() =>
  describeError(
    rolesQuery.error.value,
    '目前登入的帳號沒有「場館角色管理」權限，角色名稱與新增使用者的角色選單暫時無法使用。',
    '載入角色清單失敗，角色名稱與新增使用者的角色選單暫時無法使用。',
  ),
);

/** 場館成員清單的 queryKey，任何寫入操作成功後都要讓它失效重打，取代原本手動呼叫 fetchAll()。 */
function invalidateUsers() {
  return queryClient.invalidateQueries({ queryKey: ['merchant-users'] });
}

// 角色名稱優先用 MerchantUserRow 自帶的 roleName（後端 JOIN app_role 算出來的，一定準確）。
// 不能只靠去 roles 清單裡找 roleId，因為「編輯成員」六個核取方塊面板產生的個人專屬角色
// （member-{membershipId}）故意不會出現在 /api/v1/merchant/roles 清單裡（見後端
// RoleRepository.ListAsync 的註解），用清單反查會把這批人的角色名稱誤判成「（未指派）」。
function roleLabel(user: Pick<MerchantUserRow, 'roleId' | 'roleName'>): string {
  return user.roleName ?? '（未指派）';
}
function formatLastLogin(value: string | null): string {
  if (!value) return '—';
  return new Date(value).toLocaleString('zh-TW', { hour12: false });
}

const keyword = ref('');
const page = ref(1);
const pageSize = ref(20);
const filtered = computed(() => {
  if (!keyword.value.trim()) return users.value;
  const kw = keyword.value.trim().toLowerCase();
  return users.value.filter(
    (u) =>
      u.displayName.toLowerCase().includes(kw) ||
      u.username.toLowerCase().includes(kw) ||
      (u.email ?? '').toLowerCase().includes(kw),
  );
});
const paged = computed(() => filtered.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value));

// ── mutations ─────────────────────────
const updateUserMutation = useMutation({
  mutationFn: (input: { membershipId: number; patch: { roleId?: number; isActive?: boolean } }) =>
    updateMerchantUserApi(input.membershipId, input.patch),
  onSuccess: () => invalidateUsers(),
});

const setFeaturesMutation = useMutation({
  mutationFn: (input: { membershipId: number; features: string[] }) =>
    setMemberFeaturesApi(input.membershipId, input.features),
  onSuccess: (_data, variables) => {
    queryClient.invalidateQueries({ queryKey: ['merchant-user-features', variables.membershipId] });
    return invalidateUsers();
  },
});

const resetPasswordMutation = useMutation({
  mutationFn: (membershipId: number) => resetMerchantUserPasswordApi(membershipId),
});

const deleteUserMutation = useMutation({
  mutationFn: (membershipId: number) => deleteMerchantUserApi(membershipId),
  onSuccess: () => invalidateUsers(),
});

const createUserMutation = useMutation({
  mutationFn: createMerchantUserApi,
  onSuccess: () => invalidateUsers(),
});

// ── 編輯成員（頁面權限開關、啟用狀態）面板 ─────────────────
// UI 比照設計稿改成「這個人能看哪些頁面」的核取方塊，但底層仍然是角色算權限：
// 存檔時後端會幫這個人準備一個專屬角色（member-{membershipId}），不是真的取消角色系統。
const panelOpen = ref(false);
const panelLoading = ref(false);
const saveError = ref('');
const activeUser = ref<MerchantUserRow | null>(null);
const originalFeatures = ref<string[]>([]);
const form = reactive<{ features: string[]; isActive: boolean }>({ features: [], isActive: true });
const saving = computed(() => setFeaturesMutation.isPending.value || updateUserMutation.isPending.value);

/**
 * 場館管理員不能用這個面板編輯——面板只有六個固定開關，儲存時後端會把這個人的角色換成
 * 只有那六項的專屬角色，永久失去角色管理等未列在開關上的能力（實際發生過：管理員自己存檔後
 * 隔天發現看不到「角色管理」）。要調整場館管理員的權限，應該去角色管理改 merchant-admin 角色本身。
 * 後端 SetUserFeatures 也擋了同樣的情況，這裡只是讓使用者一開始就點不到，不用等 API 報錯。
 */
function isMerchantAdmin(u: MerchantUserRow): boolean {
  return u.roleCode === 'merchant-admin';
}

/** 跟後端 DeleteUser 的擋板邏輯一致：場館至少要留一位在職的場館管理員，UI 先擋掉沒意義的嘗試。 */
const activeAdminCount = computed(() => users.value.filter((u) => u.roleCode === 'merchant-admin' && u.isActive).length);
function isLastActiveAdmin(u: MerchantUserRow): boolean {
  return u.roleCode === 'merchant-admin' && u.isActive && activeAdminCount.value <= 1;
}
function isSelf(u: MerchantUserRow): boolean {
  return u.userId === getSessionApi()?.user.id;
}

/**
 * 管理員之間的層級保護，跟後端 MerchantMembershipGuard 一致（後端才是真正的防線，這裡只是讓按鈕點不到、
 * 並說明原因）：擁有者（第一位管理員）不能被刪除、停用、改角色，別人也不能幫他重設密碼或改名；
 * 其他管理員只有擁有者能刪除、停用、重設密碼、改名。
 */
const callerIsOwner = computed(() => users.value.some((u) => isSelf(u) && u.isOwner));
function lockReason(u: MerchantUserRow, action: 'delete' | 'toggle' | 'reset' | 'rename'): string | null {
  const self = isSelf(u);
  if (u.isOwner) {
    if (action === 'delete') return '這是場館的擁有者（第一位管理員），無法刪除';
    if (action === 'toggle') return '這是場館的擁有者（第一位管理員），無法停用';
    return self ? null : '這是場館的擁有者（第一位管理員），只有本人可以變更';
  }
  if (!isMerchantAdmin(u) || callerIsOwner.value) return null;
  if (self && (action === 'reset' || action === 'rename')) return null;
  const verb = { delete: '刪除', toggle: '停用／啟用', reset: '重設密碼', rename: '修改' }[action];
  return `這是場館管理員的帳號，只有場館擁有者可以${verb}`;
}

async function openPanel(u: MerchantUserRow) {
  if (isMerchantAdmin(u)) return;
  activeUser.value = u;
  form.isActive = u.isActive;
  saveError.value = '';
  panelOpen.value = true;
  panelLoading.value = true;
  try {
    // 這裡用 queryClient.fetchQuery 而不是 useQuery：一樣吃 QueryClient 的快取
    // （staleTime 內短時間重開同一個人的面板不用重打 API），但觸發時機是「點擊
    // 打開面板」這個指令式動作，用 fetchQuery 直接拿 Promise 比硬套響應式
    // enabled/queryKey 搭配 watch 更直接、也更符合這裡的程式碼流程。
    const { features } = await queryClient.fetchQuery({
      queryKey: ['merchant-user-features', u.membershipId],
      queryFn: () => getMemberFeaturesApi(u.membershipId),
      staleTime: 10_000,
    });
    originalFeatures.value = features;
    form.features = [...features];
  } catch (err) {
    saveError.value = err instanceof Error ? err.message : '讀取權限設定失敗，請稍後再試。';
  } finally {
    panelLoading.value = false;
  }
}
const featuresChanged = computed(() => {
  const a = [...form.features].sort().join(',');
  const b = [...originalFeatures.value].sort().join(',');
  return a !== b;
});
const changed = computed(() => {
  if (!activeUser.value) return false;
  return featuresChanged.value || form.isActive !== activeUser.value.isActive;
});
async function save() {
  if (!changed.value || saving.value || !activeUser.value) return;
  saveError.value = '';
  try {
    if (featuresChanged.value) {
      await setFeaturesMutation.mutateAsync({ membershipId: activeUser.value.membershipId, features: form.features });
    }
    if (form.isActive !== activeUser.value.isActive) {
      await updateUserMutation.mutateAsync({ membershipId: activeUser.value.membershipId, patch: { isActive: form.isActive } });
    }
    panelOpen.value = false;
  } catch (err) {
    saveError.value = err instanceof Error ? err.message : '儲存失敗，請稍後再試。';
  }
}

// ── 直接在列表切換啟用狀態 ────────────────
async function toggleEnabled(u: MerchantUserRow) {
  try {
    await updateUserMutation.mutateAsync({ membershipId: u.membershipId, patch: { isActive: !u.isActive } });
  } catch (err) {
    window.alert(err instanceof Error ? err.message : '更新失敗，請稍後再試。');
  }
}

// ── 改名（顯示姓名，跟角色/頁面權限無關，場館管理員帳號也能改）──────
const renameOpen = ref(false);
const renameError = ref('');
const renameTarget = ref<MerchantUserRow | null>(null);
const renameForm = reactive({ displayName: '' });
const renameMutation = useMutation({
  mutationFn: (input: { membershipId: number; displayName: string }) =>
    updateMerchantUserApi(input.membershipId, { displayName: input.displayName }),
  onSuccess: () => invalidateUsers(),
});

function openRename(u: MerchantUserRow) {
  renameTarget.value = u;
  renameForm.displayName = u.displayName;
  renameError.value = '';
  renameOpen.value = true;
}
async function confirmRename() {
  if (!renameTarget.value || renameMutation.isPending.value) return;
  if (!renameForm.displayName.trim()) {
    renameError.value = '使用者名稱不能是空白。';
    return;
  }
  renameError.value = '';
  try {
    await renameMutation.mutateAsync({ membershipId: renameTarget.value.membershipId, displayName: renameForm.displayName.trim() });
    renameOpen.value = false;
  } catch (err) {
    renameError.value = err instanceof Error ? err.message : '改名失敗，請稍後再試。';
  }
}

// ── 重設密碼 ─────────────────────────
const resetOpen = ref(false);
const resetting = resetPasswordMutation.isPending;
const resetError = ref('');
const resetTarget = ref<MerchantUserRow | null>(null);
const resetResultPassword = ref('');

function openReset(u: MerchantUserRow) {
  resetTarget.value = u;
  resetResultPassword.value = '';
  resetError.value = '';
  resetOpen.value = true;
}
async function confirmReset() {
  if (!resetTarget.value || resetting.value) return;
  resetError.value = '';
  try {
    const { temporaryPassword } = await resetPasswordMutation.mutateAsync(resetTarget.value.membershipId);
    resetResultPassword.value = temporaryPassword;
    await invalidateUsers();
  } catch (err) {
    resetError.value = err instanceof Error ? err.message : '重設密碼失敗，請稍後再試。';
  }
}

// ── 刪除使用者 ────────────────────────
// 刪掉的是這個人在本場館的成員資格，不是整個帳號；後端會擋刪自己、擋刪掉最後一個在職的
// 場館管理員，錯誤訊息可以直接顯示（見 deleteMerchantUserApi 的說明）。
const deleteOpen = ref(false);
const deleting = deleteUserMutation.isPending;
const deleteError = ref('');
const deleteTarget = ref<MerchantUserRow | null>(null);

function openDelete(u: MerchantUserRow) {
  deleteTarget.value = u;
  deleteError.value = '';
  deleteOpen.value = true;
}
async function confirmDelete() {
  if (!deleteTarget.value || deleting.value) return;
  deleteError.value = '';
  try {
    await deleteUserMutation.mutateAsync(deleteTarget.value.membershipId);
    deleteOpen.value = false;
  } catch (err) {
    deleteError.value = err instanceof Error ? err.message : '刪除失敗，請稍後再試。';
  }
}

// ── 新增使用者 ────────────────────────
const addOpen = ref(false);
const adding = createUserMutation.isPending;
const addError = ref('');
const addForm = reactive({ username: '', displayName: '', email: '', password: '', roleId: null as number | null });

function openAdd() {
  addForm.username = '';
  addForm.displayName = '';
  addForm.email = '';
  addForm.password = '';
  addForm.roleId = roles.value[0]?.id ?? null;
  addError.value = '';
  addOpen.value = true;
}
async function submitAdd() {
  if (adding.value) return;
  if (!addForm.username.trim() || !addForm.displayName.trim() || !addForm.password.trim() || !addForm.roleId) {
    addError.value = '帳號、使用者名稱、密碼與角色為必填。';
    return;
  }
  addError.value = '';
  try {
    await createUserMutation.mutateAsync({
      username: addForm.username.trim(),
      displayName: addForm.displayName.trim(),
      email: addForm.email.trim(),
      password: addForm.password,
      roleId: addForm.roleId,
    });
    addOpen.value = false;
  } catch (err) {
    addError.value = err instanceof Error ? err.message : '新增失敗，請稍後再試。';
  }
}
</script>

<template>
  <AdminLayout page-title="使用者管理" current-path="/admin/users">
    <div class="flex flex-col gap-4">
      <div v-if="loadError" class="p-3 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-sm">{{ loadError }}</div>
      <div v-else-if="rolesLoadError" class="p-3 rounded-lg bg-[#FFFBEB] text-[#B45309] text-sm">{{ rolesLoadError }}</div>

      <!-- 搜尋 -->
      <div class="bg-white rounded-xl border border-[#E2E8F0] p-4 flex items-center gap-3">
        <div class="relative w-72">
          <svg class="w-4 h-4 text-[#94A3B8] absolute left-3 top-2.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
          </svg>
          <input
            v-model="keyword"
            type="text"
            placeholder="搜尋使用者名稱、帳號、信箱"
            class="w-full pl-9 pr-3 py-2 text-xs rounded-lg border border-[#CBD5E1] focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8] text-black"
          />
        </div>
        <AdminButton variant="primary" size="sm" @click="page = 1">查詢</AdminButton>
      </div>

      <!-- 表格 -->
      <div class="bg-white rounded-xl border border-[#E2E8F0] overflow-hidden">
        <div class="flex justify-end px-4 pt-4">
          <AdminButton variant="primary" size="sm" :disabled="loading || !!loadError || !!rolesLoadError" @click="openAdd">
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 4v16m8-8H4" />
            </svg>
            新增使用者
          </AdminButton>
        </div>

        <div class="overflow-x-auto mt-3">
          <table class="w-full text-left text-[13px] text-[#334155]">
            <thead class="bg-[#F8FAFC] text-[#64748B] border-b border-[#E2E8F0]">
              <tr>
                <th class="px-4 py-3 font-semibold">使用者名稱</th>
                <th class="px-4 py-3 font-semibold">帳號</th>
                <th class="px-4 py-3 font-semibold">角色</th>
                <th class="px-4 py-3 font-semibold">信箱</th>
                <th class="px-4 py-3 font-semibold whitespace-nowrap">最後登入時間</th>
                <th class="px-4 py-3 font-semibold">啟用否</th>
                <th class="px-4 py-3 font-semibold">操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="u in paged" :key="u.membershipId" class="border-b border-[#F1F5F9] hover:bg-slate-50/70">
                <td class="px-4 py-3 font-medium">
                  <button
                    type="button"
                    class="flex items-center gap-1.5"
                    :class="lockReason(u, 'rename') ? 'cursor-not-allowed text-[#94A3B8]' : 'hover:text-[#00A88E]'"
                    :title="lockReason(u, 'rename') ?? '編輯使用者名稱'"
                    :disabled="!!lockReason(u, 'rename')"
                    @click="openRename(u)"
                  >
                    {{ u.displayName }}
                    <span v-if="u.isOwner" class="px-1.5 py-0.5 rounded text-[11px] font-semibold bg-[#FEF3C7] text-[#B45309]" title="場館的第一位管理員：不能被刪除、停用或改角色">擁有者</span>
                    <svg class="w-3.5 h-3.5 text-[#94A3B8]" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
                    </svg>
                  </button>
                </td>
                <td class="px-4 py-3 font-tabular text-[#64748B]">{{ u.username }}</td>
                <td class="px-4 py-3">
                  <div class="flex items-center gap-1.5">
                    <span
                      class="px-2 py-0.5 rounded text-xs font-semibold"
                      :class="u.roleCode === 'merchant-admin' ? 'bg-[#E6FBF7] text-[#10B981]' : 'bg-[#F1F5F9] text-[#64748B]'"
                    >
                      {{ roleLabel(u) }}
                    </span>
                    <span v-if="u.isLocked" class="px-2 py-0.5 rounded text-xs font-semibold bg-[#FFF5F5] text-[#FF4757]" title="連續登入失敗次數過多，暫時鎖定中">
                      已鎖定
                    </span>
                  </div>
                </td>
                <td class="px-4 py-3 text-[#64748B]">{{ u.email ?? '—' }}</td>
                <td class="px-4 py-3 font-tabular text-[#64748B] whitespace-nowrap">{{ formatLastLogin(u.lastLoginAt) }}</td>
                <td class="px-4 py-3">
                  <button
                    type="button"
                    role="switch"
                    :aria-checked="u.isActive"
                    class="relative inline-flex h-5 w-9 items-center rounded-full transition-colors"
                    :class="[u.isActive ? 'bg-[#00D1B2]' : 'bg-[#CBD5E1]', lockReason(u, 'toggle') ? 'opacity-50 cursor-not-allowed' : '']"
                    :title="lockReason(u, 'toggle') ?? undefined"
                    :disabled="!!lockReason(u, 'toggle')"
                    @click="toggleEnabled(u)"
                  >
                    <span class="inline-block h-4 w-4 transform rounded-full bg-white transition-transform" :class="u.isActive ? 'translate-x-4' : 'translate-x-0.5'" />
                  </button>
                </td>
                <td class="px-4 py-3">
                  <div class="flex items-center gap-3">
                    <span
                      v-if="isMerchantAdmin(u)"
                      class="text-[#CBD5E1] cursor-not-allowed"
                      title="場館管理員的權限由角色本身決定，請到角色管理調整，不能用此面板編輯"
                    >設定</span>
                    <button v-else type="button" class="link-action" @click="openPanel(u)">設定</button>
                    <span v-if="lockReason(u, 'reset')" class="text-[#CBD5E1] cursor-not-allowed" :title="lockReason(u, 'reset') ?? undefined">重設密碼</span>
                    <button v-else type="button" class="link-action" @click="openReset(u)">重設密碼</button>
                    <span
                      v-if="isSelf(u) || lockReason(u, 'delete') || isLastActiveAdmin(u)"
                      class="text-[#CBD5E1] cursor-not-allowed"
                      :title="isSelf(u) ? '無法刪除自己的帳號' : (lockReason(u, 'delete') ?? '場館至少要留一位在職的場館管理員，無法從這裡刪除；如需異動，請先指派另一位場館管理員')"
                    >刪除</span>
                    <button v-else type="button" class="link-action" style="color: #FF4757" @click="openDelete(u)">刪除</button>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <EmptyState v-if="!loading && !loadError && filtered.length === 0" title="查無使用者" description="沒有符合搜尋條件的使用者。" />
        <AdminPagination v-else-if="!loadError" :total="filtered.length" v-model:page="page" v-model:page-size="pageSize" />
      </div>
    </div>

    <!-- 編輯成員：角色與啟用狀態 -->
    <AdminRightPanel
      :open="panelOpen"
      title="編輯成員"
      :can-save="changed"
      :saving="saving"
      @close="panelOpen = false"
      @save="save"
    >
      <div v-if="activeUser" class="flex flex-col gap-6">
        <div v-if="saveError" class="p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ saveError }}</div>

        <section>
          <h4 class="text-sm font-bold text-[#1A202C] mb-2">使用者資料</h4>
          <div class="rounded-lg border border-[#E2E8F0] divide-y divide-[#E2E8F0] text-sm">
            <div class="flex justify-between px-3 py-2.5"><span class="text-[#64748B]">使用者名稱</span><span class="font-medium text-[#334155]">{{ activeUser.displayName }}</span></div>
            <div class="flex justify-between px-3 py-2.5"><span class="text-[#64748B]">帳號</span><span class="font-medium text-[#334155]">{{ activeUser.username }}</span></div>
            <div class="flex justify-between px-3 py-2.5"><span class="text-[#64748B]">角色</span><span class="font-medium text-[#334155]">{{ roleLabel(activeUser) }}</span></div>
            <div class="flex justify-between px-3 py-2.5"><span class="text-[#64748B]">信箱</span><span class="font-medium text-[#334155]">{{ activeUser.email ?? '—' }}</span></div>
          </div>
        </section>

        <section>
          <div class="flex items-center justify-between mb-2">
            <h4 class="text-sm font-bold text-[#1A202C]">權限</h4>
            <span class="text-xs text-[#94A3B8]">啟用否</span>
          </div>
          <div v-if="panelLoading" class="text-xs text-[#94A3B8] py-4 text-center">載入中…</div>
          <div v-else class="rounded-lg border border-[#E2E8F0] divide-y divide-[#E2E8F0]">
            <label
              v-for="opt in MEMBER_FEATURE_OPTIONS"
              :key="opt.code"
              class="flex items-center justify-between px-3 py-2.5 text-sm cursor-pointer"
            >
              <span class="text-[#334155]">{{ opt.name }}</span>
              <input type="checkbox" :value="opt.code" v-model="form.features" class="w-4 h-4 rounded accent-[#4C7DF0]" />
            </label>
          </div>
          <p class="text-xs text-[#94A3B8] mt-1.5">
            勾選這個人能看到的頁面；實際能不能新增/修改/刪除，由後端依場館目前的權限模式統一計算。
          </p>
        </section>

        <section>
          <div class="flex items-center justify-between px-1 text-sm">
            <span class="text-[#334155] font-medium">啟用此帳號</span>
            <button
              type="button"
              role="switch"
              :aria-checked="form.isActive"
              class="relative inline-flex h-5 w-9 items-center rounded-full transition-colors"
              :class="form.isActive ? 'bg-[#00D1B2]' : 'bg-[#CBD5E1]'"
              @click="form.isActive = !form.isActive"
            >
              <span class="inline-block h-4 w-4 transform rounded-full bg-white transition-transform" :class="form.isActive ? 'translate-x-4' : 'translate-x-0.5'" />
            </button>
          </div>
        </section>
      </div>
    </AdminRightPanel>

    <!-- 改名 -->
    <div v-if="renameOpen" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/40" @click.self="renameOpen = false">
      <div class="bg-white rounded-xl shadow-2xl border border-[#E2E8F0] w-full max-w-[420px] overflow-hidden">
        <div class="px-6 pt-6 pb-4 border-b border-[#E2E8F0]"><h3 class="text-base font-bold text-[#1A202C]">編輯使用者名稱</h3></div>
        <form class="px-6 py-5 flex flex-col gap-4" @submit.prevent="confirmRename">
          <div v-if="renameError" class="p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ renameError }}</div>
          <label class="flex flex-col gap-1.5 text-xs text-[#64748B]">
            使用者名稱
            <input v-model="renameForm.displayName" type="text" class="px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2]" />
          </label>
          <div class="flex justify-end gap-3 pt-1">
            <AdminButton variant="tertiary" :disabled="renameMutation.isPending.value" type="button" @click="renameOpen = false">取消</AdminButton>
            <AdminButton variant="primary" :disabled="renameMutation.isPending.value" type="submit">
              {{ renameMutation.isPending.value ? '儲存中…' : '儲存' }}
            </AdminButton>
          </div>
        </form>
      </div>
    </div>

    <!-- 重設密碼 -->
    <div v-if="resetOpen" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/40" @click.self="resetOpen = false">
      <div class="bg-white rounded-xl shadow-2xl border border-[#E2E8F0] w-full max-w-[420px] overflow-hidden">
        <div class="px-6 pt-6 pb-4 border-b border-[#E2E8F0]">
          <h3 class="text-base font-bold text-[#1A202C]">重設密碼</h3>
        </div>
        <div class="px-6 py-5">
          <div v-if="resetError" class="mb-3 p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ resetError }}</div>

          <template v-if="!resetResultPassword">
            <p class="text-sm text-[#334155]">
              確定要重設 <strong>{{ resetTarget?.displayName }}（{{ resetTarget?.username }}）</strong> 的登入密碼嗎？
            </p>
            <div class="mt-3 p-3 rounded-lg bg-[#F1F5F9] text-xs text-[#64748B]">
              系統會產生一組隨機臨時密碼，重設後這個帳號目前的登入狀態會立即失效，
              需要用新密碼重新登入。
            </div>
            <div class="flex justify-end gap-3 pt-4">
              <AdminButton variant="tertiary" :disabled="resetting" @click="resetOpen = false">取消</AdminButton>
              <AdminButton variant="primary" :disabled="resetting" @click="confirmReset">
                {{ resetting ? '處理中…' : '確認重設' }}
              </AdminButton>
            </div>
          </template>
          <template v-else>
            <div class="text-center py-2">
              <div class="w-12 h-12 rounded-full bg-[#E6FBF7] text-[#10B981] flex items-center justify-center mx-auto mb-3">
                <svg class="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M5 13l4 4L19 7" /></svg>
              </div>
              <p class="text-sm text-[#334155]">密碼已重設為以下臨時密碼：</p>
              <div class="mt-2 bg-[#F1F5F9] rounded-lg py-2 font-tabular text-base font-bold text-[#1A202C] tracking-wider select-all">{{ resetResultPassword }}</div>
              <p class="text-xs text-[#94A3B8] mt-2">請立即告知使用者——這組密碼只會顯示這一次，關閉後無法再次查看。</p>
              <AdminButton variant="primary" class="w-full mt-4" @click="resetOpen = false">完成</AdminButton>
            </div>
          </template>
        </div>
      </div>
    </div>

    <!-- 刪除使用者 -->
    <div v-if="deleteOpen" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/40" @click.self="deleteOpen = false">
      <div class="bg-white rounded-xl shadow-2xl border border-[#E2E8F0] w-full max-w-[420px] overflow-hidden">
        <div class="px-6 pt-6 pb-4 border-b border-[#E2E8F0]">
          <h3 class="text-base font-bold text-[#1A202C]">刪除使用者</h3>
        </div>
        <div class="px-6 py-5">
          <div v-if="deleteError" class="mb-3 p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ deleteError }}</div>
          <p class="text-sm text-[#334155]">
            確定要刪除 <strong>{{ deleteTarget?.displayName }}（{{ deleteTarget?.username }}）</strong>
            在本場館的帳號嗎？
          </p>
          <div class="mt-3 p-3 rounded-lg bg-[#FFF5F5] text-xs text-[#FF4757]">
            這個操作無法復原。刪除後這個人會立即失去本場館的存取權限，目前登入中的工作階段也會立刻失效。
          </div>
          <div class="flex justify-end gap-3 pt-4">
            <AdminButton variant="tertiary" :disabled="deleting" @click="deleteOpen = false">取消</AdminButton>
            <AdminButton variant="primary" style="background-color: #FF4757" :disabled="deleting" @click="confirmDelete">
              {{ deleting ? '刪除中…' : '確認刪除' }}
            </AdminButton>
          </div>
        </div>
      </div>
    </div>

    <!-- 新增使用者 -->
    <div v-if="addOpen" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/40" @click.self="addOpen = false">
      <div class="bg-white rounded-xl shadow-2xl border border-[#E2E8F0] w-full max-w-[440px] overflow-hidden">
        <div class="px-6 pt-6 pb-4 border-b border-[#E2E8F0] flex items-center justify-between">
          <h3 class="text-base font-bold text-[#1A202C]">新增使用者</h3>
          <button type="button" class="text-[#94A3B8] hover:text-[#334155]" @click="addOpen = false">
            <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12" /></svg>
          </button>
        </div>
        <form class="px-6 py-5 flex flex-col gap-4" @submit.prevent="submitAdd">
          <div v-if="addError" class="p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ addError }}</div>

          <div>
            <label class="block text-xs font-bold text-[#334155] mb-1.5">帳號 <span class="text-[#FF4757]">*</span></label>
            <input v-model="addForm.username" type="text" placeholder="登入用帳號，例如 daming.wang"
              class="w-full px-3 py-2.5 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]" />
          </div>
          <div>
            <label class="block text-xs font-bold text-[#334155] mb-1.5">使用者名稱 <span class="text-[#FF4757]">*</span></label>
            <input v-model="addForm.displayName" type="text" placeholder="顯示用姓名"
              class="w-full px-3 py-2.5 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]" />
          </div>
          <div>
            <label class="block text-xs font-bold text-[#334155] mb-1.5">信箱</label>
            <input v-model="addForm.email" type="email" placeholder="選填"
              class="w-full px-3 py-2.5 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]" />
          </div>
          <div>
            <label class="block text-xs font-bold text-[#334155] mb-1.5">初始密碼 <span class="text-[#FF4757]">*</span></label>
            <input v-model="addForm.password" type="text" placeholder="請告知使用者，日後可自行修改"
              class="w-full px-3 py-2.5 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]" />
          </div>
          <div>
            <label class="block text-xs font-bold text-[#334155] mb-1.5">角色 <span class="text-[#FF4757]">*</span></label>
            <select v-model="addForm.roleId" class="w-full px-3 py-2.5 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2]">
              <option v-for="r in roles" :key="r.id" :value="r.id">{{ r.name }}</option>
            </select>
          </div>

          <div class="flex justify-end gap-3 pt-2">
            <AdminButton type="button" variant="tertiary" @click="addOpen = false">取消</AdminButton>
            <AdminButton type="submit" variant="primary" :disabled="adding">{{ adding ? '新增中…' : '新增' }}</AdminButton>
          </div>
        </form>
      </div>
    </div>
  </AdminLayout>
</template>
