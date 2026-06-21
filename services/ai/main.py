"""
LocalMind AI service entry point.

Reads LOCALMIND_PORT and LOCALMIND_TOKEN from environment variables,
then starts the FastAPI server bound to 127.0.0.1 only.
"""

import multiprocessing
import os
import sys
from pathlib import Path

import uvicorn


def _load_dotenv() -> None:
    """Load key=value pairs from services/ai/.env into os.environ (stdlib, no dotenv package)."""
    env_path = Path(__file__).parent / ".env"
    if not env_path.exists():
        return
    for line in env_path.read_text().splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        key = key.strip()
        value = value.strip()
        if key and value and key not in os.environ:
            os.environ[key] = value


def main() -> None:
    _load_dotenv()
    port_str = os.environ.get("LOCALMIND_PORT")
    token = os.environ.get("LOCALMIND_TOKEN")

    if not port_str:
        print("LOCALMIND_PORT environment variable is not set", file=sys.stderr)
        sys.exit(1)

    if not token:
        print("LOCALMIND_TOKEN environment variable is not set", file=sys.stderr)
        sys.exit(1)

    try:
        port = int(port_str)
    except ValueError:
        print(f"LOCALMIND_PORT is not a valid integer: {port_str!r}", file=sys.stderr)
        sys.exit(1)

    from app.main import app as fastapi_app  # noqa: PLC0415

    uvicorn.run(fastapi_app, host="127.0.0.1", port=port)


if __name__ == "__main__":
    multiprocessing.freeze_support()
    main()
