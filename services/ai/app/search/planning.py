"""Small, deterministic retrieval policy, independent of provider and model."""
import re
from datetime import datetime, timezone

_FILLER = set("can could would you please provide more details about the a an this that it was is when from mentioned news latest what are me tell game".split())


def needs_freshness(message: str) -> bool:
    return bool(re.search(r"\b(latest|today|current|now|recent|breaking)\b", message, re.I))


def plan_query(message: str, history: list[dict]) -> str | None:
    text = message.strip()
    if re.fullmatch(r"(?:hello|hi|hey|thanks|thank you|ok|okay)[!.\s]*", text, re.I):
        return None
    if re.match(r"(?:rewrite|rephrase|translate|summarize (?:that|this|your)|make (?:it|that) shorter)\b", text, re.I):
        return None
    # Resolve referential questions against the most relevant sentence in the last answer.
    if re.search(r"\b(this|that|it|mentioned|more details|those|when was)\b", text, re.I):
        previous = next((m["content"] for m in reversed(history) if m["role"] == "assistant"), "")
        terms = set(re.findall(r"[a-z]{3,}", text.lower())) - _FILLER
        sentences = re.split(r"(?<=[.!?])\s+|\n", previous)
        ranked = sorted(sentences, key=lambda s: len(terms & set(re.findall(r"[a-z]{3,}", s.lower()))), reverse=True)
        if ranked and terms & set(re.findall(r"[a-z]{3,}", ranked[0].lower())):
            subject = re.sub(r"\[([^]]+)\]\([^)]+\)", r"\1", ranked[0])
            text = subject[:300] + " " + text
        elif len(text.split()) < 12:
            prior = next((m["content"] for m in reversed(history) if m["role"] == "user"), "")
            text = prior[:250] + " " + text
    if needs_freshness(message):
        text = datetime.now(timezone.utc).date().isoformat() + " " + text
    return " ".join(text.split())[:500]


def is_recent(published_at: str | None, now: datetime | None = None) -> bool:
    if not published_at:
        return False
    try:
        published = datetime.fromisoformat(published_at.replace("Z", "+00:00"))
        if published.tzinfo is None:
            published = published.replace(tzinfo=timezone.utc)
        age = (now or datetime.now(timezone.utc)) - published
        return 0 <= age.total_seconds() <= 7 * 86400
    except ValueError:
        return False
