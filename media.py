"""Standard-library helpers for the live demos: audio chunking, accuracy checks, JSON extraction and similarity."""
import array
import json
import math
import re
import struct
import wave
import zlib

ONES = ("zero one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen "
        "sixteen seventeen eighteen nineteen").split()
TENS = ("", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety")


def read_wav(path):
    with wave.open(str(path), "rb") as source:
        if source.getnchannels() != 1 or source.getsampwidth() != 2:
            raise ValueError("Expected 16-bit mono WAV audio.")
        rate = source.getframerate()
        frames = source.readframes(source.getnframes())
    pcm = array.array("h")
    pcm.frombytes(frames)
    return rate, pcm


def write_wav(path, rate, pcm):
    with wave.open(str(path), "wb") as out:
        out.setnchannels(1)
        out.setsampwidth(2)
        out.setframerate(rate)
        out.writeframes(pcm.tobytes())


def plan_chunks(pcm, rate, max_seconds=28.0, min_seconds=12.0, window_ms=20, smooth_frames=10):
    """Split audio at the latest quiet point between min and max seconds.

    Whisper decodes one 30-second window per call, so longer audio must be chunked; cutting inside a pause
    keeps words whole. Energy is averaged over 20 ms frames and smoothed over ~200 ms so a cut always sits
    at least ~100 ms away from speech.
    """
    total = len(pcm)
    if total == 0:
        return []
    max_len = int(max_seconds * rate)
    if total <= max_len:
        return [(0, total)]
    window = max(1, rate * window_ms // 1000)
    energy = [sum(abs(x) for x in pcm[i:i + window]) / len(pcm[i:i + window]) for i in range(0, total, window)]
    prefix = [0.0]
    for value in energy:
        prefix.append(prefix[-1] + value)
    half = smooth_frames // 2

    def smoothed(k):
        lo, hi = max(0, k - half), min(len(energy), k + half + 1)
        return (prefix[hi] - prefix[lo]) / (hi - lo)

    chunks, start = [], 0
    while total - start > max_len:
        lo = (start + int(min_seconds * rate)) // window
        hi = max(lo + 1, (start + max_len) // window)
        scores = [(smoothed(k), k) for k in range(lo, hi)]
        floor = min(score for score, _ in scores)
        quiet = max(floor * 1.5, floor + 30.0)
        cut = max(k for score, k in scores if score <= quiet) * window
        if cut <= start:
            cut = start + max_len
        chunks.append((start, cut))
        start = cut
    chunks.append((start, total))
    return chunks


def number_words(n):
    if n < 20:
        return [ONES[n]]
    if n < 100:
        return [TENS[n // 10]] + ([ONES[n % 10]] if n % 10 else [])
    if n < 1000:
        return [ONES[n // 100], "hundred"] + (number_words(n % 100) if n % 100 else [])
    if n < 1000000:
        return number_words(n // 1000) + ["thousand"] + (number_words(n % 1000) if n % 1000 else [])
    return [str(n)]


def normalize_words(text):
    """Lower-case words with digits spelled out, so '40%' and 'forty percent' compare equal."""
    text = str(text).lower().replace("%", " percent ").replace("&", " and ")
    words = []
    for token in re.findall(r"[a-z]+|\d+", text):
        words.extend(number_words(int(token)) if token.isdigit() and len(token) <= 6 else [token])
    return words


def word_errors(reference, hypothesis):
    """Word-level edit distance (substitutions + insertions + deletions) against a known reference."""
    ref, hyp = normalize_words(reference), normalize_words(hypothesis)
    previous = list(range(len(hyp) + 1))
    for i, word in enumerate(ref, 1):
        current = [i] + [0] * len(hyp)
        for j, other in enumerate(hyp, 1):
            current[j] = min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + (word != other))
        previous = current
    errors = previous[-1]
    accuracy = max(0.0, 1.0 - errors / len(ref)) if ref else 0.0
    return {"errors": errors, "reference_words": len(ref), "accuracy": accuracy}


def extract_json(text):
    """The first JSON object or array in model output, tolerating code fences and surrounding prose."""
    decoder = json.JSONDecoder()
    for match in re.finditer(r"[\[{]", text):
        try:
            value, _ = decoder.raw_decode(text, match.start())
        except ValueError:
            continue
        if isinstance(value, (dict, list)):
            return value
    raise ValueError("No JSON object was found in the output.")


def _number(value):
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return float(value)
    cleaned = re.sub(r"[\s$£€,]", "", str(value))
    return float(cleaned) if re.fullmatch(r"-?\d+(\.\d+)?", cleaned) else None


def field_matches(expected, actual):
    """True when the expected value appears in the extracted value (numbers compare numerically)."""
    if actual is None:
        return False
    a, b = _number(expected), _number(actual)
    if a is not None and b is not None:
        return abs(a - b) < 0.005
    want, got = normalize_words(expected), normalize_words(actual if not isinstance(actual, (list, dict)) else json.dumps(actual))
    if not want:
        return not got
    return any(got[i:i + len(want)] == want for i in range(len(got) - len(want) + 1))


def cosine(a, b):
    dot = sum(x * y for x, y in zip(a, b))
    norm = math.sqrt(sum(x * x for x in a)) * math.sqrt(sum(y * y for y in b))
    return dot / norm if norm else 0.0


def solid_png(width=32, height=32, rgb=(255, 255, 255)):
    """A tiny valid PNG, used only to warm the vision model when no fixture image exists yet."""
    raw = b"".join(b"\x00" + bytes(rgb) * width for _ in range(height))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))
