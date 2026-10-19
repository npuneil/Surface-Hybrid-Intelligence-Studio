"""Builder hub content: copy-ready snippets and starter kits that call this PC's OpenAI-compatible endpoint.

Everything here is text. The studio returns the files; the native app writes them only when the user exports a kit.
Kits use synthetic data and never call a cloud service.
"""
import re
from pathlib import Path

import demos
from edition import DISPLAY_NAME

ROOT = Path(__file__).resolve().parent
LOOPBACK = re.compile(r"^http://127\.0\.0\.1:\d{2,5}/v1$")
MODEL_ID = re.compile(r"^[A-Za-z0-9._:/-]{1,120}$")

LOCAL_AI_PY = '''"""Minimal client for an OpenAI-compatible model endpoint on this PC. Standard library only."""
import json
import os
import urllib.request

BASE_URL = os.environ.get("LOCAL_AI_BASE_URL", "{{BASE_URL}}").rstrip("/")
MODEL = os.environ.get("LOCAL_AI_MODEL", "{{MODEL}}")
TIMEOUT = float(os.environ.get("LOCAL_AI_TIMEOUT", "300"))


def _post(path, body):
    request = urllib.request.Request(BASE_URL + path, data=json.dumps(body).encode("utf-8"),
                                     headers={"Content-Type": "application/json"})
    return urllib.request.urlopen(request, timeout=TIMEOUT)


def chat(messages, max_tokens=512, temperature=0.2):
    """Return the assistant's reply text for a list of {"role", "content"} messages."""
    body = {"model": MODEL, "messages": messages, "max_tokens": max_tokens, "temperature": temperature}
    with _post("/chat/completions", body) as response:
        data = json.loads(response.read())
    return data["choices"][0]["message"].get("content") or ""


def stream(messages, max_tokens=512, temperature=0.2):
    """Yield reply text as the model generates it (server-sent events)."""
    body = {"model": MODEL, "messages": messages, "max_tokens": max_tokens, "temperature": temperature,
            "stream": True}
    with _post("/chat/completions", body) as response:
        for raw in response:
            line = raw.decode("utf-8", "replace").strip()
            if not line.startswith("data:"):
                continue
            payload = line[5:].strip()
            if payload == "[DONE]":
                break
            for choice in json.loads(payload).get("choices") or []:
                text = (choice.get("delta") or {}).get("content")
                if text:
                    yield text
'''

CHAT_PY = '''"""Streaming chat with a model running on this PC. Usage: python chat.py ["one question"]"""
import sys

import local_ai

SYSTEM = "You are a helpful assistant running entirely on this PC. Be concise."


def main():
    history = [{"role": "system", "content": SYSTEM}]
    once = " ".join(sys.argv[1:]).strip()
    print(f"Local model {local_ai.MODEL} at {local_ai.BASE_URL}. Type exit to quit.")
    while True:
        prompt = once or input("\\nYou: ").strip()
        if not prompt or prompt.lower() in ("exit", "quit"):
            return
        history.append({"role": "user", "content": prompt})
        print("AI: ", end="", flush=True)
        reply = []
        for text in local_ai.stream(history):
            print(text, end="", flush=True)
            reply.append(text)
        print()
        history.append({"role": "assistant", "content": "".join(reply)})
        if once:
            return


if __name__ == "__main__":
    main()
'''

ASK_DOCS_PY = '''"""Answer questions from private documents with citations, all on this PC.

Retrieval here is simple keyword scoring so the kit has no dependencies. Swap in embeddings when you are ready.
Usage: python ask_docs.py ["question"]
"""
import math
import re
import sys
from pathlib import Path

import local_ai

DOCS = Path(__file__).resolve().parent / "docs"
STOP = set("a an and are as at be by can do for from how i if in is it me my of on or our should the to was what "
           "when where which who why will with you your".split())
SYSTEM = ("Answer only from the numbered sources. Cite the source id in square brackets after each fact, like "
          "[POL-03]. If the sources do not answer the question, say so. Be concise.")


def words(text):
    return [w for w in re.findall(r"[a-z0-9]+", text.lower()) if w not in STOP]


def load():
    return [(p.stem, p.read_text(encoding="utf-8")) for p in sorted(DOCS.glob("*.md"))]


def retrieve(question, docs, k=3):
    terms = set(words(question))
    df = {t: sum(1 for _, text in docs if t in set(words(text))) for t in terms}
    scored = []
    for doc_id, text in docs:
        tokens = words(text)
        score = sum(tokens.count(t) * math.log(1 + len(docs) / (1 + df[t])) for t in terms)
        if score > 0:
            scored.append((score, doc_id, text))
    return sorted(scored, reverse=True)[:k]


def main():
    question = " ".join(sys.argv[1:]).strip() or "I think I left my work laptop on the train. What should I do?"
    hits = retrieve(question, load())
    if not hits:
        print("No document matched. Try different words.")
        return
    sources = "\\n\\n".join(f"[{doc_id}]\\n{text.strip()}" for _, doc_id, text in hits)
    print("Question:", question)
    print("Retrieved:", ", ".join(doc_id for _, doc_id, _ in hits))
    print("\\nAnswer: ", end="", flush=True)
    for text in local_ai.stream([{"role": "system", "content": SYSTEM},
                                 {"role": "user", "content": f"Sources:\\n{sources}\\n\\nQuestion: {question}"}],
                                max_tokens=300, temperature=0):
        print(text, end="", flush=True)
    print()


if __name__ == "__main__":
    main()
'''

