"""Extract bounded text in a disposable process so malformed PDFs cannot hang chat."""
import base64
from io import BytesIO
import json
from pathlib import Path
import subprocess
import sys
import shutil

MAX_BYTES = 8 * 1024 * 1024
MAX_PAGES = 30
PAGE_CHARS = 4000


def extract(raw: bytes, suffix: str):
    if not raw or len(raw) > MAX_BYTES:
        raise ValueError('Documents must be between 1 byte and 8 MiB.')
    if suffix == '.pdf':
        from pypdf import PdfReader
        reader = PdfReader(BytesIO(raw), strict=True)
        if reader.is_encrypted:
            raise ValueError('Unlock the PDF before attaching it.')
        if len(reader.pages) > MAX_PAGES:
            raise ValueError('Attach a PDF with at most 30 pages.')
        pages = []
        for index, page in enumerate(reader.pages, 1):
            contents = page.get_contents()
            if contents is not None and len(contents.get_data()) > 8 * 1024 * 1024:
                raise ValueError('A PDF page is too complex to extract safely.')
            text = page.extract_text() or ''
            pages.append(dict(page=index, text=text[:PAGE_CHARS], truncated=len(text) > PAGE_CHARS, scanned=not text.strip()))
    elif suffix in ('.txt', '.md', '.csv'):
        text = raw.decode('utf-8-sig')
        if '\x00' in text:
            raise ValueError('Save this document as UTF-8 text.')
        pages = [dict(page=1, text=text[:12000], truncated=len(text) > 12000, scanned=False)]
    else:
        raise ValueError('Choose a PDF, TXT, Markdown or CSV document.')
    if not pages or not any(page['text'].strip() for page in pages):
        raise ValueError('This document has no extractable text. Scanned pages need OCR or a screenshot sent to a vision model.')
    return dict(kind='document', mode='extracted-text', pages=pages,
                limitation='Only the excerpts below are sent. Images, formatting and scanned pages are not interpreted.')


def worker():
    try:
        # RLIMIT_AS is a Linux address-space limit, not a portable memory cap.
        # Darwin can reject it before even a plain-text document is read.
        # All platforms retain the parent timeout and input/excerpt bounds.
        if sys.platform == 'linux':
            import resource
            resource.setrlimit(resource.RLIMIT_AS, (768*1024*1024, 768*1024*1024))
        request = json.loads(sys.stdin.buffer.read(MAX_BYTES*2))
        if request['suffix'] == '.ocr':
            executable = shutil.which('tesseract')
            if not executable:
                raise ValueError('Tesseract OCR is not installed on this device.')
            from app.attachments import normalize_image
            data, _, _, _ = normalize_image(request['data'])
            completed = subprocess.run([executable, 'stdin', 'stdout'], input=data, capture_output=True, timeout=12,
                                       creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
            text = completed.stdout.decode('utf-8', errors='replace')[:12000]
            if completed.returncode or not text.strip():
                raise ValueError('OCR could not read text in this screenshot.')
            result = dict(kind='document', mode='ocr-only', pages=[dict(page=1, text=text, truncated=len(completed.stdout)>12000, scanned=False)],
                          limitation='OCR text only. Layout, charts, objects and visual meaning are not interpreted; check the extracted text for errors.')
        else:
            result = extract(base64.b64decode(request['data'], validate=True), request['suffix'])
        print(json.dumps({'result': result}))
    except Exception:
        print(json.dumps({'error': 'Could not extract this document. Use an unlocked PDF of at most 30 pages or UTF-8 text; scanned pages require OCR.'}))


def normalize(encoded, suffix):
    if len(encoded) > ((MAX_BYTES + 2)//3)*4:
        raise ValueError('Documents must be 8 MiB or smaller.')
    if suffix == '.ocr' and not shutil.which('tesseract'):
        raise ValueError('OCR text-only mode needs Tesseract installed on this device. Choose a vision model to interpret the screenshot instead.')
    command = [sys.executable]
    if not getattr(sys, 'frozen', False):
        command += [str(Path(__file__).resolve().parents[1] / 'main.py')]
    command += ['--extract-attachment']
    try:
        result = subprocess.run(command, input=json.dumps(dict(data=encoded, suffix=suffix)),
                                text=True, encoding='utf-8', capture_output=True, timeout=20,
                                creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        response = json.loads(result.stdout)
        if result.returncode or 'error' in response:
            raise ValueError(response.get('error', 'Document extraction failed.'))
        return json.dumps(response['result']).encode('utf-8')
    except (subprocess.TimeoutExpired, json.JSONDecodeError):
        raise ValueError('Document extraction stopped. Try a smaller document; chat is still available.') from None
