<script setup lang="ts">
/**
 * @file PlatformMerchantsPage.vue
 * 平台管理 - 場館管理（/platform/merchants）
 * 平台管理員在這裡新增場館、調整每個場館的 CRUD/子項角色權限簡化開關
 * （見 backend/README.md「權限機制」），並幫新場館建立第一個成員帳號——
 * 場館自己的「使用者管理」頁要求呼叫者已經是該場館 scope，新場館還沒有任何成員時
 * 沒有人能登入進去操作，第一個成員只能從這裡建。
 */
import { ref, reactive, computed } from 'vue';
import { useQuery, useMutation, useQueryClient } from '@tanstack/vue-query';
import PlatformLayout from '../_components/PlatformLayout.vue';
import AdminButton from '../../admin/_components/AdminButton.vue';
import { ApiError } from '../../admin/_services/auth-service';
import {
  listMerchantsApi, createMerchantApi, updateMerchantApi,
  listMerchantMembershipsApi, listMerchantAssignableRolesApi, createMerchantMembershipApi, resetMembershipPasswordApi,
  type PlatformMerchant,
} from '../_services/platform-service';

const queryClient = useQueryClient();
const merchantsQuery = useQuery({ queryKey: ['platform-merchants'], queryFn: listMerchantsApi });
const merchants = computed(() => merchantsQuery.data.value ?? []);

const loadError = computed(() => {
  const err = merchantsQuery.error.value;
  if (!err) return '';
  if (err instanceof ApiError && err.status === 403) return '目前登入的帳號沒有「場館管理」權限。';
  return err instanceof Error ? err.message : '載入場館清單失敗，請稍後再試。';
});

function invalidateMerchants() {
  return queryClient.invalidateQueries({ queryKey: ['platform-merchants'] });
}

// ── 新增場館 ──────────────────────────
const addOpen = ref(false);
const addError = ref('');
const addForm = reactive({ code: '', name: '', crudEnabled: true, optionEnabled: true });
const createMerchantMutation = useMutation({
  mutationFn: () => createMerchantApi({
    code: addForm.code.trim(), name: addForm.name.trim(),
    isRoleCrudConfigurationEnabled: addForm.crudEnabled, isRoleOptionConfigurationEnabled: addForm.optionEnabled,
  }),
  onSuccess: () => invalidateMerchants(),
});
function openAdd() {
  addForm.code = ''; addForm.name = ''; addForm.crudEnabled = true; addForm.optionEnabled = true;
  addError.value = '';
  addOpen.value = true;
}
async function submitAdd() {
  if (createMerchantMutation.isPending.value) return;
  if (!addForm.code.trim() || !addForm.name.trim()) {
    addError.value = '代碼與名稱為必填。';
    return;
  }
  addError.value = '';
  try {
    await createMerchantMutation.mutateAsync();
    addOpen.value = false;
  } catch (err) {
    addError.value = err instanceof Error ? err.message : '新增失敗，請稍後再試。';
  }
}

// ── CRUD/子項模式開關 ──────────────────
// 這是 2026-09-24 業主實測踩到的真實事故：直接切換這個開關會「立即」改變所有成員登入時
// 實際拿到的權限——透過「編輯成員」簡化面板設定的個人專屬角色（member-{id}），資料庫裡
// 永遠只存最基本的 read，完全依賴簡化模式在發 JWT 那一刻自動展開成完整 CRUD；一旦切到
// 細項模式，這個自動展開就沒有了，那些成員會突然只剩讀取權限，且完全沒有任何畫面提示。
// 不能讓使用者一勾就送出，一定要先講清楚後果再讓人確認。
const togglingId = ref<number | null>(null);
const toggleConfirmOpen = ref(false);
const toggleConfirmError = ref('');
const pendingToggle = ref<{ merchant: PlatformMerchant; field: 'isRoleCrudConfigurationEnabled' | 'isRoleOptionConfigurationEnabled' } | null>(null);

const TOGGLE_LABELS: Record<'isRoleCrudConfigurationEnabled' | 'isRoleOptionConfigurationEnabled', string> = {
  isRoleCrudConfigurationEnabled: 'CRUD',
  isRoleOptionConfigurationEnabled: '子功能',
};

function openToggleConfirm(merchant: PlatformMerchant, field: 'isRoleCrudConfigurationEnabled' | 'isRoleOptionConfigurationEnabled') {
  pendingToggle.value = { merchant, field };
  toggleConfirmError.value = '';
  toggleConfirmOpen.value = true;
}

