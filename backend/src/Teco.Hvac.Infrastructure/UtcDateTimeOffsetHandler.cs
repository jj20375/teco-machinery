using System.Data;
using Dapper;

namespace Teco.Hvac.Infrastructure;

/// <summary>
/// 重大 bug 修復：好幾個 Repository 的內部 row 類別直接把 DB 的 DATETIME 欄位宣告成
/// <c>DateTimeOffset</c> 屬性給 Dapper 自動對應（例如 MerchantRepository/RoleRepository/
/// PermissionRepository/UserRepository 的 CreatedAt/LastLoginAt/LockedUntil）。MySqlConnector
/// 讀出來的原始值是 <c>DateTimeKind.Unspecified</c> 的 DateTime，Dapper 沒有註冊自訂
/// TypeHandler 時，會用 C# 內建的 <c>(DateTimeOffset)(DateTime)value</c> 轉換——這個轉換對
/// Unspecified／Local 的 DateTime 一律套用 <c>TimeZoneInfo.Local</c> 算 offset，而這個
/// API 容器的系統時區是 TZ=Asia/Taipei（UTC+8）。結果是：明明存的是 UTC 時間，轉出來的
/// DateTimeOffset 卻套用了 +08:00 offset，代表的「絕對時間點」整整偏移了 8 小時
/// ——不是顯示格式問題，是資料本身在轉型那一刻就被算錯了。
///
/// 修法：註冊這個 TypeHandler，明確把讀出來的 DateTime 標記成 Utc 再轉换，讓 offset 一律是 0，
/// 不受容器系統時區影響。在 TecoDbConnectionFactory 的靜態建構子註冊一次即可，Dapper 全域生效。
/// </summary>
public sealed class UtcDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public override DateTimeOffset Parse(object value) =>
        new(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc));

    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) =>
        parameter.Value = value.UtcDateTime;
}
