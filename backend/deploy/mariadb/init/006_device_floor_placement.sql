-- 平面圖配置持久化：新增 device_floor_placement 表與 hvac.floor_plan 權限。
-- 在此之前，配置圖的擺放位置只存在使用者瀏覽器的 localStorage，換裝置就消失。
-- 這支檔案只在「全新初始化」的資料庫會被 docker-entrypoint-initdb.d 執行到；
-- 已經在跑的資料庫要另外手動執行（見 backend/README.md）。重複執行是安全的。
USE teco_hvac;

CREATE TABLE IF NOT EXISTS device_floor_placement (
    device_type  TINYINT UNSIGNED NOT NULL COMMENT '0=Chiller, 1=Fcu（比照 alarm_event.device_type）',
    device_id    INT UNSIGNED NOT NULL COMMENT '對應 device_chiller.id 或 device_fcu.id',
    floor        VARCHAR(8)   NOT NULL COMMENT 'B1 / B2',
    area_id      VARCHAR(16)  NOT NULL COMMENT '分區代號，如 B1-Z07 / B2-E01；對應前端 *-areas.generated.ts 的 Area.id',
    x            DECIMAL(8,2) NOT NULL COMMENT '圖面座標 X（圖面單位，非公尺）',
    y            DECIMAL(8,2) NOT NULL COMMENT '圖面座標 Y（圖面單位，非公尺）',
    rotation     SMALLINT UNSIGNED NOT NULL DEFAULT 0 COMMENT '朝向角度 0~359',
    updated_by   INT UNSIGNED NULL COMMENT '最後更新者 app_user.id',
    updated_at   DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3) COMMENT 'UTC；同時作為整層樂觀鎖的版本依據',
    PRIMARY KEY (device_type, device_id),
    KEY ix_placement_floor (floor)
) ENGINE=InnoDB;

-- 新權限碼：空間設備配置。側邊欄本來就有這個項目，但先前沒有對應的權限資源。
INSERT IGNORE INTO app_permission (code, name, scope, route_path, is_menu, sub_features_json) VALUES
    ('hvac.floor_plan', '空間設備配置', 1, '/admin/floor-plan', 1, JSON_ARRAY());

-- 授予三個系統範本角色：管理員完整 CRUD、編輯者可讀可改、檢視者唯讀。
INSERT INTO app_role_permission (role_id, permission_id, per_create, per_read, per_update, per_delete, options_json)
SELECT r.id, p.id,
       CASE r.code WHEN 'merchant-admin' THEN 1 ELSE 0 END,
       1,
       CASE r.code WHEN 'viewer' THEN 0 ELSE 1 END,
       CASE r.code WHEN 'merchant-admin' THEN 1 ELSE 0 END,
       JSON_ARRAY()
FROM app_role r, app_permission p
WHERE r.merchant_id IS NULL AND r.code IN ('merchant-admin', 'editor', 'viewer') AND p.code = 'hvac.floor_plan'
ON DUPLICATE KEY UPDATE
    per_create = VALUES(per_create), per_read = VALUES(per_read),
    per_update = VALUES(per_update), per_delete = VALUES(per_delete);
