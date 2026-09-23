-- rollup_chiller_1h 原本只有出水溫度（min_chilled_out/max_chilled_out），沒有回水溫度跟溫度差，
-- 但冰水主機運轉報表要同時顯示供水/回水溫度與溫度差——補上 min_chilled_in/max_chilled_in/
-- avg_chilled_delta，資料來源是 chiller_reading 的 chilled_water_in/chilled_water_delta
-- （供應商真的有給、直接算好的欄位，不是虛構數字或前端自己相減）。
-- 這支檔案只在「全新初始化」的資料庫會被 docker-entrypoint-initdb.d 執行到；
-- 已經在跑的資料庫要另外手動執行（見 backend/README.md）。
-- 三個欄位各自檢查是否存在才加，不綁在同一個判斷式——避免像這次一樣，資料庫已經手動加過
-- 其中一兩個欄位時，剩下的欄位因為共用同一個 IF 判斷式而被跳過。
USE teco_hvac;

SET @col_exists = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = 'teco_hvac' AND TABLE_NAME = 'rollup_chiller_1h' AND COLUMN_NAME = 'min_chilled_in'
);
SET @sql = IF(@col_exists = 0,
    'ALTER TABLE rollup_chiller_1h ADD COLUMN min_chilled_in DECIMAL(5,1) NULL AFTER max_chilled_out',
    'SELECT 1');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @col_exists = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = 'teco_hvac' AND TABLE_NAME = 'rollup_chiller_1h' AND COLUMN_NAME = 'max_chilled_in'
);
SET @sql = IF(@col_exists = 0,
    'ALTER TABLE rollup_chiller_1h ADD COLUMN max_chilled_in DECIMAL(5,1) NULL AFTER min_chilled_in',
    'SELECT 1');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @col_exists = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = 'teco_hvac' AND TABLE_NAME = 'rollup_chiller_1h' AND COLUMN_NAME = 'avg_chilled_delta'
);
SET @sql = IF(@col_exists = 0,
    'ALTER TABLE rollup_chiller_1h ADD COLUMN avg_chilled_delta DECIMAL(5,1) NULL AFTER max_chilled_in',
    'SELECT 1');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

-- 冰水主機運轉報表要顯示「累積運轉時數」，這是單調遞增的計數器（跟 accumulated_kwh 一樣），
-- 該小時的代表值取 MAX（等於該小時最後一筆讀值），不是平均或加總。
SET @col_exists = (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = 'teco_hvac' AND TABLE_NAME = 'rollup_chiller_1h' AND COLUMN_NAME = 'running_hours'
);
SET @sql = IF(@col_exists = 0,
    'ALTER TABLE rollup_chiller_1h ADD COLUMN running_hours INT NULL AFTER kwh_delta',
    'SELECT 1');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;
