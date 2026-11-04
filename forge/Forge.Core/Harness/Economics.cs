using System.Text.Json.Nodes;

namespace Forge.Core.Harness;

/// <summary>Cost, energy, carbon and water for local against cloud generation. The constants are the Python Forge's published
/// assumptions and travel in every metrics file, so a reader can see what a number rests on. Local energy is only ever a measurement:
/// a device without a power meter reports "not measured" and no cost or carbon is derived from it.</summary>
public static class Economics
{
    public const double ElectricityUsdPerKwh = 0.13;
    public const double GridKgCo2ePerKwh = 0.367;
    public const double PlatformOverheadW = 10.0;
    public const double CloudWhPer1kTokens = 1.1;
    public const double CloudMlWaterPer1kTokens = 3.6;
    public const double LocalMlWaterPer1kTokens = 0.0;

    public static IReadOnlyDictionary<string, (double In, double Out)> CloudPrices { get; } = new Dictionary<string, (double, double)>
    {
        ["claude-opus-5.5"] = (4.0, 20.0),
        ["gpt-6-astra"] = (10.0, 50.0),
    };

    public static JsonObject Assumptions() => new()
    {
        ["electricity_usd_per_kwh"] = ElectricityUsdPerKwh,
        ["grid_kgco2e_per_kwh"] = GridKgCo2ePerKwh,
        ["platform_overhead_w"] = PlatformOverheadW,
        ["cloud_wh_per_1k_tokens"] = CloudWhPer1kTokens,
        ["cloud_ml_water_per_1k_tokens"] = CloudMlWaterPer1kTokens,
        ["local_ml_water_per_1k_tokens"] = LocalMlWaterPer1kTokens,
        ["pricing_usd_per_mtok"] = new JsonObject(CloudPrices.Select(kv => KeyValuePair.Create(kv.Key,
            (JsonNode?)new JsonObject { ["in"] = kv.Value.In, ["out"] = kv.Value.Out }))),
    };

    /// <summary>The local lane. With no measured energy there is no cost or carbon figure either.</summary>
    public static JsonObject Local(EnergyReading energy, int promptTokens, int completionTokens)
    {
        var row = new JsonObject
        {
            ["lane"] = "local",
            ["water_ml"] = Math.Round((promptTokens + completionTokens) / 1000.0 * LocalMlWaterPer1kTokens, 2),
        };
        if (!energy.Measured)
        {
            row["energy_wh"] = null;
            row["cost_usd"] = null;
            row["co2e_g"] = null;
            row["basis"] = "energy not measured on this device: " + energy.Basis;
            return row;
        }
        double kwh = energy.Wh / 1000.0;
        row["energy_wh"] = Math.Round(energy.Wh, 3);
        row["cost_usd"] = Math.Round(kwh * ElectricityUsdPerKwh, 6);
        row["co2e_g"] = Math.Round(kwh * GridKgCo2ePerKwh * 1000.0, 3);
        row["basis"] = energy.Basis + "; electricity only";
        return row;
    }

    /// <summary>What the same token counts would cost at a cloud model's list price (an estimate from published assumptions).</summary>
    public static JsonObject Cloud(string model, int inputTokens, int outputTokens)
    {
        var (priceIn, priceOut) = CloudPrices[model];
        double wh = outputTokens / 1000.0 * CloudWhPer1kTokens;
        return new JsonObject
        {
            ["lane"] = "cloud",
            ["model"] = model,
            ["energy_wh"] = Math.Round(wh, 3),
            ["cost_usd"] = Math.Round(inputTokens / 1e6 * priceIn + outputTokens / 1e6 * priceOut, 6),
            ["co2e_g"] = Math.Round(wh / 1000.0 * GridKgCo2ePerKwh * 1000.0, 3),
            ["water_ml"] = Math.Round(outputTokens / 1000.0 * CloudMlWaterPer1kTokens, 2),
            ["basis"] = "API list price x tokens; energy and water are a published per-1k-output-token estimate",
        };
    }
}
