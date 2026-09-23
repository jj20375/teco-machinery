#!/bin/bash
# 建立應用程式專用 DB 帳號（非 root），密碼取自 compose 傳入的 MARIADB_APP_PASSWORD。
# docker-entrypoint-initdb.d 依檔名排序執行，這支 .sh 先跑，後面的 001/002 .sql 才有 teco_hvac 可用。
set -euo pipefail

: "${MARIADB_APP_PASSWORD:?未設定 MARIADB_APP_PASSWORD，請檢查 compose 的環境變數}"

mariadb -u root -p"${MARIADB_ROOT_PASSWORD}" <<SQL
CREATE USER IF NOT EXISTS 'teco_app'@'%' IDENTIFIED BY '${MARIADB_APP_PASSWORD}';
CREATE DATABASE IF NOT EXISTS teco_hvac CHARACTER SET utf8mb4 COLLATE utf8mb4_uca1400_ai_ci;
-- ALTER 是刻意額外授予的：PartitionMaintenanceHostedService 要用 ALTER TABLE ... ADD/DROP
-- PARTITION 做時序表的分割區增補與清除（見 2026-09-21 的決策紀錄，backend/README.md
-- 「資料保留策略接真實排程」一節）。這是有意放寬最小權限原則換取這個功能，不是疏漏。
GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON teco_hvac.* TO 'teco_app'@'%';
FLUSH PRIVILEGES;
SQL
