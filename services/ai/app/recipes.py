"""One portable synthetic repair recipe. No executable code or arbitrary file paths."""
import json
from pathlib import Path
import tempfile

from contextlib import closing
import sqlite3
from app.project_repairs import preview_text
from app.storage import data_path

MAX_PACKAGE = 16_384
RECIPE = {
    "id": "exact-markdown-repair", "version": "1.0.0", "format_version": 1,
    "entry_point": "builtin.exact_markdown_preview",
    "permissions": ["synthetic.copy.read", "synthetic.copy.write"],
    "input_schema": {"type": "object", "properties": {"old_value": {"const": "2030-05-01"}, "new_value": {"const": "2030-05-08"}}, "required": ["old_value", "new_value"], "additionalProperties": False},
    "files": {"sample.md": "# Synthetic plan\nReview date: 2030-05-01\n"},
    "checks": ["exactly_one_match", "markdown_valid", "original_unchanged", "expected_output"],
    "expected": "# Synthetic plan\nReview date: 2030-05-08\n",
}


def export_recipe():
    # No database, caller text or user-selected file participates in export.
    return json.dumps(RECIPE, indent=2) + "\n"


def _unique(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("Duplicate recipe field.")
        value[key] = item
    return value


def validate_package(package):
    if not isinstance(package, str) or len(package.encode()) > MAX_PACKAGE:
        raise ValueError("Recipe must be a JSON package smaller than 16 KiB.")
    try:
        value = json.loads(package, object_pairs_hook=_unique)
    except (ValueError, RecursionError):
        raise ValueError("Invalid recipe JSON. Your previous recipe is unchanged.") from None
    # This research pilot accepts exactly the owned recipe, not new programs.
    # Exact comparison excludes secrets, private source text and path traversal.
    if value != RECIPE or type(value.get("format_version")) is not int:
        raise ValueError("Unsupported recipe, paths, schema, permissions or checks. Import the exported synthetic recipe.")
    return value


def run_package(package):
    recipe = validate_package(package)
    before = recipe["files"]["sample.md"]
    old, new = (recipe["input_schema"]["properties"][key]["const"] for key in ("old_value", "new_value"))
    with tempfile.TemporaryDirectory(prefix="localmind-recipe-") as folder:
        root = Path(folder)
        original, staged = root / "sample.md", root / "candidate.md"
        original.write_text(before, encoding="utf-8")
        after = preview_text(before, old, new)
        staged.write_text(after, encoding="utf-8")
        checks = {"exactly_one_match": before.count(old) == 1, "markdown_valid": True,
                  "original_unchanged": original.read_text(encoding="utf-8") == before,
                  "expected_output": staged.read_text(encoding="utf-8") == recipe["expected"]}
        if not all(checks.values()):
            raise ValueError("Synthetic acceptance checks failed; recipe was not registered.")
    return {"id": recipe["id"], "version": recipe["version"], "checks": checks, "synthetic": True,
            "permissions": recipe["permissions"], "preview": after}


def _conn():
    conn = sqlite3.connect(data_path("recipes.db"))
    conn.execute("CREATE TABLE IF NOT EXISTS recipes(id TEXT PRIMARY KEY, version TEXT NOT NULL)")
    return conn


def import_recipe(package):
    result = run_package(package)
    with closing(_conn()) as conn, conn:
        conn.execute("INSERT OR REPLACE INTO recipes VALUES (?,?)", (result["id"], result["version"]))
    return result


def status():
    with closing(_conn()) as conn:
        row = conn.execute("SELECT version FROM recipes WHERE id=?", (RECIPE["id"],)).fetchone()
    return {"id": RECIPE["id"], "registered": bool(row and row[0] == RECIPE["version"]), "version": RECIPE["version"],
            "permissions": RECIPE["permissions"], "location": "local", "evidence": "Verified on temporary synthetic copies, including export/import and recovery after a rejected package."}
