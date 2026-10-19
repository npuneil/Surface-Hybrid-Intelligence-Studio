"""Studio workbench: run a request down its hybrid lane, measure GPU speed, and test the builder endpoint.

Nothing here sends data to a cloud service. Where a lane would use the cloud, the payload is shown and marked NOT SENT.
"""
import json
import secrets
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone

import demos
import hybrid
from local_engine import HarmonyFilter, model_spec

LOCAL_SYSTEM = ("You are an assistant running entirely on this PC with no internet access. Answer from the text and "
                "your own knowledge. If the request needs current information (prices, rates, news, latest versions) "
                "or organisation-wide data you cannot see, say plainly what is missing, then help as far as you can "
                "without it. Be concise.")

DECODE_SYSTEM = "You are a clear technical writer. Write plain prose with no headings or lists."
DECODE_PROMPT = ("Explain to a new developer how a laptop GPU runs a language model locally: where the weights live, "
                 "what prefill and decoding are, and why tokens per second matters for an app. About 200 words.")
PREFILL_SYSTEM = "Answer in one short sentence."
PREFILL_TARGET = 10000
SPEED_NOTE = ("One run on this PC with this model and prompt. Speeds vary with the model, prompt length, temperature "
              "and whatever else the GPU is doing. Not a benchmark or a guarantee.")


def _reasoning(engine):
    try:
        return bool(model_spec(engine.state.get("selected_model"))["reasoning"])
    except Exception:
        return False


def _model(engine):
    return dict(engine.state.get("model") or {})


def _stream(engine, prompt, system, max_tokens, stage):
    """Relay one generation as stage-tagged events and return its text."""
    parts, done = [], None
    for event in engine.stream(prompt, system, max_tokens=max_tokens):
        if event["type"] == "token":
            parts.append(event["text"])
            yield {"type": "token", "stage": stage, "text": event["text"]}
        elif event["type"] == "activity":
            yield {"type": "activity", "stage": stage, "reasoning_characters": event.get("reasoning_characters")}
        elif event["type"] == "done":
            done = event
    yield {"type": "model_done", "stage": stage, "metrics": done or {}}
    return "".join(parts)


