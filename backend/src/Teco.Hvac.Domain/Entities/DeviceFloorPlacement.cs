namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 一台設備在樓層平面圖上的擺放位置。座標是圖面單位（不是公尺），對應前端
/// <c>*-areas.generated.ts</c> 的座標系；換圖或重新劃分區時舊座標會失效，這點由前端的
/// 分區版本控制負責，後端只忠實保存前端算好的位置。
/// </summary>
public sealed class DeviceFloorPlacement
{
    public required AlarmDeviceType DeviceType { get; set; }
    public required int DeviceId { get; set; }
    public required string Floor { get; set; }
    public required string AreaId { get; set; }
    public required decimal X { get; set; }
    public required decimal Y { get; set; }
    public int Rotation { get; set; }
    public int? UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
