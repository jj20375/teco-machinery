USE teco_hvac;

-- 兩台漢鐘冰水主機
INSERT IGNORE INTO device_chiller (code, modbus_id, display_name) VALUES
    ('CH-1', 1, '冰水主機 1'),
    ('CH-2', 2, '冰水主機 2');

-- DDC1 (B1F, StationID 1-5, 共 64 台) / DDC2 (B2F, StationID 1, 共 31 台)
-- 內建配置對應說明書表 17，Address = 40051 + (Position-1)*16
DELIMITER //
CREATE PROCEDURE _seed_fcu()
BEGIN
    DECLARE v_station TINYINT;
    DECLARE v_pos INT;
    DECLARE v_max_pos INT;

    -- DDC1: station 1(1-15) 2(1-3) 3(1-21) 4(1-23) 5(1-2)
    SET v_station = 1; SET v_pos = 1; SET v_max_pos = 15;
    WHILE v_pos <= v_max_pos DO
        INSERT IGNORE INTO device_fcu (channel, station_id, position, address, floor)
            VALUES (1, v_station, v_pos, 40051 + (v_pos - 1) * 16, 'B1');
        SET v_pos = v_pos + 1;
    END WHILE;

    SET v_station = 2; SET v_pos = 1; SET v_max_pos = 3;
    WHILE v_pos <= v_max_pos DO
        INSERT IGNORE INTO device_fcu (channel, station_id, position, address, floor)
            VALUES (1, v_station, v_pos, 40051 + (v_pos - 1) * 16, 'B1');
        SET v_pos = v_pos + 1;
    END WHILE;

    SET v_station = 3; SET v_pos = 1; SET v_max_pos = 21;
    WHILE v_pos <= v_max_pos DO
        INSERT IGNORE INTO device_fcu (channel, station_id, position, address, floor)
            VALUES (1, v_station, v_pos, 40051 + (v_pos - 1) * 16, 'B1');
        SET v_pos = v_pos + 1;
    END WHILE;

    SET v_station = 4; SET v_pos = 1; SET v_max_pos = 23;
    WHILE v_pos <= v_max_pos DO
        INSERT IGNORE INTO device_fcu (channel, station_id, position, address, floor)
            VALUES (1, v_station, v_pos, 40051 + (v_pos - 1) * 16, 'B1');
        SET v_pos = v_pos + 1;
    END WHILE;

    SET v_station = 5; SET v_pos = 1; SET v_max_pos = 2;
    WHILE v_pos <= v_max_pos DO
        INSERT IGNORE INTO device_fcu (channel, station_id, position, address, floor)
            VALUES (1, v_station, v_pos, 40051 + (v_pos - 1) * 16, 'B1');
        SET v_pos = v_pos + 1;
    END WHILE;

    -- DDC2: station 1 (1-31)
    SET v_station = 1; SET v_pos = 1; SET v_max_pos = 31;
    WHILE v_pos <= v_max_pos DO
        INSERT IGNORE INTO device_fcu (channel, station_id, position, address, floor)
            VALUES (2, v_station, v_pos, 40051 + (v_pos - 1) * 16, 'B2');
        SET v_pos = v_pos + 1;
    END WHILE;
END //
DELIMITER ;

CALL _seed_fcu();
DROP PROCEDURE _seed_fcu;

-- =========================================================================
-- 場館主檔、角色與權限目錄（比照美達特權限模型，見 docs/BACKEND_INTEGRATION_PLAN.md
-- 與 backend/README.md 的「權限機制」章節）
-- =========================================================================

-- 預設場館：目前唯一場館（東元高爾夫球場）。CRUD/子項開關設為「簡化模式」（0），
-- 對應這個客戶「功能一開就有完整 CRUD」的需求；未來其他場館可由平台管理員
-- 個別切換成「細項模式」（1）。
INSERT IGNORE INTO merchant (code, name, is_role_crud_configuration_enabled, is_role_option_configuration_enabled)
    VALUES ('default', '東元高爾夫球場', 0, 0);

