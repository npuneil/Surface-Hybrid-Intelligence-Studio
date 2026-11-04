"""Live demo registry: synthetic fixtures, pipelines, authored rehearsal output and ground-truth checks.

Every fixture is fictional. Speech and document images are rendered on this laptop at runtime from the text
below, so the text is also the ground truth the live checks compare against. Checks only ever run on live output.
"""
import itertools
import re

import media
from content import SCENARIOS

# ---------------------------------------------------------------- speech fixtures (rendered by Windows TTS)
MEETING_SEGMENTS = (
    ("female", "Thanks for meeting with me today, Jordan. With your consent, I am recording this conversation for my call notes."),
    ("male", "That is fine. We want to start paying our suppliers in Europe directly from our operating account."),
    ("female", "I will send you the international wire setup forms by Friday. We have not discussed any fees yet."),
    ("male", "Our controller, Maria, will gather the last two years of financial statements. We did not agree a date for that."),
    ("female", "We also agreed that someone should review the daily wire limit, but nobody owns that yet."),
    ("male", "One more thing. We might want to increase our line of credit next year, but that is only an idea for now."),
    ("female", "Understood. That decision stays open. I will send these notes to you this afternoon. Thank you."),
)
MEMO_SEGMENTS = (
    ("male", "Field note for the Contoso Bank operations team. Location is teller desk three at the Riverside "
             "branch. Teller tablet H seventeen will "
             "not sync since the network dropped this morning. Battery is at forty percent and I restarted it twice. "
             "Please open a priority two ticket and send a replacement charging cradle. "
             "I am on site until three this afternoon."),
)

def script_text(segments):
    return " ".join(text for _, text in segments)


# ---------------------------------------------------------------- document images (rendered by System.Drawing)
WORK_ORDER = {
    "work_order": "WO-48217", "date_reported": "16 Oct 2026", "department": "Retail Banking Operations",
    "site": "Operations Center, Room 114", "asset": "Conference projector P-17", "priority": "P2 - High",
    "reported_by": "Sam Okafor", "technician": "J. Alvarez",
    "description": "Projector will not connect after the network outage. Restarted twice. Meeting moved to Room 116.",
    "status": "Open",
}
WORK_ORDER_CHECK = {"work_order": "WO-48217", "date_reported": "16 Oct 2026", "department": "Retail Banking",
                    "site": "Room 114", "asset": "P-17", "priority": "P2", "reported_by": "Okafor",
                    "technician": "Alvarez", "description": "network outage", "status": "Open"}
RECEIPT_ITEMS = (("Client lunch (6)", "72.00"), ("Coffee service", "9.00"), ("Fruit platter", "5.40"))
RECEIPT = {"merchant": "Bluebird Cafe & Catering", "date": "14 Oct 2026", "order_number": "2291",
           "subtotal": "86.40", "tax": "7.13", "total": "93.53", "card_last4": "4417"}
RECEIPT_CHECK = {"merchant": "Bluebird", "date": "14 Oct 2026", "order_number": "2291", "subtotal": "86.40",
                 "tax": "7.13", "total": "93.53", "card_last4": "4417"}

MEDIA_SPEC = {
    "files": [
        {"name": "meeting.wav", "kind": "speech", "rate": 16000,
         "segments": [{"voice": v, "text": t, "pause_ms": 550} for v, t in MEETING_SEGMENTS]},
        {"name": "voice-memo.wav", "kind": "speech", "rate": 16000,
         "segments": [{"voice": v, "text": t, "pause_ms": 300} for v, t in MEMO_SEGMENTS]},
        {"name": "work-order.png", "kind": "form", "width": 1100, "height": 900, "rotate": -1.2,
         "title": "BANK FACILITIES WORK ORDER",
         "subtitle": "Woodgrove Bank  |  Corporate facilities  |  Fictional form",
         "rows": [
             {"label": "Work order no.", "value": WORK_ORDER["work_order"]},
             {"label": "Date reported", "value": WORK_ORDER["date_reported"]},
             {"label": "Department", "value": WORK_ORDER["department"]},
             {"label": "Site", "value": WORK_ORDER["site"], "hand": True},
             {"label": "Asset", "value": WORK_ORDER["asset"], "hand": True},
             {"label": "Priority", "value": WORK_ORDER["priority"], "hand": True},
             {"label": "Reported by", "value": WORK_ORDER["reported_by"], "hand": True},
             {"label": "Technician", "value": WORK_ORDER["technician"], "hand": True},
             {"label": "Fault description", "value": WORK_ORDER["description"], "hand": True, "wide": True},
             {"label": "Status", "value": WORK_ORDER["status"]},
         ],
         "footer": "FICTIONAL WORK ORDER  |  Synthetic data for a local AI demonstration  |  No real institution"},
        {"name": "receipt.png", "kind": "receipt", "width": 620, "height": 640, "rotate": 1.5,
         "merchant": "BLUEBIRD CAFE & CATERING",
         "header": ["418 Harbor Row, Port Ellis", "Tel 555-0142", "14 Oct 2026   12:47", "Order #2291"],
         "lines": [{"left": d, "right": a} for d, a in RECEIPT_ITEMS] + [
             {"left": "---"}, {"left": "Subtotal", "right": "86.40"}, {"left": "Tax 8.25%", "right": "7.13"},
             {"left": "TOTAL", "right": "93.53", "bold": True}, {"left": "---"},
             {"left": "Paid: Corporate card", "right": "**** 4417"}],
         "footer": ["Thank you!", "FICTIONAL RECEIPT FOR DEMONSTRATION"]},
    ]
}
MEDIA_NAMES = tuple(item["name"] for item in MEDIA_SPEC["files"])

