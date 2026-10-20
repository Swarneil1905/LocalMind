"""Small, deterministic retrieval policy, independent of provider and model."""
import re
from datetime import datetime, timezone

_FILLER = set("can could would you please provide more details about the a an this that it was is when from mentioned news latest what are me tell did were happen happened date year".split())
_POLITE = r"(?:(?:please|now)\s*,?\s*)?(?:(?:can|could|would|will)\s+you\s+)?(?:please\s+)?"
_TRANSFORM = re.compile(
    rf"^{_POLITE}(?:rewrite|rephrase|translate|edit|proofread|shorten|format|summari[sz]e|"
    r"make\s+(?:it|this|that|your\s+answer)\s+(?:shorter|clearer|simpler|concise)|"
    r"turn\s+(?:it|this|that|your\s+answer)\s+into)\b"
)
_TEACHING = re.compile(
    r"\b(?:explain|teach|understand|theor(?:y|etical)|concept|analogy|"
    r"numerical example|learning (?:exercise|task)|walk me through)\b"
)
_DRAFTING = re.compile(
    rf"^{_POLITE}(?:write|draft|compose|create|make|build|plan|design|"
    r"help me (?:write|draft|plan|create))\b"
)
_REFERENCE = re.compile(
    r"\b(?:this|that|it|those|mentioned|more details|when was|when did|"
    r"what (?:date|year)|which year|what happened next)\b"
)
_LINK = re.compile(r"https?://[^\s<>\"\[\]]+", re.I)
_SOURCE_REQUEST = re.compile(
    r"\b(?:cite|citations?|fact[- ]check)\b|"
    r"\b(?:find|show|provide|include|give|check|verify|consult|read|use|using|from|according to)"
    r"\b[^.!?\n:]{0,100}\b(?:sources?(?!\s+code\b)|references?|"
    r"(?:official\s+)?(?:documentation|docs)|research papers?|articles?|webpages?|"
    r"web pages?|pages?|notices?|websites?)\b"
)
_WEB_REQUEST = re.compile(
    rf"^{_POLITE}(?:search(?!\s+(?:algorithms?|trees?)\b)|browse|google|look\s+up)\b|"
    r"\b(?:search|browse)\s+(?:the\s+)?(?:web|internet|online)\b|"
    r"\b(?:look\s+(?:it|that|this)\s+up|look\s+up|check\s+online)\b"
)
_TIME_CUE = re.compile(
    r"\b(?:latest|newest|breaking|recent(?:ly)?|up[- ]to[- ]date|today|tonight|"
    r"this\s+week(?:'s)?|past\s+(?:seven|7)\s+days)\b"
)
_CHANGING_SUBJECT = re.compile(
    r"\b(?:news|headlines?|weather|forecasts?|traffic|prices?|pricing|"
    r"exchange rates?|stock|scores?|standings|versions?|releases?|updates?|"
    r"service alerts?|schedules?|availability|developments?|research|state of)\b"
)
_OPT_OUT = re.compile(
    r"\b(?:do not|don't|dont|never|avoid|skip|stop)\s+(?:the\s+)?(?:web\s+)?"
    r"(?:search(?:ing)?|brows(?:e|ing))\b|"
    r"\b(?:do not|don't|dont|never)\s+(?:look\s+(?:(?:it|this|that)\s+)?up|go\s+online)\b|"
    r"\b(?:do not|don't|dont|never)\s+(?:use|access)\s+(?:the\s+)?(?:web|internet)\b|"
    r"\b(?:no need|(?:you\s+)?(?:do not|don't|dont)\s+need)\s+to\s+"
    r"(?:search|browse|look\s+(?:it\s+)?up)\b|"
    r"\b(?:without|no)\s+(?:a\s+)?(?:web|internet|online)\s*(?:search|browsing|access)?\b|"
    r"\b(?:without|no)\s+(?:search(?:ing)?|browsing)\b|"
    r"\bno\s+need\s+for\s+(?:a\s+)?(?:(?:web|internet|online)\s+)?search\b|"
    r"\b(?:answer|work)\s+offline\b|"
    r"\b(?:use|using|based on)\s+only\s+(?:the\s+)?(?:provided|supplied|pasted|given)"
    r"\s+(?:text|excerpt|passage|context)\b"
)