-- 系統內建角色。scope: 0=Platform, 1=Merchant。merchant_id 為 NULL 的 merchant-scope
-- 角色是跨場館共用的全域範本（比照美達特 customer-admin/editor/viewer 的做法），
-- 場館自訂角色則會有非空的 merchant_id。
INSERT IGNORE INTO app_role (code, name, scope, merchant_id, is_system, is_full_access) VALUES
    ('platform-admin',    '平台管理員', 0, NULL, 1, 1),
    ('platform-operator', '平台操作員', 0, NULL, 1, 0),
    ('merchant-admin',    '場館管理員', 1, NULL, 1, 1),
    ('editor',            '編輯者',     1, NULL, 1, 0),
    ('viewer',            '檢視者',     1, NULL, 1, 0);

-- 權限目錄：資源代碼 + CMS 選單 metadata + 子功能定義。
INSERT IGNORE INTO app_permission (code, name, scope, route_path, is_menu, sub_features_json) VALUES
    ('platform.merchants',    '場館管理',   0, '/platform/merchants',     1, JSON_ARRAY(JSON_OBJECT('key','reset_password','name','重設場館成員密碼'))),
    ('platform.system_users', '系統帳號',   0, '/platform/system-users',  1, JSON_ARRAY(JSON_OBJECT('key','reset_password','name','重設密碼'))),
    ('platform.roles',        '角色管理',   0, '/platform/roles',         1, JSON_ARRAY(JSON_OBJECT('key','assign_role','name','指派角色'))),
    ('platform.permissions',  '權限目錄',   0, '/platform/permissions',   1, JSON_ARRAY()),
    ('merchant.users',        '場館帳號',   1, '/users',                  1, JSON_ARRAY(JSON_OBJECT('key','assign_role','name','指派角色'), JSON_OBJECT('key','reset_password','name','重設密碼'))),
    ('merchant.roles',        '場館角色',   1, '/roles',                  1, JSON_ARRAY()),
    ('hvac.chillers',         '冰水主機',   1, '/chillers',               1, JSON_ARRAY()),
    ('hvac.fcus',             'FCU 設備',   1, '/fcus',                   1, JSON_ARRAY()),
    ('hvac.alarms',           '告警',       1, '/alarms',                 1, JSON_ARRAY(JSON_OBJECT('key','ack','name','確認告警'))),
    ('hvac.thresholds',       '告警門檻',   1, '/thresholds',             1, JSON_ARRAY()),
    ('hvac.reports',          '報表',       1, '/reports',                1, JSON_ARRAY()),
    ('hvac.overview',         '監控中心',   1, '/admin',                  1, JSON_ARRAY()),
    ('hvac.floor_plan',       '空間設備配置', 1, '/admin/floor-plan',     1, JSON_ARRAY()),
    ('merchant.operation_log','操作紀錄',   1, '/admin/operation-log',    1, JSON_ARRAY());

-- 角色權限授予的小工具：依 code 查 id，避免寫死 AUTO_INCREMENT 數值。
DELIMITER //
CREATE PROCEDURE _grant_role_permission(
    IN p_role_code VARCHAR(64), IN p_permission_code VARCHAR(64),
    IN p_c TINYINT, IN p_r TINYINT, IN p_u TINYINT, IN p_d TINYINT, IN p_options JSON
)
BEGIN
    INSERT INTO app_role_permission (role_id, permission_id, per_create, per_read, per_update, per_delete, options_json)
    SELECT r.id, p.id, p_c, p_r, p_u, p_d, p_options
    FROM app_role r, app_permission p
    WHERE r.code = p_role_code AND r.merchant_id IS NULL AND p.code = p_permission_code
    ON DUPLICATE KEY UPDATE
        per_create = p_c, per_read = p_r, per_update = p_u, per_delete = p_d, options_json = p_options;
END //
DELIMITER ;

-- platform-admin：全部平台資源完整 CRUD＋全部子功能（IsFullAccess 角色的實際落地）。
CALL _grant_role_permission('platform-admin', 'platform.merchants',    1,1,1,1, JSON_ARRAY('reset_password'));
CALL _grant_role_permission('platform-admin', 'platform.system_users', 1,1,1,1, JSON_ARRAY('reset_password'));
CALL _grant_role_permission('platform-admin', 'platform.roles',        1,1,1,1, JSON_ARRAY('assign_role'));
CALL _grant_role_permission('platform-admin', 'platform.permissions',  1,1,1,1, JSON_ARRAY());

