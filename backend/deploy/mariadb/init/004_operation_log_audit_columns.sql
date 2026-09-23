-- 補 operation_log 的稽核欄位（merchant 範圍、操作者快照、人話摘要、成功/失敗）。
-- 這支檔案只在「全新初始化」的資料庫會被 docker-entrypoint-initdb.d 執行到；
-- 已經在跑的資料庫要另外手動執行這支檔案（見 backend/README.md）。
USE teco_hvac;

DELIMITER //
CREATE PROCEDURE _add_column_if_missing(
    IN p_table VARCHAR(64), IN p_column VARCHAR(64), IN p_ddl VARCHAR(255)
)
BEGIN
    SET @col_exists = (
        SELECT COUNT(*) FROM information_schema.COLUMNS
        WHERE TABLE_SCHEMA = 'teco_hvac' AND TABLE_NAME = p_table AND COLUMN_NAME = p_column
    );
    IF @col_exists = 0 THEN
        SET @sql = CONCAT('ALTER TABLE ', p_table, ' ADD COLUMN ', p_ddl);
        PREPARE stmt FROM @sql;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;
    END IF;
END //
DELIMITER ;

CALL _add_column_if_missing('operation_log', 'merchant_id', 'merchant_id INT UNSIGNED NULL AFTER id');
CALL _add_column_if_missing('operation_log', 'actor_username', 'actor_username VARCHAR(64) NOT NULL DEFAULT \'\' AFTER user_id');
CALL _add_column_if_missing('operation_log', 'actor_display_name', 'actor_display_name VARCHAR(64) NOT NULL DEFAULT \'\' AFTER actor_username');
CALL _add_column_if_missing('operation_log', 'summary', 'summary VARCHAR(255) NOT NULL DEFAULT \'\' AFTER target_id');
CALL _add_column_if_missing('operation_log', 'is_success', 'is_success TINYINT(1) NOT NULL DEFAULT 1 AFTER after_json');
CALL _add_column_if_missing('operation_log', 'error_message', 'error_message VARCHAR(255) NULL AFTER is_success');

DROP PROCEDURE _add_column_if_missing;

SET @idx_exists = (
    SELECT COUNT(*) FROM information_schema.STATISTICS
    WHERE TABLE_SCHEMA = 'teco_hvac' AND TABLE_NAME = 'operation_log' AND INDEX_NAME = 'ix_oplog_merchant_time'
);
SET @sql = IF(@idx_exists = 0,
    'ALTER TABLE operation_log ADD KEY ix_oplog_merchant_time (merchant_id, created_at)',
    'SELECT 1');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;
