-- 告警門檻的兩項資料清理。全新初始化的資料庫兩段都是空操作；已經在跑的資料庫要另外手動執行（可重複執行）。
USE teco_hvac;

-- 1) 水流量門檻已從後台移除（供應商 SDK 沒有水流量量測值，這組規則從來不會觸發告警）。
--    先前在門檻面板存過的 ChilledWaterFlowRate 規則留著沒有作用、畫面上也看不到、刪不掉，這裡清掉。
--    alarm_event 用的是 rule_code 字串，不是外鍵，刪規則不影響歷史告警。
DELETE FROM alarm_rule WHERE device_type = 0 AND metric = 'ChilledWaterFlowRate';

-- 2) 關掉「門檻已經改過或清空」留下的孤兒告警。rule_code 是「指標.方向.門檻值」，改門檻後舊代碼
--    Collector 不會再評估，以前的版本不會收掉它，畫面就一直顯示異常（2026-10-02 起 API 存門檻時會順手關）。
--    只處理門檻類代碼（含 .GreaterThan. / .LessThan.）；硬體警報旗標與保養提醒的代碼沒有這段，不受影響。
--    新門檻下若仍超標，Collector 過了防抖時間會用新代碼重開一筆。
UPDATE alarm_event e
SET e.ended_at = UTC_TIMESTAMP(3)
WHERE e.ended_at IS NULL
  AND (e.rule_code LIKE '%.GreaterThan.%' OR e.rule_code LIKE '%.LessThan.%')
  AND NOT EXISTS (
      SELECT 1 FROM alarm_rule r
      WHERE r.device_type = e.device_type
        AND e.rule_code = CONCAT(r.metric, '.', CASE r.operator WHEN 0 THEN 'GreaterThan' WHEN 1 THEN 'LessThan' ELSE 'Equals' END,
                                 '.', r.threshold));
