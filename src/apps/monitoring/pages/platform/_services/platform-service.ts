/**
 * @file platform-service.ts
 * 東元電機智慧環境監控 - 平台管理服務
 * 嚴格遵守專案規範：所有對外呼叫函式一律以 *Api 結尾
 *
 * 打的是 backend/ 的 /api/v1/platform/*（見
 * backend/src/Teco.Hvac.Api/Endpoints/PlatformEndpoints.cs）。只有 platform scope 且持有
 * 對應 platform.* 權限的帳號能用；場館自己的日常管理（自家帳號/角色）走 user-service.ts／
 * role-service.ts 打 /api/v1/merchant/*，不要混用。
 */

import { authorizedJsonApi, getSessionApi } from '../../admin/_services/auth-service';

export interface PlatformMerchant {
  id: number;
  code: string;
  name: string;
  status: string;
  isRoleCrudConfigurationEnabled: boolean;
  isRoleOptionConfigurationEnabled: boolean;
  createdAt: string;
  updatedAt: string;
}

export function listMerchantsApi(): Promise<PlatformMerchant[]> {
  return authorizedJsonApi('/api/v1/platform/merchants');
}

export function createMerchantApi(input: {
  code: string;
  name: string;
  isRoleCrudConfigurationEnabled?: boolean;
  isRoleOptionConfigurationEnabled?: boolean;
}): Promise<{ id: number }> {
  return authorizedJsonApi('/api/v1/platform/merchants', { method: 'POST', body: JSON.stringify(input) });
}

export function updateMerchantApi(
  merchantId: number,
  patch: { isRoleCrudConfigurationEnabled?: boolean; isRoleOptionConfigurationEnabled?: boolean },
): Promise<PlatformMerchant> {
  return authorizedJsonApi(`/api/v1/platform/merchants/${merchantId}`, { method: 'PATCH', body: JSON.stringify(patch) });
}

/**
 * 幫場館新增成員——帶 userId 就是掛既有帳號（例如同一人身兼多個場館），不帶就用
 * username/displayName/password 建全新帳號。新場館剛建立時沒有任何成員，只能走「建全新帳號」
 * 這條路（場館自己的 /merchant/users 端點要求呼叫者已經是該場館 scope，新場館還沒有人能登入
 * 進去，見後端 PlatformEndpoints.CreateMerchantMembership 上的說明）。
 */
export function createMerchantMembershipApi(
  merchantId: number,
  input: { userId?: number; username?: string; displayName?: string; email?: string; password?: string; roleId: number },
): Promise<{ id: number }> {
  return authorizedJsonApi(`/api/v1/platform/merchants/${merchantId}/memberships`, {
    method: 'POST',
    body: JSON.stringify(input),
  });
}

export interface PlatformMerchantMembership {
  membershipId: number;
  userId: number;
  username: string;
  displayName: string;
  email: string | null;
  isActive: boolean;
  roleId: number | null;
  roleCode: string | null;
  roleName: string | null;
  lastLoginAt: string | null;
  lockedUntil: string | null;
}

export function listMerchantMembershipsApi(merchantId: number): Promise<PlatformMerchantMembership[]> {
  return authorizedJsonApi(`/api/v1/platform/merchants/${merchantId}/memberships`);
}

export interface AssignableMerchantRole {
  id: number;
  code: string;
  name: string;
  isSystem: boolean;
}

/** 這個場館可指派的角色（系統範本 merchant-admin/editor/viewer ＋該場館自訂角色）。 */
export function listMerchantAssignableRolesApi(merchantId: number): Promise<AssignableMerchantRole[]> {
  return authorizedJsonApi(`/api/v1/platform/merchants/${merchantId}/roles`);
}

export function resetMembershipPasswordApi(merchantId: number, membershipId: number): Promise<{ temporaryPassword: string }> {
  return authorizedJsonApi(`/api/v1/platform/merchants/${merchantId}/memberships/${membershipId}/reset-password`, {
    method: 'POST',
  });
}

export interface PlatformSystemUser {
  id: number;
  username: string;
  displayName: string;
  email: string | null;
  isActive: boolean;
  systemRoleId: number | null;
  roleName: string | null;
  isPlatformAdmin: boolean;
  lastLoginAt: string | null;
  lockedUntil: string | null;
  createdAt: string;
}

export function listSystemUsersApi(): Promise<PlatformSystemUser[]> {
  return authorizedJsonApi('/api/v1/platform/system-users');
}

export function createSystemUserApi(input: {
  username: string;
  displayName: string;
  password: string;
  systemRoleId: number;
}): Promise<{ id: number }> {
  return authorizedJsonApi('/api/v1/platform/system-users', { method: 'POST', body: JSON.stringify(input) });
}

export interface PlatformRoleOption {
  id: number;
  code: string;
  name: string;
  scope: number;
  merchantId: number | null;
  isSystem: boolean;
  isFullAccess: boolean;
}

/** 只回傳平台範圍角色（platform-admin/platform-operator 這類），見後端 ListRoles 的註解。 */
export function listPlatformRolesApi(): Promise<PlatformRoleOption[]> {
  return authorizedJsonApi('/api/v1/platform/roles');
}

export function createPlatformRoleApi(code: string, name: string): Promise<{ id: number }> {
  return authorizedJsonApi('/api/v1/platform/roles', {
    method: 'POST',
    body: JSON.stringify({ code, name, scope: 0, merchantId: null }),
  });
}

export interface RolePermissionDetail {
  permissionCode: string;
  perCreate: boolean;
  perRead: boolean;
  perUpdate: boolean;
  perDelete: boolean;
  /** 原始 JSON 字串（子功能鍵值陣列），例如 `["reset_password"]`。 */
  optionsJson: string;
}

export function getPlatformRolePermissionsApi(roleId: number): Promise<RolePermissionDetail[]> {
  return authorizedJsonApi(`/api/v1/platform/roles/${roleId}/permissions`);
}

export interface SetRolePermissionItem {
  permissionCode: string;
  perCreate: boolean;
  perRead: boolean;
  perUpdate: boolean;
  perDelete: boolean;
  options: string[];
}

export function setPlatformRolePermissionsApi(roleId: number, items: SetRolePermissionItem[]): Promise<void> {
  return authorizedJsonApi(`/api/v1/platform/roles/${roleId}/permissions`, { method: 'POST', body: JSON.stringify(items) });
}

export interface PermissionCatalogItem {
  id: number;
  code: string;
  name: string;
  description: string;
  subFeaturesJson: string;
}

/** 平台範圍的權限目錄（畫勾選格用），不是某個角色的授予狀態。 */
export function listPlatformPermissionCatalogApi(): Promise<PermissionCatalogItem[]> {
  return authorizedJsonApi('/api/v1/platform/permissions');
}

export function parseSubFeatures(json: string): { key: string; name: string }[] {
  try {
    const parsed = JSON.parse(json);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

/**
 * 平台頁面共用的 scope 守衛：只是 UX 層的提前判斷，真正的防線一律是後端 JWT 的
 * platform.* 權限檢查（見 CLAUDE.md 原則 2）——這裡擋掉的目的是避免場館帳號手動打
 * /platform/* 網址時，看到一堆打 API 全部 403 的破碎畫面，直接引導回他熟悉的後台首頁。
 */
export function isPlatformScopeApi(): boolean {
  return getSessionApi()?.user.scopeKind === 'platform';
}
