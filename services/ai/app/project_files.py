"""Bounded reads of explicitly registered text files inside an approved root."""
import hashlib
import os
import stat
from pathlib import Path, PurePosixPath

MAX_TEXT_BYTES = 256 * 1024
TEXT_SUFFIXES = {'.txt', '.md', '.markdown'}


def relative_name(value: str) -> str:
    name = value.replace('\\', '/')
    parts = name.split('/')
    if not name or len(name) > 1000 or any(p in {'', '.', '..'} for p in parts):
        raise ValueError('Use a file path relative to the approved folder, without .. segments.')
    if ':' in name or '\x00' in name or PurePosixPath(name).is_absolute():
        raise ValueError('Absolute paths and alternate data streams are not supported.')
    if PurePosixPath(name).suffix.lower() not in TEXT_SUFFIXES:
        raise ValueError('Dependency tracking supports UTF-8 .md, .markdown and .txt files.')
    return name


def _windows_final_path(fd):
    import ctypes
    from ctypes import wintypes
    import msvcrt
    function = ctypes.WinDLL('kernel32', use_last_error=True).GetFinalPathNameByHandleW
    function.argtypes = [wintypes.HANDLE, wintypes.LPWSTR, wintypes.DWORD, wintypes.DWORD]
    function.restype = wintypes.DWORD
    buffer = ctypes.create_unicode_buffer(32768)
    count = function(msvcrt.get_osfhandle(fd), buffer, len(buffer), 0)
    if not count or count >= len(buffer):
        raise OSError('Could not verify the opened file path.')
    value = buffer.value
    if value.startswith('\\\\?\\UNC\\'):
        value = '\\\\' + value[8:]
    elif value.startswith('\\\\?\\'):
        value = value[4:]
    return Path(value)


def read_text(root: Path, relative: str) -> dict:
    relative = relative_name(relative)
    candidate = root.joinpath(*relative.split('/'))
    resolved = candidate.resolve(strict=True)
    # Reject symlinks/junctions even when they happen to point inside the root.
    if resolved != candidate or not resolved.is_relative_to(root):
        raise ValueError('Linked paths are not supported. Choose a regular file inside the approved folder.')
    fd = None
    directories = []
    try:
        if os.name == 'nt':
            fd = os.open(candidate, os.O_RDONLY | os.O_BINARY)
            if _windows_final_path(fd) != candidate:
                raise ValueError('The opened file escaped its approved path.')
        else:
            # Walk directory handles without following any link; a parent swap
            # between path validation and open cannot redirect the read.
            directory = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            directories.append(directory)
            parts = relative.split('/')
            for part in parts[:-1]:
                directory = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=directory)
                directories.append(directory)
            fd = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
        before = os.fstat(fd)
        if not stat.S_ISREG(before.st_mode) or before.st_size > MAX_TEXT_BYTES:
            raise ValueError('Choose a regular text file no larger than 256 KiB.')
        with os.fdopen(fd, 'rb', closefd=False) as file:
            raw = file.read(MAX_TEXT_BYTES + 1)
        after = os.fstat(fd)
        if len(raw) > MAX_TEXT_BYTES or (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
            raise ValueError('The file changed while being read. Try scanning again.')
        if candidate.resolve(strict=True) != candidate or candidate.stat().st_ino != after.st_ino:
            raise ValueError('The file path changed while being read. Try scanning again.')
        text = raw.decode('utf-8-sig').replace('\r\n', '\n')
        if '\x00' in text or not text.strip():
            raise ValueError('This file has no usable UTF-8 text.')
        return {'path': relative, 'sha256': hashlib.sha256(raw).hexdigest(), 'content': text, 'raw': raw}
    finally:
        if fd is not None:
            os.close(fd)
        for directory in reversed(directories):
            os.close(directory)