# ---------------------------------------------------------------- text fixtures
NOTICE = (
    "FICTIONAL NOTICE | Woodgrove Bank\n"
    "Planned maintenance on online banking will take place on Saturday 24 October 2026, "
    "from 14:00 to 16:00. During this window, new branch appointments cannot be booked online, but existing "
    "appointments are not affected. If you have an urgent request, call customer service and quote reference "
    "HD-204. We apologise for the inconvenience and thank you for your patience."
)
PROTECTED_TERMS = ("Woodgrove Bank", "HD-204", "14:00", "16:00")
LANGUAGES = (("es", "Spanish"), ("fr", "French"), ("de", "German"), ("pt-BR", "Brazilian Portuguese"),
             ("ja", "Japanese"))

POLICIES = (
    ("POL-01", "Travel booking", "Book business travel through the approved portal at least 14 days ahead. "
     "Economy class is standard for flights under six hours. The hotel limit is 180 dollars per night unless a "
     "department head approves an exception in writing."),
    ("POL-02", "Expense claims", "Submit expense claims within 30 days with itemised receipts. Meals are reimbursed "
     "up to 60 dollars per day while travelling. Alcohol is not reimbursable."),
    ("POL-03", "Lost or stolen devices", "Report a lost or stolen laptop, tablet or phone used for bank work "
     "to the IT Security Operations desk within one hour of noticing, on extension 4400 or "
     "security@woodgrove.example. Do not delay the report to keep searching. IT will remotely lock and wipe the "
     "device."),
    ("POL-04", "Data retention", "Customer account records are kept for seven years after an account closes. "
     "Meeting recordings and transcripts are deleted after 90 days unless the Legal department places them "
     "on legal hold."),
    ("POL-05", "Branch staffing", "Branch staff may work up to 40 scheduled hours per week. "
     "Overtime needs approval from the branch manager."),
    ("POL-06", "Cash handling", "Two employees must be present whenever the vault is opened. Report any cash "
     "difference, however small, to the branch manager before the end of the day."),
    ("POL-07", "Remote work", "Staff may work remotely up to three days per week with manager agreement. "
     "Customer-facing branch roles are on site during branch hours. Use the VPN for all internal systems."),
    ("POL-08", "Software requests", "Request new software through the IT service portal. Security and model risk "
     "review takes up to ten business days. AI tools must be approved by the AI governance committee before use "
     "with customer data."),
    ("POL-09", "Training", "Each employee has 24 hours of paid professional development per year. "
     "Privacy and information security training is mandatory every 12 months for anyone with access to customer records."),
    ("POL-10", "Purchasing", "Purchases up to 2,500 dollars need one budget manager approval. Larger purchases need "
     "quotes from two suppliers and procurement approval."),
    ("POL-11", "Visitors", "Visitors to operations centers must sign in at the front desk, wear a visitor badge and be "
     "escorted at all times."),
    ("POL-12", "Customer complaints", "Acknowledge customer complaints within one business day and log them in the "
     "case system with a reference number. Escalate unresolved complaints to the customer advocacy office after "
     "five business days."),
)
DOCS_QUESTION = "I think I left my work laptop on the commuter train. What should I do, and how fast?"
DOCS_EXPECTED = "POL-03"
QUERY_INSTRUCTION = "Instruct: Given a staff question, retrieve the bank policy that answers it\nQuery: "

EMAIL_OFFICE = "Woodgrove Bank customer care team"
EMAIL = (
    "FICTIONAL EMAIL\n"
    "From: Dana Whitfield <dana.whitfield@example.com>\n"
    f"To: {EMAIL_OFFICE}\n"
    "Subject: Mailing address change\n\n"
    "Hello, please update my mailing address to 1847 Lakeview Terrace, Port Ellis, PE4 7QT. My account number is "
    "ACCT-7781-2209 and my date of birth is 3 March 1984 if you need to verify me. You can reach me on 555-0187 "
    "after six. My relationship manager Marcus Lee (marcus.lee@example.com) is copied on this request.\n"
    "Thanks, Dana"
)
IDENTIFIERS = (
    ("Name: Dana Whitfield", (r"\bDana\b", r"\bWhitfield\b")),
    ("Email: dana.whitfield@example.com", (r"dana\.whitfield",)),
    ("Street address", (r"\b1847\b", r"\bLakeview\b")),
    ("Postcode", (r"\bPE4\b", r"\b7QT\b")),
    ("Account number", (r"\b7781\b", r"\b2209\b")),
    ("Date of birth", (r"\b1984\b",)),
    ("Phone number", (r"\b0187\b",)),
    ("Name: Marcus Lee", (r"\bMarcus\b",)),
    ("Email: marcus.lee@example.com", (r"marcus\.lee",)),
)

