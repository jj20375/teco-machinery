/**
 * @file role-service.ts
 * 東元電機智慧環境監控 - 場館角色管理服務
 * 嚴格遵守專案規範：所有對外呼叫函式一律以 *Api 結尾
 *
 * 打的是 backend/ 的 /api/v1/merchant/{roles,permissions}（見
 * backend/src/Teco.Hvac.Api/Endpoints/MerchantEndpoints.cs）。只有 merchant scope 且持有
 * merchant.roles 權限的帳號能用；系統範本角色（merchant-admin/editor/viewer）只能看，
 * 改名/刪除/調整權限一律 403（後端也會擋，這裡的唯讀是配合畫面，不是唯一防線）。
 */

import { authorizedJsonApi } from './auth-service';
import type { MerchantRoleOption } from './user-service';

export type { MerchantRoleOption };

export interface PermissionCatalogItem {
  id: number;
  code: string;
  name: string;
  description: string;
  /** 原始 JSON 字串，例如 `[{"key":"ack","name":"確認告警"}]`，要自己 JSON.parse。 */
  subFeaturesJson: string;
}

export interface SubFeatureOption {
  key: string;
  name: string;
}

/** 把後端回傳的 subFeaturesJson 解析成陣列；格式壞掉就當作沒有子功能，不擋畫面。 */
export function parseSubFeatures(json: string): SubFeatureOption[] {
  try {
    const parsed = JSON.parse(json);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

export interface RolePermissionDetail {
  permissionCode: string;
  perCreate: boolean;
  perRead: boolean;
  perUpdate: boolean;
  perDelete: boolean;
  /** 原始 JSON 字串（子功能鍵值陣列），例如 `["ack"]`。 */
  optionsJson: string;
}

export function listMerchantRolesApi(): Promise<MerchantRoleOption[]> {
  return authorizedJsonApi('/api/v1/merchant/roles');
}

export function createMerchantRoleApi(code: string, name: string): Promise<{ id: number }> {
  return authorizedJsonApi('/api/v1/merchant/roles', { method: 'POST', body: JSON.stringify({ code, name }) });
}

export function renameMerchantRoleApi(roleId: number, name: string): Promise<void> {
  return authorizedJsonApi(`/api/v1/merchant/roles/${roleId}`, { method: 'PATCH', body: JSON.stringify({ name }) });
}

export function deleteMerchantRoleApi(roleId: number): Promise<void> {
  return authorizedJsonApi(`/api/v1/merchant/roles/${roleId}`, { method: 'DELETE' });
}

export function getRolePermissionsApi(roleId: number): Promise<RolePermissionDetail[]> {
  return authorizedJsonApi(`/api/v1/merchant/roles/${roleId}/permissions`);
}

export interface SetRolePermissionItem {
  permissionCode: string;
  perCreate: boolean;
  perRead: boolean;
  perUpdate: boolean;
  perDelete: boolean;
  options: string[];
}

export function setRolePermissionsApi(roleId: number, items: SetRolePermissionItem[]): Promise<void> {
  return authorizedJsonApi(`/api/v1/merchant/roles/${roleId}/permissions`, {
    method: 'POST',
    body: JSON.stringify(items),
  });
}

/** 場館範圍的權限目錄（畫勾選格用），不是某個角色的授予狀態。 */
export function listPermissionCatalogApi(): Promise<PermissionCatalogItem[]> {
  return authorizedJsonApi('/api/v1/merchant/permissions');
}
