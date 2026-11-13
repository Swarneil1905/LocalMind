"""Generate sanitized fixtures from real storage APIs; CI checks for drift.

Run this file with --write to deliberately refresh contracts/app-v1.json,
then review its changes and run the desktop/frontend contract tests.
"""
import base64
from io import BytesIO
import json
import os
from pathlib import Path
import re
import sys
import tempfile
import unittest
from unittest.mock import patch
from uuid import uuid4

from PIL import Image
from app import attachments, response_preferences
from app.db import conversations, memories

FIXTURE = Path(__file__).resolve().parents[3] / "contracts" / "app-v1.json"


def snapshot():
    with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, LOCALMIND_DATA_DIR=directory):
        cid = conversations.create_conversation("Synthetic contract conversation")["id"]
        image = BytesIO()
        Image.new("RGB", (2, 2), "white").save(image, format="PNG")
        attachment = attachments.create("fixture.png", base64.b64encode(image.getvalue()).decode(), str(uuid4()), cid)
        conversations.save_turn(cid, "Describe this fixture.", "A white square.", attachment_ids=[attachment["id"]])
        conversations.save_interrupted_turn(cid, str(uuid4()), "Continue", "Partial response", cancelled=True)
        conversations.save_interrupted_turn(cid, str(uuid4()), "Try again", "Partial response", error="Connection interrupted.")
        memory = memories.insert_memories(["You prefer concise explanations with one useful example."], conversation_id=cid)[0]
        body = {"schema_version": 1, "preferences": response_preferences.read(),
                "messages": conversations.get_messages(cid), "memory": memory}
        identifiers = {}
        def normalize(value):
            if isinstance(value, dict):
                return {k: "2000-01-01T00:00:00+00:00" if k in ("created_at", "updated_at") else normalize(v) for k, v in value.items()}
            if isinstance(value, list):
                return [normalize(item) for item in value]
            if isinstance(value, str):
                def replace(match):
                    key = match.group()
                    if key not in identifiers:
                        identifiers[key] = f"00000000-0000-4000-8000-{len(identifiers) + 1:012d}"
                    return identifiers[key]
                return re.sub(r"[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}", replace, value)
            return value
        return normalize(body)


class ApiContractTests(unittest.TestCase):
    def test_backend_payload_matches_shared_fixture(self):
        self.maxDiff = None
        self.assertEqual(snapshot(), json.loads(FIXTURE.read_text()),
                         "API contract drift: review backend changes, refresh app-v1.json with --write, then test all three consumers.")


if __name__ == "__main__":
    if "--write" in sys.argv:
        FIXTURE.parent.mkdir(exist_ok=True)
        FIXTURE.write_text(json.dumps(snapshot(), indent=2) + "\n", encoding="utf-8")
    else:
        unittest.main()
