"""Synthetic banking scenarios and deterministic business-case arithmetic."""
import math

SCENARIOS = {
    'brief': {
        'title': 'Your next leadership briefing. Already prepared.',
        'label': 'Line-of-business and CIO briefing',
        'prompt': 'Prepare an executive meeting agenda with four discovery questions. After each question include one short factual note from SOURCE A, B or C. Cover the current client-meeting preparation baseline, where customer data must stay, pending approvals and the proposed pilot. Ask questions; do not answer them or predict any improvement. Return only four numbered question/note pairs, then the final sentence. Do not add a title, meeting date, time, location or attendees. End with: Benefits are unproven; no time, accuracy or savings gain has been measured.',
        'source': "FICTIONAL INSTITUTION: Zava Financial\nSOURCE A | Commercial banking discovery notes | 14 October 2026\n320 relationship managers each prepare four client-meeting briefs per week. Preparing a brief currently takes 25 minutes. Bankers often prepare between client sites where connectivity is unreliable. No AI productivity study has been conducted.\n\nSOURCE B | Information security and model risk working session | 15 October 2026\nCustomer account data and nonpublic personal information must remain within the bank. Only de-identified queries could go to a cloud model for complex questions. Model risk review, privacy review and model licensing are not yet complete. Existing cloud productivity tools remain in use; this is not a replacement proposal.\n\nSOURCE C | Pilot proposal | 16 October 2026\nPropose a 15-banker, four-week evaluation using synthetic client records only, with every draft reviewed by the banker. Compare preparation time, accuracy, connectivity dependency and banker experience against today's workflow. No device price, battery result, comparative GPU benchmark, customer outcome or committed savings is available.",
        'system': 'You prepare concise executive discovery agendas for bank leaders from synthetic source notes. Write four questions, each with a source citation and a factual note. Use only supplied facts. Do not invent outcomes, delays, errors, inefficiencies, future task durations, customer or financial outcomes or improvement estimates. Ask what the bank should evaluate, rather than predicting benefits. Keep it under 180 words. Do not add a meeting date, time, location, attendees or title. End with the requested benefits-unproven statement.',
        'rehearsal': '1. What baseline should the pilot compare against?\nSOURCE A: 320 relationship managers each prepare four client-meeting briefs per week, taking 25 minutes per brief today.\n\n2. Which data must never leave the bank?\nSOURCE B: Customer account data and nonpublic personal information stay within the bank; only de-identified queries could reach a cloud model for complex questions.\n\n3. Which approvals must precede an evaluation?\nSOURCE B: Model risk review, privacy review and model licensing are not complete. Existing cloud productivity tools remain in use.\n\n4. What would justify scaling beyond the pilot?\nSOURCE C: A 15-banker, four-week evaluation on synthetic client records, with bankers reviewing every draft, would compare preparation time, accuracy, connectivity dependence and banker experience.\n\nBenefits are unproven; no time, accuracy or savings gain has been measured.',
    },
    "code": {
        "title": "A small request. A useful first draft.",
        "label": "Bank IT developer acceleration",
        "prompt": (
            "Write a small Python function prioritize_tickets(tickets) that returns a new list "
            "of ticket dictionaries sorted by severity (critical, high, medium, low), then "
            "descending age_hours within each severity (9 hours before 3 hours). Do not mutate "
            "the input. Unknown severities must raise ValueError. Include three checks: "
            "expected IDs T-102, T-103, T-101 for the supplied sample; unchanged input; "
            "rejection of unknown severity using try/except ValueError."
        ),
        "source": (
            "FICTIONAL BANK IT SERVICE DESK REQUIREMENT\n"
            "A bank IT team wants to prototype a repeatable triage helper for branch technology "
            "and online banking access tickets.\n"
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
    'field': {
        'title': 'From branch walk-through notes to a clear handover.',
        'label': 'Snapdragon-fit: branch technology handover',
        'prompt': "Turn the supplied synthetic walk-through notes into a shift handover with exactly three headings: Observed, Follow-up, Open questions. Cite each bullet with [A], [B] or [C]. Preserve the observed counts and status. Include owners or due times only when explicitly supplied. Follow-up must contain only the two agreed commitments: shift-lead reconciliation [A] and Support's 14:00 update [C]. Put the unverified sync status in Open questions. Connectivity has already returned; do not describe it as still absent. Do not invent a fix, a cause, an impact, a deadline or a productivity benefit. Keep it under 160 words.",
        'source': 'FICTIONAL WALK-THROUGH FIXTURE | Zava Financial branch technology | 16 October 2026\n[A] At the Riverside branch, five teller-station checks were logged this shift. Four were entered digitally and one was recorded on paper. The paper record has not been reconciled. The shift lead owns reconciliation; no due time was agreed.\n\n[B] Cash recycler CR-17 could not sync during a nine-minute Wi-Fi interruption. Connectivity returned, but CR-17 sync status has not been rechecked. Cause and customer impact are unknown.\n\n[C] The branch operations coordinator opened ticket HD-204 for the CR-17 sync check. Support owns the next status update, due at 14:00. A replacement unit has not been approved. No speech recording or image was supplied.',
        'system': 'You draft concise operational handovers for branch technology teams using only supplied synthetic notes. Distinguish observations, agreed follow-up and unresolved questions. Every bullet must cite its source. Do not infer that an interruption affected customers or that an issue is fixed. Do not offer repair instructions or invent approvals, owners, deadlines or benefits. Human review is required before any action; you do not perform actions.',
        'rehearsal': "Observed\n- The Riverside branch logged five teller-station checks: four digital and one on paper. [A]\n- CR-17 could not sync during a nine-minute Wi-Fi interruption; connectivity returned, but sync has not been rechecked. [B]\n\nFollow-up\n- Shift lead: reconcile the paper record; no due time agreed. [A]\n- Support: provide the next update on HD-204 at 14:00. [C]\n\nOpen questions\n- What is CR-17's current sync status, cause and customer impact? [B]\n- Will a replacement be approved? It is not approved now. [C]",
    },
    'meeting': {
        'title': 'Turn an operations meeting into a reviewable action register.',
        'label': 'Intel-fit: branch operations committee actions',
        'prompt': "Create a concise action register from the supplied synthetic meeting transcript, using columns Action, Owner, Due, Source. Include only explicitly agreed actions. Write 'Not assigned' or 'Not agreed' for missing owners or due dates. Follow the register with one separate Open decision. Do not turn a suggestion into an approved action, invent dates, or predict benefits. Keep it under 170 words.",
        'source': "FICTIONAL TRANSCRIPT FIXTURE | Zava Financial branch operations committee | 16 October 2026\n[A] Operations lead: 'I will document the current account-servicing workflow by 20 October 2026. We have not measured drafting time or content quality.'\n\n[B] Branch manager: 'I will select ten synthetic customer cases for review. We have not agreed a due date for that.'\n\n[C] Committee chair: 'We agree to request a model risk and privacy review, but we have not assigned the request or set a date.'\n\n[D] Regional executive: 'A two-week local-AI pilot is a suggestion. Funding and the target Intel configuration are not approved. The decision is open.'\nThis supplied text is a fixture, not a recording or a measured transcription.",
        'system': 'Extract a small action register only from explicit commitments in the supplied synthetic transcript. Cite [A], [B] or [C] for action rows, and [D] for the open decision. Preserve missing information honestly. Do not interpret a proposed pilot as approved, invent dates or owners, claim transcription was performed, or predict customer, quality, time or savings improvements. Human review is required. You do not create tasks in any external system.',
        'rehearsal': 'Action | Owner | Due | Source\nDocument current account-servicing workflow | Operations lead | 20 October 2026 | [A]\nSelect ten synthetic customer cases for review | Branch manager | Not agreed | [B]\nRequest a model risk and privacy review | Not assigned | Not agreed | [C]\n\nOpen decision: Whether to fund a two-week local-AI pilot and approve the target Intel configuration remains open. [D]',
    },
    "tutor": {
        "title": "Read code like a second language.",
        "label": "Coding tutor (plain English)",
        "prompt": (
            "Explain this Python code in plain English for a learner who is new to code. Give a one-sentence summary, "
            "then a short note for each line, then one everyday analogy, then one sentence the learner could say to a colleague. "
            "Do not run the code or claim it was run.\n\n"
            "```python\n"
            "def daily_summaries(advisors, per_advisor):\n"
            "    return advisors * per_advisor\n\n"
            "total = daily_summaries(240, 5)\n"
            "print(total)\n"
            "```"
        ),
        "source": (
            "FICTIONAL LEARNER PROFILE | Banker and operations AI literacy: Coding as a second language\n"
            "The learner works in branch operations, has never written code, and wants to "
            "follow technical conversations with bank IT developers, data scientists and architects.\n"
            "The code in the prompt is displayed for learning only. It is never executed by this app."
        ),
        "system": (
            "You are a patient coding tutor running locally, teaching a learner who is new to code. "
            "Explain only the code supplied in the prompt, in plain English, and define any jargon in brackets. "
            "Use four short sections: Summary (one sentence), Line by line (one bullet per line or block), "
            "Analogy (one everyday comparison), Say it to a colleague (one sentence). "
            "Never claim the code was run or show output as observed; say what it would do. "
            "Do not claim performance, speed, cost or learning benefits. "
            "End by noting that a developer should review and test code before anyone relies on it. "
            "Keep it under 200 words."
        ),
        "rehearsal": (
            "Summary\n"
            "This code defines a small reusable recipe that multiplies two numbers, uses it once, "
            "and would show the answer.\n\n"
            "Line by line\n"
            "- def daily_summaries(advisors, per_advisor): defines a function (a named recipe) with two inputs.\n"
            "- return advisors * per_advisor multiplies the inputs (* means multiply) and hands the answer back.\n"
            "- total = daily_summaries(240, 5) uses the recipe with 240 and 5 and stores the answer as total.\n"
            "- print(total) would show 1200 on screen.\n\n"
            "Analogy\n"
            "A function is like a recipe card: give it the ingredients and it makes the dish the same way every time.\n\n"
            "Say it to a colleague\n"
            "\"Developers build software from small, named recipes like this, and test each one on its own.\"\n\n"
            "A developer should review and test code before anyone relies on it."
        ),
    },
}

SCENARIOS.update({
    'kyc-refresh': {
        'industry': 'Know your customer',
        'client': 'Zava Financial',
        'deck_objective': 'Explain a banker-reviewed local drafting pilot that turns periodic KYC review notes into a checklist, with no risk-rating or suspicious-activity determination.',
        'title': 'KYC refresh notes to a review checklist.',
        'label': 'KYC: periodic refresh checklist',
        'prompt': 'Draft a review list with three headings: Documents on file, Missing or expired, Needs a KYC analyst. Cite every bullet [A], [B] or [C]. Keep the recorded numbers. Do not assign a risk rating, decide whether to keep or exit the customer, make a suspicious-activity determination, or invent documents or dates. Keep it under 180 words. End with: Draft only; a KYC analyst must review before any action.',
        'source': 'FICTIONAL SYNTHETIC FIXTURE | Zava Financial | Periodic review KYC-5530, small-business customer C-2291\n\n[A] Articles of organization and a beneficial-ownership certification dated March 2024 are on file. Two beneficial owners are listed.\n\n[B] The government ID for one beneficial owner expired in August 2026. The business address on file differs from the address on the latest statement.\n\n[C] The relationship manager asked whether the address change needs a new site visit. No risk-rating change has been made.',
        'system': 'Draft a cited KYC refresh checklist from synthetic review notes only. Separate documents on file, missing or expired items and questions for a KYC analyst. Do not assign risk ratings, decide on the relationship, make suspicious-activity determinations or invent documents or dates. Analyst review is required.',
        'rehearsal': "Documents on file\n- Articles of organization and a March 2024 beneficial-ownership certification; two beneficial owners listed. [A]\n\nMissing or expired\n- One beneficial owner's government ID expired in August 2026; the address on file differs from the latest statement. [B]\n\nNeeds a KYC analyst\n- Whether the address change needs a new site visit; no risk-rating change has been made. [C]\n\nDraft only; a KYC analyst must review before any action.",
    },
    'model-risk-review': {
        'industry': 'Model risk management',
        'client': 'Zava Financial',
        'deck_objective': 'Explain a local drafting pilot that turns model documentation notes into a validation-readiness checklist for a model validator, with no approval, risk tier or validation conclusion.',
        'title': 'Model documentation notes to a validation-readiness checklist.',
        'label': 'Model risk: documentation readiness',
        'prompt': 'Draft a review list with three headings: Documented, Gaps, Needs a model validator. Cite every bullet [A], [B] or [C]. Keep the recorded numbers. Do not approve the model, assign a risk tier or rating, reach a validation conclusion, or invent tests, metrics or dates. Keep it under 180 words. End with: Draft only; a model validator must review, and no validation conclusion has been reached.',
        'source': 'FICTIONAL SYNTHETIC FIXTURE | Zava Financial | Model inventory MRM-2207, call-summary drafting assistant\n\n[A] The model owner documented the intended use, the data sources and a test set of 300 synthetic call transcripts. The design document is dated 14 September 2026.\n\n[B] No ongoing monitoring plan is attached. The documentation does not describe how outputs are reviewed before they are saved.\n\n[C] The model owner asked whether a vendor model update requires a new review. No validation has started.',
        'system': 'Draft a cited validation-readiness checklist from synthetic model documentation notes only. Separate what is documented, gaps and questions for a model validator. Do not approve models, assign risk tiers or ratings, reach validation conclusions, or invent tests, metrics or dates. Model validator review is required.',
        'rehearsal': 'Documented\n- Intended use, data sources and a test set of 300 synthetic call transcripts; design document dated 14 September 2026. [A]\n\nGaps\n- No ongoing monitoring plan attached; how outputs are reviewed before saving is not described. [B]\n\nNeeds a model validator\n- Whether a vendor model update requires a new review; no validation has started. [C]\n\nDraft only; a model validator must review, and no validation conclusion has been reached.',
    },
    'card-dispute': {
        'industry': 'Card disputes',
        'client': 'Zava Financial',
        'deck_objective': 'Explain a local drafting pilot that turns card dispute intake notes into a case checklist for an analyst, with no liability or outcome determination.',
        'title': 'Dispute intake notes to a case checklist.',
        'label': 'Disputes: card dispute intake',
        'prompt': 'Draft a review list with three headings: Reported by the cardholder, Evidence on file, Needs a dispute analyst. Cite every bullet [A], [B] or [C]. Keep the recorded numbers. Do not decide liability, approve or deny the dispute, issue a provisional credit, label anything as fraud, or invent transactions, amounts or dates. Keep it under 180 words. End with: Draft only; a dispute analyst must review before any decision.',
        'source': 'FICTIONAL SYNTHETIC FIXTURE | Zava Financial | Dispute intake DSP-3307\n\n[A] The cardholder reported two charges of 89.99 dollars from one online merchant on 12 October 2026 and says only one purchase was made.\n\n[B] The order confirmation for one purchase is attached. No merchant contact has been recorded.\n\n[C] Whether the card was present for either charge is unknown. No provisional credit has been issued.',
        'system': 'Draft a cited dispute intake checklist from synthetic notes only. Separate what the cardholder reported, evidence on file and questions for a dispute analyst. Do not decide liability, approve or deny disputes, issue credits, label fraud or invent transactions, amounts or dates. Analyst review is required.',
        'rehearsal': 'Reported by the cardholder\n- Two 89.99 dollar charges from one online merchant on 12 October 2026; the cardholder says one purchase was made. [A]\n\nEvidence on file\n- One order confirmation attached; no merchant contact recorded. [B]\n\nNeeds a dispute analyst\n- Whether the card was present for either charge is unknown; no provisional credit has been issued. [C]\n\nDraft only; a dispute analyst must review before any decision.',
    },
    'settlement-breaks': {
        'industry': 'Markets operations',
        'client': 'Zava Financial',
        'deck_objective': 'Explain a local drafting pilot that turns trade settlement break notes into an exception checklist for an operations analyst, with no booking change, valuation or counterparty determination.',
        'title': 'Settlement break notes to an exception checklist.',
        'label': 'Markets operations: settlement breaks',
        'prompt': 'Draft a review list with three headings: Matched, Breaks to investigate, Needs an operations analyst. Cite every bullet [A], [B] or [C]. Keep the recorded numbers. Do not amend bookings, decide which side is correct, assign fault to a counterparty, estimate profit or loss, or invent trades, amounts or dates. Keep it under 180 words. End with: Draft only; an operations analyst must review before any booking change.',
        'source': 'FICTIONAL SYNTHETIC FIXTURE | Zava Financial | Settlement exceptions SET-6604, value date 23 October 2026\n\n[A] 412 of 418 fixed-income trades for the value date matched the custodian confirmations.\n\n[B] Four trades show a quantity difference against the counterparty confirmation. Two trades are missing standard settlement instructions.\n\n[C] The desk asked whether the four quantity breaks come from one late amendment. No booking has been changed and no counterparty has been contacted.',
        'system': 'Draft a cited settlement exception checklist from synthetic operations notes only. Separate matched items, breaks to investigate and questions for an operations analyst. Do not amend bookings, decide which side is correct, assign fault, estimate profit or loss, or invent trades, amounts or dates. Operations analyst review is required.',
        'rehearsal': 'Matched\n- 412 of 418 fixed-income trades for the 23 October 2026 value date matched custodian confirmations. [A]\n\nBreaks to investigate\n- Four trades show a quantity difference against the counterparty confirmation; two trades are missing standard settlement instructions. [B]\n\nNeeds an operations analyst\n- Whether the four quantity breaks come from one late amendment; no booking has been changed and no counterparty contacted. [C]\n\nDraft only; an operations analyst must review before any booking change.',
    },
    'treasury-onboarding': {
        'industry': 'Treasury management',
        'client': 'Zava Financial',
        'deck_objective': 'Explain a local drafting pilot that tracks commercial treasury onboarding items for an implementation specialist, with no limit or credit decision.',
        'title': 'Treasury onboarding notes to an implementation checklist.',
        'label': 'Treasury: commercial onboarding',
        'prompt': 'Draft a review list with three headings: Completed, Outstanding, Needs the implementation lead. Cite every bullet [A], [B] or [C]. Keep the recorded numbers. Do not approve payment limits or credit exposure, give legal advice, or invent services, dates or amounts. Keep it under 180 words. End with: Draft only; the implementation specialist must confirm before go-live.',
        'source': 'FICTIONAL SYNTHETIC FIXTURE | Zava Financial | Treasury onboarding TM-8821\n\n[A] The commercial client signed the service agreement for ACH origination and positive pay. Three of five user entitlements are set up.\n\n[B] The ACH file-format test has not been run. The requested daily ACH limit of 500,000 dollars has not been reviewed.\n\n[C] The client asked for go-live before month-end. No go-live date has been agreed.',
        'system': 'Draft a cited treasury onboarding checklist from synthetic notes only. Separate completed items, outstanding items and questions for the implementation lead. Do not approve limits or credit exposure, give legal advice, or invent services, dates or amounts. Implementation specialist review is required.',
        'rehearsal': 'Completed\n- Service agreement signed for ACH origination and positive pay; three of five user entitlements set up. [A]\n\nOutstanding\n- ACH file-format test not run; the requested 500,000 dollar daily ACH limit has not been reviewed. [B]\n\nNeeds the implementation lead\n- Go-live before month-end was requested; no go-live date has been agreed. [C]\n\nDraft only; the implementation specialist must confirm before go-live.',
    },
    'wealth-review': {
        'industry': 'Wealth management',
        'client': 'Zava Financial',
        'deck_objective': "Explain a local drafting pilot that prepares an advisor's review agenda from meeting notes, with no investment recommendation or suitability determination.",
        'title': 'Client review notes to an advisor agenda.',
        'label': 'Wealth: client review preparation',
        'prompt': 'Draft a review list with three headings: Discussed last time, Open follow-ups, Needs the advisor. Cite every bullet [A], [B] or [C]. Keep the recorded numbers. Do not recommend investments, assess suitability, forecast returns, or invent holdings, amounts or dates. Keep it under 180 words. End with: Draft only; the advisor must review, and this is not investment advice.',
        'source': "FICTIONAL SYNTHETIC FIXTURE | Zava Financial | Household review HH-1902\n\n[A] The household met the advisor on 2 September 2026 and discussed funding a grandchild's education account. Current holdings are not included in these notes.\n\n[B] The advisor agreed to send education-account information. Whether it was sent is not recorded.\n\n[C] The client asked about changing beneficiary designations. No change has been requested or made.",
        'system': 'Draft a cited client review agenda from synthetic meeting notes only. Separate what was discussed, open follow-ups and items for the advisor. Do not recommend investments, assess suitability, forecast returns or invent holdings, amounts or dates. Advisor review is required.',
        'rehearsal': "Discussed last time\n- Met on 2 September 2026 and discussed funding a grandchild's education account; holdings not included. [A]\n\nOpen follow-ups\n- The advisor agreed to send education-account information; whether it was sent is not recorded. [B]\n\nNeeds the advisor\n- Changing beneficiary designations was raised; no change has been requested or made. [C]\n\nDraft only; the advisor must review, and this is not investment advice.",
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
