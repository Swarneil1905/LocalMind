"""Strict adapter for the owned fixture server only. Not a general MCP marketplace."""

import asyncio
import hashlib
import json
import os
import signal
from pathlib import Path
import subprocess
import sys
from weakref import WeakKeyDictionary

from app.mcp_server import PROTOCOL, TOOLS, validate


class MCPError(ValueError):
    pass


class OwnedMCP:
    def __init__(self, timeout=5):
        self.timeout = timeout
        self.process = None
        self.enabled = False
        self.epoch = 0
        self.serial = 0
        self.lock = asyncio.Lock()
        self.stop_lock = asyncio.Lock()

    def preview(self):
        args = ["--owned-mcp"] if getattr(sys, "frozen", False) else ["-m", "app.mcp_server"]
        spec = {
            "executable": sys.executable,
            "arguments": args,
            "tools": TOOLS,
            "scope": "Built-in sample.txt only. No user files, network, credentials or writes.",
            "protocol": PROTOCOL,
        }
        return {
            **spec,
            "approval": hashlib.sha256(json.dumps(spec, sort_keys=True).encode()).hexdigest(),
            "enabled": self.enabled,
            "running": self.process is not None and self.process.returncode is None,
        }

    async def _kill(self, process):
        if process and process.returncode is None:
            # Onefile releases have a bootloader parent and a Python child.
            # Killing just the parent would leave the tool process running.
            try:
                if sys.platform == "win32":
                    killer = await asyncio.create_subprocess_exec(
                        str(Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32/taskkill.exe"),
                        "/PID", str(process.pid), "/T", "/F",
                        stdout=asyncio.subprocess.DEVNULL,
                        stderr=asyncio.subprocess.DEVNULL,
                        creationflags=subprocess.CREATE_NO_WINDOW,
                    )
                    await asyncio.wait_for(killer.wait(), 10)
                    if killer.returncode and process.returncode is None:
                        raise MCPError("Could not stop the owned MCP process tree.")
                else:
                    os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            await asyncio.wait_for(process.wait(), 10)

    async def stop(self):
        self.epoch += 1
        self.enabled = False
        async with self.stop_lock:
            process = self.process
            try:
                await self._kill(process)
            except (OSError, TimeoutError):
                raise MCPError("Tool access is disabled, but process cleanup needs another Stop attempt.") from None
            if self.process is process:
                self.process = None
        return self.preview()

    async def enable(self, approval):
        async with self.lock:
            if approval != self.preview()["approval"]:
                raise MCPError("Review the current executable and tool scopes before enabling.")
            if self.enabled:
                return self.preview()
            if self.process is not None:
                raise MCPError("Stop the previous server before enabling another.")
            epoch = self.epoch
            spec = self.preview()
            env = {
                key: os.environ[key]
                for key in ("SystemRoot", "WINDIR", "TEMP", "TMP", "LANG")
                if key in os.environ
            }
            env["PYTHONPATH"] = str(Path(__file__).resolve().parents[1])
            env["PYTHONIOENCODING"] = "utf-8"
            process = await asyncio.create_subprocess_exec(
                spec["executable"],
                *spec["arguments"],
                stdin=asyncio.subprocess.PIPE,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.DEVNULL,
                env=env,
                limit=65536,
                creationflags=subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0,
                start_new_session=sys.platform != "win32",
            )
            if epoch != self.epoch:
                await self._kill(process)
                raise MCPError("Server enable was cancelled.")
            self.process = process
            try:
                result = await self._request(
                    "initialize",
                    {
                        "protocolVersion": PROTOCOL,
                        "capabilities": {},
                        "clientInfo": {"name": "LocalMind", "version": "1.0.0"},
                    },
                    timeout=30,
                )
                if (
                    result.get("protocolVersion") != PROTOCOL
                    or result.get("capabilities") != {"tools": {}}
                    or not isinstance(result.get("serverInfo"), dict)
                    or result["serverInfo"].get("name") != "localmind-fixture"
                ):
                    raise MCPError("Unexpected server capabilities.")
                process.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
                await process.stdin.drain()
                discovered = await self._request("tools/list", {})
                if discovered != {"tools": TOOLS}:
                    raise MCPError("Server tools differ from the reviewed scope.")
                if epoch != self.epoch:
                    raise MCPError("Server enable was cancelled.")
                self.enabled = True
                return self.preview()
            except BaseException:
                await self.stop()
                raise

    async def _request(self, method, params, timeout=None):
        process = self.process
        if not process or process.returncode is not None:
            raise MCPError("The MCP server is stopped.")
        self.serial += 1
        request_id = self.serial
        try:
            async with asyncio.timeout(self.timeout if timeout is None else timeout):
                process.stdin.write(
                    json.dumps(
                        {"jsonrpc": "2.0", "id": request_id, "method": method, "params": params}
                    ).encode()
                    + b"\n"
                )
                await process.stdin.drain()
                raw = await process.stdout.readline()
                if not raw.endswith(b"\n") or len(raw) > 65536:
                    raise ValueError()
                reply = json.loads(raw)
                if (
                    not isinstance(reply, dict)
                    or set(reply) != {"jsonrpc", "id", "result"}
                    or reply["jsonrpc"] != "2.0"
                    or type(reply["id"]) is not int
                    or reply["id"] != request_id
                    or not isinstance(reply["result"], dict)
                ):
                    raise ValueError()
                return reply["result"]
        except (TimeoutError, ValueError, OSError, EOFError):
            raise MCPError(
                "MCP timed out, stopped, or returned an invalid response. Enable it again to retry."
            ) from None

    async def call(self, name, arguments):
        # Validate the grant before acquiring transport or sending any bytes.
        if not self.enabled:
            raise MCPError("MCP is disabled. Review and enable the owned server first.")
        try:
            validate(name, arguments)
        except ValueError as error:
            raise MCPError(str(error)) from None
        epoch = self.epoch
        async with self.lock:
            if not self.enabled or epoch != self.epoch:
                raise MCPError("MCP was disabled while the call was waiting.")
            try:
                result = await self._request("tools/call", {"name": name, "arguments": arguments})
                content = result.get("content")
                if (
                    set(result) != {"content", "isError"}
                    or result["isError"] is not False
                    or not isinstance(content, list)
                    or len(content) != 1
                ):
                    raise MCPError("Invalid tool response.")
                block = content[0]
                if (
                    not isinstance(block, dict)
                    or set(block) != {"type", "text"}
                    or block["type"] != "text"
                    or not isinstance(block["text"], str)
                    or len(block["text"]) > 4096
                ):
                    raise MCPError("Invalid tool content.")
                # Returned text is data only. It is never treated as a new grant.
                return {"text": block["text"], "untrusted": True}
            except BaseException:
                await self.stop()
                raise


_instances = WeakKeyDictionary()


def owned_mcp():
    loop = asyncio.get_running_loop()
    if loop not in _instances:
        _instances[loop] = OwnedMCP()
    return _instances[loop]
