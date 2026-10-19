using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace HybridIntelligenceStudio;

internal enum RunEventKind { Token, Activity, Done, Error, Ignored }

internal sealed record RunMetrics(
    string Mode, string? ModelName, string? ModelId, string? Provider, double? TtftMs, double? TotalMs,
    double? Characters, double? OutputTokens, double? TokensPerSecond, string? FinishReason, double? ReasoningCharacters);

/// <summary>Only public answer text, counts and metrics are surfaced. Any hidden reasoning text is never read.</summary>
internal sealed record RunEvent(RunEventKind Kind, string? Text = null, long ReasoningCharacters = 0,
    RunMetrics? Metrics = null, string? Error = null);

internal static class RunEventParser
{
    internal static RunEvent Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Stream event was not a JSON object.");
        switch (StatusParser.Str(root, "type"))
        {
            case "token":
                return new RunEvent(RunEventKind.Token, Text: StatusParser.Str(root, "text") ?? "");
            case "activity":
                return new RunEvent(RunEventKind.Activity, ReasoningCharacters: (long)(StatusParser.Num(root, "reasoning_characters") ?? 0));
            case "error":
                return new RunEvent(RunEventKind.Error, Error: StatusParser.Str(root, "error") ?? "The local run failed.");
            case "done":
                string? modelName = null, modelId = null, provider = null;
                if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object)
                {
                    modelName = StatusParser.Str(m, "name");
                    modelId = StatusParser.Str(m, "id");
                    provider = StatusParser.Str(m, "provider");
                }
                return new RunEvent(RunEventKind.Done, Metrics: new RunMetrics(
                    StatusParser.Str(root, "mode") ?? "unknown", modelName, modelId, provider,
                    StatusParser.Num(root, "ttft_ms"), StatusParser.Num(root, "total_ms"), StatusParser.Num(root, "characters"),
                    StatusParser.Num(root, "output_tokens"), StatusParser.Num(root, "tokens_per_second"),
                    StatusParser.Str(root, "finish_reason"), StatusParser.Num(root, "reasoning_characters")));
            default:
                return new RunEvent(RunEventKind.Ignored);
        }
    }
}

internal sealed class BackendException(string message, HttpStatusCode? status = null) : Exception(message)
{
    internal HttpStatusCode? Status { get; } = status;
}

internal sealed partial class BackendClient : IDisposable
{
    private readonly HttpClient _http;
    private string? _token;

    internal BackendClient(Uri baseUri)
    {
        BaseUri = baseUri;
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = baseUri,
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // The local service answers HTTP/1.0 and closes every connection; never race a pooled socket it is closing.
        _http.DefaultRequestHeaders.ConnectionClose = true;
    }

    /// <summary>Transport failures wrap the useful cause; show both so a presenter sees what actually happened.</summary>
    internal static string Describe(Exception exception) =>
        exception is HttpRequestException { InnerException: { } inner } && !exception.Message.Contains(inner.Message, StringComparison.Ordinal)
            ? $"{exception.Message} ({inner.Message})"
            : exception.Message;

    internal Uri BaseUri { get; }

    /// <summary>Exposed only so smoke checks can prove exports never contain the per-process token.</summary>
    internal string? CurrentToken => _token;

