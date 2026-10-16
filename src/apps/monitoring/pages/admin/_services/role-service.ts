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

/**
 * 權限目錄項目介面（定義可授權的資源與其子功能選項）
 */
export interface PermissionCatalogItem {
  /** 權限項目 ID */
  id: number;
  /** 資源代碼（例如 hvac.chillers） */
  code: string;
  /** 資源名稱（例如 冰水主機管理） */
  name: string;
  /** 資源詳細描述 */
  description: string;
  /** 原始 JSON 字串，例如 `[{"key":"ack","name":"確認告警"}]`，要自己 JSON.parse。 */
  subFeaturesJson: string;
}

/**
 * 權限子功能選項定義介面
 */
export interface SubFeatureOption {
  /** 子功能代碼鍵值（例如 'ack'） */
  key: string;
  /** 子功能中文名稱（例如 '確認告警'） */
  name: string;
}

/**
 * 把後端回傳的 subFeaturesJson 解析成陣列；格式壞掉就當作沒有子功能，不擋畫面。
 * @param json 子功能 JSON 字串
 * @returns 解析出之 SubFeatureOption 陣列
 */
export function parseSubFeatures(json: string): SubFeatureOption[] {
  try {
    const parsed = JSON.parse(json);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

/**
 * 角色細項權限設定內容介面
 */
export interface RolePermissionDetail {
  /** 資源代碼 */
  permissionCode: string;
  /** 是否允許新增 (Create) */
  perCreate: boolean;
  /** 是否允許檢視 (Read) */
  perRead: boolean;
  /** 是否允許編輯 (Update) */
  perUpdate: boolean;
  /** 是否允許刪除 (Delete) */
  perDelete: boolean;
  /** 原始 JSON 字串（子功能鍵值陣列），例如 `["ack"]`。 */
  optionsJson: string;
}

/** 取得場館目前所有角色清單 */
export function listMerchantRolesApi(): Promise<MerchantRoleOption[]> {
  return authorizedJsonApi('/api/v1/merchant/roles');
}

/**
 * 角色代碼可用性檢查結果介面
 */
export interface RoleCodeAvailability {
  /** 該代碼是否可用 */
  available: boolean;
  /** 不能用的原因（已經存在、系統保留等），可直接顯示給使用者。 */
  message: string | null;
}

/**
 * 新增角色前即時檢查代碼是否可用；送出新增時後端會再檢查一次，這支只是讓使用者早點看到提示。
 * @param code 待檢查之角色代碼
 * @returns 可用性檢查結果 Promise
 */
export function checkRoleCodeApi(code: string): Promise<RoleCodeAvailability> {
  return authorizedJsonApi(`/api/v1/merchant/roles/code-availability?code=${encodeURIComponent(code)}`);
}

/**
 * 建立新的場館自訂角色。
 * @param code 角色唯一代碼
 * @param name 角色顯示名稱
 * @returns 建立成功之角色 ID
 */
export function createMerchantRoleApi(code: string, name: string): Promise<{ id: number }> {
  return authorizedJsonApi('/api/v1/merchant/roles', { method: 'POST', body: JSON.stringify({ code, name }) });
}

/**
 * 變更角色顯示名稱。
 * @param roleId 目標角色 ID
 * @param name 新角色名稱
 */
export function renameMerchantRoleApi(roleId: number, name: string): Promise<void> {
  return authorizedJsonApi(`/api/v1/merchant/roles/${roleId}`, { method: 'PATCH', body: JSON.stringify({ name }) });
}

/**
 * 刪除場館自訂角色（若尚有成員綁定將被後端拒絕）。
 * @param roleId 目標角色 ID
 */
export function deleteMerchantRoleApi(roleId: number): Promise<void> {
  return authorizedJsonApi(`/api/v1/merchant/roles/${roleId}`, { method: 'DELETE' });
}

/**
 * 取得指定角色的細項授權清單。
 * @param roleId 目標角色 ID
 * @returns 細項授權內容陣列 Promise
 */
export function getRolePermissionsApi(roleId: number): Promise<RolePermissionDetail[]> {
  return authorizedJsonApi(`/api/v1/merchant/roles/${roleId}/permissions`);
}

/**
 * 設定角色權限項目傳輸資料介面
 */
export interface SetRolePermissionItem {
  /** 資源代碼 */
  permissionCode: string;
  /** 建立權限開關 */
  perCreate: boolean;
  /** 讀取權限開關 */
  perRead: boolean;
  /** 編輯權限開關 */
  perUpdate: boolean;
  /** 刪除權限開關 */
  perDelete: boolean;
  /** 啟用的子功能鍵值陣列 */
  options: string[];
}

/**
 * 儲存角色的細項權限配置。
 * @param roleId 目標角色 ID
 * @param items 各資源之授權項目陣列
 */
export function setRolePermissionsApi(roleId: number, items: SetRolePermissionItem[]): Promise<void> {
  return authorizedJsonApi(`/api/v1/merchant/roles/${roleId}/permissions`, {
    method: 'POST',
    body: JSON.stringify(items),
  });
}

/**
 * 場館範圍的權限目錄（畫勾選格用），不是某個角色的授予狀態。
 * @returns 權限目錄清單 Promise
 */
export function listPermissionCatalogApi(): Promise<PermissionCatalogItem[]> {
  return authorizedJsonApi('/api/v1/merchant/permissions');
}