REDACT_PY = '''"""Route a request: keep it local, send it to the cloud, or redact on this PC first.

send_to_cloud() is a stub. Wire it to your organisation's approved service; this kit never sends anything.
Usage: python redact_then_cloud.py [file.txt]
"""
import sys
from pathlib import Path

import hybrid
import local_ai


def send_to_cloud(text):
    """Replace with a call to your approved cloud service. Returns None because nothing is sent."""
    print("\\n--- NOT SENT: send_to_cloud() is a stub ---")
    print(text)
    return None


def main():
    path = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent / "sample.txt"
    text = path.read_text(encoding="utf-8")
    decision = hybrid.route(text)
    print(f"Lane: {decision['lane_label']}\\n{decision['headline']}")
    for reason in decision["reasons"]:
        print(" -", reason)
    if decision["lane"] == "local":
        print("\\nLocal answer:")
        for chunk in local_ai.stream([{"role": "user", "content": text}]):
            print(chunk, end="", flush=True)
        print()
    elif decision["lane"] == "cloud":
        send_to_cloud(text)
    else:
        # Same prompts and checks the studio uses: the request is fenced as data, then re-scanned.
        redacted = local_ai.chat([{"role": "system", "content": hybrid.REDACT_SYSTEM},
                                  {"role": "user", "content": hybrid.redaction_prompt(text)}],
                                 max_tokens=900, temperature=0)
        redacted = hybrid.unfence(redacted)
        survived = hybrid.leaks(text, redacted)
        if survived:
            print("\\nBlocked: the re-scan found", len(survived), "identifier(s) still present:",
                  ", ".join(f"{s['label']} {s['evidence']}" for s in survived))
            return
        drifted = hybrid.drift(text, redacted)
        if drifted:
            # The model answered or rewrote the request instead of redacting it.
            print(f"\\nBlocked: the model changed the request instead of only redacting it ({drifted}).")
            return
        print("\\nRe-scan passed: no detected identifier survived. Review names and context before sending.")
        send_to_cloud(redacted)


if __name__ == "__main__":
    main()
'''

