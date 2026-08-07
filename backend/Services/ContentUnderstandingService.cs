using System.Net.Http.Headers;
using System.Text.Json;
using ManufacturerExtraction.Api.Services.Interfaces;

namespace ManufacturerExtraction.Api.Services;

public class ContentUnderstandingService : IContentUnderstandingService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly string _analyzerId;
    private readonly TimeSpan _pollTimeout;

    public ContentUnderstandingService(HttpClient httpClient, IConfiguration config)
    {
        _httpClient = httpClient;
        _pollTimeout = TimeSpan.FromMinutes(
            config.GetValue<double?>("AzureContentUnderstanding:PollTimeoutMinutes") ?? 20);
        _endpoint = config["AzureContentUnderstanding:Endpoint"]
            ?? throw new InvalidOperationException("Content Understanding endpoint missing");
        _apiKey = config["AzureContentUnderstanding:ApiKey"]
            ?? throw new InvalidOperationException("Content Understanding API key missing");
        _analyzerId = config["AzureContentUnderstanding:AnalyzerId"] ?? "prebuilt-documentAnalyzer";
    }

    // Retries transient network failures (DNS blips = "Hôte inconnu", connection resets,
    // brief timeouts) before giving up. Each request message is rebuilt from scratch on
    // every attempt via the factory, because an HttpRequestMessage can't be sent twice.
    private static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient client, Func<HttpRequestMessage> buildRequest, CancellationToken ct, int maxRetries = 4)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var request = buildRequest();
                var response = await client.SendAsync(request, ct);
                return response;
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
            catch (TaskCanceledException) when (attempt < maxRetries && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }

        // Last attempt: let any exception propagate normally.
        using var lastRequest = buildRequest();
        return await client.SendAsync(lastRequest, ct);
    }

    public async Task<string> SubmitAndPollAsync(string blobUrl, CancellationToken ct = default)
    {
        var submitUrl = $"{_endpoint}/contentunderstanding/analyzers/{_analyzerId}:analyze?api-version=2025-11-01";

        var payload = new
        {
            inputs = new[] { new { url = blobUrl } }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(payload);

        var response = await SendWithRetryAsync(_httpClient, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, submitUrl)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey);
            return req;
        }, ct);

        response.EnsureSuccessStatusCode();

        if (!response.Headers.TryGetValues("Operation-Location", out var locations))
            throw new InvalidOperationException("No Operation-Location header returned by Content Understanding");

        var operationUrl = locations.First();

        // A 7000-8000 row workbook regularly needs more than the 5 minutes this used to allow —
        // the job was still running fine and we threw it away. The interval also backs off from
        // 2s to 10s so a long job doesn't cost hundreds of pointless polls.
        var deadline = DateTime.UtcNow + _pollTimeout;
        var interval = TimeSpan.FromSeconds(2);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(interval, ct);
            if (interval < TimeSpan.FromSeconds(10))
                interval += TimeSpan.FromSeconds(1);

            var pollResponse = await SendWithRetryAsync(_httpClient, () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, operationUrl);
                req.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey);
                return req;
            }, ct);

            pollResponse.EnsureSuccessStatusCode();

            var body = await pollResponse.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.GetProperty("status").GetString();

            if (status == "Succeeded") return body;
            if (status == "Failed") throw new InvalidOperationException($"Content Understanding job failed: {body}");
        }

        throw new TimeoutException(
            $"Content Understanding job did not complete within {_pollTimeout.TotalMinutes:0} minutes.");
    }
}