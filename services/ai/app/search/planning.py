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
    r"alerts?|schedules?|availability|developments?|research|state of|"
    r"s&p(?:\s*500)?|nasdaq|dow jones|markets?|bitcoin|crypto|earnings|interest rates?)\b"
)
_CHANGING_FACT = re.compile(
    r"\bwho\s+(?:is|are)\s+(?:the\s+)?(?:current|leading|top|richest)\b|"
    r"\bwho\s+won\s+(?:the\s+)?(?:last|latest|recent)\b|"
    r"\bcurrent\s+(?:president|prime minister|ceo|champion|leader|mayor)\b|"
    r"\b(?:last|this)\s+quarter\b|\bearnings\b|\bstocks\b|\bstock\s+(?:price|market|is\s+(?:up|down))\b|"
    r"\bvisa\s+(?:requirements?|for)\b|\bneed\s+a\s+visa\b"
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
_SOCIAL_PHRASE = (
    r"(?:(?:hello|hi|hey)(?:\s+there)?|good\s+(?:morning|afternoon|evening|night)|"
    r"how\s+are\s+you(?:\s+doing)?(?:\s+today)?|"
    r"how(?:'s|\s+is)\s+(?:your\s+day(?:\s+going)?|it\s+going)(?:\s+today)?|"
    r"what(?:'s|\s+is)\s+up|(?:thanks|thank\s+you)(?:\s+for\s+your\s+help)?(?:\s+today)?|"
    r"(?:i\s+)?hope\s+you(?:\s+are|'re)\s+(?:doing\s+well|well|having\s+a\s+good\s+day)(?:\s+today)?|"
    r"ok|okay|no\s+problem|never\s*mind|bye|goodbye|"
    r"that'?s\s+fine|i\s+(?:just\s+)?asked)"
)
_SOCIAL_TURN = re.compile(rf"{_SOCIAL_PHRASE}(?:[\s,.!?;:]+{_SOCIAL_PHRASE})*[\s,.!?;:]*")


def _normalized(message: str) -> str:
    return " ".join(message.replace("\u2019", "'").replace("\u2018", "'").lower().split())


def is_social_turn(message: str) -> bool:
    """Recognize whole social turns, never discard a task after a greeting."""
    text = _normalized(message)
    # Emoji decorate greetings but are not a reason to search the web. Keep
    # the surrounding words so a mixed greeting/task still takes the task path.
    text = re.sub(r"[\U0001F300-\U0001FAFF\u2600-\u27BF\uFE0F\u200D]", "", text).strip()
    return len(text) <= 240 and _SOCIAL_TURN.fullmatch(text) is not None


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
    if is_social_turn(message):
        return False
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
    # Weather conditions implicitly mean current conditions unless the user is
    # studying the subject or explicitly asking about a historical period.
    if (re.search(r"\b(?:weather|forecast)\b", text)
            and not (_TEACHING.search(text) or _TRANSFORM.search(text))
            and not re.search(r"\b(?:was|were|history|historical|yesterday|last|"
                              r"(?:19|20)\d{2})\b", text)):
        return True
    # "What's happening at X" and "any news" ask about the world right now.
    if (re.search(r"\b(?:what(?:'?s|\s+is|\s+has|\s+was)?\s+(?:happening|going\s+on|happened)|any\s+news)\b", text)
            and not (_TEACHING.search(text) or _TRANSFORM.search(text))):
        return True
    # Facts a model's memory gets wrong: office holders, winners, rankings, company results, entry rules.
    if _CHANGING_FACT.search(text) and not (_TEACHING.search(text) or _TRANSFORM.search(text)):
        return True
    if _TIME_CUE.search(text):
        # "today", "tonight" and "this week" alone are plans or the calendar date,
        # not a request for live facts ("what should I cook tonight", "today's date").
        strong = re.search(r"\b(?:latest|newest|breaking|recent(?:ly)?|up[- ]to[- ]date|past\s+(?:seven|7)\s+days)\b", text)
        if not strong and not _CHANGING_SUBJECT.search(text):
            return False
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


def current_information_reply(message: str, *, web_allowed: bool,
                              opted_out: bool = False) -> str | None:
    """Give a usable offline path rather than inviting a model to guess live facts."""
    if web_allowed or not needs_freshness(message):
        return None
    if opted_out:
        return (
            "I can't verify current information without searching, and I'll respect your "
            "request to stay offline. If you paste a dated source, I can help interpret it."
        )
    return (
        "Web search is off, so I can't verify current information for this question. "
        "Turn on Web in the chat tools and send it again, or paste a dated source for me to read."
    )


def freshness_window_seconds(message: str) -> int:
    """A publication-age ceiling, not proof of an observation's validity."""
    # plan_query prefixes the current date to live requests; it is routing
    # metadata rather than a user-specified historical year.
    text = re.sub(r"^\d{4}-\d{2}-\d{2}\s+", "", _normalized(message))
    if needs_freshness(text) and re.search(r"\b(?:weather|forecast|temperature)\b", text):
        return 3 * 3600
    return 7 * 86400


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
    if is_social_turn(text):
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


_QUERY_STOP = _FILLER | set(
    "today tonight now current currently recent recently week this these those how are the of in on for and to with "
    "whats what's happening give numbers number any new at is by or as up do be my i latest news".split())


def _terms(text: str) -> list[str]:
    text = re.sub(r"^\d{4}-\d{2}-\d{2}\s+", "", text.lower())
    return [t for t in dict.fromkeys(re.findall(r"[a-z0-9&]+", text)) if t not in _QUERY_STOP]


def rank_relevant(results: list, query: str) -> list:
    """Order headlines by how many of the question's key words they contain and drop the rest.

    Fresh-news feeds return loosely related stories ("Fed rates" also finds CD offers and student loans).
    A headline must share a key word with the question; once some headlines share more, those that share
    fewer than half as many are dropped too.
    """
    terms = _terms(query)
    if not terms:
        return list(results)

    def score(item) -> int:
        words = re.findall(r"[a-z0-9&]+", item.title.lower())
        return sum(any(w == t or (len(t) >= 4 and w.startswith(t[:5])) for w in words) for t in terms)

    scored = [(score(item), item) for item in results]
    best = max((s for s, _ in scored), default=0)
    # One shared common word ("mission", "rates") is not evidence for a multi-word question.
    if best < min(2, len(terms)):
        return []
    keep = [(s, item) for s, item in scored if s >= max(1, -(-best // 2))]
    return [item for _, item in sorted(keep, key=lambda pair: -pair[0])]  # sorted() is stable: feed order breaks ties


def is_recent(published_at: str | None, now: datetime | None = None, *,
              max_age_seconds: int = 7 * 86400) -> bool:
    if not published_at:
        return False
    try:
        if type(max_age_seconds) is not int or max_age_seconds < 1:
            return False
        # A date-only source cannot establish a sub-day freshness promise.
        if max_age_seconds < 86400 and not re.search(r"[T ]\d{2}:\d{2}", published_at):
            return False
        published = datetime.fromisoformat(published_at.replace("Z", "+00:00"))
        if published.tzinfo is None:
            if max_age_seconds < 86400:
                return False
            published = published.replace(tzinfo=timezone.utc)
        age = (now or datetime.now(timezone.utc)) - published
        return 0 <= age.total_seconds() <= max_age_seconds
    except ValueError:
        return False
