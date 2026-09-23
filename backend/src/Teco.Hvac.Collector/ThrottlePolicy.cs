using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Teco.Hvac.Contracts;

namespace Teco.Hvac.Collector;

/// <summary>
/// 決定「這一筆快照要不要落地寫入資料庫」。即時推播（POST /internal/ingest）不受此節流影響，
/// 每次 EventDataReceived 都會送；只有 DB 寫入頻率由這裡把關（計畫 P2 §寫入節流，
/// 原樣每 5 秒寫入會撐到 164 萬筆/天，資料庫撐不住）。
/// </summary>
public sealed class ThrottlePolicy(IOptions<CollectorOptions> options)
{
    private readonly CollectorOptions _options = options.Value;
    private readonly ConcurrentDictionary<int, DateTimeOffset> _lastChillerWrite = new();
    private readonly ConcurrentDictionary<(Channel, byte, int), (DateTimeOffset LastWrite, FcuSnapshot LastValue)> _lastFcuWrite = new();

    public bool ShouldWriteChiller(int modbusId, DateTimeOffset now)
    {
        if (!_lastChillerWrite.TryGetValue(modbusId, out var last) ||
            (now - last).TotalSeconds >= _options.ChillerThrottleSeconds)
        {
            _lastChillerWrite[modbusId] = now;
            return true;
        }
        return false;
    }

    public bool ShouldWriteFcu(FcuSnapshot snapshot, DateTimeOffset now)
    {
        var key = (snapshot.Channel, snapshot.StationId, snapshot.Position);

        if (!_lastFcuWrite.TryGetValue(key, out var last))
        {
            _lastFcuWrite[key] = (now, snapshot);
            return true;
        }

        bool timeElapsed = (now - last.LastWrite).TotalSeconds >= _options.FcuThrottleSeconds;
        bool stateChanged = last.LastValue.SwitchStatus != snapshot.SwitchStatus ||
                             last.LastValue.Mode != snapshot.Mode ||
                             last.LastValue.FanSpeed != snapshot.FanSpeed;
        bool tempJumped = Math.Abs(last.LastValue.Temperature - snapshot.Temperature) >= _options.FcuTemperatureChangeThreshold;

        if (timeElapsed || stateChanged || tempJumped)
        {
            _lastFcuWrite[key] = (now, snapshot);
            return true;
        }
        return false;
    }
}
