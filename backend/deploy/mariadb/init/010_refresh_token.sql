-- Refresh token 撤銷清單：access token 只有 30 分鐘，沒有這張表使用者每 30 分鐘就要重新
-- 輸入密碼。詳見 001_schema.sql 裡這張表的完整註解。
-- 這支檔案只在「全新初始化」的資料庫會被 docker-entrypoint-initdb.d 執行到；
-- 已經在跑的資料庫要另外手動執行（見 backend/README.md）。
USE teco_hvac;

CREATE TABLE IF NOT EXISTS refresh_token (
    id                      BIGINT UNSIGNED PRIMARY KEY AUTO_INCREMENT,
    user_id                 INT UNSIGNED NOT NULL,
    token_hash              CHAR(64)     NOT NULL COMMENT 'SHA-256 hex，明文 token 只回給前端一次，不落地',
    auth_version_at_issue   INT UNSIGNED NOT NULL,
    expires_at              DATETIME(3)  NOT NULL,
    revoked_at              DATETIME(3)  NULL,
    created_at              DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    UNIQUE KEY uk_refresh_token_hash (token_hash),
    KEY ix_refresh_token_user (user_id),
    FOREIGN KEY (user_id) REFERENCES app_user(id) ON DELETE CASCADE
) ENGINE=InnoDB;
