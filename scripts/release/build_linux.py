"""Check Ollama's Linux shared libraries, then build the desktop DEB/AppImage.

linuxdeploy inspects each shared library independently with ldd. Nested CUDA
backends therefore need the common Ollama library directory in the build child's
search path, even when starting Ollama itself succeeds. This environment is never
exported to the parent or to the later installed-payload smoke tests.
"""

import argparse
import os
import re
import shutil
import subprocess
from pathlib import Path

from build_sidecar import ROOT, validate_target


TARGET = "x86_64-unknown-linux-gnu"
RUNTIME = ROOT / "apps/desktop/src-tauri/resources/ollama-runtime"
BASE_LIBRARY = "libggml-base.so.0"
TAURI_COMMAND = [
    "pnpm", "--filter", "desktop", "tauri", "build", "--verbose",
    "--target", TARGET, "--bundles", "deb,appimage",
]
MISSING_DEPENDENCY = re.compile(r"^\s*(\S+)\s*=>\s*not found(?:\s|$)", re.MULTILINE)
BASE_RESOLUTION = re.compile(
    r"^\s*libggml-base\.so\.0\s*=>\s*(.+?)\s+\(0x[0-9a-fA-F]+\)\s*$", re.MULTILINE,
)


class DependencyError(RuntimeError):
    pass


def build_environment(runtime: Path, inherited: dict[str, str] | None = None) -> dict[str, str]:
    environment = dict(os.environ if inherited is None else inherited)
    common = str((runtime / "lib/ollama").resolve())
    # The target is Linux even when unit tests run on Windows. An empty colon
    # segment searches the current directory, so omit empty inherited entries.
    paths = [common, *(entry for entry in environment.get("LD_LIBRARY_PATH", "").split(":") if entry)]
    environment["LD_LIBRARY_PATH"] = ":".join(dict.fromkeys(paths))
    environment["NO_STRIP"] = "true"
    environment["LC_ALL"] = "C"
    return environment


def is_shared_elf(path: Path) -> bool:
    with path.open("rb") as stream:
        header = stream.read(64)
    return (
        len(header) == 64 and header[:7] == b"\x7fELF\x02\x01\x01"
        and int.from_bytes(header[16:18], "little") == 3  # ET_DYN
        and int.from_bytes(header[18:20], "little") == 62  # EM_X86_64
        and int.from_bytes(header[20:24], "little") == 1
    )


def shared_libraries(runtime: Path) -> tuple[Path, list[Path]]:
    common = (runtime / "lib/ollama").resolve()
    base = common / BASE_LIBRARY
    if not base.is_file() or not is_shared_elf(base):
        raise DependencyError(f"Required native Ollama base library is missing or invalid: {base}")
    real_base = base.resolve()
    if not real_base.is_relative_to(common):
        raise DependencyError(f"Ollama base library points outside its runtime: {base}")
    libraries: set[Path] = set()
    for candidate in sorted(common.rglob("*")):
        if candidate.is_symlink() and not candidate.exists():
            raise DependencyError(f"Broken Ollama runtime symlink: {candidate}")
        if not candidate.is_file():
            continue
        resolved = candidate.resolve()
        if not resolved.is_relative_to(common):
            raise DependencyError(f"Ollama runtime file points outside its library directory: {candidate}")
        if resolved not in libraries and is_shared_elf(resolved):
            libraries.add(resolved)
    return real_base, sorted(libraries)


def preflight(runtime: Path, environment: dict[str, str]) -> None:
    base, libraries = shared_libraries(runtime)
    ldd = shutil.which("ldd", path=environment.get("PATH"))
    if not ldd:
        raise DependencyError("ldd is required for the native Linux packaging dependency check")
    failures = []
    base_references = 0
    for library in libraries:
        try:
            result = subprocess.run(
                [ldd, str(library)], env=environment, capture_output=True,
                text=True, errors="replace", timeout=30, check=False,
            )
        except (OSError, subprocess.TimeoutExpired) as error:
            failures.append(f"{library}: ldd failed: {error}")
            continue
        output = result.stdout + result.stderr
        reasons = []
        if result.returncode:
            reasons.append(f"ldd exited with status {result.returncode}")
        missing = sorted(set(MISSING_DEPENDENCY.findall(output)))
        if missing:
            reasons.append("missing dependencies: " + ", ".join(missing))
        if re.search(r"^\s*libggml-base\.so\.0\s*=>", output, re.MULTILINE) and BASE_LIBRARY not in missing:
            resolution = BASE_RESOLUTION.search(output)
            if not resolution or Path(resolution.group(1)).resolve() != base:
                reasons.append(f"{BASE_LIBRARY} must resolve to staged library {base}")
            else:
                base_references += 1
        if reasons:
            failures.append(f"{library}: {'; '.join(reasons)}\n{output[-8000:].strip()}")
    if failures:
        raise DependencyError(
            "Ollama dependency preflight failed before the desktop build:\n\n" + "\n\n".join(failures)
        )
    print(
        f"Ollama dependency preflight passed: {len(libraries)} shared ELF files; "
        f"{base_references} references resolve to the staged {BASE_LIBRARY}.", flush=True,
    )


def build_linux(runtime: Path = RUNTIME) -> None:
    environment = build_environment(runtime)
    preflight(runtime, environment)
    subprocess.run(TAURI_COMMAND, cwd=ROOT, env=environment, check=True)


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args(argv)
    try:
        validate_target(TARGET)
        build_linux()
    except subprocess.CalledProcessError as error:
        parser.exit(error.returncode if error.returncode > 0 else 1,
                    f"Linux desktop build failed with status {error.returncode}.\n")
    except (OSError, RuntimeError) as error:
        parser.exit(1, f"Linux desktop build failed: {error}\n")


if __name__ == "__main__":
    main()
