"""Owned, read-only MCP fixture server. No user files, credentials, network or writes."""

import json
import sys
import time

PROTOCOL = "2025-06-18"
TOOLS = [
    {
        "name": "read_fixture",
        "description": "Read the built-in sample.txt fixture, not a file on your computer.",
        "inputSchema": {
            "type": "object",
            "properties": {"path": {"type": "string", "enum": ["sample.txt"]}},
            "required": ["path"],
            "additionalProperties": False,
        },
    },
    {
        "name": "wait_fixture",
        "description": "Wait up to 20 seconds to test cancellation. No external effects.",
        "inputSchema": {
            "type": "object",
            "properties": {"seconds": {"type": "integer", "minimum": 0, "maximum": 20}},
            "required": ["seconds"],
            "additionalProperties": False,
        },
    },
]


def validate(name, arguments):
    if not isinstance(arguments, dict):
        raise ValueError("Tool arguments must be an object.")
    if name == "read_fixture" and arguments == {"path": "sample.txt"}:
        return
    if (
        name == "wait_fixture"
        and set(arguments) == {"seconds"}
        and type(arguments["seconds"]) is int
        and 0 <= arguments["seconds"] <= 20
    ):
        return
    raise ValueError("Tool or arguments are outside the granted read-only scope.")


def serve():
    initialized = False
    for line in sys.stdin.buffer:
        if len(line) > 16384:
            return
        try:
            request = json.loads(line)
            if request.get("jsonrpc") != "2.0":
                return
            method = request.get("method")
            if method == "notifications/initialized":
                initialized = True
                continue
            if "id" not in request:
                continue
            if method == "initialize":
                result = {
                    "protocolVersion": PROTOCOL,
                    "capabilities": {"tools": {}},
                    "serverInfo": {"name": "localmind-fixture", "version": "1.0.0"},
                }
            elif initialized and method == "tools/list":
                result = {"tools": TOOLS}
            elif initialized and method == "tools/call":
                params = request["params"]
                name, arguments = params["name"], params["arguments"]
                validate(name, arguments)
                if name == "wait_fixture":
                    time.sleep(arguments["seconds"])
                result = {
                    "content": [
                        {
                            "type": "text",
                            "text": "LocalMind read-only sample. No user files were accessed."
                            if name == "read_fixture"
                            else "Wait completed.",
                        }
                    ],
                    "isError": False,
                }
            else:
                raise ValueError()
            reply = {"jsonrpc": "2.0", "id": request["id"], "result": result}
        except (ValueError, KeyError, TypeError, AttributeError):
            return  # Fail closed; never echo hostile input into logs.
        sys.stdout.buffer.write(json.dumps(reply).encode() + b"\n")
        sys.stdout.buffer.flush()


if __name__ == "__main__":
    serve()
