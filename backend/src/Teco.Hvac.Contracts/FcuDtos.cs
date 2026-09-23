namespace Teco.Hvac.Contracts;

/// <summary>
/// 單台 FCU 的定位與數值快照。唯一鍵是 (Channel, StationId, Position)，
/// 不能用 Id 字串，因為 FC_MC1_01 這類 ID 會跨 DDC 重複（說明書 6.1 提示）。
/// </summary>
public sealed class FcuSnapshot
{
    public required Channel Channel { get; init; }
    public required byte StationId { get; init; }
    public required int Position { get; init; }
    public required string Id { get; init; }
    public required ushort Address { get; init; }

    public FcuSwitchStatus SwitchStatus { get; init; }
    public FcuOperationMode Mode { get; init; }
    public FcuFanSpeed FanSpeed { get; init; }
    public double Temperature { get; init; }
}

/// <summary>單台 DDC（樓層控制器）的完整快照。</summary>
public sealed class DdcSnapshot
{
    public required Channel Channel { get; init; }
    public required DateTimeOffset UpdateTimeUtc { get; init; }
    public required ReadStatus ReadStatus { get; init; }
    public required bool IsConnected { get; init; }
    public required IReadOnlyList<FcuSnapshot> FcuList { get; init; }
}
