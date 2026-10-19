using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

internal sealed partial class MainWindow
{
    private readonly StackPanel _industryGallery = Id(Stack(16), "IndustryGallery");
    private readonly InfoBar _industryInfo = Id(new InfoBar { IsClosable = true }, "IndustryInfo");

    private FrameworkElement BuildIndustries()
    {
        _industryGallery.Children.Add(Body("Connect to the local service to load the industry scenarios."));
        var forge = Id(Primary("Open PPTX Offline Generator"), "IndustryOpenForge");
        forge.Click += (_, _) => OpenPptxForge();
        return Page(Stack(22,
            Stack(10, Eyebrow("Industry scenarios"), H1("Make local AI relevant to their work."),
                Body("Four fictional industry workflows turn supplied notes into reviewable drafts on this laptop's RTX GPU. "
                    + "Run a workflow, then prepare a source-grounded presentation brief for your offline PowerPoint generator.")),
            CardBorder(Stack(12,
                Row(10, Glyph("\uE8A5", 22, Accent), H2("PPTX Offline Generator - Offline PPTX Forge")),
                Body("Your companion WinUI app uses a local Qwen3-Coder-30B-A3B model through llama.cpp to draft a deck specification, "
                    + "then renders editable PowerPoint slides with python-pptx. It is a separate application and runtime, not the studio's Foundry Local model."),
                Small("Prepare deck in Forge saves the fictional brief into Forge's custom briefs and opens the generator. "
                    + "Select the named brief, review its facts and template, then choose Build deck. No deck is generated until you do. "
                    + "Stage Forge's model and dependencies before disconnected use; review every slide before sharing."),
                forge), 22, AccentTint),
            _industryInfo, _industryGallery));
    }

    private void PopulateIndustryScenarios()
    {
        _industryGallery.Children.Clear();
        var cards = new AdaptiveGrid(360, 2, 18);
        foreach (var scenario in _scenarios.Values.Where(s => s.Industry is not null).OrderBy(s => s.Industry))
        {
            var run = Id(Primary("Run live workflow"), "IndustryRun_" + scenario.Key);
            run.MinHeight = 40;
            run.Click += async (_, _) => await LaunchIndustryAsync(scenario.Key);
            var prepare = Id(Secondary("Prepare deck in Forge"), "IndustryForge_" + scenario.Key);
            prepare.MinHeight = 40;
            prepare.Click += (_, _) => OpenPptxForge(scenario);
            var export = Id(new HyperlinkButton { Content = "Export deck brief (JSON)", Padding = new Thickness(0), Foreground = Accent }, "IndustryExport_" + scenario.Key);
            export.Click += (_, _) => ExportIndustryBrief(scenario);
            var notes = Id(new Expander
            {
                Header = "Review synthetic source notes",
                Content = Body(scenario.Source, Ink),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            }, "IndustryNotes_" + scenario.Key);
            cards.With(Id(CardBorder(Stack(12,
                Eyebrow(scenario.Industry!), H2(scenario.Title),
                Text(scenario.Client + " - fictional organization", 13, Muted, FontWeights.SemiBold),
                Body(scenario.DeckObjective ?? ""),
                Small("Text-only drafting from supplied synthetic notes. No business-system connection, real records or automated decisions."),
                notes, run, prepare, export), 22), "IndustryCard_" + scenario.Key));
        }
        if (cards.Children.Count == 0)
            _industryGallery.Children.Add(Body("No industry scenarios were provided by the local service. Restart it with the updated studio files."));
        else _industryGallery.Children.Add(cards);
    }

    private async Task LaunchIndustryAsync(string key)
    {
        if (_running || _demoRunning || _switching || _labRunning)
        {
            ShowIndustryInfo("Workflow cannot start", "Another run or model switch is in progress. Wait for it to finish.", InfoBarSeverity.Warning);
            return;
        }
        if (!SelectScenario(key))
        {
            ShowIndustryInfo("Workflow unavailable", "The local service has not provided this workflow. Reconnect and try again.", InfoBarSeverity.Error);
            return;
        }
        Navigate("live");
        if (!_runLive.IsEnabled)
        {
            ShowRunError("Workflow cannot start", _status?.Engine.Detail ?? "The local GPU service is unavailable.");
            return;
        }
        await RunScenarioAsync("live");
    }

    private void ExportIndustryBrief(Scenario scenario)
    {
        try
        {
            string path = PptxForge.SaveBrief(scenario, Path.Combine(ExportDirectory, "forge-briefs"));
            ShowIndustryInfo("Deck brief exported", path + " - In Forge choose Open another brief (.json)..., or pass this file to forge.ps1 build. No deck has been generated.", InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowIndustryInfo("Deck brief export failed", exception.Message, InfoBarSeverity.Error);
        }
    }

    private void OpenPptxForge(Scenario? scenario = null)
    {
        try
        {
            string root = PptxForge.ResolveRoot(_options.Root);
            string exe = PptxForge.FindExecutable(root);
            string? path = scenario is null ? null : PptxForge.SaveBrief(scenario, Path.Combine(root, "briefs", "custom"));
            if (PptxForge.IsRunning())
            {
                ShowIndustryInfo("PPTX Offline Generator is already open", scenario is null
                    ? "Switch to the Offline PPTX Forge window to select or open a brief."
                    : $"Brief saved: {path}. Forge lists briefs when it starts, so in the open Forge window choose Open another brief (.json)... and pick this file, "
                      + "review the facts and template, then choose Build deck. No deck has been generated yet.",
                    InfoBarSeverity.Informational);
                return;
            }
            PptxForge.Launch(exe);
            ShowIndustryInfo("PPTX Offline Generator opened", scenario is null
                ? "Offline PPTX Forge is a separate local application. Select or open a brief there, then build your deck."
                : $"Brief saved: {path}. In Forge's Pre-staged mode select {scenario.Client} - sales (custom), review the facts and template, then choose Build deck. No deck has been generated yet.",
                InfoBarSeverity.Informational);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            ShowIndustryInfo("PPTX Offline Generator could not open", exception.Message, InfoBarSeverity.Error);
        }
    }

    private void ShowIndustryInfo(string title, string message, InfoBarSeverity severity)
    {
        _industryInfo.Title = title;
        _industryInfo.Message = message;
        _industryInfo.Severity = severity;
        _industryInfo.IsOpen = true;
        _industryInfo.StartBringIntoView();
    }
}
