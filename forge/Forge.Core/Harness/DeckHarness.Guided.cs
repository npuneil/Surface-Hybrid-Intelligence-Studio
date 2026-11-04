using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Forge.Core.Spec;

namespace Forge.Core.Harness;

public sealed partial class DeckHarness
{
    private const int AttemptsPerSlot = 2;
    private const int MaxCallFailures = 3;

    /// <summary>The canvas diagrams as the model described them. A repair shows the model its own description, never the drawn elements.</summary>
    private readonly Dictionary<int, JsonObject> _descriptions = [];
    /// <summary>Slides the model could not write, drawn as bare placeholders until a repair pass replaces them.</summary>
    private readonly HashSet<int> _placeholders = [];
    private readonly Dictionary<string, int> _topicPlan = new(StringComparer.Ordinal);
    private readonly HashSet<(string Topic, int Slide)> _topicTried = [];
    /// <summary>Slides already rewritten for exactly this feedback; asking again would get the same answer.</summary>
    private readonly HashSet<(int Slide, string Feedback)> _attempted = [];
    private int _lenientReplies;
    private int _callFailures;

    private sealed record SlotWrite(JsonObject Slide, JsonObject? Description);

    private static readonly Regex TopicName = new(@"topic '([^']+)'", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ---- Planning: which slide carries which required topic ----

    private IEnumerable<string> RequiredTopics => _brief.RequiredTopics.Keys.Where(t => t != BriefAdapter.ProvenanceTopic);

    private string Describe(string topic) =>
        $"{topic.Replace('_', ' ')}: {string.Join(", ", _brief.RequiredTopics[topic].Take(4))}";

    /// <summary>Slide indexes for a topic, best fit first: how many of the topic's words the slot's name, layout and intent share.
    /// The title slide never carries a topic.</summary>
    private List<int> SlidesFor(string topic)
    {
        var wanted = Tokens(topic.Replace('_', ' ') + " " + string.Join(" ", _brief.RequiredTopics[topic]));
        return [.. Enumerable.Range(1, Math.Max(0, _arc.Count - 1))
            .Select(i => (Index: i, Score: wanted.Count(Tokens(_arc[i].Slot.Replace('_', ' ') + " " + _arc[i].Layout + " " + _arc[i].Intent).Contains)))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Index).Select(x => x.Index)];
    }

    /// <summary>Gives every required topic a slide to appear on, at most two per slide, preferring the slide whose purpose matches it.</summary>
    private void PlanTopics()
    {
        var load = new int[_arc.Count];
        var wantedBy = RequiredTopics.ToDictionary(t => t, t => Tokens(t.Replace('_', ' ') + " " + string.Join(" ", _brief.RequiredTopics[t])), StringComparer.Ordinal);
        foreach (string topic in RequiredTopics)
        {
            var ranked = SlidesFor(topic);
            if (ranked.Count == 0) continue;
            int best = ranked.FirstOrDefault(i => load[i] < 2 && Tokens(_arc[i].Slot.Replace('_', ' ') + " " + _arc[i].Layout + " " + _arc[i].Intent).Overlaps(wantedBy[topic]));
            // No slot is about this topic: use the least loaded content slide (not the closing call to action while another is free).
            if (best == 0)
            {
                var spread = ranked.Where(i => i < _arc.Count - 1 || ranked.Count == 1).ToList();
                best = (spread.Count > 0 ? spread : ranked).OrderBy(i => load[i]).ThenBy(i => i).First();
            }
            _topicPlan[topic] = best;
            load[best]++;
        }
    }

    // ---- Drafting ----

