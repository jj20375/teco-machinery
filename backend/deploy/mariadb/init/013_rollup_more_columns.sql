-- 每小時彙總表補欄位：原始讀值只保留 FCU 90 天／冰水主機 180 天，過期後只剩彙總表。
-- 原本彙總只挑了報表當下用到的欄位，冷卻水溫度、電流、電壓、壓力、轉速、啟動次數、警報，
-- 以及 FCU 的運轉模式與風速，過期後就完全查不到歷史（FCU 報表的模式／風速欄因此永遠是 --）。
-- 見 docs/IOT_資料對照與缺口清單.md 3.1。
--
-- 這支檔案只在「全新初始化」的資料庫會被 docker-entrypoint-initdb.d 執行到（001 已含這些欄位，
-- 這裡的 IF NOT EXISTS 會直接略過）；已經在跑的資料庫要另外手動執行。
-- 套用後，彙總排程每 15 分鐘重算最近 26 小時，更早的彙總列新欄位會是 NULL，不會自動回補。
USE teco_hvac;

ALTER TABLE rollup_chiller_1h
    ADD COLUMN IF NOT EXISTS avg_chilled_out  DECIMAL(5,1) NULL,
    ADD COLUMN IF NOT EXISTS avg_chilled_in   DECIMAL(5,1) NULL,
    ADD COLUMN IF NOT EXISTS avg_cooling_out  DECIMAL(5,1) NULL,
    ADD COLUMN IF NOT EXISTS avg_cooling_in   DECIMAL(5,1) NULL,
    ADD COLUMN IF NOT EXISTS avg_current      DECIMAL(6,1) NULL,
    ADD COLUMN IF NOT EXISTS avg_voltage      DECIMAL(6,1) NULL,
    ADD COLUMN IF NOT EXISTS avg_high_pressure DECIMAL(6,2) NULL COMMENT '單位未定義，待供應商確認',
    ADD COLUMN IF NOT EXISTS avg_low_pressure DECIMAL(6,2) NULL COMMENT '單位未定義，待供應商確認',
    ADD COLUMN IF NOT EXISTS avg_rpm          INT NULL,
    ADD COLUMN IF NOT EXISTS avg_approach     DECIMAL(5,1) NULL,
    ADD COLUMN IF NOT EXISTS start_count      INT NULL COMMENT '該小時最後一筆累積啟動次數',
    ADD COLUMN IF NOT EXISTS alarm_bits       INT UNSIGNED NULL COMMENT '該小時任一時間成立過的警報旗標（BIT_OR）';

ALTER TABLE rollup_fcu_1h
    ADD COLUMN IF NOT EXISTS mode      TINYINT NULL COMMENT '1=Cooling,2=Heating,3=Ventilation',
    ADD COLUMN IF NOT EXISTS fan_speed TINYINT NULL COMMENT '0=High,1=Medium,2=Low,3=Auto';
