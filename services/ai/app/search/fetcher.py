"""
SSRF-safe HTTP fetcher for web search - Phase 5.

Security measures applied before and during every request:
  - Only http/https schemes allowed
  - Block localhost, loopback, private ranges, link-local, cloud metadata IPs
  - Maximum 3 redirects (manual, so each hop is re-checked)
  - Maximum 500 KB response body
  - 10 second timeout
  - Content-type gate: only text/html and text/plain are processed
  - Prompt injection sanitization before returning content to the model
  - Strip HTML to plain text, truncate to 2000 chars
"""

import ipaddress
import re
import socket
from urllib.parse import urlparse

import httpx

_TIMEOUT = httpx.Timeout(connect=5.0, read=10.0, write=5.0, pool=5.0)
_MAX_BYTES = 500 * 1024  # 500 KB
_MAX_REDIRECTS = 3
_STRIP_LENGTH = 2000

# Only these content-types are processed. Anything else (binary, PDF, etc)
# is rejected before the body is read.
_ALLOWED_CONTENT_TYPES = ("text/html", "text/plain", "application/xhtml+xml")

# Private / blocked IPv4 networks
_BLOCKED_NETS = [
    ipaddress.ip_network("10.0.0.0/8"),
    ipaddress.ip_network("172.16.0.0/12"),
    ipaddress.ip_network("192.168.0.0/16"),
    ipaddress.ip_network("169.254.0.0/16"),   # link-local + cloud metadata
    ipaddress.ip_network("100.64.0.0/10"),     # CGNAT
    ipaddress.ip_network("127.0.0.0/8"),       # loopback
    ipaddress.ip_network("0.0.0.0/8"),
    ipaddress.ip_network("::1/128"),
    ipaddress.ip_network("fc00::/7"),          # ULA
]

# ---------------------------------------------------------------------------
# Prompt injection patterns
#
# These are patterns that appear frequently in adversarial web content aimed
# at hijacking an LLM. We strip matches from content before injecting into
# the system prompt.
# ---------------------------------------------------------------------------
_INJECTION_PATTERNS = [
    # Classic override phrases
    r"ignore\s+(all\s+)?(?:previous|prior|above|the\s+previous)\s+instructions?",
    r"disregard\s+(all\s+)?(?:previous|prior|above|your)\s+instructions?",
    r"forget\s+(all\s+)?(?:previous|prior|above)?\s*instructions?",
    r"do\s+not\s+follow\s+(your\s+)?(?:previous\s+)?instructions?",
    # Role/persona hijacking
    r"you\s+are\s+now\s+(?:a|an)\s+\w+",
    r"act\s+as\s+(?:a|an)?\s*\w+\s+(?:without\s+restrictions?|with\s+no\s+restrictions?)?",
    r"pretend\s+(?:to\s+be|you\s+are)",
    r"roleplay\s+as",
    r"from\s+now\s+on\s+you\s+(?:are|will|must)",
    r"your\s+(?:new\s+)?(?:system\s+)?prompt\s+is",
    # Special tokens used by various LLM formats
    r"<\|(?:system|user|assistant|im_start|im_end|endoftext)[^|]*?\|>",
    r"\[/?INST\]",
    r"<</?SYS>>",
    r"\[SYSTEM\]",
    # Fake turn boundaries injected in web content
    r"^\s*(?:system|user|assistant)\s*:\s*",
    # Data exfiltration patterns
    r"(?:send|transmit|output|print|reveal|show|display)\s+(?:all\s+)?(?:your\s+)?(?:system\s+prompt|instructions?|context)",
    r"what\s+(?:are|were|is)\s+your\s+(?:original\s+)?instructions?",
]

_INJECTION_RE = re.compile(
    "|".join(f"(?:{p})" for p in _INJECTION_PATTERNS),
    flags=re.IGNORECASE | re.MULTILINE,
)

# Control characters except tab (9), newline (10), carriage return (13)
_CONTROL_CHAR_RE = re.compile(r"[\x00\x01\x02\x03\x04\x05\x06\x07\x08\x0b\x0c\x0e\x0f\x10\x11\x12\x13\x14\x15\x16\x17\x18\x19\x1a\x1b\x1c\x1d\x1e\x1f\x7f]")


class SSRFError(ValueError):
    """Raised when a URL fails SSRF checks."""


class ContentTypeError(ValueError):
    """Raised when the response Content-Type is not allowed."""


