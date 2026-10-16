"""Capture a read-only LocalMind environment inventory; never run inference.

Only fixed loopback Ollama metadata endpoints are queried. This is a snapshot,
not a benchmark or verification of an installed desktop application's version.
No service modules, user databases, or browser storage are read. The model-info
response can contain prompts/templates; these fields are discarded, never recorded.
"""

import argparse
import ast
import csv
import ctypes
import io
import json
import math
import os
import platform
import re
import subprocess
import sys
from datetime import datetime, timezone
from http.client import HTTPException
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import HTTPRedirectHandler, ProxyHandler, Request, build_opener


OLLAMA_URL = "http://127.0.0.1:11434"
MAX_RESPONSE_BYTES = 16 * 1024 * 1024
CHAT_SOURCE = "services/ai/app/api/routes/chat.py"
MODEL_SOURCE = "apps/desktop/src/types.ts"
NUMERIC_PARAMETERS = {
    "temperature", "num_ctx", "top_k", "top_p", "min_p", "seed",
    "num_predict", "num_gpu", "main_gpu", "num_thread", "repeat_penalty",
    "repeat_last_n", "mirostat", "mirostat_eta", "mirostat_tau",
}


class InvalidResponseShape(ValueError):
    pass


class ResponseTooLarge(ValueError):
    pass


class StaticConfigUnavailable(ValueError):
    pass


class RedirectRejected(ValueError):
    pass


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise RedirectRejected()


def unavailable(error: Exception | str) -> dict:
    # Exception messages can contain local paths, proxy credentials, or bodies.
    return {"status": "unavailable", "reason": error if isinstance(error, str) else type(error).__name__}


def numeric(value) -> bool:
    return type(value) is int or (type(value) is float and math.isfinite(value))


def model_name(value) -> str | None:
    if (isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.:/@-]{0,255}", value)
            and not re.match(r"^[A-Za-z]:/", value) and ".." not in value.split("/")):
        return value
    return None


def label(value) -> str | None:
    if isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9 (),._:+-]{0,255}", value):
        return value
    return None


def run_readonly(command: list[str], *, cwd: Path | None = None) -> str:
    flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
    return subprocess.run(
        command, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace",
        timeout=8, check=True, creationflags=flags,
    ).stdout


def physical_ram() -> dict:
    try:
        system = platform.system()
        if system == "Windows":
            class MemoryStatus(ctypes.Structure):
                _fields_ = [("length", ctypes.c_uint32), ("load", ctypes.c_uint32)] + [
                    (name, ctypes.c_uint64) for name in (
                        "total_physical", "available_physical", "total_page_file",
                        "available_page_file", "total_virtual", "available_virtual", "extended_virtual",
                    )
                ]
            status = MemoryStatus()
            status.length = ctypes.sizeof(status)
            if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
                raise OSError()
            amount = status.total_physical
        elif system == "Linux":
            pages, page_size = os.sysconf("SC_PHYS_PAGES"), os.sysconf("SC_PAGE_SIZE")
            if pages <= 0 or page_size <= 0:
                raise InvalidResponseShape()
            amount = pages * page_size
        elif system == "Darwin":
            amount = int(run_readonly(["sysctl", "-n", "hw.memsize"]).strip())
        else:
            return unavailable("UnsupportedPlatform")
        if amount <= 0:
            raise InvalidResponseShape()
        return {"status": "ok", "bytes": amount}
    except (OSError, ValueError, AttributeError, subprocess.SubprocessError) as error:
        return unavailable(error)


def cpu_name() -> dict:
    try:
        name = platform.processor()
        if platform.system() == "Darwin":
            name = run_readonly(["sysctl", "-n", "machdep.cpu.brand_string"]).strip()
        elif platform.system() == "Linux":
            with Path("/proc/cpuinfo").open(encoding="utf-8", errors="replace") as stream:
                match = re.search(r"^model name\s*:\s*(.+)$", stream.read(256 * 1024), re.MULTILINE)
            name = match.group(1).strip() if match else name
        return {"status": "ok", "name": label(name)} if label(name) else unavailable("NotReported")
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        return unavailable(error)


def nvidia_gpus() -> dict:
    try:
        output = run_readonly([
            "nvidia-smi", "--query-gpu=name,memory.total,driver_version", "--format=csv,noheader,nounits",
        ])
        devices = []
        for row in csv.reader(io.StringIO(output)):
            if not row:
                continue
            if len(row) != 3:
                raise InvalidResponseShape()
            name, memory, driver = (field.strip() for field in row)
            if not label(name) or not label(driver) or not memory.isdigit():
                raise InvalidResponseShape()
            devices.append({"name": name, "vram_bytes": int(memory) * 1024 * 1024, "driver_version": driver})
        return {"status": "ok", "devices": devices}
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        return unavailable(error)


