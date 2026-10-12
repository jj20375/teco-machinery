<script setup lang="ts">
import { ref, reactive, computed, watch, onBeforeUnmount } from 'vue';
import { useQuery, useMutation, useQueryClient } from '@tanstack/vue-query';
import AdminLayout from '../_components/AdminLayout.vue';
import AdminButton from '../_components/AdminButton.vue';
import AdminRightPanel from '../_components/AdminRightPanel.vue';
import { ApiError } from '../_services/auth-service';
import { listMerchantUsersApi } from '../_services/user-service';
import {
  listMerchantRolesApi,
  listPermissionCatalogApi,
  getRolePermissionsApi,
  setRolePermissionsApi,
  createMerchantRoleApi,
  checkRoleCodeApi,
  renameMerchantRoleApi,
  deleteMerchantRoleApi,
  parseSubFeatures,
  type MerchantRoleOption,
  type PermissionCatalogItem,
  type SetRolePermissionItem,
} from '../_services/role-service';

const queryClient = useQueryClient();

const rolesQuery = useQuery({ queryKey: ['merchant-roles-mgmt'], queryFn: listMerchantRolesApi });
const usersQuery = useQuery({ queryKey: ['merchant-users-for-role-count'], queryFn: listMerchantUsersApi });
const catalogQuery = useQuery({ queryKey: ['merchant-permission-catalog'], queryFn: listPermissionCatalogApi });

const roles = computed(() => rolesQuery.data.value ?? []);
const loading = computed(() => rolesQuery.isPending.value || catalogQuery.isPending.value);

function describeError(err: unknown, forbiddenMessage: string, fallbackMessage: string): string {
  if (!err) return '';
  if (err instanceof ApiError && err.status === 403) return forbiddenMessage;
  return err instanceof Error ? err.message : fallbackMessage;
}
const loadError = computed(() =>
  describeError(rolesQuery.error.value ?? catalogQuery.error.value, '目前登入的帳號沒有「場館角色管理」權限，無法查看或編輯角色。', '載入角色清單失敗，請稍後再試。'),
);

/** 每個角色目前有幾個成員在用——刪除角色前的參考資訊，跟後端 DeleteRole 的擋板邏輯一致。 */
const memberCountByRoleId = computed(() => {
  const counts = new Map<number, number>();
  for (const u of usersQuery.data.value ?? []) {
    if (u.roleId === null) continue;
    counts.set(u.roleId, (counts.get(u.roleId) ?? 0) + 1);
  }
  return counts;
});

function invalidateRoles() {
  return queryClient.invalidateQueries({ queryKey: ['merchant-roles-mgmt'] });
}

/** 依代碼前綴分組顯示（hvac.* / merchant.*），比一長串平鋪的清單好讀。 */
const groupedCatalog = computed(() => {
  const groups = new Map<string, PermissionCatalogItem[]>();
  for (const item of catalogQuery.data.value ?? []) {
    const prefix = item.code.split('.')[0];
    const label = prefix === 'hvac' ? 'HVAC 監控' : prefix === 'merchant' ? '場館管理' : prefix;
    if (!groups.has(label)) groups.set(label, []);
    groups.get(label)!.push(item);
  }
  return [...groups.entries()];
});

// ── 新增角色 ──────────────────────────
const addOpen = ref(false);
const addError = ref('');
const addForm = reactive({ code: '', name: '' });
const createRoleMutation = useMutation({
  mutationFn: () => createMerchantRoleApi(addForm.code.trim(), addForm.name.trim()),
  onSuccess: () => invalidateRoles(),
});
// 代碼即時檢查：停止輸入 400ms 後（或離開欄位時）問後端一次。用序號丟掉過期的回應，
// 避免快速連打時較慢的舊回應蓋掉最新結果。後端新增時還會再查一次，這裡只是讓人早點看到提示。
const codeCheck = reactive<{ state: 'idle' | 'checking' | 'ok' | 'taken' | 'error'; message: string }>({ state: 'idle', message: '' });
let codeCheckSeq = 0;
let codeCheckTimer: ReturnType<typeof setTimeout> | undefined;

