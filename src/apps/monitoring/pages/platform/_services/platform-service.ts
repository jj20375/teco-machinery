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

/**
 * 平台場館（租戶）基本資料結構
 */
export interface PlatformMerchant {
  /** 場館流水號 ID */
  id: number;
  /** 場館代碼（例如 teco-hq） */
  code: string;
  /** 場館名稱 */
  name: string;
  /** 營運狀態（例如 active, suspended） */
  status: string;
  /** 是否允許該場館自訂角色（CRUD） */
  isRoleCrudConfigurationEnabled: boolean;
  /** 是否允許該場館配置角色細部子功能（Options） */
  isRoleOptionConfigurationEnabled: boolean;
  /** 建立時間（ISO 字串） */
  createdAt: string;
  /** 最後更新時間（ISO 字串） */
  updatedAt: string;
}

/**
 * 取得所有平台場館（租戶）清單
 *
 * @returns 傳回平台場館陣列
 */
export function listMerchantsApi(): Promise<PlatformMerchant[]> {
  return authorizedJsonApi('/api/v1/platform/merchants');
}

/**
 * 建立新場館（租戶）
 *
 * @param input 建立場館輸入參數
 * @param input.code 場館代碼
 * @param input.name 場館名稱
 * @param input.isRoleCrudConfigurationEnabled 是否開啟角色 CRUD 配置權限
 * @param input.isRoleOptionConfigurationEnabled 是否開啟角色子功能配置權限
 * @returns 傳回新建立場館的 ID 物件
 */
export function createMerchantApi(input: {
  code: string;
  name: string;
  isRoleCrudConfigurationEnabled?: boolean;
  isRoleOptionConfigurationEnabled?: boolean;
}): Promise<{ id: number }> {
  return authorizedJsonApi('/api/v1/platform/merchants', { method: 'POST', body: JSON.stringify(input) });
}

/**
 * 更新場館配置旗標
 *
 * @param merchantId 目標場館 ID
 * @param patch 欲修改的配置旗標（局部更新）
 * @param patch.isRoleCrudConfigurationEnabled 是否開啟角色 CRUD 配置權限
 * @param patch.isRoleOptionConfigurationEnabled 是否開啟角色子功能配置權限
 * @returns 傳回更新後的場館資訊
 */
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
 *
 * @param merchantId 目標場館 ID
 * @param input 成員建立或指派參數
 * @param input.userId 既有帳號的使用者 ID（若為掛載既有帳號則提供）
 * @param input.username 全新帳號的登入使用者名稱
 * @param input.displayName 全新帳號的使用者顯示名稱
 * @param input.email 全新帳號的電子郵件
 * @param input.password 全新帳號的初始密碼
 * @param input.roleId 指派的角色 ID
 * @returns 傳回新建立的 membership ID 物件
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

/**
 * 平台檢視下的場館成員資料結構
 */
export interface PlatformMerchantMembership {
  /** 場館成員關聯 ID (membershipId) */
  membershipId: number;
  /** 使用者帳號 ID */
  userId: number;
  /** 使用者登入帳號 */
  username: string;
  /** 使用者顯示姓名 */
  displayName: string;
  /** 電子郵件信箱 */
  email: string | null;
  /** 帳號是否處於啟用狀態 */
  isActive: boolean;
  /** 指派角色 ID */
  roleId: number | null;
  /** 指派角色代碼 */
  roleCode: string | null;
  /** 指派角色顯示名稱 */
  roleName: string | null;
  /** 最後登入時間（ISO 字串） */
  lastLoginAt: string | null;
  /** 帳號鎖定截止時間（ISO 字串，null 表示未鎖定） */
  lockedUntil: string | null;
}

/**
 * 取得指定場館的所有成員清單
 *
 * @param merchantId 目標場館 ID
 * @returns 傳回場館成員陣列
 */
