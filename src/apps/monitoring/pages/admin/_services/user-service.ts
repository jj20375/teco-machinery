/**
 * @file user-service.ts
 * 東元電機智慧環境監控 - 場館使用者與角色管理服務
 * 嚴格遵守專案規範：所有對外呼叫函式一律以 *Api 結尾
 *
 * 打的是 backend/ 的 /api/v1/merchant/* 端點（見 backend/src/Teco.Hvac.Api/Endpoints/MerchantEndpoints.cs）。
 * 這條路徑只有 merchant scope 的帳號能用，且要有 merchant.users／merchant.roles 權限——
 * 平台管理員帳號（scopeKind=platform）打這裡一律 403，這是預期行為，不是 bug。
 */

import { authorizedJsonApi } from './auth-service';

export interface MerchantUserRow {
  membershipId: number;
  userId: number;
  username: string;
  displayName: string;
  email: string | null;
  isActive: boolean;
  /** 場館擁有者（第一位管理員）：不能被刪除、停用、改角色。 */
  isOwner: boolean;
  roleId: number | null;
  roleCode: string | null;
  roleName: string | null;
  lastLoginAt: string | null;
  isLocked: boolean;
}

export interface MerchantRoleOption {
  id: number;
  code: string;
  name: string;
  scope: number;
  merchantId: number | null;
  isSystem: boolean;
  isFullAccess: boolean;
}

export interface CreateMerchantUserInput {
  username: string;
  displayName: string;
  email: string;
  password: string;
  roleId: number;
}

/** 取得目前場館的成員清單（含角色名稱、啟用狀態、最後登入時間）。 */
export function listMerchantUsersApi(): Promise<MerchantUserRow[]> {
  return authorizedJsonApi('/api/v1/merchant/users');
}

/** 取得目前場館可指派的角色（系統內建範本 + 該場館自訂角色）。 */
export function listMerchantRolesApi(): Promise<MerchantRoleOption[]> {
  return authorizedJsonApi('/api/v1/merchant/roles');
}

/** 新增場館成員。帳號已存在時後端只會新增 membership，email/password 會被忽略。 */
export function createMerchantUserApi(input: CreateMerchantUserInput): Promise<{ id: number }> {
  return authorizedJsonApi('/api/v1/merchant/users', {
    method: 'POST',
    body: JSON.stringify(input),
  });
}

/** 更新成員的角色指派、啟用狀態、顯示姓名；欄位都可以只傳其中幾個。 */
export function updateMerchantUserApi(
  membershipId: number,
  patch: { roleId?: number; isActive?: boolean; displayName?: string },
): Promise<void> {
  return authorizedJsonApi(`/api/v1/merchant/users/${membershipId}`, {
    method: 'PATCH',
    body: JSON.stringify(patch),
  });
}

/**
 * 重設成員密碼：後端產生一組隨機臨時密碼並直接回傳（只有這一次看得到明碼），
 * 同時解除鎖定、讓該帳號的舊 token 立即失效。
 */
export function resetMerchantUserPasswordApi(membershipId: number): Promise<{ temporaryPassword: string }> {
  return authorizedJsonApi(`/api/v1/merchant/users/${membershipId}/reset-password`, { method: 'POST' });
}

/**
 * 刪除的是這個人在本場館的成員資格，不是整個帳號——後端會擋刪自己、擋刪掉最後一個在職的
 * 場館管理員，失敗時回傳的訊息可以直接顯示給使用者看。
 */
export function deleteMerchantUserApi(membershipId: number): Promise<void> {
  return authorizedJsonApi(`/api/v1/merchant/users/${membershipId}`, { method: 'DELETE' });
}

/**
 * 「編輯成員」的頁面開關，對應設計稿的核取方塊；順序即畫面顯示順序。
 * 設計稿原本只有六項，新增「空間設備配置」後變七項，跟側邊欄的七個項目 1:1 對應——
 * 少了這一項，用個人權限的使用者永遠拿不到空間設備配置權限（後端 MemberFeatureCodes 同步）。
 */
export const MEMBER_FEATURE_OPTIONS = [
  { code: 'hvac.overview', name: '監控中心' },
  { code: 'hvac.floor_plan', name: '空間設備配置' },
  { code: 'hvac.chillers', name: '冰水主機管理' },
  { code: 'hvac.fcus', name: 'FCU管理' },
  { code: 'hvac.reports', name: '報表統計匯出' },
  { code: 'merchant.users', name: '使用者管理' },
  { code: 'merchant.operation_log', name: '操作紀錄' },
] as const;

/** 目前這個成員的角色實際勾選了哪些頁面開關（讀角色的實際授予，系統範本或個人專屬角色都適用）。 */
export function getMemberFeaturesApi(membershipId: number): Promise<{ features: string[] }> {
  return authorizedJsonApi(`/api/v1/merchant/users/${membershipId}/features`);
}

/**
 * 儲存「編輯成員」的頁面開關。後端會幫這個人準備一個專屬角色（第一次呼叫才建立，之後沿用），
 * 不會動到 merchant-admin／editor／viewer 這三個跨場館共用的系統範本。
 */
export function setMemberFeaturesApi(membershipId: number, features: string[]): Promise<void> {
  return authorizedJsonApi(`/api/v1/merchant/users/${membershipId}/features`, {
    method: 'PUT',
    body: JSON.stringify({ features }),
  });
}
