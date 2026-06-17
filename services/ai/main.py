"""
LocalMind AI service entry point.

Reads LOCALMIND_PORT and LOCALMIND_TOKEN from environment variables,
then starts the FastAPI server bound to 127.0.0.1 only.
"""

import multiprocessing
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

    # Import the app object directly instead of passing a string to uvicorn.
    # PyInstaller cannot detect dynamic string imports ("app.main:app"), so the
    # app package would be missing from the bundle. Importing statically ensures
    # PyInstaller includes the entire app package in the binary.
    from app.main import app as fastapi_app  # noqa: PLC0415

    uvicorn.run(fastapi_app, host="127.0.0.1", port=port)


if __name__ == "__main__":
    # Required by PyInstaller on Windows when using multiprocessing.
    multiprocessing.freeze_support()
    main()
