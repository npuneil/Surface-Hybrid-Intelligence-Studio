using System.Globalization;
using System.Text.RegularExpressions;
using Forge.Core.Harness;
using SurfaceDeviceKit;
using SurfaceDeviceKit.FoundryLocal;

namespace Forge.Engine;

/// <summary>The model a deck is drafted with, as the story needs it. Built from the model that is loaded, never from a plan.</summary>
public sealed record ModelFacts(string Title, string Lane, string Device, string EpLabel, string Class, double ParamsB, double FileGb, int ContextTokens)
{
    /// <summary>The facts of a resolved model. <paramref name="window"/> is the window found after the model was loaded, which wins over what the catalog said.</summary>
    public static ModelFacts From(ResolvedModel model, long? window = null) => new(
        model.Title, model.Lane, model.Device, model.EpLabel, model.Class, model.ParamsB, model.FileGb,
        (int)Math.Clamp(window is > 0 ? window.Value : model.ContextTokens ?? 0, 0, int.MaxValue));
}

/// <summary>What was measured on this PC for this model. Nothing here is a claim: a zero or false value leaves the matching sentence out.</summary>
public sealed record StoryMeasurements(double DecodeTokensPerSecond = 0, bool GuidanceVerified = false);

/// <summary>Writes the <see cref="DeviceStory"/> for a deck: what the deck may say about the PC it is made on. Every sentence comes from
/// the detected hardware, the model that is loaded, or a measurement, and the names are cleaned up the way a person would say them
/// ("Surface Laptop, 7th Edition" becomes "Surface Laptop 7", "Snapdragon(R) X Plus - X1P64100 - Qualcomm(R) Oryon(TM) CPU" becomes
/// "Snapdragon X Plus"). Never mentions TOPS or Copilot+: the deck guardrails forbid both.</summary>
public static class StoryBuilder
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex Trademarks = new(@"\((?:R|TM)\)|[®™]", Opt);
    private static readonly Regex Edition = new(@"^(?<base>.+?),?\s+(?<n>\d{1,2})(?:st|nd|rd|th)\s+Edition$", Opt);
    private static readonly Regex Placeholder = new(
        @"^(?:system product name|to be filled.*|default string|type1productconfigid|oem|not applicable|not specified|unknown|system version|invalid|n/?a|standard pc.*|virtual machine|product name|none)$", Opt);
    private static readonly Regex Snapdragon = new(@"Snapdragon\s+(?<series>X2?)(?:\s+(?<tier>Elite\s+Extreme|Elite|Plus))?\b", Opt);
    private static readonly Regex GenPrefix = new(@"^\d{1,2}(?:st|nd|rd|th)\s+Gen\s+", Opt);
    private static readonly Regex ClockSpeed = new(@"@\s*[\d.]+\s*GHz", Opt);
    private static readonly Regex GraphicsSuffix = new(@"\s+(?:with|w/)\s+(?:AMD\s+)?(?:Radeon|Graphics|Adreno|Arc|Iris|Intel).*$", Opt);
    private static readonly Regex NoiseWords = new(@"\b(?:CPU|Processor)\b", Opt);

    // ---- Names ----

    /// <summary>The product as a person says it. Generic OEM placeholders and virtual machines become "this PC".</summary>
    public static string Name(DeviceProfile device)
    {
        string product = Squash(device.Product);
        if (product.Length == 0 || Placeholder.IsMatch(product)) return "this PC";
        var edition = Edition.Match(product);
        if (edition.Success) product = $"{edition.Groups["base"].Value.Trim().TrimEnd(',')} {edition.Groups["n"].Value}";
        if (device.IsSurface) return product;
        string maker = Maker(device.Manufacturer);
        return maker.Length == 0 || product.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? product : $"{maker} {product}";
    }

    /// <summary>The short brand of a PC maker ("Dell Inc." becomes "Dell"), or empty when the firmware gave a placeholder.</summary>
    public static string Maker(string manufacturer)
    {
        string text = Squash(manufacturer);
        if (text.Length == 0 || Placeholder.IsMatch(text) || Regex.IsMatch(text, @"^(?:system manufacturer|to be filled|default|oem|microsoft)", Opt)) return "";
        if (Regex.IsMatch(text, @"^asus", Opt)) return "ASUS";
        if (Regex.IsMatch(text, @"^(?:hp\b|hewlett)", Opt)) return "HP";
        if (Regex.IsMatch(text, @"^(?:msi\b|micro-star)", Opt)) return "MSI";
        if (Regex.IsMatch(text, @"^lg\b", Opt)) return "LG";
        string first = Regex.Replace(text.Split(' ', ',')[0], @"[^\p{L}\p{N}-]", "");
        if (first.Length == 0) return "";
        return first.Length > 3 && first == first.ToUpperInvariant()
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(first.ToLowerInvariant())
            : first;
    }

    /// <summary>The processor family as a person says it: "Snapdragon X Plus", "Intel Core Ultra 7 268V", "AMD Ryzen 7 4980U", "NVIDIA RTX Spark".</summary>
    public static string Silicon(DeviceProfile device)
    {
        string cpu = Squash(Trademarks.Replace(device.Cpu.Name, ""));
        if (cpu.Length == 0) return "";
        if (Regex.IsMatch(cpu, @"NVIDIA.*(?:N1X|RTX Spark)", Opt)) return "NVIDIA RTX Spark";
        var snapdragon = Snapdragon.Match(cpu);
        if (snapdragon.Success)
        {
            string tier = Squash(snapdragon.Groups["tier"].Value);
            return $"Snapdragon {snapdragon.Groups["series"].Value.ToUpperInvariant()}{(tier.Length > 0 ? " " + CultureInfo.InvariantCulture.TextInfo.ToTitleCase(tier.ToLowerInvariant()) : "")}";
        }
        cpu = ClockSpeed.Replace(cpu, "");
        cpu = GenPrefix.Replace(cpu, "");
        cpu = GraphicsSuffix.Replace(cpu, "");
        cpu = NoiseWords.Replace(cpu, "");
        return Squash(cpu);
    }

    /// <summary>The engine that runs the model, as a short label for diagrams (about 15 characters at most).</summary>
    public static string Engine(DeviceProfile device, ModelFacts model)
    {
        var lane = device.Lane(model.Lane);
        switch (model.Lane)
        {
            case "npu":
                string vendor = (lane is { Vendor.Length: > 0 } ? lane.Vendor : device.Npus.FirstOrDefault()?.Vendor ?? device.Cpu.Vendor).ToLowerInvariant();
                return vendor switch { "qualcomm" => "Snapdragon NPU", "intel" => "Intel NPU", "amd" => "AMD NPU", _ => "NPU" };
            case "gpu":
                var gpu = (lane is null ? null : device.Gpus.FirstOrDefault(g => g.Name == lane.Device)) ?? device.Gpus.FirstOrDefault(g => lane is not null && g.Vendor == lane.Vendor)
                    ?? device.Gpus.FirstOrDefault();
                return gpu?.FamilyId switch
                {
                    "nvidia-n1x" => "RTX Spark GPU",
                    "nvidia-rtx" => "RTX GPU",
                    "nvidia-gtx" => "NVIDIA GPU",
                    "intel-arc" => gpu.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase) ? "Intel Arc GPU" : "Intel GPU",
                    "amd-radeon" => "Radeon GPU",
                    "adreno" => "Adreno GPU",
                    _ => gpu?.Vendor switch { "nvidia" => "NVIDIA GPU", "intel" => "Intel GPU", "amd" => "Radeon GPU", "qualcomm" => "Adreno GPU", _ => "GPU" },
                };
            default:
                return "CPU";
        }
    }

    public static string Runtime(ModelFacts model) =>
        model.Lane == "cpu" || model.EpLabel is "CPU" or "unknown" or ""
            ? "Foundry Local on the CPU"
            : $"Foundry Local with the {model.EpLabel} execution provider";

    /// <summary>What the provenance slide calls this device: "Laptop", "Surface Pro", "Surface Go" or "PC".</summary>
    public static string Noun(string name) =>
        Regex.IsMatch(name, @"^Surface (?:Laptop|Book)", Opt) ? "Laptop"
        : Regex.IsMatch(name, @"^Surface Pro", Opt) ? "Surface Pro"
        : Regex.IsMatch(name, @"^Surface Go", Opt) ? "Surface Go"
        : "PC";

    // ---- The story ----

    public static DeviceStory Build(DeviceProfile device, ModelFacts model, StoryMeasurements? measured = null)
    {
        measured ??= new StoryMeasurements();
        string name = Name(device);
        string silicon = Silicon(device);
        string engine = Engine(device, model);
        string runtime = Runtime(model);
        bool unified = device.Cpu.UnifiedMemory;
        double memory = device.Memory.Tier > 0 ? device.Memory.Tier : Math.Round(device.Memory.InstalledGb);
        string memoryPhrase = memory <= 0 ? "" : unified ? $"{memory:0} GB of unified memory" : $"{memory:0} GB installed memory";

        string deviceFact = $"Device: {name}{(silicon.Length > 0 ? " with " + silicon : "")}{(memoryPhrase.Length > 0 ? (silicon.Length > 0 ? " and " : " with ") + memoryPhrase : "")}.";
        var facts = new List<string> { deviceFact, $"The deck is drafted by {model.Title} on the {engine}." };

        var technical = new List<string> { $"Runtime: {runtime}." };
        if (model.FileGb > 0)
        {
            string window = model.ContextTokens > 0 ? $" with a {model.ContextTokens.ToString("N0", CultureInfo.InvariantCulture)}-token context window" : "";
            technical.Add($"{model.Title} is a {model.FileGb:0.0} GB model file{window}.");
        }
        if (unified && memory > 0) technical.Add($"The CPU and GPU share one {memory:0} GB memory pool, so every memory view describes the same memory.");
        if (device.Memory is { InstalledGb: > 0, VisibleGb: > 0 } views && views.VisibleGb < views.InstalledGb)
            technical.Add($"Windows shows {views.VisibleGb:0.#} GB of the {Math.Round(views.InstalledGb):0} GB installed as visible memory.");
        if (device.Lane(model.Lane) is { Available: true, BudgetGb: > 0 } budget)
            technical.Add($"The memory budget for AI models on the {engine} is up to {budget.BudgetGb:0.#} GB.");
        if (measured.DecodeTokensPerSecond >= 1)
            technical.Add($"Measured on this PC: {model.Title} decodes at about {measured.DecodeTokensPerSecond:0} tokens per second on the {engine}.");
        if (measured.GuidanceVerified) technical.Add("Constrained decoding keeps every slide's JSON valid, so the model cannot return a broken slide.");

        return new DeviceStory
        {
            Name = name,
            Silicon = silicon,
            Engine = engine,
            Lane = model.Lane,
            Runtime = runtime,
            Model = model.Title,
            UnifiedMemory = unified,
            Facts = facts,
            TechnicalFacts = technical,
            Keywords = Keywords(name, silicon, engine, model.EpLabel),
            MeasuredTokensPerSecond = measured.DecodeTokensPerSecond,
            Noun = Noun(name),
        };
    }

    /// <summary>Words that show a slide covers the device. Short words are left out: the topic check matches by substring, so "x" would match every slide.</summary>
    private static List<string> Keywords(string name, string silicon, string engine, string epLabel)
    {
        var words = new List<string>();
        void Add(string word)
        {
            word = word.Trim().ToLowerInvariant();
            if (word.Length >= 3 && !words.Contains(word)) words.Add(word);
        }
        if (name != "this PC") { Add(name); foreach (string part in name.Split(' ')) Add(part); }
        if (silicon.Length > 0) { Add(silicon); Add(silicon.Split(' ')[0]); }
        Add(engine);
        Add(engine.Split(' ')[^1]);
        Add("foundry local");
        if (epLabel is not ("CPU" or "unknown" or "")) Add(epLabel);
        return words;
    }

    private static string Squash(string? text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();
}
