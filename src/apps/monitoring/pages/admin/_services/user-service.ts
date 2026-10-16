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

/**
 * 場館成員列表顯示資料列介面
 */
export interface MerchantUserRow {
  /** 場館成員關係 ID (membershipId) */
  membershipId: number;
  /** 使用者主鍵 ID (userId) */
  userId: number;
  /** 登入帳號 */
  username: string;
  /** 顯示姓名 */
  displayName: string;
  /** 電子郵件信箱 */
  email: string | null;
  /** 帳號是否處於啟用狀態 */
  isActive: boolean;
  /** 場館擁有者（第一位管理員）：不能被刪除、停用、改角色。 */
  isOwner: boolean;
  /** 綁定之角色 ID */
  roleId: number | null;
  /** 角色代碼 */
  roleCode: string | null;
  /** 角色顯示名稱 */
  roleName: string | null;
  /** 最後登入時間戳記 */
  lastLoginAt: string | null;
  /** 是否連續密碼錯誤而觸發鎖定 */
  isLocked: boolean;
}

/**
 * 場館可指派之角色選項介面
 */
export interface MerchantRoleOption {
  /** 角色主鍵 ID */
  id: number;
  /** 角色代碼 */
  code: string;
  /** 角色名稱 */
  name: string;
  /** 角色範圍 (1: Platform, 2: Merchant) */
  scope: number;
  /** 所屬場館 ID（若為系統範本則為 null） */
  merchantId: number | null;
  /** 是否為系統內建角色範本 */
  isSystem: boolean;
  /** 是否為全權限角色（例如場館管理員） */
  isFullAccess: boolean;
}

/**
 * 新增場館成員表單輸入資料介面
 */
export interface CreateMerchantUserInput {
  /** 登入帳號名稱 */
  username: string;
  /** 顯示名稱 / 姓名 */
  displayName: string;
  /** 電子郵件信箱 */
  email: string;
  /** 初始密碼 */
  password: string;
  /** 可使用的功能代碼；全部勾選（見 MEMBER_FEATURE_OPTIONS）後端就當成場館管理員。 */
  features: string[];
}

/** 取得目前場館的成員清單（含角色名稱、啟用狀態、最後登入時間）。 */
export function listMerchantUsersApi(): Promise<MerchantUserRow[]> {
  return authorizedJsonApi('/api/v1/merchant/users');
}

/** 取得目前場館可指派的角色（系統內建範本 + 該場館自訂角色）。 */
export function listMerchantRolesApi(): Promise<MerchantRoleOption[]> {
  return authorizedJsonApi('/api/v1/merchant/roles');
}

/**
 * 新增場館成員。帳號已存在時後端只會新增 membership，email/password 會被忽略。
 * @param input 建立成員表單資料
 * @returns 建立成功之 membership ID
 */
export function createMerchantUserApi(input: CreateMerchantUserInput): Promise<{ id: number }> {
  return authorizedJsonApi('/api/v1/merchant/users', {
    method: 'POST',
    body: JSON.stringify(input),
  });
}

/**
 * 更新成員的角色指派、啟用狀態、顯示姓名；欄位都可以只傳其中幾個。
 * @param membershipId 目標成員關係 ID
 * @param patch 欲調整之欄位物件
 */
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
 * @param membershipId 目標成員關係 ID
 * @returns 包含臨時明文密碼之物件
 */
export function resetMerchantUserPasswordApi(membershipId: number): Promise<{ temporaryPassword: string }> {
  return authorizedJsonApi(`/api/v1/merchant/users/${membershipId}/reset-password`, { method: 'POST' });
}

/**
 * 刪除的是這個人在本場館的成員資格，不是整個帳號——後端會擋刪自己、擋刪掉最後一個在職的
 * 場館管理員，失敗時回傳的訊息可以直接顯示給使用者看。
 * @param membershipId 目標成員關係 ID
 */
export function deleteMerchantUserApi(membershipId: number): Promise<void> {
  return authorizedJsonApi(`/api/v1/merchant/users/${membershipId}`, { method: 'DELETE' });
}

/**
 * 新增／編輯使用者畫面上的功能核取方塊，順序即畫面顯示順序，跟後端 MerchantEndpoints.MemberFeatures 一致。
 * 勾選＝可以使用該功能的全部操作（後端「CRUD 簡化模式」下 read 會展開成完整 CRUD），所以只有開／關；
 * 全部勾選就是場館管理員，不需要另外選角色。場館角色管理（merchant.roles）不在清單內，只有管理員擁有。
 */
export const MEMBER_FEATURE_OPTIONS = [
  { code: 'hvac.overview', name: '監控中心' },
  { code: 'hvac.floor_plan', name: '空間設備配置' },
  { code: 'hvac.chillers', name: '冰水主機管理' },
  { code: 'hvac.fcus', name: 'FCU管理' },
  { code: 'hvac.alarms', name: '告警' },
  { code: 'hvac.thresholds', name: '告警門檻' },
  { code: 'hvac.reports', name: '報表統計匯出' },
  { code: 'merchant.users', name: '使用者管理' },
  { code: 'merchant.operation_log', name: '操作紀錄' },
] as const;

/**
 * 目前這個成員實際可使用哪些功能（讀他現有角色的實際授予；場館管理員視為全部）。
 * @param membershipId 目標成員關係 ID
 * @returns 啟用的功能代碼字串陣列
 */
export function getMemberFeaturesApi(membershipId: number): Promise<{ features: string[] }> {
  return authorizedJsonApi(`/api/v1/merchant/users/${membershipId}/features`);
}

/**
 * 儲存功能清單。全部勾選會指到「場館管理員」系統角色，否則後端幫這個人準備專屬角色（第一次才建立）；
 * 不會動到 merchant-admin／editor／viewer 這三個跨場館共用的系統範本。
 * 升級／降級管理員會經過後端的管理員保護（擁有者不能被降級、不能降級最後一位管理員），失敗訊息可直接顯示。
 * @param membershipId 目標成員關係 ID
 * @param features 勾選之功能代碼陣列
 */
export function setMemberFeaturesApi(membershipId: number, features: string[]): Promise<void> {
  return authorizedJsonApi(`/api/v1/merchant/users/${membershipId}/features`, {
    method: 'PUT',
    body: JSON.stringify({ features }),
  });
}

