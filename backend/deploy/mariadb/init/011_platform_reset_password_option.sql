-- 平台管理員可以重設任一場館成員的密碼（先前只有場館管理員能重設自己場館成員的密碼）。
-- 沿用 merchant.users 的 reset_password 子功能命名慣例，掛在 platform.merchants 底下——
-- 「重設場館成員密碼」邏輯上屬於場館管理的一部分，不是另開一個新資源。
-- 這支檔案只在「全新初始化」的資料庫會被 docker-entrypoint-initdb.d 執行到；
-- 已經在跑的資料庫要另外手動執行（見 backend/README.md）。
USE teco_hvac;

UPDATE app_permission
SET sub_features_json = JSON_ARRAY(JSON_OBJECT('key', 'reset_password', 'name', '重設場館成員密碼'))
WHERE code = 'platform.merchants' AND JSON_LENGTH(sub_features_json) = 0;

UPDATE app_role_permission rp
JOIN app_role r ON r.id = rp.role_id
JOIN app_permission p ON p.id = rp.permission_id
SET rp.options_json = JSON_ARRAY('reset_password')
WHERE r.code = 'platform-admin' AND p.code = 'platform.merchants' AND JSON_LENGTH(rp.options_json) = 0;
