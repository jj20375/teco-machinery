-- 平台「系統診斷」頁（/platform/diagnostics）：現場 IoT 接通驗證用，只開放給平台範圍。
-- 全部唯讀，所以只授予 read；platform-admin 預設取得，其他平台角色要在角色管理頁自己勾。
-- 這支檔案只在「全新初始化」的資料庫會被 docker-entrypoint-initdb.d 執行到；
-- 已經在跑的資料庫要另外手動執行（見 docs/IOT_現場接通驗證手冊.md）。
-- 授權後已登入的平台帳號要重新登入（或等 token 更新）才會拿到新權限。
USE teco_hvac;

INSERT IGNORE INTO app_permission (code, name, scope, route_path, is_menu, sub_features_json) VALUES
    ('platform.diagnostics', '系統診斷', 0, '/platform/diagnostics', 1, JSON_ARRAY());

INSERT INTO app_role_permission (role_id, permission_id, per_create, per_read, per_update, per_delete, options_json)
SELECT r.id, p.id, 0, 1, 0, 0, JSON_ARRAY()
FROM app_role r, app_permission p
WHERE r.code = 'platform-admin' AND r.merchant_id IS NULL AND p.code = 'platform.diagnostics'
ON DUPLICATE KEY UPDATE per_read = 1;
