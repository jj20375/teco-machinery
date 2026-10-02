-- 場館「擁有者」：場館的第一位管理員。擁有者不能被刪除、停用或改角色；其他場館管理員（含擁有者以外的
-- merchant-admin）只有擁有者能刪除、停用、改角色、重設密碼，避免一般管理員互相踢人或接管帳號。
-- 擁有者本人換人要由平台管理員處理（目前沒有畫面，需要時直接改 merchant_membership.is_owner）。
--
-- 001_schema.sql 已含這個欄位（全新初始化的資料庫不需要執行這支）；已經在跑的資料庫要手動執行，可重複執行。
USE teco_hvac;

ALTER TABLE merchant_membership
    ADD COLUMN IF NOT EXISTS is_owner TINYINT(1) NOT NULL DEFAULT 0
        COMMENT '場館擁有者（第一位管理員）：不可刪除、停用、改角色；每個場館最多一位';

-- 既有場館：把「最早加入的在職場館管理員」標成擁有者（已經有擁有者的場館不動）。
UPDATE merchant_membership m
JOIN (
    SELECT mm.merchant_id, MIN(mm.id) AS owner_id
    FROM merchant_membership mm
    JOIN app_role r ON r.id = mm.role_id AND r.code = 'merchant-admin'
    WHERE mm.is_active = 1
    GROUP BY mm.merchant_id
) first_admin ON first_admin.owner_id = m.id
SET m.is_owner = 1
WHERE NOT EXISTS (SELECT 1 FROM (SELECT merchant_id FROM merchant_membership WHERE is_owner = 1) o WHERE o.merchant_id = m.merchant_id);
