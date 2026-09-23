-- 補 app_user.email（使用者管理頁需要顯示/填寫信箱）。
-- 這支檔案只在「全新初始化」的資料庫會被 docker-entrypoint-initdb.d 執行到；
-- 已經在跑的資料庫要另外手動 ALTER TABLE（見 backend/README.md 操作紀錄或直接照下面這行執行）。
USE teco_hvac;

SET @col_exists = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = 'teco_hvac' AND TABLE_NAME = 'app_user' AND COLUMN_NAME = 'email'
);
SET @sql = IF(@col_exists = 0,
    'ALTER TABLE app_user ADD COLUMN email VARCHAR(255) NULL AFTER display_name',
    'SELECT 1');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;
