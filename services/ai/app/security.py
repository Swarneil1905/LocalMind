"""Authentication for the private loopback bridge, including health checks."""

import hmac
import os


def configured_token() -> str:
    token = os.environ.get("LOCALMIND_TOKEN", "")
    if not token or not token.strip():
        raise RuntimeError("LOCALMIND_TOKEN must be provided by the desktop launcher.")
    return token


def authorized(header: str) -> bool:
    try:
        expected = f"Bearer {configured_token()}".encode("utf-8")
    except RuntimeError:
        return False
    return hmac.compare_digest(header.encode("utf-8"), expected)