INBOX = (
    ("E1", "Branch Wi-Fi down", "Tellers at the Riverside branch have had no Wi-Fi since 07:10 and the branch opens "
     "at 09:00. Please help urgently.", "incident"),
    ("E2", "New banker laptop", "Can we order a laptop and headset for a new relationship manager starting on 2 November?",
     "request"),
    ("E3", "Invoice INV-5521 query", "Our October invoice shows 14 software licences but we only have 12. "
     "Please check.", "billing"),
    ("E4", "Thank you", "The technician who fixed the branch lobby display yesterday was excellent. Just wanted "
     "to say thanks.", "feedback"),
    ("E5", "You have won a gift card", "Click this link within 24 hours to claim your prize.", "spam"),
    ("E6", "VPN keeps dropping", "Three loan officers cannot stay connected to the VPN today; they can work offline "
     "for now.", "incident"),
)
CATEGORIES = ("incident", "request", "billing", "feedback", "spam")

SITES = ("Atlas", "Birch", "Cedar", "Delta", "Harbor")
PUZZLE = (
    "Plan a five-week pilot rollout, one branch per week, for the branches Atlas, Birch, Cedar, Delta and "
    "Harbor. Constraints: (1) Delta needs a network upgrade that finishes at the end of week 2, so Delta cannot be "
    "before week 3. (2) Harbor must be in the week immediately after Cedar. (3) Atlas cannot be in week 1 or week 5. "
    "(4) Birch must come before Atlas. (5) Delta cannot be in week 5 because of quarter-end close. (6) Cedar cannot be "
    "in week 1. Find the only schedule that satisfies every constraint. Explain the key deductions in at most five "
    "short bullets, then finish with one line exactly in this format: ORDER: branch, branch, branch, "
    "branch, branch"
)
PUZZLE_SYSTEM = ("You solve scheduling puzzles carefully and concisely. Use only the stated constraints. "
                 "Keep the explanation under 120 words and always end with the ORDER line.")
# GPT-OSS samples at temperature 1.0 and its hidden reasoning shares this cap; 3,072 occasionally ran out before
# the visible answer. Typical runs finish far below this, so the cap only bounds the worst case.
REASONING_BUDGET = 6144


def schedule_violations(order):
    """Which of the six puzzle constraints a proposed week-by-week order breaks (empty list = valid)."""
    if sorted(order) != sorted(SITES):
        return ["Not a schedule of the five sites, each exactly once"]
    week = {site: index + 1 for index, site in enumerate(order)}
    rules = (
        ("(1) Delta not before week 3", week["Delta"] >= 3),
        ("(2) Harbor immediately after Cedar", week["Harbor"] == week["Cedar"] + 1),
        ("(3) Atlas not in week 1 or 5", week["Atlas"] not in (1, 5)),
        ("(4) Birch before Atlas", week["Birch"] < week["Atlas"]),
        ("(5) Delta not in week 5", week["Delta"] != 5),
        ("(6) Cedar not in week 1", week["Cedar"] != 1),
    )
    return [name for name, ok in rules if not ok]


def valid_schedules():
    return [order for order in itertools.permutations(SITES) if not schedule_violations(order)]


def parse_order(text):
    lines = [line for line in text.splitlines() if re.match(r"\s*\**\s*ORDER\s*:", line, re.IGNORECASE)]
    if not lines:
        return None
    names = re.findall(r"[A-Za-z]+", lines[-1].split(":", 1)[1])
    found = [name.capitalize() for name in names if name.capitalize() in SITES]
    return found or None


# ---------------------------------------------------------------- registry
def _step(title, role):
    return {"title": title, "role": role}


