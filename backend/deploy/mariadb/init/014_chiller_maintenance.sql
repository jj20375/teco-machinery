-- 冰水主機保養提醒改成「機車換機油」模式：主機回報的 AccumulatedRunningHours 是機器出廠至今的
-- 總時數，只增不減，不能直接拿來跟保養間隔比（12480h > 5000h 一設定就觸發、而且永遠消不掉）。
-- 改成記住「上次保養時的總時數」當基準點，距上次保養 = 目前總時數 − 基準點。
-- 保養間隔本身仍存在 alarm_rule（metric = AccumulatedRunningHours），見 ThresholdEndpoints。
--
-- 這支檔案只在「全新初始化」的資料庫會被 docker-entrypoint-initdb.d 執行到（001 已含這些欄位，
-- 這裡的 IF NOT EXISTS 會直接略過）；已經在跑的資料庫要另外手動執行。
USE teco_hvac;

ALTER TABLE device_chiller
    ADD COLUMN IF NOT EXISTS maintenance_baseline_hours INT UNSIGNED NULL
        COMMENT '上次保養（或開始計算）時的累積運轉時數；NULL＝尚未開始計算，Collector 第一次評估時以當下時數起算',
    ADD COLUMN IF NOT EXISTS maintenance_baseline_at DATETIME(3) NULL
        COMMENT '上次保養（或開始計算）的時間，UTC';

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

-- 舊做法的保養告警 rule_code 帶門檻後綴（AccumulatedRunningHours.0.5000），是拿「總時數」判斷的，
-- 新版 Collector 不再評估、也不會關閉這種格式，留著會永遠掛在有效告警裡，這裡直接結案。
-- 新版的保養告警 rule_code 固定是 AccumulatedRunningHours，不受影響。
UPDATE alarm_event
SET ended_at = UTC_TIMESTAMP(3), memo = COALESCE(memo, '保養提醒改為保養間隔模式，舊格式告警自動結案')
WHERE device_type = 0 AND rule_code LIKE 'AccumulatedRunningHours.%' AND ended_at IS NULL;
