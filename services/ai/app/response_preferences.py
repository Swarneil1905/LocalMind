"""Explicit profile preferences, separate from extracted memories and training."""
from contextlib import closing
import json
import sqlite3
from app.storage import data_path

OPTIONS = {
    "length": ("balanced", "brief", "detailed"),
    "depth": ("standard", "introductory", "technical"),
    "tone": ("neutral", "warm", "direct"),
}
DEFAULTS = {key: values[0] for key, values in OPTIONS.items()}
INSTRUCTIONS = {
    "brief": "Prefer concise answers with only the necessary explanation.",
    "detailed": "Develop the explanation with useful examples and relevant detail.",
    "introductory": "Define unfamiliar terms and build from an accessible example.",
    "technical": "Use precise technical detail and state assumptions when helpful.",
    "warm": "Use a warm, natural tone without formulaic praise or invented feelings.",
    "direct": "Lead with the answer and use direct, plain language.",
}


def _connection():
    connection = sqlite3.connect(data_path("response_preferences.db"))
    connection.execute("CREATE TABLE IF NOT EXISTS preferences (id INTEGER PRIMARY KEY CHECK(id=1), body TEXT NOT NULL)")
    return connection


def read() -> dict:
    with closing(_connection()) as connection:
        row = connection.execute("SELECT body FROM preferences WHERE id=1").fetchone()
    return {**DEFAULTS, **json.loads(row[0])} if row else dict(DEFAULTS)


def save(value: dict) -> dict:
    if set(value) != set(OPTIONS) or any(value[key] not in OPTIONS[key] for key in OPTIONS):
        raise ValueError("Choose a supported length, explanation depth and tone.")
    with closing(_connection()) as connection, connection:
        connection.execute("INSERT OR REPLACE INTO preferences VALUES (1, ?)", (json.dumps(value),))
    return dict(value)


def instruction() -> str:
    choices = read()
    hints = [INSTRUCTIONS[value] for value in choices.values() if value in INSTRUCTIONS]
    if not hints:
        return ""
    return "\nResponse preferences: " + " ".join(hints) + (
        " Follow the current request when it asks for a different style or depth."
        " These preferences do not change facts, permissions or the active topic."
        " Do not force headings or extra offers of help."
    )