AGENT_PY = '''"""A small tool-using agent on a local model, using a JSON-action protocol any chat model can follow.

Tools are allow-listed Python functions; the model can only ask for them by name. Nothing else is executed.
Usage: python agent.py ["question"]
"""
import ast
import json
import operator
import re
import sys
from datetime import date

import local_ai

POLICIES = json.loads(r\'\'\'{{POLICIES}}\'\'\')
OPS = {ast.Add: operator.add, ast.Sub: operator.sub, ast.Mult: operator.mul, ast.Div: operator.truediv,
       ast.Mod: operator.mod, ast.Pow: operator.pow, ast.USub: operator.neg, ast.UAdd: operator.pos}


def calculator(expression):
    """Arithmetic only: numbers, + - * / % ** and brackets."""
    def walk(node):
        if isinstance(node, ast.Expression):
            return walk(node.body)
        if isinstance(node, ast.Constant) and isinstance(node.value, (int, float)):
            return node.value
        if isinstance(node, ast.BinOp) and type(node.op) in OPS:
            if isinstance(node.op, ast.Pow) and abs(walk(node.right)) > 12:
                raise ValueError("exponent too large")
            return OPS[type(node.op)](walk(node.left), walk(node.right))
        if isinstance(node, ast.UnaryOp) and type(node.op) in OPS:
            return OPS[type(node.op)](walk(node.operand))
        raise ValueError("only arithmetic is allowed")
    return str(walk(ast.parse(str(expression), mode="eval")))


def policy_lookup(keyword):
    words = set(re.findall(r"[a-z]+", str(keyword).lower()))
    hits = [f"{pid} {title}: {text}" for pid, (title, text) in POLICIES.items()
            if words & set(re.findall(r"[a-z]+", (title + " " + text).lower()))]
    return "\\n".join(hits[:2]) or "No policy matched."


def today(_=None):
    return date.today().isoformat()


TOOLS = {"calculator": calculator, "policy_lookup": policy_lookup, "today": today}
SYSTEM = (
    "You are an assistant that can use tools. Reply with exactly one JSON object and nothing else.\\n"
    'To use a tool: {"tool": "<name>", "input": "<text>"}\\n'
    'To finish: {"answer": "<final answer for the user>"}\\n'
    "Tools:\\n"
    "- calculator: arithmetic expression, for example 4*3*60\\n"
    "- policy_lookup: keywords to search the company's policies, for example meals travel\\n"
    "- today: the current date\\n"
    "Use policy_lookup before stating any policy. Use calculator for any arithmetic.")


def first_json(text):
    text = re.sub(r"```(?:json)?", "", text)
    start = text.find("{")
    while start != -1:
        depth = 0
        for end in range(start, len(text)):
            depth += {"{": 1, "}": -1}.get(text[end], 0)
            if depth == 0:
                try:
                    return json.loads(text[start:end + 1])
                except ValueError:
                    break
        start = text.find("{", start + 1)
    return None


def main():
    question = " ".join(sys.argv[1:]).strip() or (
        "Four of us are travelling for three days. What is the most we can claim for meals in total, "
        "and what is the hotel limit per night?")
    messages = [{"role": "system", "content": SYSTEM}, {"role": "user", "content": question}]
    print("Question:", question)
    for step in range(1, 7):
        reply = local_ai.chat(messages, max_tokens=300, temperature=0)
        action = first_json(reply)
        messages.append({"role": "assistant", "content": reply})
        if not isinstance(action, dict):
            messages.append({"role": "user", "content": "Reply with one JSON object only."})
            continue
        if "answer" in action:
            print("\\nAnswer:", action["answer"])
            return
        name = action.get("tool")
        if name not in TOOLS:
            observation = f"Unknown tool {name!r}. Use one of: {', '.join(TOOLS)}."
        else:
            try:
                observation = TOOLS[name](action.get("input"))
            except Exception as exc:
                observation = f"Tool error: {exc}"
        print(f"Step {step}: {name}({action.get('input')!r}) -> {observation[:160]}")
        messages.append({"role": "user", "content": f"Observation: {observation}"})
    print("\\nStopped after 6 steps without a final answer.")


if __name__ == "__main__":
    main()
'''

CSPROJ = '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
'''

PROGRAM_CS = '''// Streaming chat with a model on this PC through its OpenAI-compatible endpoint. No NuGet packages needed.
// Usage: dotnet run -- "one question"   (or run without arguments for a chat loop)
using System.Net.Http.Json;
using System.Text.Json;

