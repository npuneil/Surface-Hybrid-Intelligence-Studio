using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace HybridIntelligenceStudio;

/// <summary>Hybrid router, builder hub and speedometer calls. Responses stay as JSON; pages read only the fields they show.</summary>
internal sealed partial class BackendClient
{
    internal async Task<JsonElement> GetJsonAsync(string path, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        using var response = await _http.GetAsync(path, cts.Token);
        using var document = await ReadJsonAsync(response, cts.Token);
        return document.RootElement.Clone();
    }

    internal async Task<JsonElement> PostJsonAsync(string path, Dictionary<string, object> body, CancellationToken cancellationToken, TimeSpan timeout)
    {
        using var response = await PostAsync(path, body, HttpCompletionOption.ResponseContentRead, cancellationToken, timeout);
        using var document = await ReadJsonAsync(response, cancellationToken);
        return document.RootElement.Clone();
    }

    internal async IAsyncEnumerable<JsonElement> StreamJsonAsync(string path, Dictionary<string, object> body,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await PostAsync(path, body, HttpCompletionOption.ResponseHeadersRead, cancellationToken, null);
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
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("Stream event was not a JSON object.");
            yield return document.RootElement.Clone();
        }
    }
}
