using Teco.Hvac.Contracts;

namespace Teco.Hvac.Api.Services.Diagnostics;

/// <summary>
/// 平台診斷頁判斷「數據內容合不合理」的範圍，全部集中在這裡。
///
/// ⚠️ 這些範圍是依照一般冰水主機／FCU 運轉常識訂的**暫定值**，不是供應商規格——
/// 現場接通後要拿真實數據回來校正（見 docs/IOT_現場接通驗證手冊.md「數據內容逐欄核對」）。
/// 超出範圍一律只標 warn（需要人工確認），不標 error：數值怪不代表程式壞了，
/// 也可能是設備真的處於特殊狀態，要現場人員判斷。
/// </summary>
public static class DataValidationRules
{
    public const string LevelOk = "ok";
    public const string LevelWarn = "warn";
    public const string LevelError = "error";
    public const string LevelUnknown = "unknown";

    /// <summary>通道超過這個秒數沒有成功讀取就視為異常；跟 Collector 的 WatchdogStaleSeconds 預設值一致。</summary>
    public const double ChannelStaleSeconds = 60;

    /// <summary>API 超過這個秒數沒收到 Collector 推送就視為中斷（Collector 正常約 5 秒推一次）。</summary>
    public const double IngestStaleSeconds = 30;

    /// <summary>
    /// 事件時間（UpdateTime）與 Collector 收到時間相差超過這個秒數就警告。專門抓時區換算錯誤——
    /// 本專案真的發生過整整差 8 小時的 bug（見 CLAUDE.md 原則 1），差距超過 1 小時直接標 error。
    /// UpdateTime 是供應商程式建立事件時的主機 DateTime.Now，不是設備量測時間（說明書 4.2），
    /// 所以這項檢查看不到現場設備自己的時鐘。
    /// </summary>
    public const double ClockSkewWarnSeconds = 30;
    public const double ClockSkewErrorSeconds = 3600;

    /// <summary>落地統計看「最近幾分鐘」；FCU 節流最長 60 秒一筆，10 分鐘內每台至少該有好幾筆。</summary>
    public static readonly TimeSpan PersistenceRecentWindow = TimeSpan.FromMinutes(10);

    public const double FcuTemperatureMin = 0;
    public const double FcuTemperatureMax = 50;

    public sealed record FieldRule(
        string Key, string Label, string Unit, Func<ChillerSnapshot, double> Read, double? Min, double? Max,
        bool MustNotDecrease = false);

    /// <summary>冰水主機逐欄規則。欄位順序就是診斷頁表格的顯示順序。</summary>
    public static readonly IReadOnlyList<FieldRule> ChillerFields =
    [
        new("chilledWaterOutletTemperature", "冰水出水溫度", "°C", c => c.ChilledWaterOutletTemperature, 0, 30),
        new("chilledWaterInletTemperature", "冰水入水溫度", "°C", c => c.ChilledWaterInletTemperature, 0, 30),
        new("chilledWaterTemperatureDifference", "冰水溫差 ΔT", "°C", c => c.ChilledWaterTemperatureDifference, -5, 15),
        new("coolingWaterOutletTemperature", "冷卻水出水溫度", "°C", c => c.CoolingWaterOutletTemperature, 5, 50),
        new("coolingWaterInletTemperature", "冷卻水入水溫度", "°C", c => c.CoolingWaterInletTemperature, 5, 50),
        new("approachTemperature", "趨近溫度", "°C", c => c.ApproachTemperature, -5, 20),
        new("loadPercentage", "負載率", "%", c => c.LoadPercentage, 0, 100),
        new("inputVoltage", "輸入電壓", "V", c => c.InputVoltage, 0, 500),
        new("inputCurrent", "輸入電流", "A", c => c.InputCurrent, 0, 2000),
        new("inputPowerKilowatt", "輸入功率", "kW", c => c.InputPowerKilowatt, 0, 2000),
        new("actualRpm", "實際轉速", "rpm", c => c.ActualRpm, 0, 10000),
        // 高低壓物理單位供應商尚未確認（見 ChillerSnapshot 註解），只檢查不可為負。
        new("highPressure", "高壓", "（單位待確認）", c => c.HighPressure, 0, null),
        new("lowPressure", "低壓", "（單位待確認）", c => c.LowPressure, 0, null),
        new("accumulatedRunningHours", "累計運轉時數", "h", c => c.AccumulatedRunningHours, 0, null, MustNotDecrease: true),
        new("accumulatedStartCount", "累計啟動次數", "次", c => c.AccumulatedStartCount, 0, null, MustNotDecrease: true),
        new("accumulatedEnergyKilowattHour", "累計耗電量", "kWh", c => c.AccumulatedEnergyKilowattHour, 0, null, MustNotDecrease: true),
    ];

    public static bool IsOutOfRange(double value, double? min, double? max) =>
        (min is not null && value < min) || (max is not null && value > max);

    /// <summary>讀取成功卻所有數值欄位都是 0——幾乎可以確定是暫存器位址/站號對錯，讀到空資料。</summary>
    public static bool LooksAllZero(ChillerSnapshot c) => ChillerFields.All(f => f.Read(c) == 0);

    /// <summary>BACKEND_INTEGRATION_PLAN §2.2 的啟發式：狀態欄位全部 Unknown 且溫度為 0，視為該台沒回應。</summary>
    public static bool LooksUnresponsive(FcuSnapshot f) =>
        f.SwitchStatus == FcuSwitchStatus.Unknown && f.Mode == FcuOperationMode.Unknown &&
        f.FanSpeed == FcuFanSpeed.Unknown && f.Temperature == 0;

    public static bool IsFcuTemperatureOutOfRange(FcuSnapshot f) =>
        IsOutOfRange(f.Temperature, FcuTemperatureMin, FcuTemperatureMax);

    public static string Worst(IEnumerable<string> levels)
    {
        var worst = LevelOk;
        foreach (var level in levels)
        {
            if (Rank(level) > Rank(worst)) worst = level;
        }
        return worst;
    }

    private static int Rank(string level) => level switch
    {
        LevelError => 3,
        LevelWarn => 2,
        LevelUnknown => 1,
        _ => 0,
    };
}