string baseUrl = (Environment.GetEnvironmentVariable("LOCAL_AI_BASE_URL") ?? "{{BASE_URL}}").TrimEnd('/');
string model = Environment.GetEnvironmentVariable("LOCAL_AI_MODEL") ?? "{{MODEL}}";
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
var history = new List<object> { new { role = "system", content = "You are a helpful assistant running on this PC. Be concise." } };
string? once = args.Length > 0 ? string.Join(' ', args) : null;
Console.WriteLine($"Local model {model} at {baseUrl}. Type exit to quit.");
while (true)
{
    string? prompt = once;
    if (prompt is null) { Console.Write("\\nYou: "); prompt = Console.ReadLine(); }
    if (string.IsNullOrWhiteSpace(prompt) || prompt.Trim() is "exit" or "quit") return;
    history.Add(new { role = "user", content = prompt });
    using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions")
    {
        Content = JsonContent.Create(new { model, messages = history, max_tokens = 512, temperature = 0.2, stream = true }),
    };
    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
    var reply = new System.Text.StringBuilder();
    Console.Write("AI: ");
    while (await reader.ReadLineAsync() is string line)
    {
        if (!line.StartsWith("data:")) continue;
        string payload = line[5..].Trim();
        if (payload == "[DONE]") break;
        using var chunk = JsonDocument.Parse(payload);
        if (!chunk.RootElement.TryGetProperty("choices", out var choices)) continue;
        foreach (var choice in choices.EnumerateArray())
            if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                Console.Write(content.GetString());
                reply.Append(content.GetString());
            }
    }
    Console.WriteLine();
    history.Add(new { role = "assistant", content = reply.ToString() });
    if (once is not null) return;
}
'''

SAMPLE_TXT = (
    "FICTIONAL. Draft a reply to this customer that quotes today's exchange rates for euros to US dollars.\n\n"
    "From: Jonas Becker <jonas.becker@example.com>\n"
    "Account number ACCT-5521-0093. I am paying a 12,400 EUR invoice next week. Roughly what will that be in dollars?\n"
)

COMMON_README = """
## Point it at a model on this PC

The kit was generated for:

- Base URL: `{{BASE_URL}}`
- Model: `{{MODEL}}`

Override either one with environment variables:

```powershell
$env:LOCAL_AI_BASE_URL = "{{BASE_URL}}"
$env:LOCAL_AI_MODEL = "{{MODEL}}"
```

In the studio, open **Build on Surface** and choose **Start endpoint** for Foundry Local. Larger models loaded through
llama.cpp already serve the same API on their own loopback port. Both endpoints listen on 127.0.0.1 only and have no
authentication, so other apps on this PC can call them while they run. The Foundry Local endpoint stops when you stop
it or close the studio.

The same code talks to any OpenAI-compatible service, local or cloud: change the base URL, model and (for a cloud
service) add its authentication header.

## Boundaries

- Sample code, not a product. Data in this kit is synthetic.
- Nothing here calls a cloud service. Where a kit shows a cloud step, it is a clearly marked stub.
- Model output is a draft. Review it before you act on it.
"""

KITS = {
    "chat": {
        "title": "Streaming chat (Python)", "language": "Python 3.10+, standard library only",
        "summary": "A chat loop that streams tokens from the local model. The smallest possible starting point.",
        "shows": "OpenAI-compatible streaming from a model on this PC's GPU.",
        "run": 'python chat.py "Give me three ideas for a team offsite"',
    },
    "private-docs": {
        "title": "Ask private documents (Python)", "language": "Python 3.10+, standard library only",
        "summary": "Retrieves the most relevant policy files and asks the local model for a cited answer.",
        "shows": "Private knowledge stays on the PC; answers cite their source.",
        "run": 'python ask_docs.py "I lost my work laptop on the train. What should I do?"',
    },
    "redact-then-cloud": {
        "title": "Redact, then cloud (Python)", "language": "Python 3.10+, standard library only",
        "summary": "Routes a request with the studio's hybrid policy, redacts on the PC, re-scans, then calls a stub.",
        "shows": "The hybrid pattern: only a clean, re-checked payload would ever leave the device.",
        "run": "python redact_then_cloud.py sample.txt",
    },
    "tool-agent": {
        "title": "Tool-using agent (Python)", "language": "Python 3.10+, standard library only",
        "summary": "A step-by-step agent with allow-listed tools: a safe calculator, a policy lookup and today's date.",
        "shows": "Agentic workflows on a local model with a JSON-action protocol any chat model can follow.",
        "run": "python agent.py",
    },
    "csharp-chat": {
        "title": "Streaming chat (C# / .NET 10)", "language": ".NET 10 SDK, no NuGet packages",
        "summary": "A console app that streams from the local endpoint with HttpClient and System.Text.Json.",
        "shows": "Windows developers can call local models with nothing but the .NET base library.",
        "run": 'dotnet run -- "Explain a Surface Laptop GPU to a new developer in two sentences"',
    },
}


def _policy_docs():
    return {f"docs/{pid}.md": f"# {pid} {title}\n\n{text}\n\nSynthetic policy for demonstration only.\n"
            for pid, title, text in demos.POLICIES}


def _policies_json():
    import json
    return json.dumps({pid: [title, text] for pid, title, text in demos.POLICIES}, indent=1)


def _readme(kit_id):
    kit = KITS[kit_id]
    first = "dotnet run" if kit_id == "csharp-chat" else "python"
    return (f"# {kit['title']}\n\n{kit['summary']}\n\n**What it shows:** {kit['shows']}\n\n"
            f"**Needs:** {kit['language']}, and a model endpoint on this PC.\n\n## Run it\n\n```powershell\n"
            f"{kit['run']}\n```\n\nGenerated by {DISPLAY_NAME}. "
            f"{'Run from this folder.' if first == 'python' else 'Run from this folder; the first build restores the SDK packs.'}\n"
            + COMMON_README)


def validate_target(base_url, model):
    if not isinstance(base_url, str) or not LOOPBACK.match(base_url):
        raise ValueError("Starter kits target a loopback endpoint (http://127.0.0.1:<port>/v1).")
    if not isinstance(model, str) or not MODEL_ID.match(model):
        raise ValueError("The model id is missing or invalid. Load a model first.")


def files(kit_id, base_url, model):
    """The kit as {relative path: text}. Pure: nothing is written here."""
    if kit_id not in KITS:
        raise ValueError("Choose one of the listed starter kits.")
    validate_target(base_url, model)
    out = {"README.md": _readme(kit_id)}
    if kit_id == "csharp-chat":
        out.update({"LocalChat.csproj": CSPROJ, "Program.cs": PROGRAM_CS})
    else:
        out["local_ai.py"] = LOCAL_AI_PY
        if kit_id == "chat":
            out["chat.py"] = CHAT_PY
        elif kit_id == "private-docs":
            out["ask_docs.py"] = ASK_DOCS_PY
            out.update(_policy_docs())
        elif kit_id == "redact-then-cloud":
            out["redact_then_cloud.py"] = REDACT_PY
            out["hybrid.py"] = (ROOT / "hybrid.py").read_text(encoding="utf-8")
            out["sample.txt"] = SAMPLE_TXT
        elif kit_id == "tool-agent":
            out["agent.py"] = AGENT_PY.replace("{{POLICIES}}", _policies_json())
    return {path: text.replace("{{BASE_URL}}", base_url).replace("{{MODEL}}", model) for path, text in out.items()}


def snippets(base_url, model):
    """Copy-ready calls in several languages for the given loopback endpoint."""
    validate_target(base_url, model)
    root = base_url[: -len("/v1")]
    body = ('{"model": "%s", "messages": [{"role": "user", "content": "Say hello from my Surface."}], '
            '"max_tokens": 64}' % model)
    escaped = body.replace('"', '\\"')
    return [
        {"id": "python-openai", "label": "Python (openai package)", "code":
            "from openai import OpenAI\n\n"
            f'client = OpenAI(base_url="{base_url}", api_key="not-needed-locally")\n'
            f'reply = client.chat.completions.create(\n    model="{model}",\n'
            '    messages=[{"role": "user", "content": "Say hello from my Surface."}],\n    max_tokens=64,\n)\n'
            "print(reply.choices[0].message.content)\n"},
        {"id": "python-stdlib", "label": "Python (standard library)", "code":
            "import json, urllib.request\n\n"
            f"body = {body}\n"
            f'request = urllib.request.Request("{base_url}/chat/completions", data=json.dumps(body).encode(),\n'
            '                                 headers={"Content-Type": "application/json"})\n'
            "with urllib.request.urlopen(request) as response:\n"
            '    print(json.loads(response.read())["choices"][0]["message"]["content"])\n'},
        {"id": "csharp", "label": "C# (.NET HttpClient)", "code":
            "using System.Net.Http.Json;\nusing System.Text.Json;\n\n"
            "using var http = new HttpClient();\n"
            f'var response = await http.PostAsJsonAsync("{base_url}/chat/completions", new\n{{\n'
            f'    model = "{model}",\n    messages = new[] {{ new {{ role = "user", content = "Say hello from my Surface." }} }},\n'
            "    max_tokens = 64,\n});\n"
            "using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());\n"
            'Console.WriteLine(json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content"));\n'},
        {"id": "javascript", "label": "JavaScript (Node 18+ fetch)", "code":
            f'const response = await fetch("{base_url}/chat/completions", {{\n'
            '  method: "POST",\n  headers: { "Content-Type": "application/json" },\n'
            f"  body: JSON.stringify({body}),\n}});\n"
            "const data = await response.json();\nconsole.log(data.choices[0].message.content);\n"},
        {"id": "powershell", "label": "PowerShell", "code":
            f"$body = @'\n{body}\n'@\n"
            f'$reply = Invoke-RestMethod -Method Post -Uri "{base_url}/chat/completions" -ContentType "application/json" -Body $body\n'
            "$reply.choices[0].message.content\n"},
        {"id": "curl", "label": "curl (Command Prompt)", "code":
            f'curl.exe {base_url}/chat/completions -H "Content-Type: application/json" -d "{escaped}"\n'},
        {"id": "models", "label": "List models (curl)", "code": f"curl.exe {root}/v1/models\n"},
    ]


def public():
    return {"kits": [{"id": k, **{n: v[n] for n in ("title", "language", "summary", "shows", "run")}}
                     for k, v in KITS.items()],
            "env": {"base_url": "LOCAL_AI_BASE_URL", "model": "LOCAL_AI_MODEL"}}
