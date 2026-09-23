-- 補 alarm_rule 的天然鍵：同一設備對同一指標的同一方向（大於/小於）只應該有一條規則，
-- 「告警門檻設定」畫面的 upsert（INSERT ... ON DUPLICATE KEY UPDATE）要靠這個唯一鍵運作。
-- 這支檔案只在「全新初始化」的資料庫會被 docker-entrypoint-initdb.d 執行到；
-- 已經在跑的資料庫要另外手動執行（見 backend/README.md）。
USE teco_hvac;

SET @idx_exists = (
    SELECT COUNT(*) FROM information_schema.STATISTICS
    WHERE TABLE_SCHEMA = 'teco_hvac' AND TABLE_NAME = 'alarm_rule' AND INDEX_NAME = 'uk_alarm_rule'
);
SET @sql = IF(@idx_exists = 0,
    'ALTER TABLE alarm_rule ADD UNIQUE KEY uk_alarm_rule (device_type, scope, metric, operator)',
    'SELECT 1');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;