def request_metadata(endpoint: str, model: str | None = None) -> dict:
    if endpoint not in ("version", "tags", "ps", "show") or (endpoint == "show") != (model is not None):
        raise ValueError("Unsupported metadata request")
    data = json.dumps({"model": model}).encode("utf-8") if model is not None else None
    request = Request(f"{OLLAMA_URL}/api/{endpoint}", data=data, headers={"Content-Type": "application/json"})
    # Never use proxy environment variables, global urllib openers, or redirects.
    opener = build_opener(ProxyHandler({}), NoRedirect())
    with opener.open(request, timeout=5) as response:
        raw = response.read(MAX_RESPONSE_BYTES + 1)
    if len(raw) > MAX_RESPONSE_BYTES:
        raise ResponseTooLarge()
    result = json.loads(raw)
    if not isinstance(result, dict):
        raise InvalidResponseShape()
    return result


def details_metadata(value) -> dict:
    if not isinstance(value, dict):
        raise InvalidResponseShape()
    result = {}
    for key in ("format", "family", "parameter_size", "quantization_level"):
        if label(value.get(key)):
            result[key] = value[key]
    if model_name(value.get("parent_model")):
        result["parent_model"] = value["parent_model"]
    if isinstance(value.get("families"), list):
        result["families"] = [item for item in value["families"] if label(item)]
    return result


def models_metadata(value: dict, *, active: bool = False) -> dict:
    if not isinstance(value.get("models"), list):
        raise InvalidResponseShape()
    result = []
    for entry in value["models"]:
        if not isinstance(entry, dict) or not any(model_name(entry.get(key)) for key in ("name", "model")):
            raise InvalidResponseShape()
        record = {key: entry[key] for key in ("name", "model") if model_name(entry.get(key))}
        if "digest" in entry:
            if not isinstance(entry["digest"], str) or not re.fullmatch(r"(?:sha256:)?[0-9a-fA-F]{64}", entry["digest"]):
                raise InvalidResponseShape()
            record["digest"] = entry["digest"]
        for key in (("size", "size_vram", "context_length") if active else ("size",)):
            if key in entry:
                if type(entry[key]) is not int or entry[key] < 0:
                    raise InvalidResponseShape()
                record[key] = entry[key]
        if "details" in entry:
            record["details"] = details_metadata(entry["details"])
        if active and "context_length" not in record:
            record["context_length_status"] = "not_reported"
        result.append(record)
    return {"status": "ok", "models": result}


def show_metadata(value: dict) -> dict:
    details = details_metadata(value.get("details"))
    info = value.get("model_info")
    if not isinstance(info, dict):
        raise InvalidResponseShape()
    allowed = {}
    for key, item in info.items():
        if key == "general.architecture" and label(item):
            allowed[key] = item
        elif (key == "general.parameter_count" or re.fullmatch(r"[A-Za-z0-9_]+\.context_length", key)):
            if type(item) is int and item > 0:
                allowed[key] = item
    parameters = {}
    raw = value.get("parameters", "")
    if not isinstance(raw, str):
        raise InvalidResponseShape()
    for line in raw.splitlines():
        parts = line.split(maxsplit=1)
        if len(parts) != 2 or parts[0] not in NUMERIC_PARAMETERS:
            continue
        try:
            item = json.loads(parts[1])
        except ValueError:
            continue
        if numeric(item):
            parameters[parts[0]] = item
    return {"status": "ok", "details": details, "model_info": allowed, "numeric_parameters": parameters}


def ollama_metadata(model: str) -> dict:
    result = {"base_url": OLLAMA_URL, "selected_model": model}
    for endpoint, key in (("version", "version"), ("tags", "installed_models"), ("ps", "active_models"), ("show", "selected_model_metadata")):
        try:
            raw = request_metadata(endpoint, model if endpoint == "show" else None)
            if endpoint == "version":
                if not label(raw.get("version")):
                    raise InvalidResponseShape()
                result[key] = {"status": "ok", "version": raw["version"]}
            elif endpoint == "show":
                result[key] = show_metadata(raw)
            else:
                result[key] = models_metadata(raw, active=endpoint == "ps")
        except HTTPError as error:
            result[key] = unavailable("ModelNotFound" if endpoint == "show" and error.code == 404 else error)
        except (OSError, ValueError, TypeError, HTTPException) as error:
            result[key] = unavailable(error)
    return result


def find_repository(start: Path = Path(__file__)) -> Path:
    for directory in (start.resolve().parent, *start.resolve().parents):
        if (directory / CHAT_SOURCE).is_file() and (directory / MODEL_SOURCE).is_file():
            return directory
    raise FileNotFoundError()


def source_text(path: Path) -> str:
    with path.open(encoding="utf-8-sig") as stream:
        text = stream.read(1024 * 1024 + 1)
    if len(text) > 1024 * 1024:
        raise StaticConfigUnavailable()
    return text