-- platform-operator：唯讀查看場館列表，示範「非完整存取」的平台角色。
CALL _grant_role_permission('platform-operator', 'platform.merchants', 0,1,0,0, JSON_ARRAY());

-- merchant-admin：全部場館資源完整 CRUD＋全部子功能，同時也是簡化模式下的權限上限。
CALL _grant_role_permission('merchant-admin', 'merchant.users',  1,1,1,1, JSON_ARRAY('assign_role','reset_password'));
CALL _grant_role_permission('merchant-admin', 'merchant.roles',  1,1,1,1, JSON_ARRAY());
CALL _grant_role_permission('merchant-admin', 'hvac.chillers',   1,1,1,1, JSON_ARRAY());
CALL _grant_role_permission('merchant-admin', 'hvac.fcus',       1,1,1,1, JSON_ARRAY());
CALL _grant_role_permission('merchant-admin', 'hvac.alarms',     1,1,1,1, JSON_ARRAY('ack'));
CALL _grant_role_permission('merchant-admin', 'hvac.thresholds', 1,1,1,1, JSON_ARRAY());
CALL _grant_role_permission('merchant-admin', 'hvac.reports',    1,1,1,1, JSON_ARRAY());
CALL _grant_role_permission('merchant-admin', 'hvac.overview',   1,1,1,1, JSON_ARRAY());
CALL _grant_role_permission('merchant-admin', 'hvac.floor_plan', 1,1,1,1, JSON_ARRAY());
CALL _grant_role_permission('merchant-admin', 'merchant.operation_log', 1,1,1,1, JSON_ARRAY());

-- editor：可看與處理現場資料（含確認告警），但不能管帳號/角色、不能刪資料。
CALL _grant_role_permission('editor', 'hvac.chillers',   0,1,0,0, JSON_ARRAY());
CALL _grant_role_permission('editor', 'hvac.fcus',       0,1,0,0, JSON_ARRAY());
CALL _grant_role_permission('editor', 'hvac.alarms',     0,1,0,0, JSON_ARRAY('ack'));
CALL _grant_role_permission('editor', 'hvac.thresholds', 0,1,0,0, JSON_ARRAY());
CALL _grant_role_permission('editor', 'hvac.reports',    0,1,0,0, JSON_ARRAY());
CALL _grant_role_permission('editor', 'hvac.overview',   0,1,0,0, JSON_ARRAY());
-- editor 可以改平面圖配置（現場人員調整設備位置是日常作業，不是管理員專屬）
CALL _grant_role_permission('editor', 'hvac.floor_plan', 0,1,1,0, JSON_ARRAY());

-- viewer：純唯讀，連確認告警都不行。
CALL _grant_role_permission('viewer', 'hvac.chillers',   0,1,0,0, JSON_ARRAY());
CALL _grant_role_permission('viewer', 'hvac.fcus',       0,1,0,0, JSON_ARRAY());
CALL _grant_role_permission('viewer', 'hvac.alarms',     0,1,0,0, JSON_ARRAY());
CALL _grant_role_permission('viewer', 'hvac.thresholds', 0,1,0,0, JSON_ARRAY());
CALL _grant_role_permission('viewer', 'hvac.reports',    0,1,0,0, JSON_ARRAY());
CALL _grant_role_permission('viewer', 'hvac.overview',   0,1,0,0, JSON_ARRAY());
CALL _grant_role_permission('viewer', 'hvac.floor_plan', 0,1,0,0, JSON_ARRAY());

DROP PROCEDURE _grant_role_permission;

-- FCU 異常門檻：絕對室溫上下限（決策見 docs/BACKEND_INTEGRATION_PLAN.md §2.1）
-- Scope='*' 代表全樓層通用初始值，後台可依樓層/分區覆寫。
INSERT IGNORE INTO alarm_rule (device_type, scope, metric, operator, threshold, severity, debounce_seconds) VALUES
    (1, '*', 'Temperature', 0, 28.0, 1, 60),   -- FCU 室溫 > 28°C → Warning
    (1, '*', 'Temperature', 1, 16.0, 1, 60);   -- FCU 室溫 < 16°C → Warning