async function confirmToggle() {
  if (!pendingToggle.value || togglingId.value === pendingToggle.value.merchant.id) return;
  const { merchant, field } = pendingToggle.value;
  togglingId.value = merchant.id;
  try {
    await updateMerchantApi(merchant.id, { [field]: !merchant[field] });
    await invalidateMerchants();
    toggleConfirmOpen.value = false;
  } catch (err) {
    toggleConfirmError.value = err instanceof Error ? err.message : '更新失敗，請稍後再試。';
  } finally {
    togglingId.value = null;
  }
}

// ── 管理成員 ──────────────────────────
const membersOpen = ref(false);
const membersLoading = ref(false);
const membersError = ref('');
const activeMerchant = ref<PlatformMerchant | null>(null);
const members = ref<Awaited<ReturnType<typeof listMerchantMembershipsApi>>>([]);
const assignableRoles = ref<Awaited<ReturnType<typeof listMerchantAssignableRolesApi>>>([]);

async function openMembers(merchant: PlatformMerchant) {
  activeMerchant.value = merchant;
  membersOpen.value = true;
  membersLoading.value = true;
  membersError.value = '';
  try {
    const [memberList, roleList] = await Promise.all([
      listMerchantMembershipsApi(merchant.id),
      listMerchantAssignableRolesApi(merchant.id),
    ]);
    members.value = memberList;
    assignableRoles.value = roleList;
  } catch (err) {
    membersError.value = err instanceof Error ? err.message : '讀取成員清單失敗，請稍後再試。';
  } finally {
    membersLoading.value = false;
  }
}

const addMemberForm = reactive({ username: '', displayName: '', email: '', password: '', roleId: null as number | null });
const addMemberError = ref('');
const addMemberMutation = useMutation({
  mutationFn: () => createMerchantMembershipApi(activeMerchant.value!.id, {
    username: addMemberForm.username.trim(),
    displayName: addMemberForm.displayName.trim(),
    email: addMemberForm.email.trim() || undefined,
    password: addMemberForm.password,
    roleId: addMemberForm.roleId!,
  }),
});
async function submitAddMember() {
  if (!activeMerchant.value || addMemberMutation.isPending.value) return;
  if (!addMemberForm.username.trim() || !addMemberForm.displayName.trim() || !addMemberForm.password || !addMemberForm.roleId) {
    addMemberError.value = '帳號、使用者名稱、密碼、角色都是必填。';
    return;
  }
  addMemberError.value = '';
  try {
    await addMemberMutation.mutateAsync();
    addMemberForm.username = ''; addMemberForm.displayName = ''; addMemberForm.email = ''; addMemberForm.password = ''; addMemberForm.roleId = null;
    await openMembers(activeMerchant.value);
  } catch (err) {
    addMemberError.value = err instanceof Error ? err.message : '新增成員失敗，請稍後再試。';
  }
}

// ── 重設成員密碼 ──────────────────────
const resetResultMembershipId = ref<number | null>(null);
const resetResultPassword = ref('');
const resetErrorByMembership = reactive<Record<number, string>>({});
const resettingId = ref<number | null>(null);
async function resetPassword(membershipId: number) {
  if (!activeMerchant.value || resettingId.value === membershipId) return;
  resettingId.value = membershipId;
  delete resetErrorByMembership[membershipId];
  try {
    const result = await resetMembershipPasswordApi(activeMerchant.value.id, membershipId);
    resetResultMembershipId.value = membershipId;
    resetResultPassword.value = result.temporaryPassword;
  } catch (err) {
    resetErrorByMembership[membershipId] = err instanceof Error ? err.message : '重設失敗，請稍後再試。';
  } finally {
    resettingId.value = null;
  }
}
</script>

