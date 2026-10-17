using System.Text.Json;
using System.Text.Json.Nodes;

namespace HybridIntelligenceStudio;

internal sealed record LineNote(string Code, string Meaning);

internal sealed record QuizQuestion(string Question, string[] Options, int Correct, string Explanation);

internal sealed record Lesson(
    string Id,
    string Theme,
    string Title,
    string WhyItMatters,
    string Analogy,
    string Code,
    LineNote[] Notes,
    string SayIt,
    QuizQuestion Quiz,
    string? SizingLink = null);

/// <summary>
/// "Coding as a second language": an optional onboarding track that teaches sellers to read code like a new language
/// (vocabulary, grammar, sentences, conversation). Code is displayed for learning and never executed by this app.
/// </summary>
internal static class CodingTrack
{
    internal const string TutorScenario = "tutor";

    internal const string TutorIntro =
        "Explain this Python code in plain English for a seller who is new to code. Give a one-sentence summary, " +
        "then a short note for each line, then one everyday analogy, then one sentence the seller could say to a customer. " +
        "Do not run the code or claim it was run.";

    internal const string Promise =
        "You will not write production software here. You will learn to read short code the way you would read a new language: " +
        "vocabulary first, then grammar, then whole sentences, then conversations between programs.";

    internal static readonly Lesson[] Lessons =
    [
        new("variables", "Vocabulary", "Variables: naming things",
            "Every script starts by naming the facts it works with. Spot the names and you know what a program cares about.",
            "A variable is a labelled box: the label is the name, and what is inside is the value.",
            "account = \"Northstar Field Services\"\nmanagers = 240\nsummaries_per_day = 5",
            [
                new("account = \"Northstar Field Services\"", "Stores some text under the name account. Quotes mark text, which programmers call a string."),
                new("managers = 240", "Stores the number 240 under the name managers. A single = means 'store this', not 'is equal to'."),
                new("summaries_per_day = 5", "Names use underscores instead of spaces so the computer reads them as one word."),
            ],
            "The script starts by naming the facts it works with, so anyone can check its assumptions.",
            new("What does managers = 240 do?",
                ["Stores 240 under the name managers", "Checks whether managers is equal to 240", "Adds 240 new managers"], 0,
                "A single = stores a value. Checking whether two things are equal uses ==, which is the next lesson.")),

        new("decisions", "Grammar", "Decisions: if and else",
            "Business rules such as approval thresholds and escalation paths become if/else decisions in code.",
            "Like a decision tree in a support script: if this is true, do that; otherwise, do something else.",
            "severity = \"critical\"\nif severity == \"critical\":\n    print(\"Page the on-call engineer\")\nelse:\n    print(\"Add to the normal queue\")",
            [
                new("if severity == \"critical\":", "Asks a yes/no question. == compares two values; the colon starts the 'then' part."),
                new("    print(\"Page the on-call engineer\")", "Indented lines belong to the if. Python uses indentation the way writing uses paragraphs."),
                new("else:", "What happens when the answer is no."),
            ],
            "Business rules become if/else decisions in code, so they can be read and reviewed line by line.",
            new("In Python, what does == do?",
                ["Compares two values", "Stores a value", "Adds two numbers"], 0,
                "== asks 'are these equal?'. A single = stores a value.")),

        new("loops", "Grammar", "Loops: do it for each one",
            "Most automation starts with a loop: the same steps applied to every ticket, invoice or record.",
            "Like working through an inbox: open each message in turn and apply the same steps.",
            "tickets = [\"T-101\", \"T-102\", \"T-103\"]\nfor ticket in tickets:\n    print(\"Reviewing\", ticket)",
            [
                new("tickets = [\"T-101\", \"T-102\", \"T-103\"]", "Square brackets make a list: several values in order, under one name."),
                new("for ticket in tickets:", "Repeats the indented steps once for each item, calling the current item ticket."),
                new("    print(\"Reviewing\", ticket)", "Runs three times: once for T-101, once for T-102 and once for T-103."),
            ],
            "Loops let a script apply the same steps to every item, which is where most automation starts.",
            new("How many times does the print line run?",
                ["Three times, once per ticket", "Once", "Never"], 0,
                "The loop repeats its indented steps once for each of the three items in the list.")),

        new("functions", "Sentences", "Functions: reusable recipes",
            "Software is built from small named functions. When a developer says 'we'll add a function', this is what they mean.",
            "A function is a recipe card: give it ingredients (inputs) and it returns the dish (output) the same way every time.",
            "def daily_summaries(managers, per_manager):\n    return managers * per_manager\n\ntotal = daily_summaries(240, 5)\nprint(total)",
            [
                new("def daily_summaries(managers, per_manager):", "def defines a recipe named daily_summaries with two inputs."),
                new("    return managers * per_manager", "Multiplies the two inputs (* means multiply) and hands back the answer."),
                new("total = daily_summaries(240, 5)", "Uses the recipe with 240 and 5, and stores the answer under the name total."),
                new("print(total)", "Would show 1200 on screen."),
            ],
            "Developers build software from small, named recipes like this, and test each one on its own.",
            new("What would daily_summaries(10, 3) return?",
                ["30", "13", "103"], 0,
                "The function multiplies its two inputs: 10 x 3 = 30.")),

        new("data", "Sentences", "Data shapes: dictionaries and JSON",
            "Integrations, exports and APIs all pass data around as labelled fields. Recognising the shape helps you follow the conversation.",
            "A dictionary is like a contact card: labelled fields, each with a value.",
            "ticket = {\"id\": \"T-101\", \"severity\": \"high\", \"age_hours\": 3}\nprint(ticket[\"severity\"])",
            [
                new("ticket = {\"id\": \"T-101\", ...}", "Curly braces make a dictionary: pairs of label (key) and value."),
                new("print(ticket[\"severity\"])", "Looks up the field named severity and would show: high."),
                new("JSON", "JSON looks almost identical and is how apps exchange data. This studio's exports are JSON files."),
            ],
            "Apps exchange data as labelled fields called JSON; this studio's exports are JSON files you can open in any text editor.",
            new("What does ticket[\"severity\"] give you?",
                ["high", "T-101", "3"], 0,
                "Square brackets with a key look up that one field's value.")),

        new("apis", "Conversation", "APIs: how programs talk",
            "Customers ask about integrations. An API is the agreed way two programs talk, and the first thing an architect will ask about.",
            "An API is like a restaurant menu and waiter: you order from a fixed list, and the kitchen sends back a standard plate.",
            "status = get(\"http://127.0.0.1:8790/api/status\")\nprint(status[\"engine\"][\"state\"])",
            [
                new("get(...)", "Simplified: sends a request to an address and returns the answer as a dictionary."),
                new("127.0.0.1", "A special address that always means 'this computer', so the request stays on the laptop."),
                new("/api/status", "The menu item being ordered: this studio's local service reports its status here."),
                new("status[\"engine\"][\"state\"]", "Looks up a field inside a field, for example: ready."),
            ],
            "This studio's screen talks to a local AI service through an API on the laptop itself, at 127.0.0.1.",
            new("What does the address 127.0.0.1 mean?",
                ["This computer", "A cloud data centre", "The customer's website"], 0,
                "127.0.0.1 is the loopback address: the request goes to a program on the same machine.")),

        new("ai-words", "Conversation", "AI words: parameters, bits and memory",
            "Customers will ask 'how big a model can it run?'. Three words, parameters, bits and memory, answer most of it.",
            "Bits are like photo compression: fewer bits per number makes the model smaller, with some loss of detail.",
            "parameters_billions = 14\nbits = 4\nweight_gib = parameters_billions * 1e9 * bits / 8 / 2**30\nprint(round(weight_gib, 2))",
            [
                new("parameters_billions = 14", "Parameters are the numbers a model learned. More parameters means a bigger model; 14 billion is Large here."),
                new("bits = 4", "How many bits store each parameter. 4-bit is compressed; 16-bit is four times bigger."),
                new("... * bits / 8 / 2**30", "Turns bits into bytes (divide by 8), then bytes into GiB (2**30 bytes is one GiB)."),
                new("print(round(weight_gib, 2))", "Would show 6.52: the same weights figure the Technical sizing view shows for 14B at 4-bit."),
            ],
            "Model size is mostly parameters times precision; a 14-billion-parameter model needs about 6.5 GiB for its weights at 4-bit, before any working memory.",
            new("Roughly how much memory do a 14B model's weights need at 4-bit?",
                ["About 6.5 GiB", "About 14 GiB", "About 56 GiB"], 0,
                "14 x 10^9 x 4 / 8 / 2^30 is about 6.52 GiB. At 16-bit it would be four times as much."),
            "L"),

        new("ai-code", "Fluency", "Reading AI-written code",
            "Local coding assistants draft code like this. You can now read it, and you know the questions a careful developer asks.",
            "Treat AI-written code like a first draft from a new colleague: useful, but read and test it before relying on it.",
            "def prioritize_tickets(tickets):\n    ranks = {\"critical\": 0, \"high\": 1, \"medium\": 2, \"low\": 3}\n    for ticket in tickets:\n        if ticket[\"severity\"] not in ranks:\n            raise ValueError(\"Unknown severity\")\n    return sorted(tickets, key=lambda t: (ranks[t[\"severity\"]], -t[\"age_hours\"]))",
            [
                new("ranks = {...}", "A dictionary that turns each severity into a position: critical first."),
                new("for ticket in tickets: / if ... not in ranks:", "A loop and a decision: check every ticket for an unknown severity."),
                new("raise ValueError(...)", "Stops with a clear error instead of guessing. Failing loudly on bad input is a good sign."),
                new("return sorted(...)", "Returns a new list ordered by severity, then oldest first. The original list is left unchanged."),
            ],
            "A local coding assistant drafts code like this on the laptop; developers still review and test every line.",
            new("What is the right next step with AI-generated code?",
                ["Review and test it before use", "Ship it as-is", "Assume it is correct because it ran on a GPU"], 0,
                "Generated code is a draft. Human review and tests come before anyone relies on it.")),
    ];