    internal async Task<StatusSnapshot> GetStatusAsync(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));
        using var response = await _http.GetAsync("/api/status", cts.Token);
        using var document = await ReadJsonAsync(response, cts.Token);
        var snapshot = StatusParser.Parse(document.RootElement);
        if (snapshot.AppId != StatusParser.ExpectedAppId)
            throw new BackendException($"Port {BaseUri.Port} is used by a different application.");
        _token = StatusParser.Str(document.RootElement, "token");
        return snapshot;
    }

    internal async Task<Dictionary<string, Scenario>> GetScenariosAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await _http.GetAsync("/api/scenarios", cts.Token);
        using var document = await ReadJsonAsync(response, cts.Token);
        return StatusParser.ParseScenarios(document.RootElement);
    }

    internal async Task SwitchModelAsync(string alias, CancellationToken cancellationToken)
    {
        // 30 s: a larger model is pre-checked against live GPU memory and other model servers before it is accepted.
        using var response = await PostAsync("/api/model", new Dictionary<string, object> { ["model"] = alias },
            HttpCompletionOption.ResponseContentRead, cancellationToken, TimeSpan.FromSeconds(30));
        using var _ = await ReadJsonAsync(response, cancellationToken);
    }

    internal async Task<JsonDocument> CalculateAsync(IReadOnlyDictionary<string, double> values, CancellationToken cancellationToken)
    {
        using var response = await PostAsync("/api/calculate", values.ToDictionary(k => k.Key, k => (object)k.Value),
            HttpCompletionOption.ResponseContentRead, cancellationToken, TimeSpan.FromSeconds(15));
        return await ReadJsonAsync(response, cancellationToken);
    }

    internal async IAsyncEnumerable<RunEvent> RunAsync(string scenario, string mode, string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await PostAsync("/api/run",
            new Dictionary<string, object> { ["scenario"] = scenario, ["mode"] = mode, ["prompt"] = prompt },
            HttpCompletionOption.ResponseHeadersRead, cancellationToken, null);
        if (!response.IsSuccessStatusCode)
        {
            using var error = await ReadJsonAsync(response, cancellationToken);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) yield break;
            if (line.Length == 0) continue;
            yield return RunEventParser.Parse(line);
        }
    }

    internal async Task<DemoCatalog> GetDemosAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await _http.GetAsync("/api/demos", cts.Token);
        using var document = await ReadJsonAsync(response, cts.Token);
        return DemoParser.ParseCatalog(document.RootElement);
    }

    /// <summary>Synthetic demo media rendered by the local service (allow-listed names only).</summary>
    internal async Task<byte[]> GetDemoMediaAsync(string name, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _http.GetAsync("/api/demo-media/" + Uri.EscapeDataString(name), cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            using var _ = await ReadJsonAsync(response, cts.Token);
        }
        return await response.Content.ReadAsByteArrayAsync(cts.Token);
    }

    internal async IAsyncEnumerable<DemoEvent> RunDemoAsync(string demo, string mode, string? option, string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object> { ["demo"] = demo, ["mode"] = mode, ["prompt"] = prompt };
        if (option is not null) body["option"] = option;
        using var response = await PostAsync("/api/demo/run", body, HttpCompletionOption.ResponseHeadersRead, cancellationToken, null);
        if (!response.IsSuccessStatusCode)
        {
            using var error = await ReadJsonAsync(response, cancellationToken);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) yield break;
            if (line.Length == 0) continue;
            yield return DemoParser.ParseEvent(line);
        }
    }

    private async Task<HttpResponseMessage> PostAsync(string path, Dictionary<string, object> body,
        HttpCompletionOption completion, CancellationToken cancellationToken, TimeSpan? timeout)
    {
        if (_token is null) await GetStatusAsync(cancellationToken);
        bool transportRetried = false;
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Showcase-Token", _token);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout is TimeSpan t) cts.CancelAfter(t);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, completion, timeout is null ? cancellationToken : cts.Token);
            }
            catch (HttpRequestException) when (!transportRetried && !cancellationToken.IsCancellationRequested)
            {
                // No response headers arrived. Retrying once is safe: the service serialises runs and answers 409 if one is active.
                transportRetried = true;
                await Task.Delay(300, cancellationToken);
                continue;
            }
            // A restarted backend issues a new per-process token; refresh once and retry.
            if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0)
            {
                response.Dispose();
                await GetStatusAsync(cancellationToken);
                continue;
            }
            return response;
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonDocument? document = null;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException) { }
        if (!response.IsSuccessStatusCode)
        {
            string message = document is not null && StatusParser.Str(document.RootElement, "error") is string e
                ? e : $"Local service returned HTTP {(int)response.StatusCode}.";
            document?.Dispose();
            throw new BackendException(message, response.StatusCode);
        }
        return document ?? throw new BackendException("Local service returned an unreadable response.");
    }

    public void Dispose() => _http.Dispose();
}