async function runCodeCheck() {
  clearTimeout(codeCheckTimer);
  const code = addForm.code.trim();
  if (!code) {
    codeCheckSeq++;
    codeCheck.state = 'idle';
    return;
  }
  const seq = ++codeCheckSeq;
  codeCheck.state = 'checking';
  try {
    const result = await checkRoleCodeApi(code);
    if (seq !== codeCheckSeq) return;
    codeCheck.state = result.available ? 'ok' : 'taken';
    codeCheck.message = result.message ?? '';
  } catch (err) {
    if (seq !== codeCheckSeq) return;
    // 檢查本身失敗（網路、權限）不擋新增，後端送出時仍會檢查。
    codeCheck.state = 'error';
    codeCheck.message = err instanceof Error ? err.message : '無法確認代碼是否重複。';
  }
}
watch(() => addForm.code, () => {
  clearTimeout(codeCheckTimer);
  codeCheckSeq++;
  if (!addForm.code.trim()) {
    codeCheck.state = 'idle';
    return;
  }
  codeCheck.state = 'checking';
  codeCheckTimer = setTimeout(runCodeCheck, 400);
});
onBeforeUnmount(() => clearTimeout(codeCheckTimer));

function openAdd() {
  addForm.code = '';
  addForm.name = '';
  addError.value = '';
  codeCheck.state = 'idle';
  addOpen.value = true;
}
async function submitAdd() {
  if (createRoleMutation.isPending.value) return;
  if (!addForm.code.trim() || !addForm.name.trim()) {
    addError.value = '代碼與名稱為必填。';
    return;
  }
  if (codeCheck.state === 'taken') {
    addError.value = codeCheck.message || '這個角色代碼已經存在，請換一個。';
    return;
  }
  addError.value = '';
  try {
    await createRoleMutation.mutateAsync();
    addOpen.value = false;
  } catch (err) {
    addError.value = err instanceof Error ? err.message : '新增失敗，請稍後再試。';
  }
}

// ── 刪除 ──────────────────────────────
const deleteOpen = ref(false);
const deleteError = ref('');
const deleteTarget = ref<MerchantRoleOption | null>(null);
const deleteRoleMutation = useMutation({
  mutationFn: (roleId: number) => deleteMerchantRoleApi(roleId),
  onSuccess: () => invalidateRoles(),
});
function openDelete(role: MerchantRoleOption) {
  deleteTarget.value = role;
  deleteError.value = '';
  deleteOpen.value = true;
}
async function confirmDelete() {
  if (!deleteTarget.value || deleteRoleMutation.isPending.value) return;
  deleteError.value = '';
  try {
    await deleteRoleMutation.mutateAsync(deleteTarget.value.id);
    deleteOpen.value = false;
  } catch (err) {
    deleteError.value = err instanceof Error ? err.message : '刪除失敗，請稍後再試。';
  }
}

// ── 編輯權限 面板 ──────────────────────
interface PermissionRow {
  code: string;
  create: boolean;
  read: boolean;
  update: boolean;
  delete: boolean;
  options: string[];
}

const panelOpen = ref(false);
const panelLoading = ref(false);
const saveError = ref('');
const activeRole = ref<MerchantRoleOption | null>(null);
const rowsByCode = reactive<Record<string, PermissionRow>>({});
const originalSnapshot = ref('');
// 角色名稱跟權限在同一個面板編輯、同一個「儲存」送出；originalName 是最後一次成功存下的名稱，
// 改名成功但權限儲存失敗時，重試只需要重送權限，不會再改一次名。
const roleName = ref('');
const originalName = ref('');
const nameError = ref('');
/** merchant-admin 的實際授予——編輯其他角色時的上限，跟後端 SetRolePermissions 的
 * ceiling 檢查是同一份邏輯，這裡先在前端擋掉不可能過關的勾選，不用等後端 400 才知道。 */
const ceilingByCode = ref<Record<string, PermissionRow>>({});