DEMOS = {
    "meeting": {
        "title": "Client meeting to call notes and actions", "category": "Listen", "glyph": "\uE720", "workload": "transcription",
        "pitch": "Tier 1 and Tier 2 on one PC: a recorded client meeting becomes call notes and an action list without leaving the laptop.",
        "proof": "Speech recognition and drafting run on {where}; word accuracy is measured against the script.",
        "input": {"kind": "audio", "media": "meeting.wav",
                  "label": "Synthetic two-voice client meeting recorded with Windows text-to-speech"},
        "source": script_text(MEETING_SEGMENTS),
        "prompt": ("Turn this client meeting transcript into call notes with three sections: Summary (two sentences), "
                   "Actions (a table with columns Action, Owner, Due), and Open decisions. Use only what was said. "
                   "Write 'Not assigned' or 'Not agreed' when an owner or date is missing."),
        "system": ("You write concise, factual client call notes from a machine transcript. Do not invent owners, "
                   "dates, approvals, fees, rates or credit decisions. An idea is not an approved action. Keep it under "
                   "170 words. The banker must review the notes before they are sent or filed."),
        "max_tokens": 700,
        "steps": [_step("Transcribe the meeting (Tier 1)", "asr"), _step("Draft call notes and actions (Tier 2)", "chat")],
        "rehearsal": [script_text(MEETING_SEGMENTS), (
            "Summary\nJordan's business wants to pay European suppliers directly from its operating account. A larger "
            "line of credit was mentioned as an idea only; nothing was approved.\n\n"
            "Actions\nAction | Owner | Due\n"
            "Send the international wire setup forms | Relationship manager | Friday\n"
            "Gather the last two years of financial statements | Maria (controller) | Not agreed\n"
            "Review the daily wire limit | Not assigned | Not agreed\n"
            "Send the call notes | Relationship manager | This afternoon\n\n"
            "Open decisions\nWhether to increase the line of credit next year.\n\n"
            "Banker review required before the notes are sent.")],
    },
    "voice-memo": {
        "title": "Voice memo to work order", "category": "Listen", "glyph": "\uE707", "workload": "field",
        "pitch": "A technician talks; a structured work order comes back, ready for a person to check.",
        "proof": "Speech to text, then text to structured fields, on {where}; fields are checked against the memo.",
        "input": {"kind": "audio", "media": "voice-memo.wav",
                  "label": "Synthetic branch voice memo recorded with Windows text-to-speech"},
        "source": script_text(MEMO_SEGMENTS),
        "prompt": ("Convert this voice memo transcript into a work order. Return only one JSON object with the keys "
                   "department, location, asset, issue, actions_taken, battery_percent, priority, parts_requested and "
                   "technician_available_until. Give location in full as the memo states it, including any desk, room "
                   "or floor. Use null when the memo does not say."),
        "system": ("You turn branch voice memos into structured work orders. Use only what the transcript says. "
                   "Do not invent ticket numbers, causes or fixes. Return JSON only."),
        "max_tokens": 400,
        "steps": [_step("Transcribe the voice memo", "asr"), _step("Structure the work order", "chat")],
        "rehearsal": [script_text(MEMO_SEGMENTS), (
            '{\n  "department": "Contoso Bank operations team",\n  "location": "Teller desk 3, Riverside branch",\n  "asset": "Teller tablet H-17",\n'
            '  "issue": "Will not sync since the network dropped this morning",\n'
            '  "actions_taken": "Restarted twice",\n  "battery_percent": 40,\n  "priority": "P2",\n'
            '  "parts_requested": "Replacement charging cradle",\n'
            '  "technician_available_until": "Three this afternoon"\n}')],
        "check_fields": {"department": "operations", "location": "desk 3", "asset": "H17",
                         "actions_taken": "restarted", "battery_percent": "40", "priority": "2",
                         "parts_requested": "charging cradle"},
    },
    "translate": {
        "title": "Translate on the device", "category": "Write", "glyph": "\uF2B7", "workload": "translation",
        "pitch": "Customer notices translated locally, keeping names, references and times intact.",
        "proof": "Runs on the selected local model; protected terms are checked automatically, meaning needs a fluent reviewer.",
        "input": {"kind": "text", "label": "Synthetic customer notice"},
        "source": NOTICE,
        "prompt": ("Translate the notice into {language}. Keep the brand name Woodgrove Bank in English, the "
                   "reference HD-204 and the times 14:00 and 16:00 exactly as written. Return only the translation."),
        "system": ("You are a professional translator running locally. Translate faithfully and naturally. "
                   "Do not add or remove information. Never translate brand names. Keep names, references, numbers "
                   "and 24-hour times unchanged."),
        "max_tokens": 600,
        "options": [{"id": code, "label": name} for code, name in LANGUAGES],
        "option_label": "Target language",
        "steps": [_step("Translate", "chat")],
        "rehearsal": [(
            "AVISO | Woodgrove Bank\n"
            "El mantenimiento programado de la banca en línea se realizará el sábado 24 de octubre de 2026, de "
            "14:00 a 16:00. Durante este periodo no se podrán reservar en línea nuevas citas en sucursal, pero las "
            "citas existentes no se verán afectadas. Si tiene una solicitud urgente, llame al servicio de atención "
            "al cliente e indique la "
            "referencia HD-204. Disculpe las molestias y gracias por su paciencia.")],
    },
    "brief": {
        "title": "Executive brief from notes", "category": "Write", "glyph": "\uE70F", "workload": "writing",
        "pitch": "Discovery notes become a cited line-of-business and CIO agenda, on the laptop.",
        "proof": "Every point must cite its source note; the required no-benefit-claimed sentence is checked.",
        "input": {"kind": "text", "label": "Synthetic bank notes (three sources)"},
        "source": SCENARIOS["brief"]["source"], "prompt": SCENARIOS["brief"]["prompt"],
        "system": SCENARIOS["brief"]["system"], "max_tokens": 650,
        "steps": [_step("Draft the brief", "chat")],
        "rehearsal": [SCENARIOS["brief"]["rehearsal"]],
    },
    "redact": {
        "title": "De-identify before escalating (Tier 3)", "category": "Protect", "glyph": "\uE72E", "workload": "writing",
        "pitch": "Before a complex question escalates to the cloud, customer identifiers are stripped inside the firm; only the de-identified text would leave.",
        "proof": "Counts how many of the nine known synthetic identifiers survive. This demo sends nothing anywhere.",
        "input": {"kind": "text", "label": "Synthetic customer email with personal data"},
        "source": EMAIL,
        "prompt": ("Redact every personal identifier in this email so it can be shared with a cloud service. Replace "
                   "each one with a typed placeholder: [NAME], [EMAIL], [PHONE], [ADDRESS], [POSTCODE], [ACCOUNT] or "
                   "[DATE_OF_BIRTH]. Keep all other wording unchanged. Return only the redacted email."),
        "system": ("You are a privacy filter running locally. Replace personal identifiers (people's names, email "
                   "addresses, phone numbers, street addresses, postcodes, account numbers, dates of birth) with "
                   "placeholders. Institution names and office wording stay. Never echo an identifier."),
        "max_tokens": 450,
        "steps": [_step("Redact personal data", "chat")],
        "rehearsal": [(
            "FICTIONAL EMAIL\nFrom: [NAME] <[EMAIL]>\nTo: Woodgrove Bank customer care team\n"
            "Subject: Mailing address change\n\n"
            "Hello, please update my mailing address to [ADDRESS], Port Ellis, [POSTCODE]. My account number is "
            "[ACCOUNT] and my date of birth is [DATE_OF_BIRTH] if you need to verify me. You can reach me on [PHONE] "
            "after six. My relationship manager [NAME] ([EMAIL]) is copied on this request.\nThanks, [NAME]")],
    },
    "triage": {
        "title": "Inbox triage to structured data", "category": "Write", "glyph": "\uE715", "workload": "writing",
        "pitch": "A morning's bank IT inbox sorted into incidents, requests and noise, as data a workflow can use.",
        "proof": "Output must be valid JSON; categories are compared with the fixture's labels.",
        "input": {"kind": "text", "label": "Six synthetic emails"},
        "source": "FICTIONAL INBOX\n" + "\n".join(f"[{i}] Subject: {s}. {b}" for i, s, b, _ in INBOX),
        "prompt": ("Triage these six emails. Return only a JSON array with one object per email, using the keys id, "
                   "category, urgency and next_step. category must be one of incident, request, billing, feedback or "
                   "spam. urgency must be high, medium or low. next_step is one short sentence."),
        "system": ("You triage an IT service desk inbox. Classify each email from its text only. Do not reply to anyone, "
                   "click links or invent details. Return JSON only."),
        "max_tokens": 600,
        "steps": [_step("Triage the inbox", "chat")],
        "rehearsal": ['[\n' + ",\n".join(
            f'  {{"id": "{i}", "category": "{c}", "urgency": "{u}", "next_step": "{n}"}}' for i, c, u, n in (
                ("E1", "incident", "high", "Dispatch network support to the Riverside branch before 09:00."),
                ("E2", "request", "medium", "Raise a hardware order for the new relationship manager."),
                ("E3", "billing", "medium", "Ask billing to reconcile the licence count on INV-5521."),
                ("E4", "feedback", "low", "Share the thanks with the technician's manager."),
                ("E5", "spam", "low", "Report as phishing and delete; do not click the link."),
                ("E6", "incident", "medium", "Open a network ticket for the VPN drops."))) + "\n]"],
    },
    "ask-docs": {
        "title": "Ask your documents", "category": "Know", "glyph": "\uE8A5", "workload": "documents",
        "pitch": "Questions answered from approved bank policies, with citations, on the laptop.",
        "proof": "Embeddings find the right policy (checked), then the local model answers only from it; both run on {where}.",
        "input": {"kind": "text", "label": "Twelve synthetic bank policies"},
        "source": "\n".join(f"[{pid}] {title}: {text}" for pid, title, text in POLICIES),
        "prompt": DOCS_QUESTION, "prompt_label": "Question",
        "system": ("Answer the staff member's question only from the supplied policy excerpts. Cite the policy ID in "
                   "square brackets after each fact, for example [POL-03]. If the excerpts do not answer the "
                   "question, say so. Keep it under 90 words."),
        "max_tokens": 300,
        "steps": [_step("Search the documents", "embed"), _step("Answer with citations", "chat")],
        "rehearsal": [
            "1. POL-03 Lost or stolen devices\n2. POL-08 Software requests\n3. POL-07 Remote work",
            ("Report the lost laptop to the IT Security Operations desk within one hour of noticing, on extension "
             "4400 or security@woodgrove.example [POL-03]. Do not delay the report to keep searching; IT will "
             "remotely lock and wipe the device [POL-03].")],
    },
    "vision": {
        "title": "Scanned form to structured data", "category": "See", "glyph": "\uE722", "workload": "vision",
        "pitch": "Photos of forms and receipts read on the laptop, including handwriting, into data a system can use.",
        "proof": "A vision-language model runs on {where}; every field is compared with the synthetic document.",
        "input": {"kind": "image", "label": "Synthetic scanned document rendered on this laptop"},
        "source": "", "prompt": "", "max_tokens": 450,
        "options": [{"id": "work-order", "label": "Work order form (handwritten fields)"},
                    {"id": "receipt", "label": "Expense receipt"}],
        "option_label": "Document",
        "documents": {
            "work-order": {"media": "work-order.png", "check": WORK_ORDER_CHECK, "prompt": (
                "Extract the fields from this scanned work order as one JSON object with exactly these keys: "
                "work_order, date_reported, department, site, asset, priority, reported_by, technician, description, "
                "status. Copy each value exactly as printed or handwritten. Use null for a missing value. "
                "Return only JSON.")},
            "receipt": {"media": "receipt.png", "check": RECEIPT_CHECK, "prompt": (
                "Extract this receipt as one JSON object with exactly these keys: merchant, date, order_number, "
                "line_items (a list of objects with description and amount), subtotal, tax, total, card_last4. "
                "Copy values exactly as printed. Return only JSON.")},
        },
        "system": "You extract data from document images accurately. Never guess unreadable values; use null.",
        "steps": [_step("Read the document", "vision")],
        "rehearsal_options": {
            "work-order": ['{\n' + ",\n".join(f'  "{k}": "{v}"' for k, v in WORK_ORDER.items()) + "\n}"],
            "receipt": ['{\n  "merchant": "Bluebird Cafe & Catering",\n  "date": "14 Oct 2026",\n'
                        '  "order_number": "2291",\n  "line_items": [\n'
                        + ",\n".join(f'    {{"description": "{d}", "amount": "{a}"}}' for d, a in RECEIPT_ITEMS)
                        + '\n  ],\n  "subtotal": "86.40",\n  "tax": "7.13",\n  "total": "93.53",\n'
                        '  "card_last4": "4417"\n}'],
        },
    },
    "code": {
        "title": "Local coding assistant", "category": "Build", "glyph": "\uE943", "workload": "coding",
        "pitch": "{chat} drafts and explains code with no source leaving the laptop.",
        "proof": "Runs on {where}. The code is displayed for review and is never executed by this app.",
        "input": {"kind": "text", "label": "Synthetic bank IT requirement"},
        "source": SCENARIOS["code"]["source"], "prompt": SCENARIOS["code"]["prompt"],
        "system": SCENARIOS["code"]["system"], "max_tokens": 1600,
        "steps": [_step("Draft the code", "chat")],
        "rehearsal": [SCENARIOS["code"]["rehearsal"]],
    },
    "reasoning": {
        "title": "Plan it out with a reasoning model", "category": "Think", "glyph": "\uE82F", "workload": "reasoning",
        "pitch": "{reasoner} works through a multi-constraint plan, locally.",
        "proof": "The engine checks the proposed schedule against all six constraints; exactly one schedule is valid.",
        "input": {"kind": "text", "label": "Synthetic rollout constraints"},
        "source": "", "prompt": PUZZLE, "system": PUZZLE_SYSTEM, "max_tokens": REASONING_BUDGET, "model": "gpt-oss-20b",
        "steps": [_step("Reason through the plan", "gpt-oss-20b")],
        "rehearsal": [(
            "- Delta must be week 3 or 4, and Atlas week 2, 3 or 4.\n"
            "- Week 1 cannot be Delta, Atlas, Cedar or Harbor (Harbor follows Cedar), so Birch is week 1.\n"
            "- Cedar and Harbor need consecutive weeks: 2-3 and 3-4 leave no valid place for Delta.\n"
            "- So Cedar is week 4 and Harbor week 5; Delta takes week 3 and Atlas week 2.\n"
            "ORDER: Birch, Atlas, Delta, Cedar, Harbor")],
    },
    "faceoff": {
        "title": "Model face-off: {faceoff}", "category": "Scale", "glyph": "\uE9D5", "workload": "prototyping",
        "pitch": "Two large models, both resident on one laptop, answer the same question side by side.",
        "proof": "Both run on {where} with measured speed. The engine grades each answer against the puzzle's "
                 "single valid schedule, so you see which model got it right.",
        "input": {"kind": "text", "label": "Same prompt for both models"},
        "source": "", "prompt": PUZZLE, "system": PUZZLE_SYSTEM, "max_tokens": REASONING_BUDGET,
        "steps": [_step("{model} answers", "qwen2.5-coder-14b"), _step("{model} answers", "gpt-oss-20b")],
        "rehearsal": None, "independent": True,
    },
    "team": {
        "title": "Five models, one laptop", "category": "Scale", "glyph": "\uE950", "workload": "prototyping",
        "pitch": "Chat, reasoning, speech, vision and search models all resident at once, each doing real work.",
        "proof": "Each model runs a task; residency is read from Foundry Local and memory from Windows counters.",
        "input": {"kind": "text", "label": "Measurement: no customer data"},
        "source": "", "prompt": "",
        "steps": [_step("Chat model", "qwen2.5-coder-14b"), _step("Reasoning model", "gpt-oss-20b"),
                  _step("Speech model", "asr"), _step("Vision model", "vision"), _step("Embedding model", "embed"),
                  _step("Measure residency and memory", "measure")],
        "rehearsal": None, "independent": True,
    },
}
ORDER = ("meeting", "voice-memo", "vision", "ask-docs", "translate", "brief", "redact", "triage", "code",
         "reasoning", "faceoff", "team")
