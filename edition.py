"""Edition identity for the local service, read from edition.json (the tree's brand manifest)."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
REQUIRED = ("schema", "id", "displayName", "device", "serviceAppId")


def load_edition(path=ROOT / "edition.json"):
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    missing = [key for key in REQUIRED if not isinstance(data.get(key), str) or not data[key].strip()]
    if missing:
        raise ValueError(f"edition.json is missing {', '.join(missing)}")
    if data["schema"] != "studio-edition/1":
        raise ValueError("edition.json schema must be studio-edition/1")
    return data


EDITION = load_edition()
APP_ID = EDITION["serviceAppId"]
DISPLAY_NAME = EDITION["displayName"]
ENV_PREFIX = EDITION.get("envPrefix") or "HYBRID_STUDIO_"
