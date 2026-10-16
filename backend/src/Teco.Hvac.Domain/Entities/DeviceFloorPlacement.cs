namespace Teco.Hvac.Domain.Entities;

/// <summary>
/// 設備於樓層平面圖上的擺放座標與視覺定位實體 (<see cref="DeviceFloorPlacement"/>)。
/// 對應資料庫 device_floor_placement 資料表。
/// 
/// 【圖面座標系統】
/// X 與 Y 座標係採用前端平面圖專用座標系（基於 CAD 匯出之向量畫布單位，非實體公尺距離）。
/// 包含設備所在的樓層、分區代碼、中心點座標與旋轉角度，供 2D 與 3D 監控畫布精準標註設備錨點。
/// </summary>
public sealed class DeviceFloorPlacement
{
    /// <summary>被標註的設備類型（Chiller 或 Fcu）。</summary>
    public required AlarmDeviceType DeviceType { get; set; }

    /// <summary>對應之設備資料庫主鍵 ID。</summary>
    public required int DeviceId { get; set; }

    /// <summary>設備所在樓層代碼（如 "B1"、"B2"）。</summary>
    public required string Floor { get; set; }

    /// <summary>所屬平面圖分區識別代碼（例如 "B1-Z07"）。</summary>
    public required string AreaId { get; set; }

    /// <summary>平面圖畫布上的 X 軸向中心點座標。</summary>
    public required decimal X { get; set; }

    /// <summary>平面圖畫布上的 Y 軸向中心點座標。</summary>
    public required decimal Y { get; set; }

    /// <summary>設備圖示在畫布上的旋轉角度（單位：度，0 ~ 360）。</summary>
    public int Rotation { get; set; }

    /// <summary>最後執行圖面位置微調或儲存的使用者 ID；尚未有紀錄時為 null。</summary>
    public int? UpdatedByUserId { get; set; }

    /// <summary>最後更新座標配置的標準 UTC 時間戳記。</summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