# ------------------------------------------------------------------ hybrid router run
def hybrid_run(engine, text, network="online", scale="fits"):
    started = time.perf_counter()
    decision = hybrid.route(text, network, scale)
    yield {"type": "route", "decision": decision}
    lane = decision["lane"]
    ran = False
    note = None
    if lane == "cloud":
        yield {"type": "payload", "withheld": False, **decision["cloud_payload"]}
        note = "No model ran: nothing sensitive was found, so this goes to the cloud as written. The studio never sends it."
    elif lane == "local":
        yield {"type": "stage", "stage": "local", "title": "Answer on this PC's GPU",
               "detail": "The full text stays on the device."}
        yield from _stream(engine, text.strip(), LOCAL_SYSTEM, engine.generation_budget("brief"), "local")
        ran = True
        if decision["deferred"] and decision["cloud_payload"]:
            yield {"type": "queued", **decision["cloud_payload"],
                   "detail": "Held on this PC until the network returns, then sent to your approved service."}
            note = "Offline is a what-if toggle. The studio does not change or test your network connection."
        elif decision["cloud_blocked"]:
            note = "The cloud step is blocked because the text holds credentials."
    else:
        yield {"type": "stage", "stage": "redact", "title": "Redact on this PC's GPU",
               "detail": "The on-device model replaces identifiers with placeholders."}
        budget = max(engine.generation_budget("brief"), min(1600, len(text) // 3 + 200))
        prompt = hybrid.redaction_prompt(text)
        redacted = (yield from _stream(engine, prompt, hybrid.REDACT_SYSTEM, budget, "redact"))
        redacted = hybrid.unfence(redacted)
        ran = True
        survived = hybrid.leaks(text, redacted)
        drifted = None if survived else hybrid.drift(text, redacted)
        if survived:
            detail = f"Blocked: {len(survived)} detected identifier(s) survived redaction, so nothing would be sent."
        elif drifted:
            detail = (f"Blocked: the on-device model changed the request instead of only redacting it ({drifted}), "
                      "so nothing would be sent.")
        else:
            detail = ("Re-scan passed: no identifier the patterns detected survived. Names and context can still "
                      "slip through, so a person reviews before anything is shared.")
        yield {"type": "check", "passed": not survived and not drifted, "leaks": survived, "drift": drifted,
               "detail": detail}
        if survived or drifted:
            yield {"type": "payload", "withheld": True, "sent": False, "text": None, "characters": 0,
                   "note": "Withheld. The check blocked this payload; run it again or redact by hand."}
        else:
            yield {"type": "payload", "withheld": False, "sent": False, "text": redacted, "characters": len(redacted),
                   "note": hybrid.NOT_SENT}
    yield {"type": "complete", "lane": lane, "model_ran": ran, "sent": False, "note": note,
           "total_ms": round((time.perf_counter() - started) * 1000)}


# ------------------------------------------------------------------ speedometer
def prefill_text(nonce):
    """Synthetic policy text repeated to roughly PREFILL_TARGET characters. The nonce defeats prompt caching."""
    body = []
    while sum(len(line) + 1 for line in body) < PREFILL_TARGET:
        body.extend(f"{pid} {title}. {text}" for pid, title, text in demos.POLICIES)
    return (f"Reference {nonce}. Synthetic policy text, repeated:\n" + "\n".join(body)[:PREFILL_TARGET]
            + "\n\nQuestion: what is the hotel limit per night?")


def phase_result(phase, timeline, done, prompt_chars):
    """Turn one generation's timing into labelled speeds. `timeline` holds (seconds since request, kind) events."""
    first = timeline[0][0] if timeline else None
    last = timeline[-1][0] if timeline else None
    done = done or {}
    tokens = done.get("output_tokens")
    prompt_tokens = done.get("prompt_tokens")
    out = {"phase": phase, "ttft_ms": round(first * 1000) if first is not None else None,
           "total_ms": done.get("total_ms") or (round(last * 1000) if last is not None else None),
           "output_tokens": tokens, "prompt_tokens": prompt_tokens, "events": len(timeline)}
    if done.get("decode_tokens_per_second"):
        out["decode_tps"], out["decode_source"] = done["decode_tokens_per_second"], "llama.cpp runtime timings"
    elif tokens and tokens > 1 and first is not None and last and last > first:
        out["decode_tps"] = round((tokens - 1) / (last - first), 1)
        out["decode_source"] = "Measured: output tokens after the first, over streaming time"
    else:
        out["decode_tps"], out["decode_source"] = None, "Not enough tokens to measure"
    if done.get("prompt_tokens_per_second"):
        out["prefill_tps"], out["prefill_source"] = done["prompt_tokens_per_second"], "llama.cpp runtime timings"
    elif prompt_tokens and first:
        out["prefill_tps"] = round(prompt_tokens / first)
        out["prefill_source"] = "Estimate: prompt tokens over time to first token (includes request setup)"
    elif first:
        out["prefill_tps"] = round(prompt_chars / 4 / first)
        out["prefill_source"] = "Rough estimate: about 4 characters per token, over time to first token"
    else:
        out["prefill_tps"], out["prefill_source"] = None, "Not measured"
    return out


def _timed(engine, phase, prompt, system, max_tokens, show_text):
    started = time.perf_counter()
    timeline, done, last_tick, chars = [], None, 0.0, 0
    try:
        for event in engine.stream(prompt, system, max_tokens=max_tokens):
            now = time.perf_counter() - started
            if event["type"] in ("token", "activity"):
                timeline.append((now, event["type"]))
                if event["type"] == "token":
                    chars += len(event["text"])
                    if show_text:
                        yield {"type": "token", "phase": phase, "text": event["text"]}
                if now - last_tick >= 0.1:
                    last_tick = now
                    span = now - timeline[0][0]
                    yield {"type": "progress", "phase": phase, "elapsed_ms": round(now * 1000),
                           "chunks": len(timeline), "characters": chars,
                           "live_rate": round((len(timeline) - 1) / span, 1) if span > 0.25 else None}
            elif event["type"] == "done":
                done = event
    except RuntimeError as exc:
        # A reasoning model can spend a tiny prefill budget thinking; the timing up to then is still valid.
        if "no visible answer" not in str(exc) or not timeline:
            raise
    return timeline, done


def speedtest(engine):
    reasoning = _reasoning(engine)
    model = _model(engine)
    yield {"type": "start", "model": model, "reasoning": reasoning,
           "phases": [{"id": "decode", "title": "Generation speed",
                       "detail": "A fixed ~200-word explainer. Measures tokens generated per second."},
                      {"id": "prefill", "title": "Reading speed",
                       "detail": f"About {PREFILL_TARGET:,} characters of synthetic policy text. Measures how fast "
                                 "the GPU reads a long prompt."}]}
    yield {"type": "phase", "phase": "decode", "state": "running"}
    timeline, done = yield from _timed(engine, "decode", DECODE_PROMPT, DECODE_SYSTEM,
                                       768 if reasoning else 320, True)
    decode = phase_result("decode", timeline, done, len(DECODE_PROMPT))
    yield {"type": "phase", "phase": "decode", "state": "done", "result": decode}
    prompt = prefill_text(secrets.token_hex(4))
    yield {"type": "phase", "phase": "prefill", "state": "running", "characters": len(prompt)}
    timeline, done = yield from _timed(engine, "prefill", prompt, PREFILL_SYSTEM, 512 if reasoning else 16, False)
    prefill = phase_result("prefill", timeline, done, len(prompt))
    yield {"type": "phase", "phase": "prefill", "state": "done", "result": prefill}
    yield {"type": "complete", "model": model, "measured_at": datetime.now(timezone.utc).isoformat(),
           "decode_tps": decode["decode_tps"], "decode_source": decode["decode_source"],
           "ttft_ms": decode["ttft_ms"], "output_tokens": decode["output_tokens"],
           "prefill_tps": prefill["prefill_tps"], "prefill_source": prefill["prefill_source"],
           "prefill_prompt_tokens": prefill["prompt_tokens"], "prefill_characters": len(prompt),
           "note": SPEED_NOTE}


# ------------------------------------------------------------------ builder endpoint
def active_target(engine):
    """(endpoint dict, base_url, model) for the endpoint a builder would call now."""
    info = engine.endpoint_info()
    endpoint = info["large"] or info["foundry"]
    return info, endpoint, endpoint.get("base_url"), endpoint.get("model")


def endpoint_test(engine, timeout=300):
    """One real, non-streaming call to the loopback endpoint, exactly as a builder's code would make it."""
    info, endpoint, base_url, model = active_target(engine)
    if endpoint.get("state") != "running" or not base_url or not model:
        raise RuntimeError("Start the Foundry Local endpoint first, or load a larger model that serves its own.")
    body = {"model": model, "max_tokens": 512 if _reasoning(engine) else 64, "temperature": 0,
            "messages": [{"role": "user", "content": "Say hello from my Surface in one short sentence."}]}
    request = urllib.request.Request(base_url + "/chat/completions", data=json.dumps(body).encode(),
                                     headers={"Content-Type": "application/json"})
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            data = json.loads(response.read())
    except urllib.error.HTTPError as exc:
        detail = exc.read()[:300].decode("utf-8", "replace")
        raise RuntimeError(f"The endpoint answered HTTP {exc.code}: {detail}") from None
    except (urllib.error.URLError, OSError) as exc:
        raise RuntimeError(f"Could not reach {base_url}: {getattr(exc, 'reason', exc)}") from None
    choice = (data.get("choices") or [{}])[0]
    message = choice.get("message") or {}
    reply, withheld = visible_reply(message, _reasoning(engine))
    return {"ok": True, "runtime": endpoint.get("runtime"), "base_url": base_url, "model": model,
            "ms": round((time.perf_counter() - started) * 1000), "request": body,
            "reply": reply, "reasoning_characters_withheld": withheld, "finish_reason": choice.get("finish_reason"),
            "usage": data.get("usage") or {}, "endpoints": info}


def visible_reply(message, reasoning_model):
    """The final answer only. Private reasoning (a separate field or GPT-OSS channel framing) is counted, never shown."""
    content = message.get("content") or ""
    withheld = len(message.get("reasoning_content") or message.get("reasoning") or "")
    if reasoning_model or "<|channel|>" in content:
        try:
            content, hidden = HarmonyFilter().feed(content, final=True)
            withheld += hidden
        except RuntimeError:
            withheld += len(content)
            content = ""
    return content.strip(), withheld
