using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

// Three-tier illustrative banking reference architecture, with each tier proved by an existing live demo.
internal sealed partial class MainWindow
{
    private FrameworkElement BuildArchitecture()
    {
        var tier1 = TierCard("Tier 1", "Smart capture", "Banker's Surface at the client meeting",
            ["Capture the conversation with consent", "Transcribe locally", "Draft call notes on the device", "Sync securely inside the firm"],
            "Run client meeting capture", "ArchTier1", () => { if (!OpenDemo("meeting")) ShowServiceError("Live demo unavailable", _demoCatalogError ?? "The local service has not provided this demo."); },
            Card, Accent);
        var tier2 = TierCard("Tier 2", "In-firm drafting", "Local AI beside the firm's systems of record (illustrative)",
            ["Apply policies and procedures", "Draft checklists and next steps", "Keep client records inside the firm"],
            "Draft a KYC refresh checklist", "ArchTier2", () => _ = LaunchIndustryAsync("kyc-refresh"),
            Card, Accent);
        var escalate = TierCard("Escalate only when needed", "Algorithmic delegation", "Runs inside the firm",
            ["Remove NPI and account numbers", "Minimize context", "Route by rule, with every reason shown"],
            "Route a complex question", "ArchEscalate", () => OpenArchitecturePreset("complex-case"),
            AmberTint, AmberInk);
        var tier3 = TierCard("Tier 3  |  External cloud", "Cloud support", "Azure OpenAI frontier reasoning for complex questions",
            ["Receives de-identified queries only", "Returns model support", "Never sees recordings, account data or NPI"],
            "Preview the cloud payload", "ArchTier3", () => { if (!OpenDemo("redact")) ShowServiceError("Live demo unavailable", _demoCatalogError ?? "The local service has not provided this demo."); },
            SlateTint, Slate);

        var flow = Id(CardBorder(Stack(8,
            Set(Text("FLOW", 12, Accent, FontWeights.SemiBold), t => t.CharacterSpacing = 80),
            Body("Client meeting  \u2192  Tier 1 capture and local transcript  \u2192  Tier 2 drafting against firm policy  \u2192  draft notes + next steps  \u2192  banker review and approval.", Ink),
            Body("Only when a question is complex: remove NPI and minimize context  \u2192  de-identified query to Tier 3  \u2192  model support returns to Tier 2. Every client decision stays with the firm's people.", Ink)), 18), "ArchFlow");

        var review = Id(CardBorder(Lead(12, Glyph("\uE8FB", 22, GreenInk), Stack(6,
            Set(H3("Draft notes + checklists  \u2192  banker review and approval"), t => t.Foreground = GreenInk),
            Body("AI drafts; bankers decide. No credit, account, dispute, fraud or investment decision is made or released without an authorized banker.", Ink))), 18, GreenTint), "ArchReview");

        var sensitive = Id(CardBorder(Lead(12, Glyph("\uE72E", 22, AccentDeep), Stack(6,
            H3("Client data stay in the firm"),
            Body("Recordings, account data and nonpublic personal information (NPI) remain inside the firm. Only de-identified queries go out; only model support comes back.", Ink))), 18, AccentTint), "ArchSensitive");

        var mapping = Id(Small("Illustrative reference architecture, not any bank's actual design. In this showcase the Surface Laptop Ultra stands in for Tier 1 capture and Tier 2 local AI, using synthetic clients only. "
            + "Tier 3 is shown as a NOT SENT payload preview: the studio never calls a cloud service, and it does not connect to core banking, CRM, payments or any system of record. "
            + "De-identification removes structured identifiers only; it is not certified and makes no regulatory or compliance determination."), "ArchMapping");

        return Id(Stack(14,
            Eyebrow("Industry example  |  Banking reference architecture"),
            H2("Three tiers. Client data stay in the firm. The cloud only when needed."),
            new AdaptiveGrid(250, 4).With(tier1, tier2, escalate, tier3),
            flow,
            new AdaptiveGrid(320, 2).With(review, sensitive),
            mapping), "OverviewArchitecture");
    }

    private Border TierCard(string tier, string title, string where, string[] points, string action, string id, Action onClick,
        Microsoft.UI.Xaml.Media.SolidColorBrush background, Microsoft.UI.Xaml.Media.SolidColorBrush ink)
    {
        var go = Id(Secondary(action + "  \u2192"), id + "Run");
        go.HorizontalAlignment = HorizontalAlignment.Left;
        go.Click += (_, _) => onClick();
        var list = Stack(4);
        foreach (string point in points) list.Children.Add(Body("- " + point, Ink));
        return Id(CardBorder(Stack(8,
            Set(Text(tier.ToUpperInvariant(), 12, ink, FontWeights.SemiBold), t => t.CharacterSpacing = 80),
            H3(title), Small(where), list, go), 20, background), id);
    }

    private void OpenArchitecturePreset(string presetId)
    {
        Navigate("hybrid");
        if (_hybridCatalog is { } catalog && Items(catalog, "presets").FirstOrDefault(p => Str(p, "id") == presetId) is { ValueKind: JsonValueKind.Object } preset)
            _ = ApplyHybridPresetAsync(preset);
    }
}