<template>
  <PlatformLayout page-title="場館管理" current-path="/platform/merchants">
    <div class="flex flex-col gap-4">
      <div class="flex justify-end">
        <AdminButton variant="primary" size="sm" @click="openAdd">＋ 新增場館</AdminButton>
      </div>

      <div v-if="loadError" class="p-3 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-sm">{{ loadError }}</div>

      <div v-else class="bg-white rounded-xl border border-[#E2E8F0] overflow-hidden">
        <div v-if="merchantsQuery.isPending.value" class="p-6 text-center text-sm text-[#94A3B8]">載入中…</div>
        <div v-else class="overflow-x-auto">
          <table class="w-full text-left text-[13px] text-[#334155]">
            <thead class="bg-[#F8FAFC] text-[#64748B] border-b border-[#E2E8F0]">
              <tr>
                <th class="px-4 py-3 font-semibold">場館名稱</th>
                <th class="px-4 py-3 font-semibold">代碼</th>
                <th class="px-4 py-3 font-semibold">狀態</th>
                <th class="px-4 py-3 font-semibold">角色 CRUD 細項模式</th>
                <th class="px-4 py-3 font-semibold">角色子功能細項模式</th>
                <th class="px-4 py-3 font-semibold">操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="m in merchants" :key="m.id" class="border-b border-[#F1F5F9] hover:bg-slate-50/70 align-top">
                <td class="px-4 py-3 font-medium">{{ m.name }}</td>
                <td class="px-4 py-3 font-tabular text-[#64748B]">{{ m.code }}</td>
                <td class="px-4 py-3">
                  <span class="px-2 py-0.5 rounded text-xs font-semibold" :class="m.status === 'active' ? 'bg-[#E6FBF7] text-[#10B981]' : 'bg-[#FFF5F5] text-[#FF4757]'">
                    {{ m.status === 'active' ? '啟用中' : m.status }}
                  </span>
                </td>
                <td class="px-4 py-3">
                  <div class="flex flex-col gap-1.5">
                    <span
                      class="px-2 py-0.5 rounded text-xs font-semibold w-fit"
                      :class="m.isRoleCrudConfigurationEnabled ? 'bg-[#FEF3C7] text-[#92400E]' : 'bg-[#E6FBF7] text-[#10B981]'"
                    >
                      目前：{{ m.isRoleCrudConfigurationEnabled ? '細項模式' : '簡化模式（授予即完整 CRUD）' }}
                    </span>
                    <label class="flex items-center gap-1.5 cursor-pointer" :class="{ 'opacity-50 pointer-events-none': togglingId === m.id }">
                      <input type="checkbox" class="w-3.5 h-3.5 rounded accent-[#4C7DF0]" :checked="m.isRoleCrudConfigurationEnabled" @change="openToggleConfirm(m, 'isRoleCrudConfigurationEnabled')" />
                      <span class="text-xs text-[#64748B]">啟用細項設定</span>
                    </label>
                  </div>
                </td>
                <td class="px-4 py-3">
                  <div class="flex flex-col gap-1.5">
                    <span
                      class="px-2 py-0.5 rounded text-xs font-semibold w-fit"
                      :class="m.isRoleOptionConfigurationEnabled ? 'bg-[#FEF3C7] text-[#92400E]' : 'bg-[#E6FBF7] text-[#10B981]'"
                    >
                      目前：{{ m.isRoleOptionConfigurationEnabled ? '細項模式' : '簡化模式（授予即完整子功能）' }}
                    </span>
                    <label class="flex items-center gap-1.5 cursor-pointer" :class="{ 'opacity-50 pointer-events-none': togglingId === m.id }">
                      <input type="checkbox" class="w-3.5 h-3.5 rounded accent-[#4C7DF0]" :checked="m.isRoleOptionConfigurationEnabled" @change="openToggleConfirm(m, 'isRoleOptionConfigurationEnabled')" />
                      <span class="text-xs text-[#64748B]">啟用細項設定</span>
                    </label>
                  </div>
                </td>
                <td class="px-4 py-3">
                  <button type="button" class="link-action" @click="openMembers(m)">管理成員</button>
                </td>
              </tr>
              <tr v-if="merchants.length === 0">
                <td colspan="6" class="px-4 py-6 text-center text-[#94A3B8]">目前還沒有任何場館。</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </div>

    <!-- 新增場館 -->
    <div v-if="addOpen" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/40" @click.self="addOpen = false">
      <div class="bg-white rounded-xl shadow-2xl border border-[#E2E8F0] w-full max-w-[420px] overflow-hidden">
        <div class="px-6 pt-6 pb-4 border-b border-[#E2E8F0]"><h3 class="text-base font-bold text-[#1A202C]">新增場館</h3></div>
        <form class="px-6 py-5 flex flex-col gap-4" @submit.prevent="submitAdd">
          <div v-if="addError" class="p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ addError }}</div>
          <label class="flex flex-col gap-1.5 text-xs text-[#64748B]">
            場館代碼
            <input v-model="addForm.code" type="text" placeholder="例如 north-branch" class="px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]" />
          </label>
          <label class="flex flex-col gap-1.5 text-xs text-[#64748B]">
            場館名稱
            <input v-model="addForm.name" type="text" placeholder="例如 東元北區廠房" class="px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]" />
          </label>
          <label class="flex items-center gap-2 text-xs text-[#64748B] cursor-pointer">
            <input v-model="addForm.crudEnabled" type="checkbox" class="w-3.5 h-3.5 rounded accent-[#4C7DF0]" />
            啟用角色 CRUD 細項設定（關閉＝授予資源即自動取得完整 CRUD，適合大部分場館）
          </label>
          <label class="flex items-center gap-2 text-xs text-[#64748B] cursor-pointer">
            <input v-model="addForm.optionEnabled" type="checkbox" class="w-3.5 h-3.5 rounded accent-[#4C7DF0]" />
            啟用角色子功能細項設定
          </label>
          <div class="flex justify-end gap-3 pt-1">
            <AdminButton variant="tertiary" :disabled="createMerchantMutation.isPending.value" type="button" @click="addOpen = false">取消</AdminButton>
            <AdminButton variant="primary" :disabled="createMerchantMutation.isPending.value" type="submit">
              {{ createMerchantMutation.isPending.value ? '新增中…' : '新增' }}
            </AdminButton>
          </div>
        </form>
      </div>
    </div>

    <!-- 管理成員 -->
    <div v-if="membersOpen" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/40" @click.self="membersOpen = false">
      <div class="bg-white rounded-xl shadow-2xl border border-[#E2E8F0] w-full max-w-[640px] max-h-[85vh] overflow-y-auto">
        <div class="px-6 pt-6 pb-4 border-b border-[#E2E8F0] flex items-center justify-between">
          <h3 class="text-base font-bold text-[#1A202C]">{{ activeMerchant?.name }}（{{ activeMerchant?.code }}）的成員</h3>
          <button type="button" class="text-[#94A3B8] hover:text-[#334155]" @click="membersOpen = false">
            <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12" /></svg>
          </button>
        </div>

        <div class="px-6 py-5 flex flex-col gap-5">
          <div v-if="membersError" class="p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ membersError }}</div>
          <div v-if="membersLoading" class="text-xs text-[#94A3B8] text-center py-4">載入中…</div>

          <template v-else>
            <section>
              <h4 class="text-sm font-bold text-[#1A202C] mb-2">現有成員（{{ members.length }} 人）</h4>
              <div v-if="members.length === 0" class="text-xs text-[#94A3B8] p-3 rounded-lg bg-[#F8FAFC]">
                這個場館還沒有任何成員，請用下方表單建立第一個帳號（通常是場館管理員）。
              </div>
              <div v-else class="rounded-lg border border-[#E2E8F0] divide-y divide-[#E2E8F0]">
                <div v-for="member in members" :key="member.membershipId" class="px-3 py-2.5 flex items-center justify-between text-sm">
                  <div>
                    <div class="font-medium text-[#334155]">{{ member.displayName }} <span class="text-xs text-[#94A3B8]">（{{ member.username }}）</span></div>
                    <div class="text-xs text-[#64748B] mt-0.5">
                      {{ member.roleName ?? '（未指派）' }}
                      <span v-if="!member.isActive" class="ml-1.5 text-[#FF4757]">已停用</span>
                    </div>
                    <p v-if="resetResultMembershipId === member.membershipId" class="mt-1 text-xs text-[#10B981]">
                      臨時密碼：<span class="font-tabular font-bold">{{ resetResultPassword }}</span>（請自行轉交，這裡不會再顯示第二次）
                    </p>
                    <p v-if="resetErrorByMembership[member.membershipId]" class="mt-1 text-xs text-[#FF4757]">{{ resetErrorByMembership[member.membershipId] }}</p>
                  </div>
                  <button type="button" class="link-action shrink-0" :disabled="resettingId === member.membershipId" @click="resetPassword(member.membershipId)">
                    {{ resettingId === member.membershipId ? '處理中…' : '重設密碼' }}
                  </button>
                </div>
              </div>
            </section>

            <section>
              <h4 class="text-sm font-bold text-[#1A202C] mb-2">新增成員</h4>
              <form class="rounded-lg border border-[#E2E8F0] p-4 flex flex-col gap-3" @submit.prevent="submitAddMember">
                <div v-if="addMemberError" class="p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ addMemberError }}</div>
                <div class="grid grid-cols-2 gap-3">
                  <label class="flex flex-col gap-1.5 text-xs text-[#64748B]">
                    帳號
                    <input v-model="addMemberForm.username" type="text" class="px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2]" />
                  </label>
                  <label class="flex flex-col gap-1.5 text-xs text-[#64748B]">
                    使用者名稱
                    <input v-model="addMemberForm.displayName" type="text" class="px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2]" />
                  </label>
                  <label class="flex flex-col gap-1.5 text-xs text-[#64748B]">
                    Email（選填）
                    <input v-model="addMemberForm.email" type="email" class="px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2]" />
                  </label>
                  <label class="flex flex-col gap-1.5 text-xs text-[#64748B]">
                    初始密碼
                    <input v-model="addMemberForm.password" type="text" class="px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2]" />
                  </label>
                  <label class="flex flex-col gap-1.5 text-xs text-[#64748B] col-span-2">
                    角色
                    <select v-model.number="addMemberForm.roleId" class="px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2]">
                      <option :value="null" disabled>請選擇角色</option>
                      <option v-for="r in assignableRoles" :key="r.id" :value="r.id">{{ r.name }}{{ r.isSystem ? '（系統範本）' : '' }}</option>
                    </select>
                  </label>
                </div>
                <div class="flex justify-end">
                  <AdminButton variant="primary" size="sm" :disabled="addMemberMutation.isPending.value" type="submit">
                    {{ addMemberMutation.isPending.value ? '新增中…' : '＋ 新增成員' }}
                  </AdminButton>
                </div>
              </form>
            </section>
          </template>
        </div>
      </div>
    </div>

    <!-- 切換 CRUD/子項細項模式確認 -->
    <div v-if="toggleConfirmOpen" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/40" @click.self="toggleConfirmOpen = false">
      <div class="bg-white rounded-xl shadow-2xl border border-[#E2E8F0] w-full max-w-[480px] overflow-hidden">
        <div class="px-6 pt-6 pb-4 border-b border-[#E2E8F0]">
          <h3 class="text-base font-bold text-[#1A202C]">
            切換「{{ pendingToggle ? TOGGLE_LABELS[pendingToggle.field] : '' }}」模式
          </h3>
        </div>
        <div class="px-6 py-5 flex flex-col gap-3">
          <div v-if="toggleConfirmError" class="p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ toggleConfirmError }}</div>
          <p class="text-sm text-[#334155]">
            確定要把「<strong>{{ pendingToggle?.merchant.name }}</strong>」的
            {{ pendingToggle ? TOGGLE_LABELS[pendingToggle.field] : '' }} 模式切換成
            <strong>{{ pendingToggle && !pendingToggle.merchant[pendingToggle.field] ? '細項模式' : '簡化模式' }}</strong>
            嗎？
          </p>
          <div v-if="pendingToggle && !pendingToggle.merchant[pendingToggle.field]" class="p-3 rounded-lg bg-[#FFF9E6] text-xs text-[#92400E] leading-relaxed">
            <strong>切到細項模式會立即影響所有成員實際能用的功能，不是只有畫面上的設定：</strong>
            用「編輯成員」簡化面板設定過權限的成員（個人專屬角色），資料庫裡實際上只存了
            最基本的讀取權限，一直是靠簡化模式在登入時自動展開成完整功能。切到細項模式後，
            這個自動展開會停止，這些成員下次登入會突然只剩讀取，新增/修改/刪除全部消失，
            且畫面上不會有任何提示。確定要繼續的話，切換後請記得到「角色管理」頁面幫這些
            成員補上正確的權限。
          </div>
          <div v-else class="p-3 rounded-lg bg-[#F0FDFB] text-xs text-[#0F766E] leading-relaxed">
            切回簡化模式後，只要角色被授予某資源（哪怕只勾讀取），登入時就會自動展開成完整
            CRUD 與該資源全部子功能——會讓部分成員的實際權限比畫面上勾選的還要多，請確認這是
            你要的結果。
          </div>
          <div class="flex justify-end gap-3 pt-1">
            <AdminButton variant="tertiary" :disabled="togglingId !== null" type="button" @click="toggleConfirmOpen = false">取消</AdminButton>
            <AdminButton variant="primary" :disabled="togglingId !== null" type="button" @click="confirmToggle">
              {{ togglingId !== null ? '處理中…' : '確定切換' }}
            </AdminButton>
          </div>
        </div>
      </div>
    </div>
  </PlatformLayout>
</template>