function emptyRow(code: string): PermissionRow {
  return { code, create: false, read: false, update: false, delete: false, options: [] };
}

async function loadCeiling() {
  const adminRole = roles.value.find((r) => r.code === 'merchant-admin' && r.merchantId === null);
  if (!adminRole) return;
  const details = await getRolePermissionsApi(adminRole.id);
  const map: Record<string, PermissionRow> = {};
  for (const d of details) {
    map[d.permissionCode] = {
      code: d.permissionCode, create: d.perCreate, read: d.perRead, update: d.perUpdate, delete: d.perDelete,
      options: parseOptionsJson(d.optionsJson),
    };
  }
  ceilingByCode.value = map;
}
function parseOptionsJson(json: string): string[] {
  try {
    const parsed = JSON.parse(json);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}
function isAllowedByCeiling(code: string, field: 'create' | 'read' | 'update' | 'delete'): boolean {
  return ceilingByCode.value[code]?.[field] ?? false;
}
function isOptionAllowedByCeiling(code: string, key: string): boolean {
  return ceilingByCode.value[code]?.options.includes(key) ?? false;
}

// hvac.overview／hvac.reports 只控制側邊欄選單看不看得到（見 AdminSidebar.vue 的 route_path／
// is_menu），不是資料權限本身——監控中心／各張報表實際讀的是冰水主機/FCU 設定/告警這三個資源
// 權限（見 AdminOverviewPage.vue、AdminChillerReportPage.vue 等頁面的 403 錯誤訊息）。這兩層
// 完全獨立、不會互相連動勾選，只勾「監控中心」或「報表」而不勾對應資源權限，畫面會變成選單
// 進得去、內容卻整頁顯示沒有權限——這裡加提示文字，讓管理員勾選時就看得到這個依賴關係。
const PERMISSION_DEPENDENCY_HINTS: Record<string, string> = {
  'hvac.overview': '只控制「監控中心」選單看不看得到；頁面要能顯示資料，還要同時勾選下面的「冰水主機」「FCU 設定」讀取。',
  'hvac.reports': '只控制「統計報表」選單看不看得到；個別報表要能顯示資料，還要同時勾選對應的「冰水主機」「FCU 設定」「告警」讀取。',
};
function permissionDependencyHint(code: string): string {
  return PERMISSION_DEPENDENCY_HINTS[code] ?? '';
}

const isSystemRole = computed(() => activeRole.value?.isSystem ?? false);

async function openPanel(role: MerchantRoleOption) {
  activeRole.value = role;
  saveError.value = '';
  nameError.value = '';
  roleName.value = role.name;
  originalName.value = role.name;
  panelOpen.value = true;
  panelLoading.value = true;
  try {
    if (Object.keys(ceilingByCode.value).length === 0) await loadCeiling();
    const details = await getRolePermissionsApi(role.id);
    const byCode = new Map(details.map((d) => [d.permissionCode, d]));
    for (const key of Object.keys(rowsByCode)) delete rowsByCode[key];
    for (const item of catalogQuery.data.value ?? []) {
      const existing = byCode.get(item.code);
      rowsByCode[item.code] = existing
        ? {
            code: item.code, create: existing.perCreate, read: existing.perRead,
            update: existing.perUpdate, delete: existing.perDelete, options: parseOptionsJson(existing.optionsJson),
          }
        : emptyRow(item.code);
    }
    originalSnapshot.value = JSON.stringify(rowsByCode);
  } catch (err) {
    saveError.value = err instanceof ApiError && err.status === 403
      ? '目前登入的帳號沒有「場館角色管理」的查看權限。'
      : err instanceof Error ? err.message : '讀取權限設定失敗，請稍後再試。';
  } finally {
    panelLoading.value = false;
  }
}
const permissionsChanged = computed(() => JSON.stringify(rowsByCode) !== originalSnapshot.value);
const nameChanged = computed(() => roleName.value.trim() !== originalName.value);
const changed = computed(() => permissionsChanged.value || nameChanged.value);

function toggleOption(code: string, key: string) {
  const row = rowsByCode[code];
  if (!row) return;
  row.options = row.options.includes(key) ? row.options.filter((k) => k !== key) : [...row.options, key];
}

const savingPermissions = ref(false);
async function savePermissions() {
  if (!activeRole.value || !changed.value || savingPermissions.value) return;
  const name = roleName.value.trim();
  if (nameChanged.value && !name) {
    nameError.value = '角色名稱不能是空白。';
    return;
  }
  savingPermissions.value = true;
  saveError.value = '';
  nameError.value = '';
  try {
    if (nameChanged.value) {
      await renameMerchantRoleApi(activeRole.value.id, name);
      originalName.value = name;
      activeRole.value = { ...activeRole.value, name };
    }
    if (permissionsChanged.value) {
      const items: SetRolePermissionItem[] = Object.values(rowsByCode).map((r) => ({
        permissionCode: r.code, perCreate: r.create, perRead: r.read, perUpdate: r.update, perDelete: r.delete, options: r.options,
      }));
      await setRolePermissionsApi(activeRole.value.id, items);
    }
    await invalidateRoles();
    panelOpen.value = false;
  } catch (err) {
    saveError.value = err instanceof Error ? err.message : '儲存失敗，請稍後再試。';
    // 改名可能已經成功，清單要跟上，不然畫面上還是舊名稱。
    await invalidateRoles();
  } finally {
    savingPermissions.value = false;
  }
}
</script>

<template>
  <AdminLayout page-title="角色管理" current-path="/admin/roles">
    <div class="flex flex-col gap-4">
      <div class="flex justify-end">
        <AdminButton variant="primary" size="sm" :disabled="loading || !!loadError" @click="openAdd">＋ 新增角色</AdminButton>
      </div>

      <div v-if="loadError" class="p-3 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-sm">{{ loadError }}</div>

      <div v-else class="bg-white rounded-xl border border-[#E2E8F0] overflow-hidden">
        <div v-if="loading" class="p-6 text-center text-sm text-[#94A3B8]">載入中…</div>
        <div v-else class="overflow-x-auto">
          <table class="w-full text-left text-[13px] text-[#334155]">
            <thead class="bg-[#F8FAFC] text-[#64748B] border-b border-[#E2E8F0]">
              <tr>
                <th class="px-4 py-3 font-semibold">角色名稱</th>
                <th class="px-4 py-3 font-semibold">代碼</th>
                <th class="px-4 py-3 font-semibold">類型</th>
                <th class="px-4 py-3 font-semibold">使用人數</th>
                <th class="px-4 py-3 font-semibold">操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="r in roles" :key="r.id" class="border-b border-[#F1F5F9] hover:bg-slate-50/70">
                <td class="px-4 py-3 font-medium">{{ r.name }}</td>
                <td class="px-4 py-3 font-tabular text-[#64748B]">{{ r.code }}</td>
                <td class="px-4 py-3">
                  <span
                    class="px-2 py-0.5 rounded text-xs font-semibold"
                    :class="r.isSystem ? 'bg-[#F1F5F9] text-[#64748B]' : 'bg-[#E6FBF7] text-[#10B981]'"
                  >
                    {{ r.isSystem ? '系統範本' : '自訂角色' }}
                  </span>
                </td>
                <td class="px-4 py-3 font-tabular text-[#64748B]">{{ memberCountByRoleId.get(r.id) ?? 0 }} 人</td>
                <td class="px-4 py-3">
                  <div class="flex items-center gap-3">
                    <button type="button" class="link-action" @click="openPanel(r)">
                      {{ r.isSystem ? '檢視權限' : '編輯權限' }}
                    </button>
                    <template v-if="!r.isSystem">
                      <button type="button" class="link-action" style="color: #FF4757" @click="openDelete(r)">刪除</button>
                    </template>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </div>

    <!-- 編輯權限 面板 -->
    <AdminRightPanel
      :open="panelOpen"
      :title="activeRole ? `${activeRole.name}（${activeRole.code}）` : ''"
      :can-save="changed && !isSystemRole"
      :saving="savingPermissions"
      @close="panelOpen = false"
      @save="savePermissions"
    >
      <div v-if="panelLoading" class="text-xs text-[#94A3B8] py-4 text-center">載入中…</div>
      <div v-else class="flex flex-col gap-5">
        <div v-if="saveError" class="p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ saveError }}</div>
        <label class="flex flex-col gap-1.5 text-xs text-[#64748B]">
          角色名稱
          <input
            v-model="roleName"
            type="text"
            maxlength="64"
            :disabled="isSystemRole"
            @input="nameError = ''"
            class="px-3 py-2 text-sm text-black border rounded-lg focus:outline-none disabled:bg-[#F8FAFC] disabled:text-[#94A3B8]"
            :class="nameError ? 'border-[#FF4757] focus:border-[#FF4757]' : 'border-[#CBD5E1] focus:border-[#00D1B2]'"
          />
          <span v-if="nameError" class="text-[#FF4757]">{{ nameError }}</span>
        </label>
        <p v-if="isSystemRole" class="text-xs text-[#94A3B8]">
          系統範本角色（場館管理員／編輯者／檢視者）只能檢視，僅平台管理員可以調整。
        </p>
        <p v-else class="text-xs text-[#94A3B8]">
          任何項目都不能超過「場館管理員」角色目前實際擁有的權限——灰掉的勾選框代表場館管理員
          自己也沒有這項權限，勾了也會被後端拒絕。
        </p>

        <section v-for="[groupLabel, items] in groupedCatalog" :key="groupLabel">
          <h4 class="text-sm font-bold text-[#1A202C] mb-2">{{ groupLabel }}</h4>
          <div class="rounded-lg border border-[#E2E8F0] divide-y divide-[#E2E8F0]">
            <div v-for="item in items" :key="item.code" class="px-3 py-2.5">
              <div class="flex items-center justify-between text-sm">
                <span class="text-[#334155] font-medium">{{ item.name }}</span>
                <div class="flex items-center gap-3 text-xs text-[#64748B]">
                  <label
                    v-for="(label, field) in { create: '新增', read: '讀取', update: '修改', delete: '刪除' }"
                    :key="field"
                    class="flex items-center gap-1 cursor-pointer"
                    :class="{ 'opacity-40 cursor-not-allowed': isSystemRole || !isAllowedByCeiling(item.code, field as any) }"
                  >
                    <input
                      type="checkbox"
                      class="w-3.5 h-3.5 rounded accent-[#4C7DF0]"
                      :disabled="isSystemRole || !isAllowedByCeiling(item.code, field as any)"
                      v-model="rowsByCode[item.code][field as 'create' | 'read' | 'update' | 'delete']"
                    />
                    {{ label }}
                  </label>
                </div>
              </div>
              <p v-if="permissionDependencyHint(item.code)" class="mt-1 text-xs text-[#94A3B8]">
                {{ permissionDependencyHint(item.code) }}
              </p>
              <div v-if="parseSubFeatures(item.subFeaturesJson).length > 0" class="flex flex-wrap gap-3 mt-2 pl-1">
                <label
                  v-for="opt in parseSubFeatures(item.subFeaturesJson)"
                  :key="opt.key"
                  class="flex items-center gap-1.5 text-xs text-[#64748B] cursor-pointer"
                  :class="{ 'opacity-40 cursor-not-allowed': isSystemRole || !isOptionAllowedByCeiling(item.code, opt.key) }"
                >
                  <input
                    type="checkbox"
                    class="w-3.5 h-3.5 rounded accent-[#4C7DF0]"
                    :disabled="isSystemRole || !isOptionAllowedByCeiling(item.code, opt.key)"
                    :checked="rowsByCode[item.code].options.includes(opt.key)"
                    @change="toggleOption(item.code, opt.key)"
                  />
                  {{ opt.name }}
                </label>
              </div>
            </div>
          </div>
        </section>
      </div>
    </AdminRightPanel>

    <!-- 新增角色 -->
    <div v-if="addOpen" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/40" @click.self="addOpen = false">
      <div class="bg-white rounded-xl shadow-2xl border border-[#E2E8F0] w-full max-w-[420px] overflow-hidden">
        <div class="px-6 pt-6 pb-4 border-b border-[#E2E8F0]"><h3 class="text-base font-bold text-[#1A202C]">新增角色</h3></div>
        <form class="px-6 py-5 flex flex-col gap-4" @submit.prevent="submitAdd">
          <div v-if="addError" class="p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ addError }}</div>
          <label class="flex flex-col gap-1.5 text-xs text-[#64748B]">
            角色代碼
            <input
              v-model="addForm.code"
              type="text"
              placeholder="例如 supervisor"
              class="px-3 py-2 text-sm text-black border rounded-lg focus:outline-none placeholder-[#94A3B8]"
              :class="codeCheck.state === 'taken' ? 'border-[#FF4757] focus:border-[#FF4757]' : 'border-[#CBD5E1] focus:border-[#00D1B2]'"
              @blur="runCodeCheck"
            />
            <span v-if="codeCheck.state === 'checking'" class="text-[#94A3B8]">檢查代碼中…</span>
            <span v-else-if="codeCheck.state === 'taken'" class="text-[#FF4757]">{{ codeCheck.message }}</span>
            <span v-else-if="codeCheck.state === 'ok'" class="text-[#10B981]">這個代碼可以使用</span>
            <span v-else-if="codeCheck.state === 'error'" class="text-[#B45309]">{{ codeCheck.message }}（送出時仍會再檢查一次）</span>
          </label>
          <label class="flex flex-col gap-1.5 text-xs text-[#64748B]">
            角色名稱
            <input v-model="addForm.name" type="text" placeholder="例如 值班主管" class="px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]" />
          </label>
          <p class="text-xs text-[#94A3B8]">新角色一開始沒有任何權限，建立後要另外開啟「編輯權限」勾選。</p>
          <div class="flex justify-end gap-3 pt-1">
            <AdminButton variant="tertiary" :disabled="createRoleMutation.isPending.value" type="button" @click="addOpen = false">取消</AdminButton>
            <AdminButton variant="primary" :disabled="createRoleMutation.isPending.value || codeCheck.state === 'taken'" type="submit">
              {{ createRoleMutation.isPending.value ? '新增中…' : '新增' }}
            </AdminButton>
          </div>
        </form>
      </div>
    </div>

    <!-- 刪除角色 -->
    <div v-if="deleteOpen" class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/40" @click.self="deleteOpen = false">
      <div class="bg-white rounded-xl shadow-2xl border border-[#E2E8F0] w-full max-w-[420px] overflow-hidden">
        <div class="px-6 pt-6 pb-4 border-b border-[#E2E8F0]"><h3 class="text-base font-bold text-[#1A202C]">刪除角色</h3></div>
        <div class="px-6 py-5">
          <div v-if="deleteError" class="mb-3 p-2.5 rounded-lg bg-[#FFF5F5] text-[#FF4757] text-xs">{{ deleteError }}</div>
          <p class="text-sm text-[#334155]">確定要刪除角色 <strong>{{ deleteTarget?.name }}（{{ deleteTarget?.code }}）</strong> 嗎？</p>
          <div class="mt-3 p-3 rounded-lg bg-[#FFF5F5] text-xs text-[#FF4757]">
            這個操作無法復原。如果還有成員在使用這個角色，會被拒絕，請先幫他們改指派其他角色。
          </div>
          <div class="flex justify-end gap-3 pt-4">
            <AdminButton variant="tertiary" :disabled="deleteRoleMutation.isPending.value" @click="deleteOpen = false">取消</AdminButton>
            <AdminButton variant="primary" style="background-color: #FF4757" :disabled="deleteRoleMutation.isPending.value" @click="confirmDelete">
              {{ deleteRoleMutation.isPending.value ? '刪除中…' : '確認刪除' }}
            </AdminButton>
          </div>
        </div>
      </div>
    </div>
  </AdminLayout>
</template>