def source_defaults(root: Path) -> dict:
    result = {"meaning": "Repository source defaults; not observed UI selection or a running request."}
    try:
        tree = ast.parse(source_text(root / CHAT_SOURCE))
        candidates = []
        for function in tree.body:
            if isinstance(function, (ast.FunctionDef, ast.AsyncFunctionDef)) and function.name == "_stream_ollama":
                for node in ast.walk(function):
                    if isinstance(node, ast.Assign) and any(isinstance(target, ast.Name) and target.id == "payload" for target in node.targets) and isinstance(node.value, ast.Dict):
                        for key, value in zip(node.value.keys, node.value.values):
                            if isinstance(key, ast.Constant) and key.value == "options":
                                options = ast.literal_eval(value)
                                if isinstance(options, dict) and all(numeric(options.get(key)) for key in ("temperature", "num_ctx")):
                                    candidates.append({key: options[key] for key in ("temperature", "num_ctx")})
        if len(candidates) != 1:
            raise StaticConfigUnavailable()
        result["chat_options"] = {"status": "ok", "source": CHAT_SOURCE, "values": candidates[0]}
    except (OSError, ValueError, SyntaxError, TypeError) as error:
        result["chat_options"] = {**unavailable(error), "source": CHAT_SOURCE}
    try:
        text = source_text(root / MODEL_SOURCE)
        match = re.search(r"export\s+const\s+MODEL_MAP(?:\s*:[^=;{]{0,200})?\s*=\s*\{([^{}]{0,2048})\}\s*;", text)
        if not match:
            raise StaticConfigUnavailable()
        pattern = r'''\s*(speed|balanced|boost)\s*:\s*(["'])([A-Za-z0-9_.:/@-]{1,256})\2\s*,?'''
        entries = list(re.finditer(pattern, match.group(1)))
        if re.sub(pattern, "", match.group(1)).strip() or len(entries) != 3:
            raise StaticConfigUnavailable()
        mapping = {entry.group(1): entry.group(3) for entry in entries}
        if set(mapping) != {"speed", "balanced", "boost"} or not all(model_name(name) for name in mapping.values()):
            raise StaticConfigUnavailable()
        result["model_mapping"] = {"status": "ok", "source": MODEL_SOURCE, "values": mapping}
    except (OSError, ValueError) as error:
        result["model_mapping"] = {**unavailable(error), "source": MODEL_SOURCE}
    return result


def repository_metadata(root: Path) -> dict:
    try:
        head = run_readonly(["git", "rev-parse", "HEAD"], cwd=root).strip()
        if not re.fullmatch(r"[0-9a-fA-F]{40,64}", head):
            raise InvalidResponseShape()
        dirty = bool(run_readonly([
            "git", "--no-optional-locks", "status", "--porcelain", "--untracked-files=normal",
        ], cwd=root).strip())
        git = {"status": "ok", "head": head, "dirty": dirty}
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        git = unavailable(error)
    return {"git": git, "defaults": source_defaults(root)}


def capture(model: str = "qwen3:8b", root: Path | None = None) -> dict:
    if not model_name(model):
        raise ValueError("Invalid model name")
    report = {
        "schema_version": 1,
        "captured_at_utc": datetime.now(timezone.utc).isoformat(),
        "scope": {
            "kind": "read_only_environment_inventory", "benchmark_performed": False,
            "installed_app_version": {"status": "not_verified"},
            "actual_ui_model_selection": {"status": "unknown", "reason": "Saved choices live in desktop localStorage; it was not read."},
            "model_context_capacity_note": "Model metadata describes capacity; active_models.context_length describes loaded context only when reported.",
        },
        "host": {
            "os": {"system": platform.system(), "release": platform.release()},
            "architecture": platform.machine(), "python_version": platform.python_version(),
            "physical_ram": physical_ram(), "cpu_threads": os.cpu_count(), "cpu": cpu_name(), "nvidia": nvidia_gpus(),
        },
        "ollama": ollama_metadata(model),
    }
    try:
        report["repository"] = repository_metadata(root if root is not None else find_repository())
    except OSError as error:
        report["repository"] = unavailable(error)
    return report


def write_report(report: dict, output: Path, *, force: bool = False) -> None:
    encoded = json.dumps(report, indent=2, ensure_ascii=False, allow_nan=False) + "\n"
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("w" if force else "x", encoding="utf-8", newline="\n") as stream:
        stream.write(encoded)


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", required=True, type=Path, help="JSON output path; existing files are preserved by default")
    parser.add_argument("--model", default="qwen3:8b", help="Installed Ollama model to inspect, without loading it")
    parser.add_argument("--force", action="store_true", help="Allow replacing the requested output file")
    arguments = parser.parse_args(argv)
    try:
        if arguments.out.exists() and not arguments.force:
            raise FileExistsError()
        if not model_name(arguments.model):
            raise ValueError()
        write_report(capture(arguments.model), arguments.out, force=arguments.force)
    except (OSError, ValueError) as error:
        parser.exit(1, f"Inventory capture failed: {type(error).__name__}. Existing output requires --force.\n")
    print("Environment inventory written; no benchmark or inference was performed.")


if __name__ == "__main__":
    main()
