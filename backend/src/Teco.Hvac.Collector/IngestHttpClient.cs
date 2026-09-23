using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Teco.Hvac.Contracts;

namespace Teco.Hvac.Collector;

/// <summary>
/// 把每次事件即時推給 Api 的 /internal/ingest（compose 內網 + 共享 token）。
/// 這條路徑不節流——即時看板需要每 5 秒更新；節流只套用在 DB 落地（見 ThrottlePolicy）。
/// 送失敗只記警告、不中斷收集流程：即時推播是加分項，不是資料完整性的保證來源
///（保證來源是 DB，DB 寫入走獨立、有重試空間的路徑）。
/// </summary>
public sealed class IngestHttpClient(HttpClient httpClient, IOptions<CollectorOptions> options, ILogger<IngestHttpClient> logger)
{
    private readonly CollectorOptions _options = options.Value;

    public async Task PostDataAsync(IngestPayload payload, CancellationToken ct)
    {
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

    public async Task PostConnectionStatusAsync(ConnectionStatusPayload payload, CancellationToken ct)
    {
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