assert set(ORDER) == set(DEMOS)


def option_ids(demo):
    return [option["id"] for option in demo.get("options", [])]


def default_option(demo):
    ids = option_ids(demo)
    return ids[0] if ids else None


def prompt_for(demo_id, option=None):
    demo = DEMOS[demo_id]
    if demo_id == "vision":
        return demo["documents"][option or default_option(demo)]["prompt"]
    if demo_id == "translate":
        language = dict(LANGUAGES)[option or default_option(demo)]
        return demo["prompt"].replace("{language}", language)
    return demo["prompt"]


def rehearsal_for(demo_id, option=None):
    demo = DEMOS[demo_id]
    if "rehearsal_options" in demo:
        return demo["rehearsal_options"][option or default_option(demo)]
    if demo_id == "translate" and (option or default_option(demo)) != default_option(demo):
        return None
    return demo.get("rehearsal")


def is_editable(demo_id):
    return demo_id != "team"


def has_rehearsal(demo_id, option=None):
    return rehearsal_for(demo_id, option) is not None


def media_for(demo_id, option=None):
    demo = DEMOS[demo_id]
    if demo_id == "vision":
        return demo["documents"][option or default_option(demo)]["media"]
    return demo["input"].get("media")


# ---------------------------------------------------------------- wording for the PC the demos run on
# The registry text above names no model and no hardware. {where}, {chat}, {reasoner}, {faceoff} (and {model} in a step title) are filled
# from what this PC actually runs, so the same registry reads correctly on an RTX GPU, an NPU or a CPU. REFERENCE is the PC the demos
# were authored on; it is what public() and render() use until they are given the engine's own wording().
_RTX = "the RTX GPU"
REFERENCE = {"roles": {
    "chat": {"label": "Qwen2.5-Coder 14B", "where": _RTX, "lane": "gpu", "available": True},
    "qwen2.5-coder-14b": {"label": "Qwen2.5-Coder 14B", "where": _RTX, "lane": "gpu", "available": True},
    "gpt-oss-20b": {"label": "GPT-OSS 20B", "where": _RTX, "lane": "gpu", "available": True},
    "asr": {"label": "Whisper medium", "where": _RTX, "lane": "gpu", "available": True},
    "vision": {"label": "Qwen3-VL 4B", "where": _RTX, "lane": "gpu", "available": True},
    "embed": {"label": "Qwen3-Embedding 0.6B", "where": _RTX, "lane": "gpu", "available": True},
}}
ROLES = tuple(REFERENCE["roles"])
# What a role is called when this PC has no model for it.
GENERIC = {"chat": "The chat model", "qwen2.5-coder-14b": "The chat model", "gpt-oss-20b": "The reasoning model",
           "asr": "The speech model", "vision": "The vision model", "embed": "The embedding model"}
