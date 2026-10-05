namespace Teco.Hvac.Api.Services;

/// <summary>
/// 操作紀錄「動作類型」欄位的中文分類，所有 action 代碼的唯一對照表。新增任何會寫 operation_log 的 action，
/// 都要在這裡補一筆，不然畫面上會直接顯示英文代碼。失敗的紀錄由前端在後面加「（失敗）」。
/// </summary>
public static class OperationActionLabels
{
    public const string Denied = "access.denied";
    public const string Login = "auth.login";
    public const string Logout = "auth.logout";
    public const string AccountLocked = "auth.account_locked";

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["auth.login"] = "登入",
        ["auth.logout"] = "登出",
        ["auth.account_locked"] = "帳號鎖定",
        ["auth.change_password"] = "重設密碼",
        ["access.denied"] = "權限拒絕",

        ["merchant.user.create"] = "使用者異動",
        ["merchant.user.update"] = "使用者異動",
        ["merchant.user.delete"] = "使用者異動",
        ["merchant.user.features.update"] = "使用者異動",
        ["merchant.user.reset_password"] = "重設密碼",
        ["merchant.role.create"] = "角色設定",
        ["merchant.role.rename"] = "角色設定",
        ["merchant.role.delete"] = "角色設定",
        ["merchant.role.permission.update"] = "角色設定",

        ["alarm.ack"] = "告警處理",
        ["hvac.threshold.chiller.update"] = "告警門檻",
        ["hvac.threshold.fcu.update"] = "告警門檻",
        ["hvac.chillers.update"] = "設備設定",
        ["hvac.fcus.update"] = "設備設定",
        ["hvac.floor_plan.update"] = "空間配置",
        ["hvac.chiller.maintenance.reset"] = "設備保養",

        ["platform.merchant.create"] = "場館管理",
        ["platform.merchant.update"] = "場館管理",
        ["platform.membership.create"] = "使用者異動",
        ["platform.membership.reset_password"] = "重設密碼",
        ["platform.system_user.create"] = "使用者異動",
        ["platform.role.create"] = "角色設定",
        ["platform.role.permission.update"] = "角色設定",
    };

    /// <summary>沒有對照的動作回傳原始代碼（不隱藏，才看得出來是漏了）。</summary>
    public static string For(string action) => Labels.GetValueOrDefault(action, action);
}
