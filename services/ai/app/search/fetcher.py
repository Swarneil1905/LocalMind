"""
SSRF-safe HTTP fetcher for web search - Phase 5.

Applies all SSRF protection rules from the spec before making any request:
- Only http/https schemes
- Block localhost, loopback, private IP ranges, link-local, cloud metadata IPs
- Maximum 3 redirects (manual, so we can check each hop)
- Maximum 500 KB response
- 10 second timeout
- Strip HTML to 2000 chars of plain text
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


class SSRFError(ValueError):
    """Raised when a URL fails SSRF checks."""


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
    """Remove HTML tags and collapse whitespace."""
    text = re.sub(r"<style[^>]*>.*?</style>", " ", html, flags=re.DOTALL | re.IGNORECASE)
    text = re.sub(r"<script[^>]*>.*?</script>", " ", text, flags=re.DOTALL | re.IGNORECASE)
    text = re.sub(r"<[^>]+>", " ", text)
    text = re.sub(r"&nbsp;", " ", text)
    text = re.sub(r"&amp;", "&", text)
    text = re.sub(r"&lt;", "<", text)
    text = re.sub(r"&gt;", ">", text)
    text = re.sub(r"\s+", " ", text).strip()
    return text[:_STRIP_LENGTH]


async def fetch_page(url: str) -> str:
    """
    Fetch a web page safely and return stripped plain text up to 2000 chars.
    Raises SSRFError on blocked URLs, httpx errors on network failures.
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

            # Read with size cap
            content = b""
            async for chunk in resp.aiter_bytes(chunk_size=8192):
                content += chunk
                if len(content) > _MAX_BYTES:
                    break

            content_type = resp.headers.get("content-type", "")
            text = content.decode("utf-8", errors="replace")
            if "html" in content_type:
                return _strip_html(text)
            return text[:_STRIP_LENGTH]
