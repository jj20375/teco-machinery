using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Teco.Hvac.Contracts;

namespace Teco.Hvac.Collector;

/// <summary>
/// 內部 HTTP 遙測攝取客戶端 (IngestHttpClient)。
/// 
/// 負責將 Collector 所接收到的每次硬體資料事件與連線狀態即時推送給 Web API 的 /internal/ingest 端點
/// （透過 Docker Compose 內部虛擬網路與共享安全權杖 X-Internal-Token 驗證）。
/// 
/// 【特性與設計考量】
/// 1. 不套用節流：前台即時監控儀表板需要 5 秒一次的更新頻率，因此此推播管道每次收到事件皆即刻發送。
/// 2. 失敗不中斷：若推播失敗僅記錄日誌警告，絕不中斷採集核心的運作；因為時序資料的持久性由獨立的資料庫落地管道保障。
/// </summary>
public sealed class IngestHttpClient(HttpClient httpClient, IOptions<CollectorOptions> options, ILogger<IngestHttpClient> logger)
{
    /// <summary>採集器組態設定選項，包含 API 端點 BaseUrl 與內部存取權杖。</summary>
    private readonly CollectorOptions _options = options.Value;

    /// <summary>
    /// 以 HTTP POST 將最新採集到的設備遙測快照非同步推送給 Web API。
    /// </summary>
    /// <param name="payload">包含漢鐘 1/2 號主機與 DDC 1/2 快照之完整遙測酬載物件。</param>
    /// <param name="ct">控制非同步請求取消的語彙基元。</param>
    /// <returns>代表非同步推送作業的 <see cref="Task"/>。</returns>
    public async Task PostDataAsync(IngestPayload payload, CancellationToken ct)
    {
        // 若未設定 API 端點網址，則直接略過推送
        if (string.IsNullOrWhiteSpace(_options.ApiBaseUrl)) return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/ingest/data")
            {
                Content = JsonContent.Create(payload),
            };
            if (!string.IsNullOrWhiteSpace(_options.InternalToken))
            {
                request.Headers.Add("X-Internal-Token", _options.InternalToken);
            }
            using var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("推播資料事件到 Api 失敗，狀態碼 {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "推播資料事件到 Api 時發生例外，本次跳過");
        }
    }

    /// <summary>
    /// 以 HTTP POST 將通訊通道之連線狀態變更事件非同步推送給 Web API。
    /// </summary>
    /// <param name="payload">包含通道識別、IP、連接埠與最新連線狀態之酬載物件。</param>
    /// <param name="ct">控制非同步請求取消的語彙基元。</param>
    /// <returns>代表非同步推送作業的 <see cref="Task"/>。</returns>
    public async Task PostConnectionStatusAsync(ConnectionStatusPayload payload, CancellationToken ct)
    {
        // 若未設定 API 端點網址，則直接略過推送
        if (string.IsNullOrWhiteSpace(_options.ApiBaseUrl)) return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/ingest/connection")
            {
                Content = JsonContent.Create(payload),
            };
            if (!string.IsNullOrWhiteSpace(_options.InternalToken))
            {
                request.Headers.Add("X-Internal-Token", _options.InternalToken);
            }
            using var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("推播連線事件到 Api 失敗，狀態碼 {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "推播連線事件到 Api 時發生例外，本次跳過");
        }
    }
}
