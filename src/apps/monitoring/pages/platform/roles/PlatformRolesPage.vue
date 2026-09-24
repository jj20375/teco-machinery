<script setup lang="ts">
/**
 * @file PlatformRolesPage.vue
 * 平台管理 - 角色管理（/platform/roles）
 * 比照 admin/roles/AdminRolesPage.vue 的編輯權限面板，但刻意沒有「改名」「刪除」——
 * PlatformEndpoints 目前只有 List/Create/GetPermissions/SetPermissions，沒有對應的
 * PATCH/DELETE 端點（跟場館角色管理不對稱），這裡照實反映後端現況，不是漏做。
 * 平台角色數量少（目前只有 platform-admin/platform-operator 兩個系統範本），
 * 也還沒有「上限交集」規則（MerchantRolePermissionCeilingRules 只套用在場館範圍），
 * 所以這裡的勾選格沒有場館角色管理頁那種「灰掉代表超過上限」的邏輯。
 */
import { ref, reactive, computed } from 'vue';
import { useQuery, useMutation, useQueryClient } from '@tanstack/vue-query';
import PlatformLayout from '../_components/PlatformLayout.vue';
import AdminButton from '../../admin/_components/AdminButton.vue';
import AdminRightPanel from '../../admin/_components/AdminRightPanel.vue';
import { ApiError } from '../../admin/_services/auth-service';
import {
  listPlatformRolesApi, listSystemUsersApi, listPlatformPermissionCatalogApi,
  getPlatformRolePermissionsApi, setPlatformRolePermissionsApi, createPlatformRoleApi,
  parseSubFeatures, type PlatformRoleOption, type PermissionCatalogItem, type SetRolePermissionItem,
} from '../_services/platform-service';

const rolesQuery = useQuery({ queryKey: ['platform-roles-mgmt'], queryFn: listPlatformRolesApi });
const usersQuery = useQuery({ queryKey: ['platform-system-users-for-role-count'], queryFn: listSystemUsersApi });
const catalogQuery = useQuery({ queryKey: ['platform-permission-catalog'], queryFn: listPlatformPermissionCatalogApi });
const queryClient = useQueryClient();

const roles = computed(() => rolesQuery.data.value ?? []);
const loading = computed(() => rolesQuery.isPending.value || catalogQuery.isPending.value);

function describeError(err: unknown, forbiddenMessage: string, fallbackMessage: string): string {
  if (!err) return '';
  if (err instanceof ApiError && err.status === 403) return forbiddenMessage;
  return err instanceof Error ? err.message : fallbackMessage;
}
const loadError = computed(() =>
  describeError(rolesQuery.error.value ?? catalogQuery.error.value, '目前登入的帳號沒有「平台角色管理」權限，無法查看或編輯角色。', '載入角色清單失敗，請稍後再試。'),
);

const memberCountByRoleId = computed(() => {
  const counts = new Map<number, number>();
  for (const u of usersQuery.data.value ?? []) {
    if (u.systemRoleId === null) continue;
    counts.set(u.systemRoleId, (counts.get(u.systemRoleId) ?? 0) + 1);
  }
  return counts;
});

function invalidateRoles() {
  return queryClient.invalidateQueries({ queryKey: ['platform-roles-mgmt'] });
}

const groupedCatalog = computed(() => {
  const groups = new Map<string, PermissionCatalogItem[]>();
  for (const item of catalogQuery.data.value ?? []) {
    if (!groups.has('平台管理')) groups.set('平台管理', []);
    groups.get('平台管理')!.push(item);
  }
  return [...groups.entries()];
});