export function listMerchantMembershipsApi(merchantId: number): Promise<PlatformMerchantMembership[]> {
  return authorizedJsonApi(`/api/v1/platform/merchants/${merchantId}/memberships`);
}

/**
 * 可指派給場館成員的角色選項
 */
export interface AssignableMerchantRole {
  /** 角色 ID */
  id: number;
  /** 角色代碼 */
  code: string;
  /** 角色名稱 */
  name: string;
  /** 是否為系統內建預設角色（不可刪除） */
  isSystem: boolean;
}

/**
 * 取得指定場館可指派的角色清單
 *
 * 這個場館可指派的角色（系統範本 merchant-admin/editor/viewer ＋該場館自訂角色）。
 *
 * @param merchantId 目標場館 ID
 * @returns 傳回可指派的角色陣列
 */
export function listMerchantAssignableRolesApi(merchantId: number): Promise<AssignableMerchantRole[]> {
  return authorizedJsonApi(`/api/v1/platform/merchants/${merchantId}/roles`);
}

/**
 * 平台管理員重設指定場館成員密碼
 *
 * @param merchantId 目標場館 ID
 * @param membershipId 目標場館成員關聯 ID
 * @returns 傳回系統自動產生的一次性暫時密碼物件
 */
export function resetMembershipPasswordApi(merchantId: number, membershipId: number): Promise<{ temporaryPassword: string }> {
  return authorizedJsonApi(`/api/v1/platform/merchants/${merchantId}/memberships/${membershipId}/reset-password`, {
    method: 'POST',
  });
}

/**
 * 平台系統使用者資料結構
 */
export interface PlatformSystemUser {
  /** 使用者流水號 ID */
  id: number;
  /** 登入帳號名稱 */
  username: string;
  /** 顯示姓名 */
  displayName: string;
  /** 電子郵件信箱 */
  email: string | null;
  /** 帳號是否啟用 */
  isActive: boolean;
  /** 系統管理角色 ID */
  systemRoleId: number | null;
  /** 系統管理角色名稱 */
  roleName: string | null;
  /** 是否具備最高平台超級管理員權限 */
  isPlatformAdmin: boolean;
  /** 最後登入時間（ISO 字串） */
  lastLoginAt: string | null;
  /** 帳號鎖定截止時間（ISO 字串） */
  lockedUntil: string | null;
  /** 帳號建立時間（ISO 字串） */
  createdAt: string;
}

/**
 * 取得平台系統管理者帳號清單
 *
 * @returns 傳回平台系統使用者陣列
 */
export function listSystemUsersApi(): Promise<PlatformSystemUser[]> {
  return authorizedJsonApi('/api/v1/platform/system-users');
}

/**
 * 建立全新平台系統管理者帳號
 *
 * @param input 建立系統使用者輸入參數
 * @param input.username 帳號名稱
 * @param input.displayName 顯示名稱
 * @param input.password 初始密碼
 * @param input.systemRoleId 指派的平台系統角色 ID
 * @returns 傳回新建立使用者的 ID 物件
 */
export function createSystemUserApi(input: {
  username: string;
  displayName: string;
  password: string;
  systemRoleId: number;
}): Promise<{ id: number }> {
  return authorizedJsonApi('/api/v1/platform/system-users', { method: 'POST', body: JSON.stringify(input) });
}

/**
 * 平台角色選項資料結構
 */
export interface PlatformRoleOption {
  /** 角色 ID */
  id: number;
  /** 角色代碼 */
  code: string;
  /** 角色顯示名稱 */
  name: string;
  /** 角色範圍（0=Platform, 1=Merchant） */
  scope: number;
  /** 所屬場館 ID（平台角色為 null） */
  merchantId: number | null;
  /** 是否為系統內建角色 */
  isSystem: boolean;
  /** 是否具備全功能存取權限 */
  isFullAccess: boolean;
}

/**
 * 取得平台範圍角色清單
 *
 * 只回傳平台範圍角色（platform-admin/platform-operator 這類），見後端 ListRoles 的註解。
 *
 * @returns 傳回平台角色陣列
 */
