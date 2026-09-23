-- teco_app 原本只有 SELECT/INSERT/UPDATE/DELETE（刻意的最小權限設計，見
-- 000_create_app_user.sh）。PartitionMaintenanceHostedService 要用 ALTER TABLE ...
-- ADD/DROP PARTITION 做時序表（chiller_reading/fcu_reading）的分割區增補與清除，
-- 這個操作需要 ALTER 權限，所以額外授予——這是有意放寬最小權限原則換取這個功能，
-- 經過使用者確認同意（2026-09-21），不是疏漏。
--
-- 這支檔案只在「全新初始化」的資料庫會被 docker-entrypoint-initdb.d 執行到（且要用
-- root 帳號跑，000_create_app_user.sh 也已經直接加了這個 GRANT）；已經在跑的資料庫
-- 要另外用 root 帳號手動執行這行 GRANT。
GRANT ALTER ON teco_hvac.* TO 'teco_app'@'%';
FLUSH PRIVILEGES;