def _normalized(message: str) -> str:
    return " ".join(message.replace("\u2019", "'").replace("\u2018", "'").lower().split())


def _instruction(message: str) -> str:
    # A rewrite's pasted payload is data: words such as "latest news" in it
    # must not turn the rewrite into a current-information request.
    return re.split(r":\s+|\n", message, maxsplit=1)[0]


def _sentence_candidates(answer: str) -> list[str]:
    """Keep a named subject together across abbreviations such as 'No. 1'."""
    candidates = []
    for paragraph in re.split(r"\n+", answer):
        start = 0
        for boundary in re.finditer(r"(?<=[.!?])\s+", paragraph):
            preceding = paragraph[:boundary.start()]
            if re.search(r"\b(?:No|Dr|Mr|Mrs|Ms|Prof|St|vs)\.$", preceding, re.I):
                continue
            if re.search(r"\b[A-Z](?:\.[A-Z])*\.$", preceding):
                continue
            candidates.append(paragraph[start:boundary.start()])
            start = boundary.end()
        candidates.append(paragraph[start:])
    return candidates


def search_opted_out(message: str) -> bool:
    """Latest-turn no-search instructions take precedence over a query override."""
    return bool(_OPT_OUT.search(_normalized(message)))


def _source_requested(message: str) -> bool:
    if not _SOURCE_REQUEST.search(message):
        return False
    # Teaching citation syntax or programming references is itself a local
    # task. A separate instruction to use/cite evidence still requests sources.
    if re.match(rf"^{_POLITE}(?:explain|teach|help me understand|walk me through)\b", message):
        return bool(re.search(r"(?:\b(?:and|with|using|from|according to)\b|[.;])"
                              r"[^.!?\n]{0,100}\b(?:cite|sources?|citations?|references?|"
                              r"documentation|docs|papers?|articles?|pages?|notices?)\b", message))
    return True


def needs_freshness(message: str) -> bool:
    text = _normalized(message)
    if _TRANSFORM.search(text):
        text = _normalized(_instruction(message))
    # Conversational recency and electric current are not live-world evidence.
    text = re.sub(r"\b(?:latest|recent|current)\s+(?:answer|message|draft|example|"
                  r"step|prediction|predictions|value|values)\b", "", text)
    # A user's supplied updates are conversation context, not a request to
    # obtain newer updates. Other recency requests in the same turn remain.
    text = re.sub(r"\b(?:latest|newest|recent|current)\s+(?:updates?|notes?|"
                  r"details?|results?|changes?|information)\s+(?:that\s+)?"
                  r"(?:i|we|you)\s+(?:just\s+)?(?:shared|provided|pasted|sent|discussed)\b", "", text)
    if _TIME_CUE.search(text):
        # "Write a study plan for today" schedules local work. A plan using
        # today's weather/news still needs those external facts.
        only_task_time = (
            (_TEACHING.search(text) or _DRAFTING.search(text))
            and not _CHANGING_SUBJECT.search(text)
            and not re.search(r"\b(?:latest|newest|breaking|recent(?:ly)?|up[- ]to[- ]date)\b", text)
        )
        return not bool(only_task_time)
    if re.search(r"\bcurrent\b", text) and _CHANGING_SUBJECT.search(text):
        return True
    # "Now explain ..." is sequencing. "What is happening now?" and
    # "the price right now" genuinely ask about changing conditions.
    return bool(re.search(r"\bnow\b", text) and (
        _CHANGING_SUBJECT.search(text)
        or re.search(r"\b(?:happening|open|available|in stock|who is)\b", text)
    ) and not re.match(r"^now\s*,?\s*(?:please\s+)?(?:explain|teach|rewrite|"
                         r"rephrase|translate|summari[sz]e)\b", text))


