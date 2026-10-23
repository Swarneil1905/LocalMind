"""Bounded document extraction with stable page/line provenance."""
import hashlib
from io import BytesIO
from pathlib import Path

MAX_FILE_BYTES = 10 * 1024 * 1024
CHUNK_SIZE = 1800
CHUNK_OVERLAP = 200


def extract_passages(path: Path) -> list[dict]:
    if path.stat().st_size > MAX_FILE_BYTES:
        raise ValueError("File exceeds the 10 MiB indexing limit.")
    with path.open("rb") as stream:
        raw = stream.read(MAX_FILE_BYTES + 1)
    if len(raw) > MAX_FILE_BYTES:
        raise ValueError("File exceeds the 10 MiB indexing limit.")
    digest = hashlib.sha256(raw).hexdigest()
    if path.suffix.lower() == ".pdf":
        from pypdf import PdfReader
        reader = PdfReader(BytesIO(raw))
        if reader.is_encrypted:
            raise ValueError("Unlock the PDF before indexing it.")
        if len(reader.pages) > 500:
            raise ValueError("PDF exceeds the 500-page indexing limit.")
        sections = [(number, page.extract_text() or "") for number, page in enumerate(reader.pages, 1)]
    else:
        text = raw.decode("utf-8-sig")
        if "\x00" in text:
            raise ValueError("This file is binary or uses an unsupported text encoding. Save it as UTF-8.")
        sections = [(None, text.replace("\r\n", "\n").replace("\r", "\n"))]
    passages = []
    for page, text in sections:
        for start in range(0, len(text), CHUNK_SIZE - CHUNK_OVERLAP):
            end = min(start + CHUNK_SIZE, len(text))
            content = text[start:end]
            if content.strip():
                passages.append({"file_path": str(path), "file_sha256": digest,
                                 "chunk_index": len(passages), "content": content,
                                 "page": page, "line_start": text.count("\n", 0, start) + 1,
                                 "line_end": text.count("\n", 0, max(start, end - 1)) + 1})
    return passages


def location_label(chunk: dict) -> str:
    if chunk.get("page"):
        return f"Page {chunk['page']}"
    if chunk.get("line_start"):
        return f"Lines {chunk['line_start']}-{chunk['line_end']}"
    return f"Passage {chunk.get('chunk_index', 0) + 1} (reindex for line locations)"