def _check_url(url: str) -> None:
    """Raise SSRFError if the URL should be blocked."""
    parsed = urlparse(url)

    if parsed.scheme not in ("http", "https"):
        raise SSRFError(f"Blocked scheme: {parsed.scheme!r}")

    host = parsed.hostname or ""
    if not host:
        raise SSRFError("Empty hostname")

    # Resolve hostname to IP(s) and check each
    try:
        infos = socket.getaddrinfo(host, None)
    except socket.gaierror as exc:
        raise SSRFError(f"DNS resolution failed: {exc}") from exc

    for info in infos:
        addr_str = info[4][0]
        try:
            addr = ipaddress.ip_address(addr_str)
        except ValueError:
            continue
        if addr.is_loopback or addr.is_link_local or addr.is_private:
            raise SSRFError(f"Blocked address: {addr_str}")
        for net in _BLOCKED_NETS:
            if addr in net:
                raise SSRFError(f"Blocked network: {addr_str} is in {net}")


def _strip_html(html: str) -> str:
    """Remove HTML tags, decode common entities, collapse whitespace."""
    text = re.sub(r"<style[^>]*>.*?</style>", " ", html, flags=re.DOTALL | re.IGNORECASE)
    text = re.sub(r"<script[^>]*>.*?</script>", " ", text, flags=re.DOTALL | re.IGNORECASE)
    text = re.sub(r"<[^>]+>", " ", text)
    text = re.sub(r"&nbsp;", " ", text)
    text = re.sub(r"&amp;", "&", text)
    text = re.sub(r"&lt;", "<", text)
    text = re.sub(r"&gt;", ">", text)
    text = re.sub(r"\s+", " ", text).strip()
    return text[:_STRIP_LENGTH]


def _sanitize_for_prompt(text: str) -> str:
    """
    Sanitize extracted web text before it is injected into the system prompt.

    Steps:
    1. Remove null bytes and non-printable control characters.
    2. Replace prompt injection patterns with a safe placeholder.
    3. Collapse excessive whitespace.
    4. Truncate to _STRIP_LENGTH.
    """
    # Step 1: strip control characters
    text = _CONTROL_CHAR_RE.sub("", text)

    # Step 2: neutralize injection patterns - replace with a visible marker so
    # the model can see something was removed rather than getting confused by
    # a sudden gap in content.
    text = _INJECTION_RE.sub("[content removed]", text)

    # Step 3: collapse whitespace
    text = re.sub(r"[ \t]{2,}", " ", text)
    text = re.sub(r"\n{3,}", "\n\n", text)

    # Step 4: trim
    return text.strip()[:_STRIP_LENGTH]


async def fetch_page(url: str) -> str:
    """
    Fetch a web page safely and return sanitized plain text up to 2000 chars.

    Raises:
      SSRFError        - URL failed SSRF checks
      ContentTypeError - response is not text/html or text/plain
      httpx errors     - network failures
    """
    _check_url(url)

    async with httpx.AsyncClient(
        timeout=_TIMEOUT,
        follow_redirects=False,
        max_redirects=0,
    ) as client:
        redirect_count = 0
        current_url = url

        while True:
            _check_url(current_url)
            resp = await client.get(current_url, headers={"User-Agent": "LocalMind/0.5"})

            if resp.is_redirect:
                redirect_count += 1
                if redirect_count > _MAX_REDIRECTS:
                    raise SSRFError("Too many redirects")
                location = resp.headers.get("location", "")
                if not location:
                    break
                # Resolve relative redirects
                if location.startswith("/"):
                    parsed = urlparse(current_url)
                    location = f"{parsed.scheme}://{parsed.netloc}{location}"
                current_url = location
                continue

            # Content-type gate - check BEFORE reading the body to avoid
            # downloading binary files or PDFs.
            content_type = resp.headers.get("content-type", "text/html").lower()
            if not any(allowed in content_type for allowed in _ALLOWED_CONTENT_TYPES):
                raise ContentTypeError(
                    f"Blocked content-type: {content_type!r}. "
                    f"Only text/html and text/plain are processed."
                )

            # Read with size cap
            content = b""
            async for chunk in resp.aiter_bytes(chunk_size=8192):
                content += chunk
                if len(content) > _MAX_BYTES:
                    break

            text = content.decode("utf-8", errors="replace")
            if "html" in content_type:
                text = _strip_html(text)
            else:
                text = text[:_STRIP_LENGTH]

            return _sanitize_for_prompt(text)
