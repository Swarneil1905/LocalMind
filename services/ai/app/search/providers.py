"""
Web search provider adapters - Phase 5.

Supported providers:
  duckduckgo  - no API key, rate-limited, good for light use
  brave       - API key required, reliable, good results
  tavily      - API key required, designed for AI pipelines
  searxng     - self-hosted, no key, best privacy

Each adapter returns a list of SearchResult dicts:
  { "title": str, "url": str, "snippet": str }
"""

import re as _re
from dataclasses import dataclass

import httpx

_TIMEOUT = httpx.Timeout(connect=5.0, read=10.0, write=5.0, pool=5.0)


@dataclass
class SearchResult:
    title: str
    url: str
    snippet: str

    def to_dict(self) -> dict:
        return {"title": self.title, "url": self.url, "snippet": self.snippet}


# ---------------------------------------------------------------------------
# Query sanitization
# ---------------------------------------------------------------------------

# Strip null bytes and ASCII control chars (except normal whitespace).
# \x09 = tab, \x0a = LF, \x0d = CR are handled separately (replaced with space).
_QUERY_CTRL_RE = _re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")


def _sanitize_query(query: str) -> str:
    """
    Sanitize a user-supplied search query before sending it to any provider.

    - Replace tab/newline/CR with a space so multi-line pastes become one query.
    - Remove remaining ASCII control characters.
    - Collapse runs of whitespace.
    - Hard-cap at 200 characters.
    """
    q = query.replace("\t", " ").replace("\n", " ").replace("\r", " ")
    q = _QUERY_CTRL_RE.sub("", q)
    q = " ".join(q.split())
    return q[:200]


def _scrub_key(text: str, key: str) -> str:
    """Replace any occurrence of key in an error string with [REDACTED]."""
    if not key:
        return text
    return text.replace(key, "[REDACTED]")


# ---------------------------------------------------------------------------
# DuckDuckGo (no API key - uses lite HTML endpoint)
# ---------------------------------------------------------------------------

async def search_duckduckgo(query: str, max_results: int = 5) -> list[SearchResult]:
    """Search via DuckDuckGo HTML lite endpoint. No API key required."""
    async with httpx.AsyncClient(timeout=_TIMEOUT, follow_redirects=True) as client:
        resp = await client.post(
            "https://html.duckduckgo.com/html/",
            data={"q": query},
            headers={
                "User-Agent": "LocalMind/0.5",
                "Accept": "text/html",
            },
        )
        resp.raise_for_status()
        html = resp.text

    results: list[SearchResult] = []

    # Parse result links and snippets from DDG HTML response
    result_blocks = _re.findall(
        r'<a rel="nofollow" class="result__a" href="([^"]+)">(.*?)</a>.*?'
        r'<a class="result__snippet"[^>]*>(.*?)</a>',
        html,
        _re.DOTALL,
    )
    for url, title_html, snippet_html in result_blocks[:max_results]:
        title = _re.sub(r"<[^>]+>", "", title_html).strip()
        snippet = _re.sub(r"<[^>]+>", "", snippet_html).strip()
        if url and title:
            results.append(SearchResult(title=title, url=url, snippet=snippet))

    return results


# ---------------------------------------------------------------------------
# Brave Search API
# ---------------------------------------------------------------------------

async def search_brave(query: str, api_key: str, max_results: int = 5) -> list[SearchResult]:
    """Search via Brave Search API. Requires API key."""
    try:
        async with httpx.AsyncClient(timeout=_TIMEOUT) as client:
            resp = await client.get(
                "https://api.search.brave.com/res/v1/web/search",
                params={"q": query, "count": max_results},
                headers={
                    "Accept": "application/json",
                    "Accept-Encoding": "gzip",
                    "X-Subscription-Token": api_key,
                },
            )
            resp.raise_for_status()
            data = resp.json()
    except Exception as exc:
        raise RuntimeError(_scrub_key(str(exc), api_key)) from None

    results: list[SearchResult] = []
    for item in data.get("web", {}).get("results", [])[:max_results]:
        results.append(SearchResult(
            title=item.get("title", ""),
            url=item.get("url", ""),
            snippet=item.get("description", ""),
        ))
    return results


# ---------------------------------------------------------------------------
# Tavily API
# ---------------------------------------------------------------------------

async def search_tavily(query: str, api_key: str, max_results: int = 5) -> list[SearchResult]:
    """Search via Tavily. Designed for AI pipelines."""
    try:
        async with httpx.AsyncClient(timeout=_TIMEOUT) as client:
            resp = await client.post(
                "https://api.tavily.com/search",
                # Note: api_key is in the body per Tavily spec. _scrub_key below
                # ensures it never appears in exception messages.
                json={"api_key": api_key, "query": query, "max_results": max_results},
            )
            resp.raise_for_status()
            data = resp.json()
    except Exception as exc:
        raise RuntimeError(_scrub_key(str(exc), api_key)) from None

    results: list[SearchResult] = []
    for item in data.get("results", [])[:max_results]:
        results.append(SearchResult(
            title=item.get("title", ""),
            url=item.get("url", ""),
            snippet=item.get("content", "")[:300],
        ))
    return results


# ---------------------------------------------------------------------------
# SearxNG (self-hosted)
# ---------------------------------------------------------------------------

async def search_searxng(query: str, base_url: str, max_results: int = 5) -> list[SearchResult]:
    """Search via a self-hosted SearxNG instance."""
    async with httpx.AsyncClient(timeout=_TIMEOUT) as client:
        resp = await client.get(
            f"{base_url.rstrip('/')}/search",
            params={"q": query, "format": "json"},
            headers={"User-Agent": "LocalMind/0.5"},
        )
        resp.raise_for_status()
        data = resp.json()

    results: list[SearchResult] = []
    for item in data.get("results", [])[:max_results]:
        results.append(SearchResult(
            title=item.get("title", ""),
            url=item.get("url", ""),
            snippet=item.get("content", ""),
        ))
    return results


# ---------------------------------------------------------------------------
# Dispatcher
# ---------------------------------------------------------------------------

async def search(
    query: str,
    provider: str = "duckduckgo",
    api_key: str = "",
    searxng_url: str = "http://localhost:8080",
    max_results: int = 5,
) -> list[SearchResult]:
    """Route to the correct provider and return results."""
    query = _sanitize_query(query)
    if not query:
        return []
    if provider == "brave":
        return await search_brave(query, api_key, max_results)
    if provider == "tavily":
        return await search_tavily(query, api_key, max_results)
    if provider == "searxng":
        return await search_searxng(query, searxng_url, max_results)
    # Default: duckduckgo
    return await search_duckduckgo(query, max_results)
