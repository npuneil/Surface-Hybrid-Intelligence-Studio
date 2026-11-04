namespace HybridIntelligenceStudio;

/// <summary>Device detection calls: what this PC is and can run, and the preview of another Surface.</summary>
internal sealed partial class BackendClient
{
    /// <summary>The device kit's report for the PC shown, its model lineup and how each demo fits it.</summary>
    internal async Task<DeviceReport> GetDeviceAsync(CancellationToken cancellationToken)
    {
        // The first call can run detection probes and the model catalog lookup, so it gets more time than a status poll.
        var root = await GetJsonAsync("/api/device", cancellationToken, TimeSpan.FromSeconds(20));
        return DeviceReport.Parse(root);
    }

    /// <summary>Shows another Surface from the catalog (live runs pause while it is shown); null goes back to this PC.</summary>
    internal async Task<DeviceReport> SimulateDeviceAsync(string? deviceId, CancellationToken cancellationToken)
    {
        // A null id is sent as JSON null: it means "this PC".
        var body = new Dictionary<string, object> { ["id"] = deviceId! };
        using var response = await PostAsync("/api/device/simulate", body, HttpCompletionOption.ResponseContentRead, cancellationToken,
            TimeSpan.FromSeconds(30));
        using var document = await ReadJsonAsync(response, cancellationToken);
        return DeviceReport.Parse(document.RootElement);
    }
}