PLACEHOLDER = re.compile(r"\{(where|chat|reasoner|faceoff|model)\}")
SIZE = re.compile(r"\b\d+(?:\.\d+)?B\b")


def wording(engine):
    """The context render() and public() fill the registry's placeholders from, read off the engine: for every role, which model this PC
    runs, where it runs, and whether it can run at all."""
    roles = {}
    for role in ROLES:
        info = engine.describe(None if role == "chat" else role)
        roles[role] = {key: info[key] for key in ("label", "where", "lane", "available")}
    return {"roles": roles}


def _join(items):
    return items[0] if len(items) == 1 else ", ".join(items[:-1]) + " and " + items[-1]


def _label(context, role):
    info = context["roles"].get(role) or {}
    return info["label"] if info.get("available") else GENERIC.get(role, "The model")


def _faceoff(context):
    chat, reasoner = (context["roles"].get(role) or {} for role in ("qwen2.5-coder-14b", "gpt-oss-20b"))
    if not (chat.get("available") and reasoner.get("available")):
        return "chat model versus reasoning model"
    sizes = SIZE.findall(chat["label"]), SIZE.findall(reasoner["label"])
    # Sizes alone ("14B versus 20B") make the shorter title; names are used when a label has no size or both sizes match.
    if sizes[0] and sizes[1] and sizes[0][-1] != sizes[1][-1]:
        return f"{sizes[0][-1]} versus {sizes[1][-1]}"
    return f"{chat['label']} versus {reasoner['label']}"


