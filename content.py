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

SCENARIOS.update({
    "manufacturing": {
        "industry": "Manufacturing",
        "client": "Fabrikam Components",
        "deck_objective": "Explain a human-reviewed local drafting pilot for quality shift handovers, using only the fictional fixture.",
        "title": "Quality notes to a reviewable shift handover.",
        "label": "Manufacturing: quality handover",
        "prompt": (
            "Draft a shift handover with three headings: Recorded facts, Agreed follow-up, "
            "Open questions. Cite every bullet [A], [B] or [C]. Preserve the counts and "
            "recorded status. Do not infer a cause, prescribe a repair, release a lot, "
            "invent a deadline or claim a productivity gain. Keep it under 180 words. "
            "End with: Draft only; quality staff must review before any action."
        ),
        "source": (
            "FICTIONAL SYNTHETIC FIXTURE | Fabrikam Components | Quality shift notes\n\n"
            "[A] Lot FC-204 has 24 inspection records: 22 marked complete and 2 awaiting "
            "a reviewer signature. No product defect is recorded in these notes.\n\n"
            "[B] The quality coordinator agreed to check the two unsigned records. "
            "No due time was agreed. Lot release has not been approved.\n\n"
            "[C] The next shift asked whether the inspection template changed. "
            "The template version and any cause of the missing signatures are unknown. "
            "No machine telemetry, image or repair instruction was supplied."
        ),
        "system": (
            "Draft a source-cited handover from synthetic manufacturing notes only. "
            "Separate recorded facts, commitments and unknowns. Do not diagnose equipment, "
            "provide repair instructions, infer defects, authorize release or invent "
            "owners, dates or benefits. Human review by quality staff is required."
        ),
        "rehearsal": (
            "Recorded facts\n"
            "- FC-204 has 24 records: 22 complete and 2 awaiting a reviewer signature. "
            "No defect is recorded in the notes. [A]\n\n"
            "Agreed follow-up\n"
            "- Quality coordinator: check the two unsigned records; no due time agreed. "
            "Lot release is not approved. [B]\n\n"
            "Open questions\n"
            "- Did the template change? Its version and the cause of the missing signatures "
            "are unknown. No telemetry or image was supplied. [C]\n\n"
            "Draft only; quality staff must review before any action."
        ),
    },
    "insurance": {
        "industry": "Insurance operations",
        "client": "Beacon Mutual",
        "deck_objective": "Explain a human-reviewed local drafting pilot for claims intake administration, not claims adjudication.",
        "title": "Claims intake notes to an administrative checklist.",
        "label": "Insurance: claims intake",
        "prompt": (
            "Create a claims intake checklist under Received, Missing information, "
            "Agreed next step. Cite each bullet [A], [B] or [C]. Distinguish a received "
            "document from a verified document. Do not decide coverage, eligibility, "
            "payment, liability or fraud, request sensitive identifiers, or invent "
            "a deadline or business benefit. Keep it under 180 words. "
            "End with: Administrative draft only; an authorized claims professional must review."
        ),
        "source": (
            "FICTIONAL SYNTHETIC FIXTURE | Beacon Mutual | Intake case BM-318\n\n"
            "[A] The intake desk recorded receipt of a notification form and an attachment "
            "list. Neither has been verified. No real claimant information is included.\n\n"
            "[B] The incident date is absent from the form. An attachment on the list "
            "has not been received. The policy wording is not supplied; coverage is unknown.\n\n"
            "[C] The intake coordinator agreed to reconcile the attachment list and flag "
            "the missing date for an authorized reviewer. No due date or payment decision "
            "was agreed. No claim outcome or service-time baseline is available."
        ),
        "system": (
            "Use only synthetic insurance intake notes to draft an administrative checklist. "
            "Cite the sources and preserve unverified and missing statuses. Do not infer "
            "coverage, eligibility, liability, fraud or payment, provide advice, or ask for "
            "sensitive identifiers. Human review by an authorized claims professional is "
            "required. Do not invent outcomes, deadlines or measured benefits."
        ),
        "rehearsal": (
            "Received\n"
            "- Notification form and attachment list received, but neither verified. [A]\n\n"
            "Missing information\n"
            "- Incident date absent; one listed attachment not received. Policy wording "
            "not supplied; coverage unknown. [B]\n\n"
            "Agreed next step\n"
            "- Intake coordinator: reconcile the attachment list and flag the missing "
            "date for an authorized reviewer. No due date or payment decision agreed. [C]\n\n"
            "Administrative draft only; an authorized claims professional must review."
        ),
    },
    "retail": {
        "industry": "Retail",
        "client": "Northwind Retail",
        "deck_objective": "Explain a local drafting pilot for store exception briefings, preserving inventory discrepancies until reconciliation.",
        "title": "Store exception notes to a manager briefing.",
        "label": "Retail: store exceptions",
        "prompt": (
            "Draft a store manager briefing with Reported discrepancy, Agreed follow-up, "
            "and Still unknown. Cite each bullet [A], [B] or [C]. Preserve both counts "
            "rather than choosing a correct one. Do not infer theft, stock loss, demand "
            "or a cause, approve an inventory adjustment, or predict sales or savings. "
            "Keep it under 180 words. End with: Draft only; the store manager must review."
        ),
        "source": (
            "FICTIONAL SYNTHETIC FIXTURE | Northwind Retail | Store NW-12\n\n"
            "[A] A closing note lists 18 units of item NW-42. The inventory screen lists "
            "21 units. Neither count has been reconciled; the reason is unknown.\n\n"
            "[B] The stock lead agreed to compare the count sheet with the screen at "
            "09:00. No inventory adjustment is approved.\n\n"
            "[C] A delivery reference is absent from the handover. Arrival time and "
            "delivery contents are not confirmed. The manager requested that this "
            "uncertainty be included; no customer impact or sales effect was measured."
        ),
        "system": (
            "Draft a cited store briefing using only synthetic notes. Separate reported "
            "counts, agreed commitments and unknowns. Do not infer theft, employee "
            "misconduct, stock loss, demand or delivery details. Do not adjust inventory "
            "or invent business outcomes. Human review by the store manager is required."
        ),
        "rehearsal": (
            "Reported discrepancy\n"
            "- NW-42: closing note lists 18 units; inventory screen lists 21. "
            "Neither is reconciled and the reason is unknown. [A]\n\n"
            "Agreed follow-up\n"
            "- Stock lead: compare the count sheet and screen at 09:00. "
            "No inventory adjustment is approved. [B]\n\n"
            "Still unknown\n"
            "- Delivery reference is absent; arrival and contents are unconfirmed. "
            "No customer impact or sales effect was measured. [C]\n\n"
            "Draft only; the store manager must review."
        ),
    },
    "public-sector": {
        "industry": "Public sector",
        "client": "Cedar Borough",
        "deck_objective": "Explain a human-reviewed local drafting pilot for public-service request administration without eligibility decisions.",
        "title": "Service request notes to a reviewable case brief.",
        "label": "Public sector: service requests",
        "prompt": (
            "Create an administrative case brief with Recorded requests, Agreed next "
            "steps, and Open questions. Cite each bullet [A], [B] or [C]. Distinguish "
            "receipt from acceptance. Do not decide eligibility, priority, funding or "
            "legal rights, infer personal circumstances, or invent response deadlines "
            "or benefits. Keep it under 180 words. End with: Administrative draft only; "
            "an authorized service officer must review."
        ),
        "source": (
            "FICTIONAL SYNTHETIC FIXTURE | Cedar Borough | Service desk CB-71\n\n"
            "[A] The desk logged 3 requests about a community-room booking. "
            "Receipt is acknowledged; no booking has been accepted. No resident "
            "identifiers or personal circumstances are included.\n\n"
            "[B] The service coordinator agreed to compare the requested dates with "
            "the room calendar. No completion date was agreed. The calendar is not supplied.\n\n"
            "[C] The applicable booking policy and any fee approval are not supplied. "
            "The next officer must verify those points before a response is approved. "
            "No eligibility, funding or legal decision has been made."
        ),
        "system": (
            "Use only synthetic service-desk notes for an administrative brief. "
            "Cite facts and distinguish requests, commitments and unknowns. Do not "
            "infer personal circumstances, determine eligibility, priority, funding "
            "or legal rights, or approve a booking. Human review by an authorized "
            "officer is required. Do not invent policies, deadlines or benefits."
        ),
        "rehearsal": (
            "Recorded requests\n"
            "- Three booking requests logged; receipt acknowledged, none accepted. [A]\n\n"
            "Agreed next steps\n"
            "- Service coordinator: compare requested dates with the room calendar; "
            "no completion date agreed and calendar not supplied. [B]\n"
            "- Next officer: verify the applicable policy and fee approval before "
            "a response is approved. [C]\n\n"
            "Open questions\n"
            "- What does the calendar allow? [B] Which policy and fee approval apply? "
            "No eligibility, funding or legal decision has been made. [C]\n\n"
            "Administrative draft only; an authorized service officer must review."
        ),
    },
})

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
