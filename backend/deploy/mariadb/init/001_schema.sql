-- 東元智慧環境監控 — MariaDB Schema
-- 單一自用系統，單庫（結案 spec 審查第 2 項：不做 Platform/Customer 雙庫）。
-- 分割表使用 RANGE(TO_DAYS(ts))，清除用 DROP PARTITION（零成本），初期先建立涵蓋
-- 未來 24 個月的分割區，之後由排程（見 collector 的每日維護工作）自動增補與清除。

SET NAMES utf8mb4;
SET time_zone = '+08:00';

CREATE DATABASE IF NOT EXISTS teco_hvac
    CHARACTER SET utf8mb4
    COLLATE utf8mb4_uca1400_ai_ci;

USE teco_hvac;

-- =========================================================================
-- 設備主檔
-- =========================================================================

CREATE TABLE IF NOT EXISTS device_chiller (
    id                  INT UNSIGNED PRIMARY KEY AUTO_INCREMENT COMMENT '主鍵，內部識別碼',
    code                VARCHAR(32)     NOT NULL COMMENT '冰水主機代號（如 CH-1），前後台顯示與 API 查詢用；本表自訂，非供應商文件欄位',
    modbus_id           TINYINT UNSIGNED NOT NULL COMMENT '對應供應商 Collector 的漢鐘 Modbus ID（1 或 2），比對即時快照 TecoGolfHanbellStatus 的鍵值',
    display_name        VARCHAR(64)     NOT NULL COMMENT '顯示名稱（如「冰水主機 1」），由後台維護；本表自訂，非供應商文件欄位',
    rated_capacity_rt   DECIMAL(10,2)   NULL COMMENT '額定容量 RT，待供應商/東元確認',
    is_active           TINYINT(1)      NOT NULL DEFAULT 1 COMMENT '是否啟用；停用後不再顯示於清單，不影響歷史資料',
    maintenance_baseline_hours INT UNSIGNED NULL COMMENT '上次保養（或開始計算）時的累積運轉時數；NULL＝尚未開始計算（見 014）',
    maintenance_baseline_at    DATETIME(3)  NULL COMMENT '上次保養（或開始計算）的時間，UTC',
    UNIQUE KEY uk_chiller_code (code),
    UNIQUE KEY uk_chiller_modbus (modbus_id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS device_fcu (
    id                  INT UNSIGNED PRIMARY KEY AUTO_INCREMENT COMMENT '主鍵，內部識別碼',
    channel             TINYINT UNSIGNED NOT NULL COMMENT '1=DDC1, 2=DDC2',
    station_id          TINYINT UNSIGNED NOT NULL COMMENT '站號，對應 Modbus TCP Unit ID（說明書表18 FCUUnit.StationID）',
    position            SMALLINT UNSIGNED NOT NULL COMMENT '站號內的位置序號，從 1 開始（說明書表18 FCUUnit.Position，不是陣列索引）',
    address             SMALLINT UNSIGNED NOT NULL COMMENT '4X Holding Register 文件位置，如 40051（說明書表18 FCUUnit.Address）',
    floor               VARCHAR(8)      NOT NULL COMMENT 'B1 / B2',
    zone_code           VARCHAR(16)     NULL COMMENT '對應前台圖面分區，如 B1-Z07；由後台維護',
    display_name        VARCHAR(64)     NULL COMMENT '顯示名稱，由後台維護；尚未指派時為 NULL；本表自訂，非供應商文件欄位',
    is_active           TINYINT(1)      NOT NULL DEFAULT 1 COMMENT '是否啟用；停用後不再顯示於清單，不影響歷史資料',
    -- 唯一鍵：FC_MC1_01 這類 ID 會跨 DDC 重複，不能拿 ID 字串當主鍵（說明書 6.1）
    UNIQUE KEY uk_fcu_addr (channel, station_id, position)
) ENGINE=InnoDB;

-- 平面圖配置：一台設備在樓層平面圖上的擺放位置。刻意獨立成一張表而不是在
-- device_chiller/device_fcu 加欄位——device_chiller 本來就沒有樓層概念，而且「擺在圖上哪裡」
-- 是編輯器狀態，跟設備主檔是不同性質的資料。主鍵 (device_type, device_id) 天然保證
-- 一台設備只會有一個位置；雙型別參照的寫法比照 alarm_event / operation_log。
CREATE TABLE IF NOT EXISTS device_floor_placement (
    device_type  TINYINT UNSIGNED NOT NULL COMMENT '0=Chiller, 1=Fcu（比照 alarm_event.device_type）',
    device_id    INT UNSIGNED NOT NULL COMMENT '對應 device_chiller.id 或 device_fcu.id',
    floor        VARCHAR(8)   NOT NULL COMMENT 'B1 / B2',
    area_id      VARCHAR(16)  NOT NULL COMMENT '分區代號，如 B1-Z07 / B2-E01；對應前端 *-areas.generated.ts 的 Area.id',
    x            DECIMAL(8,2) NOT NULL COMMENT '圖面座標 X（圖面單位，非公尺）',
    y            DECIMAL(8,2) NOT NULL COMMENT '圖面座標 Y（圖面單位，非公尺）',
    rotation     SMALLINT UNSIGNED NOT NULL DEFAULT 0 COMMENT '朝向角度 0~359',
    -- 不建 app_user 的外鍵：app_user 在這支檔案後面才建立，而且既有的 operation_log.user_id
    -- 也是同樣的處理方式（操作性資料表不綁使用者外鍵，帳號刪除不該連帶影響歷史紀錄）。
    updated_by   INT UNSIGNED NULL COMMENT '最後更新者 app_user.id',
    updated_at   DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3) COMMENT 'UTC；同時作為整層樂觀鎖的版本依據',
    PRIMARY KEY (device_type, device_id),
    KEY ix_placement_floor (floor)
) ENGINE=InnoDB;

-- =========================================================================
-- 時序資料（月分割）
-- =========================================================================

CREATE TABLE IF NOT EXISTS chiller_reading (
    device_id                          INT UNSIGNED NOT NULL,
    ts                                  DATETIME(3) NOT NULL COMMENT 'UTC，對應 UpdateTime 轉換後',
    received_at                        DATETIME(3) NOT NULL COMMENT '後端實際收到時間，UTC',
    cooling_water_in                  DECIMAL(5,1) NULL,
    cooling_water_out                 DECIMAL(5,1) NULL,
    chilled_water_in                  DECIMAL(5,1) NULL,
    chilled_water_out                 DECIMAL(5,1) NULL,
    chilled_water_delta               DECIMAL(5,1) NULL,
    input_current                     DECIMAL(6,1) NULL,
    input_voltage                     DECIMAL(6,1) NULL,
    high_pressure                     DECIMAL(6,2) NULL COMMENT '單位未定義，待供應商確認',
    low_pressure                      DECIMAL(6,2) NULL COMMENT '單位未定義，待供應商確認',
    actual_rpm                        INT NULL,
    running_hours                     INT NULL,
    start_count                       INT NULL,
    input_power_kw                    DECIMAL(6,1) NULL,
    approach_temp                     DECIMAL(5,1) NULL,
    accumulated_kwh                   DECIMAL(10,1) NULL,
    load_percentage                   TINYINT NULL,
    water_control                     TINYINT NULL COMMENT '0=InletWater, 1=OutletWater',
    alarm_bits                        INT UNSIGNED NOT NULL DEFAULT 0 COMMENT '14 個警報旗標壓成 bitmask',
    read_status                       TINYINT NOT NULL,
    PRIMARY KEY (device_id, ts)
) ENGINE=InnoDB
PARTITION BY RANGE (TO_DAYS(ts)) (
    PARTITION p_before_2026_10 VALUES LESS THAN (TO_DAYS('2026-10-01')),
    PARTITION p_2026_10 VALUES LESS THAN (TO_DAYS('2026-11-01')),
    PARTITION p_2026_11 VALUES LESS THAN (TO_DAYS('2026-12-01')),
    PARTITION p_2026_12 VALUES LESS THAN (TO_DAYS('2027-01-01')),
    PARTITION p_2027_01 VALUES LESS THAN (TO_DAYS('2027-02-01')),
    PARTITION p_2027_02 VALUES LESS THAN (TO_DAYS('2027-03-01')),
    PARTITION p_future VALUES LESS THAN (MAXVALUE)
);

CREATE TABLE IF NOT EXISTS fcu_reading (
    device_id       INT UNSIGNED NOT NULL,
    ts              DATETIME(3) NOT NULL COMMENT 'UTC',
    switch_status   TINYINT NULL COMMENT '0=Off,1=On,-1=Unknown',
    mode            TINYINT NULL COMMENT '1=Cooling,2=Heating,3=Ventilation,-1=Unknown',
    fan_speed       TINYINT NULL COMMENT '0=High,1=Medium,2=Low,3=Auto,-1=Unknown',
    temperature     DECIMAL(4,1) NULL,
    read_status     TINYINT NOT NULL,
    PRIMARY KEY (device_id, ts)
) ENGINE=InnoDB
PARTITION BY RANGE (TO_DAYS(ts)) (
    PARTITION p_before_2026_10 VALUES LESS THAN (TO_DAYS('2026-10-01')),
    PARTITION p_2026_10 VALUES LESS THAN (TO_DAYS('2026-11-01')),
    PARTITION p_2026_11 VALUES LESS THAN (TO_DAYS('2026-12-01')),
    PARTITION p_2026_12 VALUES LESS THAN (TO_DAYS('2027-01-01')),
    PARTITION p_2027_01 VALUES LESS THAN (TO_DAYS('2027-02-01')),
    PARTITION p_2027_02 VALUES LESS THAN (TO_DAYS('2027-03-01')),
    PARTITION p_future VALUES LESS THAN (MAXVALUE)
);

-- =========================================================================
-- 告警與健康
-- =========================================================================

CREATE TABLE IF NOT EXISTS alarm_rule (
    id                  INT UNSIGNED PRIMARY KEY AUTO_INCREMENT,
    device_type         TINYINT NOT NULL COMMENT '0=Chiller, 1=Fcu',
    scope               VARCHAR(32) NOT NULL COMMENT 'FCU: 樓層/分區代碼或 *；Chiller: ModbusId',
    metric              VARCHAR(64) NOT NULL,
    operator            TINYINT NOT NULL COMMENT '0=GreaterThan,1=LessThan,2=Equals',
    threshold           DOUBLE NOT NULL,
    severity            TINYINT NOT NULL COMMENT '0=Info,1=Warning,2=Critical',
    debounce_seconds    INT NOT NULL DEFAULT 30,
    is_enabled          TINYINT(1) NOT NULL DEFAULT 1,
    -- 天然鍵：同一設備對同一指標的同一方向（大於/小於）只會有一條規則，靠這個做 upsert
    -- （見 AlarmRepository.UpsertRuleAsync），改門檻值是覆寫，不是疊加新規則。
    UNIQUE KEY uk_alarm_rule (device_type, scope, metric, operator)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS alarm_event (
    id                  BIGINT UNSIGNED PRIMARY KEY AUTO_INCREMENT,
    device_type         TINYINT NOT NULL,
    device_id           INT UNSIGNED NOT NULL,
    rule_code           VARCHAR(64) NOT NULL,
    severity            TINYINT NOT NULL,
    started_at          DATETIME(3) NOT NULL,
    ended_at            DATETIME(3) NULL,
    peak_value          DOUBLE NULL,
    ack_by              INT UNSIGNED NULL,
    ack_at              DATETIME(3) NULL,
    memo                VARCHAR(255) NULL,
    KEY ix_alarm_active (device_type, device_id, rule_code, ended_at)
) ENGINE=InnoDB;

-- 冰水主機保養履歷（「保養完成・重置」按鈕寫入），說明見 014_chiller_maintenance.sql。
CREATE TABLE IF NOT EXISTS chiller_maintenance_log (
    id                    BIGINT UNSIGNED PRIMARY KEY AUTO_INCREMENT,
    device_id             INT UNSIGNED NOT NULL COMMENT 'device_chiller.id',
    performed_at          DATETIME(3) NOT NULL COMMENT '按下「保養完成」的時間，UTC',
    performed_by          INT UNSIGNED NOT NULL COMMENT 'app_user.id',
    hours_at_reset        INT UNSIGNED NOT NULL COMMENT '保養當下的累積運轉時數，成為下一輪的基準點',
    hours_since_previous  INT UNSIGNED NULL COMMENT '距上次保養運轉了多少小時；之前沒有基準點時為 NULL',
    alarm_event_id        BIGINT UNSIGNED NULL COMMENT '這次保養一併關閉的保養告警；未達保養時數就提前保養時為 NULL',
    memo                  VARCHAR(255) NULL,
    KEY ix_chiller_maintenance_device (device_id, performed_at)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS channel_health (
    id                  BIGINT UNSIGNED PRIMARY KEY AUTO_INCREMENT,
    channel             TINYINT UNSIGNED NOT NULL COMMENT '0=Gateway,1=DDC1,2=DDC2',
    connection_state    TINYINT NOT NULL,
    read_status         TINYINT NOT NULL,
    changed_at          DATETIME(3) NOT NULL,
    duration_seconds    INT NULL,
    KEY ix_channel_health_channel_time (channel, changed_at)
) ENGINE=InnoDB;

-- =========================================================================
-- 聚合（每小時排程寫入）
-- =========================================================================

CREATE TABLE IF NOT EXISTS rollup_chiller_1h (
    device_id       INT UNSIGNED NOT NULL,
    bucket          DATETIME NOT NULL COMMENT '該小時起始時間，UTC',
    avg_load_pct    DECIMAL(5,1) NULL,
    min_chilled_out DECIMAL(5,1) NULL,
    max_chilled_out DECIMAL(5,1) NULL,
    min_chilled_in  DECIMAL(5,1) NULL,
    max_chilled_in  DECIMAL(5,1) NULL,
    avg_chilled_delta DECIMAL(5,1) NULL,
    avg_power_kw    DECIMAL(6,1) NULL,
    kwh_delta       DECIMAL(10,1) NULL,
    running_hours   INT NULL,
    run_minutes     SMALLINT NULL,
    -- 以下由 013 補上：原始讀值過期後（冰水主機 180 天）仍要查得到這些欄位的歷史
    avg_chilled_out  DECIMAL(5,1) NULL,
    avg_chilled_in   DECIMAL(5,1) NULL,
    avg_cooling_out  DECIMAL(5,1) NULL,
    avg_cooling_in   DECIMAL(5,1) NULL,
    avg_current      DECIMAL(6,1) NULL,
    avg_voltage      DECIMAL(6,1) NULL,
    avg_high_pressure DECIMAL(6,2) NULL COMMENT '單位未定義，待供應商確認',
    avg_low_pressure DECIMAL(6,2) NULL COMMENT '單位未定義，待供應商確認',
    avg_rpm          INT NULL,
    avg_approach     DECIMAL(5,1) NULL,
    start_count      INT NULL COMMENT '該小時最後一筆累積啟動次數',
    alarm_bits       INT UNSIGNED NULL COMMENT '該小時任一時間成立過的警報旗標（BIT_OR）',
    PRIMARY KEY (device_id, bucket)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS rollup_fcu_1h (
    device_id       INT UNSIGNED NOT NULL,
    bucket          DATETIME NOT NULL,
    avg_temp        DECIMAL(4,1) NULL,
    min_temp        DECIMAL(4,1) NULL,
    max_temp        DECIMAL(4,1) NULL,
    on_minutes      SMALLINT NULL,
    -- 以下由 013 補上：該小時出現最多次的運轉模式／風速（排除 Unknown=-1），全部 Unknown 時為 NULL
    mode            TINYINT NULL COMMENT '1=Cooling,2=Heating,3=Ventilation',
    fan_speed       TINYINT NULL COMMENT '0=High,1=Medium,2=Low,3=Auto',
    PRIMARY KEY (device_id, bucket)
) ENGINE=InnoDB;

-- =========================================================================
-- 後台使用者與權限（Role/Permission/RolePermission 三層
-- CRUD＋子功能，雙軌帳號設計，Merchant 級 CRUD/子項簡化開關）
-- 補 spec §陸 ERD 缺項（審查第 3 項）。
-- =========================================================================

-- 場館／商家主檔。TECO 的多租戶需求是「同集團其他場館」，用本表 + merchant_id
-- 範圍隔離即可，不做「一商家一資料庫」的實體隔離與 provisioning。
CREATE TABLE IF NOT EXISTS merchant (
    id                              INT UNSIGNED PRIMARY KEY AUTO_INCREMENT,
    code                            VARCHAR(32)  NOT NULL,
    name                            VARCHAR(64)  NOT NULL,
    status                          VARCHAR(16)  NOT NULL DEFAULT 'active' COMMENT 'active/suspended',
    is_role_crud_configuration_enabled   TINYINT(1) NOT NULL DEFAULT 1 COMMENT '場館是否使用 CRUD 細項角色權限設定；停用時授予資源即取得完整 CRUD',
    is_role_option_configuration_enabled TINYINT(1) NOT NULL DEFAULT 1 COMMENT '場館是否使用附加功能細項角色權限設定；停用時授予資源即取得所有附加功能',
    created_at                      DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    updated_at                      DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
    UNIQUE KEY uk_merchant_code (code)
) ENGINE=InnoDB;

-- 登入主體統一用這張表。system_role_id 非空＝平台系統帳號；場館成員資格見 merchant_membership。
CREATE TABLE IF NOT EXISTS app_user (
    id                  INT UNSIGNED PRIMARY KEY AUTO_INCREMENT,
    username            VARCHAR(64)  NOT NULL,
    display_name        VARCHAR(64)  NOT NULL,
    email               VARCHAR(255) NULL,
    password_hash       VARCHAR(255) NOT NULL,
    is_active           TINYINT(1)   NOT NULL DEFAULT 1,
    system_role_id      INT UNSIGNED NULL COMMENT '指派的平台系統角色（Scope=platform）；非空代表可切到 platform scope',
    is_platform_admin   TINYINT(1)   NOT NULL DEFAULT 0 COMMENT '平台超級管理員旗標，跨場館最高權限',
    auth_version        INT UNSIGNED NOT NULL DEFAULT 1 COMMENT '密碼/角色/權限/場館開關變更時遞增，使舊 JWT 失效',
    failed_login_count  INT UNSIGNED NOT NULL DEFAULT 0,
    locked_until        DATETIME(3)  NULL,
    last_login_at       DATETIME(3)  NULL,
    created_at          DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    UNIQUE KEY uk_user_username (username)
) ENGINE=InnoDB;

-- Refresh token（存 hash，不存明文）：access token 只有 30 分鐘（見 appsettings.json 的
-- AccessTokenMinutes），沒有這張表的話使用者每 30 分鐘就要重新輸入密碼。
-- auth_version_at_issue 存簽發當下的 app_user.auth_version，換發新 access token 時要跟
-- 使用者「現在」的 auth_version 比對，密碼被改/權限被調整過就算沒過期也要失效，
-- 理由跟 access token 本身的 auth_version 檢查一致（見 Program.cs 的 OnTokenValidated）。
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

-- 平台或場館範圍的角色。merchant_id 非空＝該場館自訂角色；系統內建通用角色為 NULL。
CREATE TABLE IF NOT EXISTS app_role (
    id                          INT UNSIGNED PRIMARY KEY AUTO_INCREMENT,
    code                        VARCHAR(64)  NOT NULL,
    name                        VARCHAR(64)  NOT NULL,
    scope                       TINYINT      NOT NULL COMMENT '0=Platform, 1=Merchant',
    merchant_id                 INT UNSIGNED NULL,
    is_system                   TINYINT(1)   NOT NULL DEFAULT 0 COMMENT '內建系統角色，Code/Name 鎖定，不可從介面刪除',
    is_full_access              TINYINT(1)   NOT NULL DEFAULT 0 COMMENT 'true 時永遠自動同步為同 Scope 全部權限的完整 CRUD＋所有子功能',
    is_permissions_customized   TINYINT(1)   NOT NULL DEFAULT 0 COMMENT '平台管理員已手動調整過此系統角色，Seeder 不再覆寫',
    created_at                  DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    UNIQUE KEY uk_role_scope_code_merchant (scope, code, merchant_id),
    FOREIGN KEY (merchant_id) REFERENCES merchant(id) ON DELETE CASCADE
) ENGINE=InnoDB;

ALTER TABLE app_user
    ADD CONSTRAINT fk_user_system_role FOREIGN KEY (system_role_id) REFERENCES app_role(id) ON DELETE SET NULL;

-- 資源、CRUD 與子功能組成的權限目錄。CRUD 由 app_role_permission 的獨立欄位表示，不併入 code。
CREATE TABLE IF NOT EXISTS app_permission (
    id                  INT UNSIGNED PRIMARY KEY AUTO_INCREMENT,
    code                VARCHAR(64)  NOT NULL COMMENT '資源代碼，例如 hvac.chillers、platform.merchants',
    name                VARCHAR(64)  NOT NULL,
    description         VARCHAR(255) NOT NULL DEFAULT '',
    scope               TINYINT      NOT NULL COMMENT '0=Platform, 1=Merchant',
    parent_id           INT UNSIGNED NULL COMMENT '權限樹父節點，供 CMS 選單分群；不影響授權判斷',
    sort_order          INT          NOT NULL DEFAULT 0,
    is_menu             TINYINT(1)   NOT NULL DEFAULT 0,
    route_path          VARCHAR(255) NULL,
    sub_features_json   JSON         NOT NULL DEFAULT (JSON_ARRAY()) COMMENT '例如 [{"key":"ack","name":"確認告警"}]',
    created_at          DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    UNIQUE KEY uk_permission_code (code),
    FOREIGN KEY (parent_id) REFERENCES app_permission(id) ON DELETE SET NULL
) ENGINE=InnoDB;

-- 角色與權限的 CRUD／子功能授予。資料庫永遠存細項真相；場館簡化開關只影響發 JWT 時怎麼展開。
CREATE TABLE IF NOT EXISTS app_role_permission (
    role_id         INT UNSIGNED NOT NULL,
    permission_id   INT UNSIGNED NOT NULL,
    per_create      TINYINT(1) NOT NULL DEFAULT 0,
    per_read        TINYINT(1) NOT NULL DEFAULT 0,
    per_update      TINYINT(1) NOT NULL DEFAULT 0,
    per_delete      TINYINT(1) NOT NULL DEFAULT 0,
    options_json    JSON NOT NULL DEFAULT (JSON_ARRAY()) COMMENT '例如 ["ack"]',
    PRIMARY KEY (role_id, permission_id),
    FOREIGN KEY (role_id) REFERENCES app_role(id) ON DELETE CASCADE,
    FOREIGN KEY (permission_id) REFERENCES app_permission(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- 使用者在單一場館的角色指派。TECO 場館是扁平的，沒有 tenant closure table 要繼承。
CREATE TABLE IF NOT EXISTS merchant_membership (
    id              INT UNSIGNED PRIMARY KEY AUTO_INCREMENT,
    merchant_id     INT UNSIGNED NOT NULL,
    user_id         INT UNSIGNED NOT NULL,
    role_id         INT UNSIGNED NULL,
    is_active       TINYINT(1) NOT NULL DEFAULT 1,
    created_at      DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    UNIQUE KEY uk_membership_merchant_user (merchant_id, user_id),
    FOREIGN KEY (merchant_id) REFERENCES merchant(id) ON DELETE CASCADE,
    FOREIGN KEY (user_id) REFERENCES app_user(id) ON DELETE CASCADE,
    FOREIGN KEY (role_id) REFERENCES app_role(id) ON DELETE SET NULL
) ENGINE=InnoDB;

-- merchant_id：merchant.operation_log 是場館範圍權限，查詢一定要能夾這個欄位，
-- 否則多場館之後會互相看到彼此的操作紀錄；platform-scope 操作（例如建立商家）此欄位為 NULL。
-- actor_username / actor_display_name：寫入當下就快照操作者身分，不靠 JOIN app_user 反查——
-- 使用者之後改名、停用或被刪除，舊紀錄的操作者欄位都不該跟著變。
-- summary：寫入當下就組好的人話句子，避免讀取端要為每種 action 各自兜組句邏輯。
CREATE TABLE IF NOT EXISTS operation_log (
    id                  BIGINT UNSIGNED PRIMARY KEY AUTO_INCREMENT,
    merchant_id         INT UNSIGNED NULL,
    user_id             INT UNSIGNED NOT NULL,
    actor_username      VARCHAR(64) NOT NULL,
    actor_display_name  VARCHAR(64) NOT NULL,
    action              VARCHAR(64) NOT NULL,
    target_type         VARCHAR(64) NOT NULL,
    target_id           VARCHAR(64) NOT NULL,
    summary             VARCHAR(255) NOT NULL,
    before_json         JSON NULL,
    after_json          JSON NULL,
    is_success          TINYINT(1) NOT NULL DEFAULT 1,
    error_message       VARCHAR(255) NULL,
    ip                  VARCHAR(45) NULL,
    created_at          DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    KEY ix_oplog_target (target_type, target_id),
    KEY ix_oplog_user (user_id),
    KEY ix_oplog_merchant_time (merchant_id, created_at)
) ENGINE=InnoDB;
