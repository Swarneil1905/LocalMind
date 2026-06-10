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
# DuckDuckGo (no API key - uses lite HTML endpoint)
# ---------------------------------------------------------------------------

async def search_duckduckgo(query: str, max_results: int = 5) -> list[SearchResult]:
    """Search via DuckDuckGo HTML lite endpoint. No API key required."""
    import re

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
    result_blocks = re.findall(
        r'<a rel="nofollow" class="result__a" href="([^"]+)">(.*?)</a>.*?'
        r'<a class="result__snippet"[^>]*>(.*?)</a>',
        html,
        re.DOTALL,
    )
    for url, title_html, snippet_html in result_blocks[:max_results]:
        title = re.sub(r"<[^>]+>", "", title_html).strip()
        snippet = re.sub(r"<[^>]+>", "", snippet_html).strip()
        if url and title:
            results.append(SearchResult(title=title, url=url, snippet=snippet))

    return results


# ---------------------------------------------------------------------------
# Brave Search API
# ---------------------------------------------------------------------------

async def search_brave(query: str, api_key: str, max_results: int = 5) -> list[SearchResult]:
    """Search via Brave Search API. Requires API key."""
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
    async with httpx.AsyncClient(timeout=_TIMEOUT) as client:
        resp = await client.post(
            "https://api.tavily.com/search",
            json={"api_key": api_key, "query": query, "max_results": max_results},
        )
        resp.raise_for_status()
        data = resp.json()

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
# Query sanitization
# ---------------------------------------------------------------------------

import re as _re

# Strip null bytes and ASCII control chars (except normal whitespace)
_QUERY_CTRL_RE = _re.compile(
    r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]"
)


def _sanitize_query(query: str) -> str:
    """
    Sanitize a user-supplied search query before sending it to any provider.

    - Remove null bytes and ASCII control characters (tabs and newlines are
      normalised to spaces, not removed).
    - Collapse internal whitespace.
    - Hard-cap at 200 characters so we never send an oversized string to a
      third-party API.
    """
    # Replace tab/newline with space so multi-line pastes become one query
    q = query.replace("\t", " ").replace("\n", " ").replace("\r", " ")
    # Remove remaining control chars
    q = _QUERY_CTRL_RE.sub("", q)
    # Collapse runs of whitespace
    q = " ".join(q.split())
    # Hard cap
    return q[:200]


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
