-- 補 device_chiller / device_fcu 缺漏的欄位註解（使用者在 DB 管理工具裡發現 id/station_id/
-- position/address/display_name/is_active 沒有註解）。這支檔案只在「全新初始化」的資料庫會被
-- docker-entrypoint-initdb.d 執行到；已經在跑的資料庫要另外手動執行這支檔案（見 backend/README.md）。
-- ALTER TABLE ... MODIFY COLUMN 只改註解、不動型別與資料，重複執行是安全的。
USE teco_hvac;

ALTER TABLE device_chiller
    MODIFY COLUMN id                INT UNSIGNED NOT NULL AUTO_INCREMENT COMMENT '主鍵，內部識別碼',
    MODIFY COLUMN code              VARCHAR(32)  NOT NULL COMMENT '冰水主機代號（如 CH-1），前後台顯示與 API 查詢用；本表自訂，非供應商文件欄位',
    MODIFY COLUMN modbus_id         TINYINT UNSIGNED NOT NULL COMMENT '對應供應商 Collector 的漢鐘 Modbus ID（1 或 2），比對即時快照 TecoGolfHanbellStatus 的鍵值',
    MODIFY COLUMN display_name      VARCHAR(64)  NOT NULL COMMENT '顯示名稱（如「冰水主機 1」），由後台維護；本表自訂，非供應商文件欄位',
    MODIFY COLUMN is_active         TINYINT(1)   NOT NULL DEFAULT 1 COMMENT '是否啟用；停用後不再顯示於清單，不影響歷史資料';

ALTER TABLE device_fcu
    MODIFY COLUMN id                INT UNSIGNED NOT NULL AUTO_INCREMENT COMMENT '主鍵，內部識別碼',
    MODIFY COLUMN station_id        TINYINT UNSIGNED NOT NULL COMMENT '站號，對應 Modbus TCP Unit ID（說明書表18 FCUUnit.StationID）',
    MODIFY COLUMN position          SMALLINT UNSIGNED NOT NULL COMMENT '站號內的位置序號，從 1 開始（說明書表18 FCUUnit.Position，不是陣列索引）',
    MODIFY COLUMN address           SMALLINT UNSIGNED NOT NULL COMMENT '4X Holding Register 文件位置，如 40051（說明書表18 FCUUnit.Address）',
    MODIFY COLUMN display_name      VARCHAR(64)  NULL COMMENT '顯示名稱，由後台維護；尚未指派時為 NULL；本表自訂，非供應商文件欄位',
    MODIFY COLUMN is_active         TINYINT(1)   NOT NULL DEFAULT 1 COMMENT '是否啟用；停用後不再顯示於清單，不影響歷史資料';
