"""
LocalMind AI service entry point.

Reads LOCALMIND_PORT and LOCALMIND_TOKEN from environment variables,
then starts the FastAPI server bound to 127.0.0.1 only.
"""

import os
import sys

import uvicorn


def main() -> None:
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

    uvicorn.run("app.main:app", host="127.0.0.1", port=port)


if __name__ == "__main__":
    main()
