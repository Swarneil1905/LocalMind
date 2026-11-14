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
# Representative encoded size, not a promise about Pillow/zlib compression.
CONTRACT_IMAGE_BYTES = 76


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
        for message in body["messages"]:
            for saved in message["attachments"]:
                preview = attachments.preview(saved["id"], conversation_id=cid)
                stored = base64.b64decode(preview["data_url"].split(",", 1)[1], validate=True)
                if type(saved["bytes"]) is not int or saved["bytes"] <= 0 or saved["bytes"] != len(stored):
                    raise AssertionError("Attachment bytes must report the actual stored image size.")
                # Identical pixels can encode differently across Pillow/zlib builds.
                # Validate the real metadata before normalizing the shared fixture.
                saved["bytes"] = CONTRACT_IMAGE_BYTES
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

    def test_contract_is_stable_across_valid_png_compression_sizes(self):
        normalize_image = attachments.normalize_image
        sizes = []
        for level in (0, 9):
            def encode(encoded, compress_level=level):
                data, mime, width, height = normalize_image(encoded)
                output = BytesIO()
                with Image.open(BytesIO(data)) as image:
                    image.save(output, format="PNG", compress_level=compress_level)
                sizes.append(len(output.getvalue()))
                return output.getvalue(), mime, width, height

            with self.subTest(compress_level=level), patch.object(attachments, "normalize_image", side_effect=encode):
                self.assertEqual(snapshot(), json.loads(FIXTURE.read_text()))
        self.assertNotEqual(sizes[0], sizes[1])

    def test_normalization_does_not_hide_incorrect_size_metadata(self):
        get_messages = conversations.get_messages
        for size in (0, -1, 999, "76", True, None):
            def incorrect_metadata(cid):
                messages = get_messages(cid)
                messages[0]["attachments"][0]["bytes"] = size
                return messages

            with self.subTest(size=size), patch.object(conversations, "get_messages", side_effect=incorrect_metadata):
                with self.assertRaisesRegex(AssertionError, "actual stored image size"):
                    snapshot()


if __name__ == "__main__":
    if "--write" in sys.argv:
        FIXTURE.parent.mkdir(exist_ok=True)
        FIXTURE.write_text(json.dumps(snapshot(), indent=2) + "\n", encoding="utf-8")
    else:
        unittest.main()
