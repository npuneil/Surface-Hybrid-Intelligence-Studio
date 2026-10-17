"""Synthetic seller scenarios and deterministic business-case arithmetic."""
import math

SCENARIOS = {
    "brief": {
        "title": "Your next meeting. Already briefed.",
        "label": "Executive briefing",
        "prompt": (
            "Prepare an executive meeting agenda with four discovery questions. After each "
            "question include one short factual note from SOURCE A, B or C. Cover connectivity, "
            "the current workflow baseline, pending approvals and the proposed pilot. "
            "Ask questions; do not answer them or predict any improvement. "
            "Return only four numbered question/note pairs, then the final sentence. "
            "Do not add a title, meeting date, time, location or attendees. End with: "
            "Benefits are unproven; no time, accuracy or savings gain has been measured."
        ),
        "source": (
            "FICTIONAL ACCOUNT: Northstar Field Services\n"
            "SOURCE A | Discovery notes | 14 October 2026\n"
            "240 field managers. Connectivity is intermittent at 18 of 60 sites. "
            "Managers draft five shift summaries per day. Current summary preparation takes "
            "12 minutes each; no AI productivity study has been conducted.\n\n"
            "SOURCE B | IT working session | 15 October 2026\n"
            "The IT team wants to evaluate local AI for drafting from internal notes. "
            "Security review, app compatibility and model licensing are not yet approved. "
            "The cloud collaboration environment remains in use; this is not a replacement proposal.\n\n"
            "SOURCE C | Pilot proposal | 16 October 2026\n"
            "Propose a 10-user, two-week evaluation. Compare task completion time, answer "
            "accuracy, connectivity dependency and user experience against today's workflow. "
            "No device price, battery result, comparative GPU benchmark or committed savings is available."
        ),
        "system": (
            "You prepare concise executive discovery agendas from synthetic source notes. "
            "Write four questions, each with a source citation and a factual note. "
            "Use only supplied facts. Do not invent outcomes, delays, errors, inefficiencies, "
            "future task durations or improvement estimates. Ask what the customer should "
            "evaluate, rather than predicting benefits. Keep it under 180 words. "
            "Do not add a meeting date, time, location, attendees or title. "
            "End with the requested benefits-unproven statement."
        ),
        "rehearsal": (
            "1. Where does connectivity constrain the current workflow?\n"
            "SOURCE A: 18 of 60 sites have intermittent connectivity.\n\n"
            "2. Which baseline should the pilot compare against?\n"
            "SOURCE A: 240 managers prepare five summaries per day, taking 12 minutes each today.\n\n"
            "3. Which approvals must precede an evaluation?\n"
            "SOURCE B: Security, model licensing and application compatibility remain unapproved. "
            "Existing cloud collaboration remains in use.\n\n"
            "4. What would justify scaling beyond the pilot?\n"
            "SOURCE C: A proposed 10-user, two-week evaluation would compare task time, "
            "accuracy, connectivity dependence and user experience.\n\n"
            "Benefits are unproven; no time, accuracy or savings gain has been measured."
        ),
    },
    "code": {
        "title": "A small request. A useful first draft.",
        "label": "Developer acceleration",
        "prompt": (
            "Write a small Python function prioritize_tickets(tickets) that returns a new list "
            "of ticket dictionaries sorted by severity (critical, high, medium, low), then "
            "descending age_hours within each severity (9 hours before 3 hours). Do not mutate "
            "the input. Unknown severities must raise ValueError. Include three checks: "
            "expected IDs T-102, T-103, T-101 for the supplied sample; unchanged input; "
            "rejection of unknown severity using try/except ValueError."
        ),
        "source": (
            "FICTIONAL SERVICE DESK REQUIREMENT\n"
            "A customer wants to prototype a repeatable ticket triage helper.\n"
            "Input: [{id: 'T-101', severity: 'high', age_hours: 3}, "
            "{id: 'T-102', severity: 'critical', age_hours: 1}, "
            "{id: 'T-103', severity: 'high', age_hours: 9}]\n"
            "Expected order: T-102, T-103, T-101.\n"
            "No connection to a real service desk. Generated code is displayed, never executed."
        ),
        "system": (
            "You are a careful Python coding assistant running locally. Return a single "
            "Python fenced code block containing the requested function and three small "
            "checks. Sort severity ascending, age_hours descending. For the mutation check, "
            "take a copy before calling the function and compare the original after. "
            "For the exception check use try/except ValueError, with else raising AssertionError; "
            "never compare a function result to an exception object. Use no dependencies, "
            "network calls, file access or shell commands. Keep it under 65 lines. "
            "Generated code is a draft for human review."
        ),
        "rehearsal": (
            "```python\n"
            "def prioritize_tickets(tickets):\n"
            "    ranks = {'critical': 0, 'high': 1, 'medium': 2, 'low': 3}\n"
            "    for ticket in tickets:\n"
            "        if ticket['severity'] not in ranks:\n"
            "            raise ValueError('Unknown severity: ' + ticket['severity'])\n"
            "    return sorted(tickets, key=lambda t: (ranks[t['severity']], -t['age_hours']))\n\n"
            "tickets = [\n"
            "    {'id': 'T-101', 'severity': 'high', 'age_hours': 3},\n"
            "    {'id': 'T-102', 'severity': 'critical', 'age_hours': 1},\n"
            "    {'id': 'T-103', 'severity': 'high', 'age_hours': 9},\n"
            "]\n"
            "before = [dict(t) for t in tickets]\n"
            "assert [t['id'] for t in prioritize_tickets(tickets)] == ['T-102', 'T-103', 'T-101']\n"
            "assert tickets == before\n"
            "try:\n"
            "    prioritize_tickets([{'severity': 'urgent', 'age_hours': 1}])\n"
            "except ValueError:\n"
            "    pass\n"
            "else:\n"
            "    raise AssertionError('Unknown severity accepted')\n"
            "```"
        ),
    },
    "field": {
        "title": "From field notes to a clear handover.",
        "label": "Snapdragon-fit: field handover",
        "prompt": (
            "Turn the supplied synthetic field notes into a shift handover with exactly "
            "three headings: Observed, Follow-up, Open questions. Cite each bullet with "
            "[A], [B] or [C]. Preserve the observed counts and status. Include owners or "
            "due times only when explicitly supplied. Follow-up must contain only the "
            "two agreed commitments: shift-lead reconciliation [A] and Support's "
            "14:00 update [C]. Put the unverified sync status in Open questions. "
            "Connectivity has already returned; do not describe it as still absent. "
            "Do not invent a fix, a cause, "
            "an impact, a deadline or a productivity benefit. Keep it under 160 words."
        ),
        "source": (
            "FICTIONAL FIELD-NOTE FIXTURE | Harbor Distribution | 16 October 2026\n"
            "[A] At Dock 3, five deliveries were logged this shift. Four were entered "
            "digitally and one was recorded on paper. The paper record has not been "
            "reconciled. The shift lead owns reconciliation; no due time was agreed.\n\n"
            "[B] Handheld H-17 could not sync during a nine-minute connectivity "
            "interruption. Connectivity returned, but H-17 sync status has not been "
            "rechecked. Cause and business impact are unknown.\n\n"
            "[C] The support coordinator opened ticket HD-204 for the H-17 sync check. "
            "Support owns the next status update, due at 14:00. A replacement device "
            "has not been approved. No speech recording or image was supplied."
        ),
        "system": (
            "You draft concise operational handovers using only supplied synthetic "
            "notes. Distinguish observations, agreed follow-up and unresolved questions. "
            "Every bullet must cite its source. Do not infer that an interruption "
            "caused lost work or that an issue is fixed. Do not offer repair instructions "
            "or invent approvals, owners, deadlines or business benefits. Human review "
            "is required before any action; you do not perform actions."
        ),
        "rehearsal": (
            "Observed\n"
            "- Dock 3 logged five deliveries: four digital and one on paper. [A]\n"
            "- H-17 could not sync during a nine-minute interruption; connectivity "
            "returned, but sync has not been rechecked. [B]\n\n"
            "Follow-up\n"
            "- Shift lead: reconcile the paper record; no due time agreed. [A]\n"
            "- Support: provide the next update on HD-204 at 14:00. [C]\n\n"
            "Open questions\n"
            "- What is H-17's current sync status, cause and business impact? [B]\n"
            "- Will a replacement be approved? It is not approved now. [C]"
        ),
    },
    "meeting": {
        "title": "Turn a meeting into a reviewable action register.",
        "label": "Intel-fit: meeting actions",
        "prompt": (
            "Create a concise action register from the supplied synthetic meeting "
            "transcript, using columns Action, Owner, Due, Source. Include only "
            "explicitly agreed actions. Write 'Not assigned' or 'Not agreed' for "
            "missing owners or due dates. Follow the register with one separate "
            "Open decision. Do not turn a suggestion into an approved action, "
            "invent dates, or predict benefits. Keep it under 170 words."
        ),
        "source": (
            "FICTIONAL TRANSCRIPT FIXTURE | Cedar Services IT planning | 16 October 2026\n"
            "[A] IT lead: 'I will document the current ticket-response workflow by "
            "20 October 2026. We have not measured drafting time or answer quality.'\n\n"
            "[B] Service desk lead: 'I will select ten synthetic tickets for review. "
            "We have not agreed a due date for that.'\n\n"
            "[C] Chair: 'We agree to request a security review, but we have not "
            "assigned the request or set a date.'\n\n"
            "[D] IT lead: 'A two-week local-AI pilot is a suggestion. Funding and "
            "the target Intel configuration are not approved. The decision is open.'\n"
            "This supplied text is a fixture, not a recording or a measured transcription."
        ),
        "system": (
            "Extract a small action register only from explicit commitments in the "
            "supplied synthetic transcript. Cite [A], [B] or [C] for action rows, "
            "and [D] for the open decision. Preserve missing information honestly. "
            "Do not interpret a proposed pilot as approved, invent dates or owners, "
            "claim transcription was performed, or predict quality, time or savings "
            "improvements. Human review is required. You do not create tasks in "
            "any external system."
        ),
        "rehearsal": (
            "Action | Owner | Due | Source\n"
            "Document current ticket-response workflow | IT lead | 20 October 2026 | [A]\n"
            "Select ten synthetic tickets for review | Service desk lead | Not agreed | [B]\n"
            "Request a security review | Not assigned | Not agreed | [C]\n\n"
            "Open decision: Whether to fund a two-week local-AI pilot and approve "
            "the target Intel configuration remains open. [D]"
        ),
    },
    "tutor": {
        "title": "Read code like a second language.",
        "label": "Coding tutor (plain English)",
        "prompt": (
            "Explain this Python code in plain English for a seller who is new to code. Give a one-sentence summary, "
            "then a short note for each line, then one everyday analogy, then one sentence the seller could say to a customer. "
            "Do not run the code or claim it was run.\n\n"
            "```python\n"
            "def daily_summaries(managers, per_manager):\n"
            "    return managers * per_manager\n\n"
            "total = daily_summaries(240, 5)\n"
            "print(total)\n"
            "```"
        ),
        "source": (
            "FICTIONAL LEARNER PROFILE | Seller onboarding: Coding as a second language\n"
            "The learner sells devices and services, has never written code, and wants to "
            "follow technical conversations with customers' developers and architects.\n"
            "The code in the prompt is displayed for learning only. It is never executed by this app."
        ),
        "system": (
            "You are a patient coding tutor running locally, teaching a seller who is new to code. "
            "Explain only the code supplied in the prompt, in plain English, and define any jargon in brackets. "
            "Use four short sections: Summary (one sentence), Line by line (one bullet per line or block), "
            "Analogy (one everyday comparison), Say it to a customer (one sentence). "
            "Never claim the code was run or show output as observed; say what it would do. "
            "Do not claim performance, speed, cost or business benefits. "
            "End by noting that a developer should review and test code before anyone relies on it. "
            "Keep it under 200 words."
        ),
        "rehearsal": (
            "Summary\n"
            "This code defines a small reusable recipe that multiplies two numbers, uses it once, "
            "and would show the answer.\n\n"
            "Line by line\n"
            "- def daily_summaries(managers, per_manager): defines a function (a named recipe) with two inputs.\n"
            "- return managers * per_manager multiplies the inputs (* means multiply) and hands the answer back.\n"
            "- total = daily_summaries(240, 5) uses the recipe with 240 and 5 and stores the answer as total.\n"
            "- print(total) would show 1200 on screen.\n\n"
            "Analogy\n"
            "A function is like a recipe card: give it the ingredients and it makes the dish the same way every time.\n\n"
            "Say it to a customer\n"
            "\"Developers build software from small, named recipes like this, and test each one on its own.\"\n\n"
            "A developer should review and test code before anyone relies on it."
        ),
    },
}