// ── 新增角色 ──────────────────────────
const addOpen = ref(false);
const addError = ref('');
const addForm = reactive({ code: '', name: '' });
const createRoleMutation = useMutation({
  mutationFn: () => createPlatformRoleApi(addForm.code.trim(), addForm.name.trim()),
  onSuccess: () => invalidateRoles(),
});
function openAdd() {
  addForm.code = ''; addForm.name = ''; addError.value = '';
  addOpen.value = true;
}
async function submitAdd() {
  if (createRoleMutation.isPending.value) return;
  if (!addForm.code.trim() || !addForm.name.trim()) {
    addError.value = '代碼與名稱為必填。';
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

// ── 編輯權限面板 ──────────────────────
interface PermissionRow {
  code: string; create: boolean; read: boolean; update: boolean; delete: boolean; options: string[];
}

const panelOpen = ref(false);
const panelLoading = ref(false);
const saveError = ref('');
const activeRole = ref<PlatformRoleOption | null>(null);
const rowsByCode = reactive<Record<string, PermissionRow>>({});
const originalSnapshot = ref('');

function emptyRow(code: string): PermissionRow {
  return { code, create: false, read: false, update: false, delete: false, options: [] };
}
function parseOptionsJson(json: string): string[] {
  try {
    const parsed = JSON.parse(json);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

// 只有 platform-admin（IsPlatformAdmin）能改系統範本角色，一般平台操作員只能看——
// 這裡沒有精準判斷目前登入者是不是 platform-admin，保守起見系統範本角色一律唯讀，
// 跟後端「role.IsSystem && !scope.IsPlatformAdmin 才擋」比起來寧可畫面更保守一點，
// 真正能不能存還是後端說了算，前端唯讀只是避免看起來能按、實際 403 的落差。
const isSystemRole = computed(() => activeRole.value?.isSystem ?? false);

async function openPanel(role: PlatformRoleOption) {
  activeRole.value = role;
  saveError.value = '';
  panelOpen.value = true;
  panelLoading.value = true;
  try {
    const details = await getPlatformRolePermissionsApi(role.id);
    const byCode = new Map(details.map((d) => [d.permissionCode, d]));
    for (const key of Object.keys(rowsByCode)) delete rowsByCode[key];
    for (const item of catalogQuery.data.value ?? []) {
      const existing = byCode.get(item.code);
      rowsByCode[item.code] = existing
        ? { code: item.code, create: existing.perCreate, read: existing.perRead, update: existing.perUpdate, delete: existing.perDelete, options: parseOptionsJson(existing.optionsJson) }
        : emptyRow(item.code);
    }
    originalSnapshot.value = JSON.stringify(rowsByCode);
  } catch (err) {
    saveError.value = err instanceof ApiError && err.status === 403
      ? '目前登入的帳號沒有「平台角色管理」的查看權限。'
      : err instanceof Error ? err.message : '讀取權限設定失敗，請稍後再試。';
  } finally {
    panelLoading.value = false;
  }
}
const changed = computed(() => JSON.stringify(rowsByCode) !== originalSnapshot.value);

function toggleOption(code: string, key: string) {
  const row = rowsByCode[code];
  if (!row) return;
  row.options = row.options.includes(key) ? row.options.filter((k) => k !== key) : [...row.options, key];
}

const savingPermissions = ref(false);
async function savePermissions() {
  if (!activeRole.value || !changed.value || savingPermissions.value) return;
  savingPermissions.value = true;
  saveError.value = '';
  try {
    const items: SetRolePermissionItem[] = Object.values(rowsByCode).map((r) => ({
      permissionCode: r.code, perCreate: r.create, perRead: r.read, perUpdate: r.update, perDelete: r.delete, options: r.options,
    }));
    await setPlatformRolePermissionsApi(activeRole.value.id, items);
    panelOpen.value = false;
  } catch (err) {
    saveError.value = err instanceof Error ? err.message : '儲存失敗，請稍後再試。';
  } finally {
    savingPermissions.value = false;
  }
}
</script>

<template>
  <PlatformLayout page-title="角色管理" current-path="/platform/roles">
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
                  <span class="px-2 py-0.5 rounded text-xs font-semibold" :class="r.isSystem ? 'bg-[#F1F5F9] text-[#64748B]' : 'bg-[#E6FBF7] text-[#10B981]'">
                    {{ r.isSystem ? '系統範本' : '自訂角色' }}
                  </span>
                </td>
                <td class="px-4 py-3 font-tabular text-[#64748B]">{{ memberCountByRoleId.get(r.id) ?? 0 }} 人</td>
                <td class="px-4 py-3">
                  <button type="button" class="link-action" @click="openPanel(r)">{{ r.isSystem ? '檢視權限' : '編輯權限' }}</button>
                </td>
              </tr>
              <tr v-if="roles.length === 0">
                <td colspan="5" class="px-4 py-6 text-center text-[#94A3B8]">目前還沒有任何平台角色。</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </div>

    <!-- 編輯權限面板 -->
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
        <p v-if="isSystemRole" class="text-xs text-[#94A3B8]">系統範本角色（平台管理員／平台操作員）只能檢視，僅平台管理員本人可以調整。</p>

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
                    :class="{ 'opacity-40 cursor-not-allowed': isSystemRole }"
                  >
                    <input
                      type="checkbox"
                      class="w-3.5 h-3.5 rounded accent-[#4C7DF0]"
                      :disabled="isSystemRole"
                      v-model="rowsByCode[item.code][field as 'create' | 'read' | 'update' | 'delete']"
                    />
                    {{ label }}
                  </label>
                </div>
              </div>
              <div v-if="parseSubFeatures(item.subFeaturesJson).length > 0" class="flex flex-wrap gap-3 mt-2 pl-1">
                <label
                  v-for="opt in parseSubFeatures(item.subFeaturesJson)"
                  :key="opt.key"
                  class="flex items-center gap-1.5 text-xs text-[#64748B] cursor-pointer"
                  :class="{ 'opacity-40 cursor-not-allowed': isSystemRole }"
                >
                  <input
                    type="checkbox"
                    class="w-3.5 h-3.5 rounded accent-[#4C7DF0]"
                    :disabled="isSystemRole"
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
            <input v-model="addForm.code" type="text" placeholder="例如 platform-auditor" class="px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]" />
          </label>
          <label class="flex flex-col gap-1.5 text-xs text-[#64748B]">
            角色名稱
            <input v-model="addForm.name" type="text" placeholder="例如 稽核人員" class="px-3 py-2 text-sm text-black border border-[#CBD5E1] rounded-lg focus:outline-none focus:border-[#00D1B2] placeholder-[#94A3B8]" />
          </label>
          <p class="text-xs text-[#94A3B8]">新角色一開始沒有任何權限，建立後要另外開啟「編輯權限」勾選。</p>
          <div class="flex justify-end gap-3 pt-1">
            <AdminButton variant="tertiary" :disabled="createRoleMutation.isPending.value" type="button" @click="addOpen = false">取消</AdminButton>
            <AdminButton variant="primary" :disabled="createRoleMutation.isPending.value" type="submit">
              {{ createRoleMutation.isPending.value ? '新增中…' : '新增' }}
            </AdminButton>
          </div>
        </form>
      </div>
    </div>
  </PlatformLayout>
</template>