    internal static Lesson? Find(string? id) => Lessons.FirstOrDefault(l => l.Id == id);

    internal static string TutorPrompt(Lesson lesson) => $"{TutorIntro}\n\n```python\n{lesson.Code}\n```";

    internal static bool IsCorrect(Lesson lesson, int selected) => selected == lesson.Quiz.Correct;

    /// <summary>Deterministic per-lesson rotation so the correct answer is not always shown first. Values are original option indices.</summary>
    internal static int[] DisplayOrder(Lesson lesson)
    {
        int n = lesson.Quiz.Options.Length;
        int shift = Math.Max(0, Array.IndexOf(Lessons, lesson)) % n;
        return Enumerable.Range(0, n).Select(i => (i + shift) % n).ToArray();
    }

    internal static IEnumerable<string> AllText() => Lessons.SelectMany(l =>
            new[] { l.Theme, l.Title, l.WhyItMatters, l.Analogy, l.SayIt, l.Quiz.Question, l.Quiz.Explanation }
                .Concat(l.Notes.Select(n => n.Meaning)).Concat(l.Quiz.Options))
        .Concat([TutorIntro, Promise]);
}

/// <summary>Learner progress and the optional-tab preference, stored as a small local JSON file.</summary>
internal sealed class LearningState
{
    internal HashSet<string> Completed { get; } = new(StringComparer.Ordinal);
    internal bool ShowTab { get; set; } = true;
    internal string? LastLesson { get; set; }

    internal int CompletedCount => CodingTrack.Lessons.Count(l => Completed.Contains(l.Id));

    internal static LearningState Load(string path)
    {
        var state = new LearningState();
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return state;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return state;
            if (root["show_tab"] is JsonValue show && show.TryGetValue(out bool visible)) state.ShowTab = visible;
            if (root["last_lesson"] is JsonValue last && last.TryGetValue(out string? lastId) && CodingTrack.Find(lastId) is not null) state.LastLesson = lastId;
            if (root["completed"] is JsonArray done)
                foreach (var node in done)
                    if (node is JsonValue v && v.TryGetValue(out string? id) && CodingTrack.Find(id) is not null) state.Completed.Add(id!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
        return state;
    }

    internal void Save(string path)
    {
        var root = new JsonObject
        {
            ["show_tab"] = ShowTab,
            ["last_lesson"] = LastLesson,
            ["completed"] = new JsonArray(CodingTrack.Lessons.Where(l => Completed.Contains(l.Id)).Select(l => (JsonNode)l.Id).ToArray()),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString());
        File.Move(temp, path, overwrite: true);
    }
}