    private async Task<Draft> GuidedAsync()
    {
        int total = _arc.Count;
        var slides = new List<JsonObject>();
        var raw = new StringBuilder();
        PlanTopics();
        var pending = _topicPlan.Keys.ToList();

        for (int i = 0; i < total; i++)
        {
            var slot = _arc[i];
            SetStep("draft", i + 1, total, $"Slide {i + 1} of {total}: {SlideTidy.PrettySlot(slot.Slot)}");
            var due = i == total - 1 ? pending : [.. pending.Where(t => _topicPlan[t] <= i)];
            var write = await WriteSlotAsync(i, Titles(slides, -1), [.. due.Take(3).Select(Describe)], null, null, raw).ConfigureAwait(false);
            if (write is null)
            {
                _warnings.Add($"The model could not write slide {i + 1} ({SlideTidy.PrettySlot(slot.Slot)}); a placeholder was used and repaired afterwards.");
                _placeholders.Add(i);
                slides.Add(SlideTidy.Skeleton(slot, _story));
                continue;
            }
            slides.Add(write.Slide);
            if (write.Description is not null) _descriptions[i] = write.Description;
            string text = SlideText(write.Slide);
            pending.RemoveAll(t => Mentions(text, _brief.RequiredTopics[t]));
        }

        var spec = new JsonObject { ["client"] = _brief.Client, ["slides"] = new JsonArray([.. slides.Select(s => (JsonNode?)s)]) };
        if (slides.Count > 0 && SlideTidy.TextOf(slides[0], "title") is { Length: > 0 } deckTitle) spec["title"] = deckTitle;
        spec = SpecJson.Normalize(spec, _brief);

        var (draftIssues, _) = Evaluate(spec, true);
        spec = await RepairGuidedAsync(spec, draftIssues, raw).ConfigureAwait(false);
        return new Draft(spec, raw.ToString(), _lenientReplies == 0, true, HarnessMode.Guided, draftIssues);
    }

    private static List<string> Titles(IReadOnlyList<JsonObject> slides, int except) =>
        [.. slides.Select((s, i) => (Title: SlideTidy.TextOf(s, "title"), Index: i)).Where(x => x.Index != except && x.Title.Length > 0).Select(x => x.Title)];

