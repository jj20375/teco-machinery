/**
 * @file admin-menu.ts
 * 後台側邊欄選單定義。獨立成檔是因為「登入後該先去哪一頁」也要用同一份清單判斷（見 AdminLayout.vue），
 * 兩邊各寫一份遲早會不一致。
 */

import type { PermissionGrant } from './auth-service';

export type IconType = 'dashboard' | 'floor' | 'chiller' | 'fcu' | 'report' | 'user' | 'role' | 'log';

export interface Child {
  title: string;
  path: string;
}
export interface MenuItem {
  title: string;
  path: string;
  iconType: IconType;
  /** 對應後端權限資源代碼（見 MerchantEndpoints.MemberFeatures）；沒有這個代碼的帳號看不到這個項目，不是只擋 API 卻選單照樣顯示。 */
  permissionCode: string;
  children?: Child[];
}

export const ADMIN_MENU_ITEMS: MenuItem[] = [
  { title: '監控中心', path: '/admin', iconType: 'dashboard', permissionCode: 'hvac.overview' },
  { title: '空間設備配置', path: '/admin/floor-plan', iconType: 'floor', permissionCode: 'hvac.floor_plan' },
  { title: '冰水主機管理', path: '/admin/chiller', iconType: 'chiller', permissionCode: 'hvac.chillers' },
  { title: 'FCU管理', path: '/admin/fcu', iconType: 'fcu', permissionCode: 'hvac.fcus' },
  {
    title: '統計報表',
    path: '/admin/reports',
    iconType: 'report',
    permissionCode: 'hvac.reports',
    children: [
      { title: '冰水主機運轉報表', path: '/admin/reports/chiller' },
      { title: 'FCU運轉報表', path: '/admin/reports/fcu' },
      { title: '異常告警報表', path: '/admin/reports/alarms' },
    ],
  },
  { title: '使用者管理', path: '/admin/users', iconType: 'user', permissionCode: 'merchant.users' },
  // 角色管理先隱藏：新增／編輯使用者改成直接勾選功能（全部勾選＝場館管理員），不再需要選角色。
  // 頁面（/admin/roles）與後端都保留，之後要恢復只要取消下面這行的註解。
  // { title: '角色管理', path: '/admin/roles', iconType: 'role', permissionCode: 'merchant.roles' },
  { title: '操作紀錄', path: '/admin/operation-log', iconType: 'log', permissionCode: 'merchant.operation_log' },
];

function canRead(grants: readonly PermissionGrant[], code: string): boolean {
  return grants.some((g) => g.code === code && g.actions.includes('read'));
}

/** 這個帳號第一個有權限進入的選單頁面；一個都沒有回傳 null。統計報表（有子項）取第一個子頁。 */
export function firstAccessibleAdminPath(grants: readonly PermissionGrant[]): string | null {
  const item = ADMIN_MENU_ITEMS.find((i) => canRead(grants, i.permissionCode));
  if (!item) return null;
  return item.children?.[0]?.path ?? item.path;
}

export function canReadAdminFeature(grants: readonly PermissionGrant[], code: string): boolean {
  return canRead(grants, code);
}