def _source_context(message: str, history: list[dict]) -> str | None:
    """Follow a referential chain, stopping when the user's task changes."""
    if (_TEACHING.search(message) or _TRANSFORM.search(message)
            or _DRAFTING.search(message) or not _REFERENCE.search(message)):
        return None
    for turn in reversed(history):
        if turn.get("role") != "user" or not isinstance(turn.get("content"), str):
            continue
        prior = _normalized(turn["content"])
        if search_opted_out(prior):
            return None
        if (_source_requested(prior) or _WEB_REQUEST.search(prior)
                or needs_freshness(prior) or _LINK.search(prior)):
            return turn["content"]
        if _TEACHING.search(prior) or _TRANSFORM.search(prior) or _DRAFTING.search(prior):
            return None
        if re.search(r"\b(?:news|headlines?|weather|forecast)\b", prior):
            return turn["content"]
        if not _REFERENCE.search(prior):
            return None
    return None


def plan_query(message: str, history: list[dict]) -> str | None:
    text = message.strip()
    normalized = _normalized(text)
    if search_opted_out(text):
        return None
    if re.fullmatch(r"(?:hello|hi|hey|thanks|thank you|ok|okay)[!.\s]*", text, re.I):
        return None
    instruction = _normalized(_instruction(text)) if _TRANSFORM.search(normalized) else normalized
    links = _LINK.findall(text)
    # Prefer fetching one requested page directly to searching for its URL.
    # A URL inside pasted prose being edited is not itself a retrieval request.
    if _TRANSFORM.search(normalized) and _instruction(text) != text:
        body = text[len(_instruction(text)):].lstrip(": \n")
        if not _LINK.fullmatch(body):
            links = _LINK.findall(_instruction(text))
    external = bool(links or _WEB_REQUEST.search(instruction) or _source_requested(instruction))
    fresh = needs_freshness(text)
    source_context = _source_context(normalized, history)
    historical_date = not fresh and bool(re.search(
        r"\b(?:when (?:did|was|were)|what (?:date|year)|which year|(?:19|20)\d{2})\b", normalized
    ))
    if _TRANSFORM.search(normalized) and not (external or fresh):
        return None
    if not (external or fresh or source_context
            or (not _TEACHING.search(normalized)
                and re.search(r"\b(?:news|headlines?|weather|forecast)\b", normalized))):
        return None
    if len(links) == 1:
        return links[0].rstrip(".,;:!?)")
    # Resolve referential questions against the most relevant sentence in the last answer.
    if re.search(r"\b(this|that|it|mentioned|more details|those|when was|what (?:date|year)|which year)\b", text, re.I):
        previous = next((m.get("content", "") for m in reversed(history)
                         if m.get("role") == "assistant" and isinstance(m.get("content"), str)), "")
        terms = set(re.findall(r"[a-z]{3,}", text.lower())) - _FILLER
        sentences = _sentence_candidates(previous)
        ranked = sorted(sentences, key=lambda s: len(terms & set(re.findall(r"[a-z]{3,}", s.lower()))), reverse=True)
        if ranked and terms & set(re.findall(r"[a-z]{3,}", ranked[0].lower())):
            subject = re.sub(r"\[([^]]+)\]\([^)]+\)", r"\1", ranked[0])
            if historical_date:
                subject = _TIME_CUE.sub("", _normalized(subject))
                subject = re.sub(r"\b(?:current|now)\b", "", subject)
            text = subject[:300] + " " + text
        elif len(text.split()) < 12:
            prior = next((m.get("content", "") for m in reversed(history)
                          if m.get("role") == "user" and isinstance(m.get("content"), str)), "")
            if historical_date:
                prior = _TIME_CUE.sub("", _normalized(prior))
                prior = re.sub(r"\b(?:current|now)\b", "", prior)
            text = prior[:250] + " " + text
    if source_context and not (fresh or historical_date):
        if needs_freshness(source_context):
            # Preserve the active current-news intent in the effective query,
            # including for callers applying freshness after query resolution.
            text = "latest " + text
            fresh = True
    if fresh:
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
