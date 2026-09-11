"""The desktop profile boundary shared by every LocalMind data store.

The launcher supplies LOCALMIND_DATA_DIR. Standalone development uses the same
OS-user directory, never a machine-wide temporary folder or the working tree.
An explicit override is an isolated profile; it never imports the user's data.
"""

import os
import shutil
import sqlite3
import sys
from contextlib import closing, contextmanager
from pathlib import Path


def default_data_dir() -> Path:
    if sys.platform == "win32":
        base = os.environ.get("APPDATA")
        if not base or not Path(base).is_absolute():
            raise RuntimeError("Cannot locate your application data folder (APPDATA).")
        return Path(base) / "LocalMind"
    if sys.platform == "darwin":
        return Path.home() / "Library" / "Application Support" / "LocalMind"
    base = os.environ.get("XDG_DATA_HOME", "")
    return (Path(base) if base and Path(base).is_absolute() else Path.home() / ".local" / "share") / "LocalMind"


def data_dir() -> Path:
    override = os.environ.get("LOCALMIND_DATA_DIR", "").strip()
    path = Path(override).expanduser() if override else default_data_dir()
    if not path.is_absolute():
        raise RuntimeError("LOCALMIND_DATA_DIR must be an absolute path.")
    path.mkdir(parents=True, exist_ok=True, mode=0o700)
    return path


def data_path(name: str) -> Path:
    return data_dir() / name


@contextmanager
def profile_lock():
    """One AI service owns a profile, avoiding duplicate connector sync jobs."""
    lock = (data_dir() / "runtime.lock").open("a+b")
    try:
        lock.seek(0, os.SEEK_END)
        if lock.tell() == 0:
            lock.write(b"\0")
            lock.flush()
        lock.seek(0)
        try:
            if sys.platform == "win32":
                import msvcrt
                msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as exc:
            raise RuntimeError("LocalMind is already using this profile. Close the other instance and retry.") from exc
        try:
            yield
        finally:
            lock.seek(0)
            if sys.platform == "win32":
                import msvcrt
                msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                fcntl.flock(lock.fileno(), fcntl.LOCK_UN)
    finally:
        lock.close()


def migrate_legacy_connectors() -> None:
    """Copy legacy same-user connector data once; never delete or overwrite it.

    Called under profile_lock before connector initialization. SQLite's backup
    API includes committed WAL contents. A custom/test profile imports nothing.
    We deliberately do not import the old shared temporary development folder.
    """
    target_root = data_dir()
    try:
        default_root = default_data_dir()
    except RuntimeError:
        # A valid explicit profile can still run without a platform default.
        return
    if target_root.resolve() != default_root.resolve():
        return
    completed = target_root / ".legacy-migration-v1"
    if completed.exists():
        return
    old_root = Path.home() / ".localmind"
    for name in ("messages.db", "people.db", "search_config.db"):
        source, target = old_root / name, target_root / name
        if not source.is_file() or target.exists():
            continue
        staging = target.with_suffix(".migration")
        try:
            with closing(sqlite3.connect(f"{source.as_uri()}?mode=ro", uri=True)) as src:
                with closing(sqlite3.connect(staging)) as dst:
                    src.backup(dst)
            staging.replace(target)
        finally:
            staging.unlink(missing_ok=True)
    for name in ("tokens", "wa_session"):
        source, target = old_root / name, target_root / name
        if source.is_dir() and not target.exists():
            # Publish a complete directory so an interrupted copy is retryable.
            staging = target_root / f".{name}.migration"
            shutil.copytree(source, staging, dirs_exist_ok=True, symlinks=True)
            staging.rename(target)
    # Once migrated, deleting/disconnecting a session must stay deleted. Keeping
    # the old files as a backup must not silently restore them on the next run.
    completed.write_text("completed\n", encoding="utf-8")