DEFAULT_ASSUMPTIONS = {
    "users": 100,
    "tasks": 4,
    "minutes": 3,
    "days": 220,
    "adoption": 60,
    "hourly": 60,
    "premium": 600,
}
LIMITS = {
    "users": (1, 100000), "tasks": (0, 100), "minutes": (0, 120),
    "days": (1, 366), "adoption": (0, 100), "hourly": (0, 1000),
    "premium": (0, 20000),
}


def calculate(values):
    if not isinstance(values, dict) or set(values) != set(LIMITS):
        raise ValueError("Provide exactly the seven calculator assumptions.")
    for name, (low, high) in LIMITS.items():
        value = values[name]
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise ValueError(f"{name} must be a number.")
        if not math.isfinite(value) or not low <= value <= high:
            raise ValueError(f"{name} must be between {low} and {high}.")
    hours = values["users"] * values["tasks"] * values["minutes"] / 60 * values["days"] * values["adoption"] / 100
    capacity = hours * values["hourly"]
    investment = values["users"] * values["premium"]
    return {
        "hours": round(hours, 2),
        "capacity": round(capacity, 2),
        "investment": round(investment, 2),
        "assumptions": values,
        "disclaimer": "Illustrative capacity value, not cash savings or an ROI forecast. "
        "Excludes software, implementation, support, financing and change-management costs. "
        "AI time improvements and adoption must be validated in a pilot. Cloud subscriptions are unchanged.",
    }
