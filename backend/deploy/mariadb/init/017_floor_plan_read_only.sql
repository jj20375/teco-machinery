-- 「空間設備配置」可以設成「僅檢視」（2026-10-19）。
-- 場館的 CRUD 簡化模式原本是「有授予就展開成完整 CRUD」，hvac.floor_plan 改成例外：原始授予沒有 per_update 時只給讀取
-- （見 MerchantRolePermissionConfigurationRules.ReadOnlyCapableResources）。
--
-- 這會改變既有自訂角色的行為：勾選功能時系統只寫 per_read（例如 member-{id} 個人專屬角色），以前靠展開變成可編輯，
-- 規則改了之後會變唯讀。所以把「簡化模式場館」裡的自訂角色補上完整 CRUD，維持原本可編輯的狀態。
-- 細項模式（is_role_crud_configuration_enabled = 1）的場館本來就照實際勾選生效，不動。
-- 系統範本（merchant_id IS NULL）不動：editor 本來就有 per_update；viewer 只有讀取，規則改了之後就是唯讀，符合「檢視者」的意思。
--
-- 重複執行是安全的（只動 per_update = 0 的列）。
USE teco_hvac;

UPDATE app_role_permission rp
JOIN app_role r ON r.id = rp.role_id
JOIN app_permission p ON p.id = rp.permission_id
JOIN merchant m ON m.id = r.merchant_id
SET rp.per_create = 1, rp.per_update = 1, rp.per_delete = 1
WHERE p.code = 'hvac.floor_plan'
  AND rp.per_read = 1
  AND rp.per_update = 0
  AND r.is_system = 0
  AND r.is_full_access = 0
  AND m.is_role_crud_configuration_enabled = 0;