def render(demo_id, context=None):
    """The demo's title, pitch, proof and step titles with this PC's model names and lanes filled in."""
    context = context or REFERENCE
    demo = DEMOS[demo_id]
    places = []
    for step in demo["steps"]:
        info = context["roles"].get(step["role"]) or {}
        if info.get("available") and info["where"] not in places:
            places.append(info["where"])
    values = {"where": _join(places) if places else "this PC", "chat": _label(context, "chat"),
              "reasoner": _label(context, "gpt-oss-20b"), "faceoff": _faceoff(context)}

    def fill(text, extra=None):
        merged = {**values, **(extra or {})}
        return PLACEHOLDER.sub(lambda found: merged.get(found.group(1), found.group(0)), text)

    steps = [fill(step["title"], {"model": _label(context, step["role"])}) for step in demo["steps"]]
    return {"title": fill(demo["title"]), "pitch": fill(demo["pitch"]), "proof": fill(demo["proof"]), "steps": steps}


def public(context=None):
    """Registry as served to the apps; every option carries its own default prompt, media and rehearsal flag."""
    rows = []
    for demo_id in ORDER:
        demo = DEMOS[demo_id]
        text = render(demo_id, context)
        row = {key: demo.get(key) for key in ("category", "glyph", "workload", "source", "option_label")}
        options = [{**option, "prompt": prompt_for(demo_id, option["id"]),
                    "media": media_for(demo_id, option["id"]), "rehearsal": has_rehearsal(demo_id, option["id"])}
                   for option in demo.get("options", [])]
        row.update(id=demo_id, title=text["title"], pitch=text["pitch"], proof=text["proof"],
                   input=dict(demo["input"], media=media_for(demo_id)), options=options,
                   prompt=prompt_for(demo_id), prompt_label=demo.get("prompt_label", "Instruction"),
                   editable=is_editable(demo_id), rehearsal=has_rehearsal(demo_id),
                   steps=[{"title": title, "role": step["role"]} for title, step in zip(text["steps"], demo["steps"])],
                   independent=bool(demo.get("independent")), default_option=default_option(demo))
        rows.append(row)
    return rows