export function listPlatformRolesApi(): Promise<PlatformRoleOption[]> {
  return authorizedJsonApi('/api/v1/platform/roles');
}

/**
 * 建立平台自訂角色
 *
 * @param code 角色唯一代碼
 * @param name 角色顯示名稱
 * @returns 傳回新建立角色的 ID 物件
 */
export function createPlatformRoleApi(code: string, name: string): Promise<{ id: number }> {
  return authorizedJsonApi('/api/v1/platform/roles', {
    method: 'POST',
    body: JSON.stringify({ code, name, scope: 0, merchantId: null }),
  });
}

/**
 * 角色功能權限明細結構
 */
export interface RolePermissionDetail {
  /** 功能權限代碼（例如 platform.merchants） */
  permissionCode: string;
  /** 是否允許新增（Create） */
  perCreate: boolean;
  /** 是否允許檢視（Read） */
  perRead: boolean;
  /** 是否允許修改（Update） */
  perUpdate: boolean;
  /** 是否允許刪除（Delete） */
  perDelete: boolean;
  /** 原始 JSON 字串（子功能鍵值陣列），例如 `["reset_password"]` */
  optionsJson: string;
}

/**
 * 取得指定平台角色的功能權限配置
 *
 * @param roleId 平台角色 ID
 * @returns 傳回角色的權限項目陣列
 */
export function getPlatformRolePermissionsApi(roleId: number): Promise<RolePermissionDetail[]> {
  return authorizedJsonApi(`/api/v1/platform/roles/${roleId}/permissions`);
}

/**
 * 儲存角色權限輸入項目結構
 */
export interface SetRolePermissionItem {
  /** 功能權限代碼 */
  permissionCode: string;
  /** 是否允許新增 */
  perCreate: boolean;
  /** 是否允許檢視 */
  perRead: boolean;
  /** 是否允許修改 */
  perUpdate: boolean;
  /** 是否允許刪除 */
  perDelete: boolean;
  /** 勾選的子功能鍵值清單（例如 `["reset_password"]`） */
  options: string[];
}

/**
 * 批次儲存指定平台角色的功能權限配置
 *
 * @param roleId 平台角色 ID
 * @param items 權限項目清單
 * @returns Promise<void>
 */
export function setPlatformRolePermissionsApi(roleId: number, items: SetRolePermissionItem[]): Promise<void> {
  return authorizedJsonApi(`/api/v1/platform/roles/${roleId}/permissions`, { method: 'POST', body: JSON.stringify(items) });
}

/**
 * 權限目錄項目（定義系統所有可用模組與子功能）
 */
export interface PermissionCatalogItem {
  /** 權限目錄項 ID */
  id: number;
  /** 權限項目代碼（如 platform.merchants） */
  code: string;
  /** 權限項目顯示名稱 */
  name: string;
  /** 權限詳細說明 */
  description: string;
  /** 可選子功能清單之 JSON 字串（結構為 `[{ key, name }]`） */
  subFeaturesJson: string;
}

/**
 * 取得平台範圍的權限目錄清單
 *
 * 平台範圍的權限目錄（畫勾選格用），不是某個角色的授予狀態。
 *
 * @returns 傳回平台權限目錄項目陣列
 */
export function listPlatformPermissionCatalogApi(): Promise<PermissionCatalogItem[]> {
  return authorizedJsonApi('/api/v1/platform/permissions');
}

/**
 * 解析子功能 JSON 字串為物件陣列
 *
 * @param json 子功能 JSON 格式字串
 * @returns 解析成功傳回 `{ key: string; name: string }[]`，失敗傳回空陣列 `[]`
 */
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
 *
 * @returns 若當前登入者為平台管理者 scope 則傳回 true，否則傳回 false
 */
export function isPlatformScopeApi(): boolean {
  return getSessionApi()?.user.scopeKind === 'platform';
}