    /// <summary>Asks for one slide, up to twice. A reply that cannot become a slide is shown back to the model with what was wrong.</summary>
    private async Task<SlotWrite?> WriteSlotAsync(int index, IReadOnlyList<string> deckSoFar, IReadOnlyList<string> topics,
        string? previous, IReadOnlyList<string>? issues, StringBuilder raw)
    {
        var slot = _arc[index];
        for (int attempt = 1; attempt <= AttemptsPerSlot; attempt++)
        {
            LlmReply reply;
            try
            {
                string? shown = previous;
                var shownIssues = issues;
                reply = await ChatAsync(level => Prompts.Slot(new SlotPrompt(_brief, _story, _arc, index, deckSoFar, topics, level, GuidedDecoding, shown, shownIssues))).ConfigureAwait(false);
                _callFailures = 0;
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or HarnessException))
            {
                if (++_callFailures >= MaxCallFailures) throw new HarnessException("The model stopped answering: " + exception.Message, exception);
                _stats.Retries++;
                continue;
            }

            raw.AppendLine(reply.Text.Trim());
            var parsed = SpecJson.Parse(reply.Text);
            string error;
            if (parsed.Spec is null) error = "the reply was not a JSON object";
            else if (SlideTidy.TryBuild(parsed.Spec, slot, _story, out var slide, out var description, out error))
            {
                if (!parsed.Strict) _lenientReplies++;
                return new SlotWrite(slide, description);
            }
            _stats.Retries++;
            previous = Prompts.Clip(reply.Text.Trim(), 1500);
            issues = [$"{char.ToUpperInvariant(error[0])}{error[1..]}. Return one complete JSON object for this slide."];
        }
        return null;
    }

    // ---- Repair ----

    /// <summary>What one slide is asked to fix. <see cref="Worth"/> is false when every finding is a note (a few words over, a shrunk
    /// label): a model call would rarely fix those, so the slide is only rewritten when something else is wrong with it too.</summary>
    private sealed class RepairTarget
    {
        public List<string> Messages { get; } = [];
        public bool Hard { get; set; }
        public bool Worth { get; set; }
        public string Feedback => string.Join("\n", Messages);
    }

    /// <summary>Which slide each finding is sent to. A deck-level topic finding goes to the slide that fits the topic best and has not
    /// been tried for it yet; findings about the deck's size or analogy count cannot be fixed one slide at a time and are only reported.
    /// A slide the model could not write is always worth a call.</summary>
    private Dictionary<int, RepairTarget> Targets(JsonObject spec, IReadOnlyList<LintIssue> issues)
    {
        var targets = new Dictionary<int, RepairTarget>();
        int count = SpecJson.SlideObjects(spec).Count;
        RepairTarget Of(int slide)
        {
            if (!targets.TryGetValue(slide, out var target)) targets[slide] = target = new RepairTarget();
            return target;
        }
        void Add(int slide, LintIssue issue)
        {
            var target = Of(slide);
            if (!target.Messages.Contains(issue.Msg)) target.Messages.Add(issue.Msg);
            var severity = Lint.SeverityOf(issue.Code);
            target.Hard |= severity == LintSeverity.Hard;
            target.Worth |= severity != LintSeverity.Note;
        }
        foreach (var issue in issues)
        {
            if (issue.Slide >= 0 && issue.Slide < count) { Add(issue.Slide, issue); continue; }
            if (issue.Code != "topic") continue;
            var match = TopicName.Match(issue.Msg);
            if (!match.Success || !_brief.RequiredTopics.ContainsKey(match.Groups[1].Value)) continue;
            string topic = match.Groups[1].Value;
            int slide = SlidesFor(topic).FirstOrDefault(i => i < count && !_topicTried.Contains((topic, i)), -1);
            if (slide < 0) continue;
            _topicTried.Add((topic, slide));
            Add(slide, issue);
        }
        foreach (int index in _placeholders.Where(i => i < count))
        {
            var target = Of(index);
            target.Hard = target.Worth = true;
        }
        return targets;
    }

    /// <summary>Topics that are visible on this slide and nowhere else, so a rewrite must keep them.</summary>
    private List<string> KeepTopics(JsonObject spec, int index)
    {
        var texts = SpecJson.SlideObjects(spec).Select(s => SlideText(s)).ToList();
        var keep = new List<string>();
        foreach (string topic in RequiredTopics)
        {
            var words = _brief.RequiredTopics[topic];
            if (index < texts.Count && Mentions(texts[index], words) && !texts.Where((_, j) => j != index).Any(t => Mentions(t, words))) keep.Add(Describe(topic));
        }
        return keep;
    }

    /// <summary>Regenerates the slides the lint flagged, a few per pass, the ones with something wrong first. A new slide is kept only when
    /// the deck's findings weigh less than before (see <see cref="Lint.Cost"/>), so a repair can never make the deck worse. A slide is not
    /// asked again with the same feedback after a rewrite that did not help: the model would answer the same way.</summary>
    private async Task<JsonObject> RepairGuidedAsync(JsonObject spec, List<LintIssue> issues, StringBuilder raw)
    {
        for (int pass = 1; pass <= _options.MaxRepairPasses; pass++)
        {
            var batch = Targets(spec, issues)
                .Where(t => t.Value.Worth && (_placeholders.Contains(t.Key) || !_attempted.Contains((t.Key, t.Value.Feedback))))
                .OrderByDescending(t => t.Value.Hard).ThenByDescending(t => t.Value.Messages.Count).ThenBy(t => t.Key)
                .Take(Math.Max(1, _options.MaxRepairsPerPass)).OrderBy(t => t.Key).ToList();
            if (batch.Count == 0) break;
            int before = issues.Count, step = 0;
            var fixedSlides = new List<int>();

            foreach (var (index, target) in batch)
            {
                SetStep("repair", ++step, batch.Count, $"Pass {pass}: improving slide {index + 1} ({SlideTidy.PrettySlot(_arc[index].Slot)})");
                _attempted.Add((index, target.Feedback));
                var slides = SpecJson.SlideObjects(spec);
                string? previous = null;
                if (!_placeholders.Contains(index))
                    previous = Prompts.Clip(_descriptions.TryGetValue(index, out var description) ? SpecJson.Compact(description) : SpecJson.Compact(slides[index]), 3000);
                var write = await WriteSlotAsync(index, Titles(slides, index), KeepTopics(spec, index), previous, target.Messages, raw).ConfigureAwait(false);
                if (write is null) continue;

                var candidate = ReplaceSlide(spec, index, write.Slide);
                var (after, _) = Evaluate(candidate, true);
                if (Lint.Cost(after) >= Lint.Cost(issues)) continue;
                spec = candidate;
                issues = after;
                if (write.Description is not null) _descriptions[index] = write.Description;
                _placeholders.Remove(index);
                fixedSlides.Add(index);
            }

            _repairs.Add(new RepairRecord(pass, before, issues.Count, fixedSlides.Count > 0, fixedSlides, Codes(issues)));
            if (fixedSlides.Count == 0) break;
        }
        return spec;
    }
}