# ---------------------------------------------------------------- checks (live output only)
def check(label, value, passed, detail=""):
    return {"type": "check", "label": label, "value": value, "pass": passed, "detail": detail}


def transcript_check(segments, transcript):
    score = media.word_errors(script_text(segments), transcript)
    return check("Word accuracy vs the synthetic script", f"{score['accuracy'] * 100:.1f}%",
                 score["accuracy"] >= 0.9,
                 f"{score['errors']} word errors in {score['reference_words']} reference words "
                 "(substitutions, insertions and deletions).")


def fields_check(label, expected, text):
    try:
        data = media.extract_json(text)
    except ValueError:
        return [check("Valid JSON", "No", False, "The output did not contain a JSON object.")]
    if isinstance(data, list):
        data = data[0] if data and isinstance(data[0], dict) else {}
    matched = [key for key, want in expected.items() if media.field_matches(want, data.get(key))]
    missed = [key for key in expected if key not in matched]
    return [check("Valid JSON", "Yes", True),
            check(label, f"{len(matched)} of {len(expected)}", len(matched) == len(expected),
                  "Mismatched: " + ", ".join(missed) if missed else "Every checked field matches.")]


def receipt_sum_check(text):
    try:
        data = media.extract_json(text)
        items = data.get("line_items") if isinstance(data, dict) else None
        amounts = [media._number(item.get("amount")) for item in items or [] if isinstance(item, dict)]
        subtotal = media._number(data.get("subtotal"))
    except (ValueError, AttributeError):
        return check("Line items add up to the subtotal", "Not checked", False, "No readable line items.")
    if not amounts or None in amounts or subtotal is None:
        return check("Line items add up to the subtotal", "Not checked", False, "Amounts were missing or not numbers.")
    total = round(sum(amounts), 2)
    return check("Line items add up to the subtotal", f"{total:.2f} vs {subtotal:.2f}", abs(total - subtotal) < 0.005,
                 f"{len(amounts)} line items summed by the engine.")


def schedule_check(text):
    order = parse_order(text)
    if not order:
        return check("Schedule satisfies all six constraints", "No ORDER line", False,
                     "The answer did not end with an ORDER line the engine could read.")
    broken = schedule_violations(order)
    return check("Schedule satisfies all six constraints", "Yes" if not broken else f"{len(broken)} broken",
                 not broken, ", ".join(order) + ("" if not broken else " | Broken: " + "; ".join(broken)))


def leak_check(text):
    leaked = [label for label, patterns in IDENTIFIERS
              if any(re.search(p, text, re.IGNORECASE) for p in patterns)]
    return check("Synthetic identifiers remaining", f"{len(leaked)} of {len(IDENTIFIERS)}", not leaked,
                 "Leaked: " + ", ".join(leaked) if leaked else "None of the nine known identifiers appear.")


def protected_terms_check(text):
    kept = [term for term in PROTECTED_TERMS if term in text]
    return check("Protected terms kept unchanged", f"{len(kept)} of {len(PROTECTED_TERMS)}",
                 len(kept) == len(PROTECTED_TERMS),
                 "Missing: " + ", ".join(t for t in PROTECTED_TERMS if t not in kept) if len(kept) < len(PROTECTED_TERMS)
                 else "Company name, reference and both times are intact. Meaning still needs a fluent reviewer.")


def triage_checks(text):
    try:
        data = media.extract_json(text)
    except ValueError:
        return [check("Valid JSON", "No", False, "The output did not contain a JSON array.")]
    rows = {str(row.get("id")).strip().upper(): row for row in data if isinstance(row, dict)} \
        if isinstance(data, list) else {}
    expected = {i: c for i, _, _, c in INBOX}
    valid = all(str(row.get("category", "")).lower() in CATEGORIES for row in rows.values())
    agree = [i for i, c in expected.items() if str(rows.get(i, {}).get("category", "")).lower() == c]
    return [check("Valid JSON with all six emails", "Yes" if set(expected) <= set(rows) else "No",
                  set(expected) <= set(rows) and valid,
                  "Every category is from the allowed list." if valid else "A category is outside the allowed list."),
            check("Categories agree with fixture labels", f"{len(agree)} of {len(expected)}",
                  len(agree) == len(expected),
                  "Disagree: " + ", ".join(i for i in expected if i not in agree) if len(agree) < len(expected)
                  else "All six match the labels written into the fixture.")]


def brief_checks(text):
    cites = len(re.findall(r"SOURCE\s+[ABC]\b", text))
    return [check("Source citations", str(cites), cites >= 4, "Each of the four points should cite SOURCE A, B or C."),
            check("No-benefit-claimed sentence present", "Yes" if "Benefits are unproven" in text else "No",
                  "Benefits are unproven" in text, "The brief must end by saying no gain has been measured.")]


def code_checks(text):
    has_block = "```python" in text and "def prioritize_tickets" in text
    return [check("Python function drafted", "Yes" if has_block else "No", has_block,
                  "Displayed for review only. This app never runs generated code.")]
