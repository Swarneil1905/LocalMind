import base64
from contextlib import closing
import copy
import json
import os
import tempfile
import unittest
from unittest.mock import patch
from uuid import uuid4

from app import attachments, visuals
from app.db import conversations
from app.document_attachments import extract, normalize

SPEC = {'schema_version': 1, 'type': 'diagram', 'title': 'A process',
        'nodes': [{'id': 'a', 'label': 'First', 'x': 0, 'y': 0}, {'id': 'b', 'label': 'Next', 'x': 240, 'y': 0}],
        'edges': [{'id': 'ab', 'source': 'a', 'target': 'b'}], 'steps': [{'node': 'a', 'text': 'Start here.'}]}


class VisualTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        env = patch.dict(os.environ, {'LOCALMIND_DATA_DIR': folder.name})
        env.start()
        self.addCleanup(env.stop)
        self.cid = conversations.create_conversation('Visuals')['id']
        self.raw = json.dumps(SPEC)
        self.content = 'A process:\n```localmind-visual\n' + self.raw + '\n```'
        conversations.save_turn(self.cid, 'Draw the process', self.content)

    def capture(self):
        return visuals.capture(self.cid, self.content, self.raw, SPEC)

    def test_capture_is_idempotent_and_canonical(self):
        a, b = self.capture(), self.capture()
        self.assertEqual(a, b)
        self.assertEqual(len(a['versions']), 1)
        with closing(conversations._get_conn()) as db:
            self.assertEqual(db.execute('SELECT content FROM conversation_messages WHERE id=?', (a['source_message_id'],)).fetchone()[0], self.content)

    def test_capture_cannot_claim_different_content_as_its_origin(self):
        changed = copy.deepcopy(SPEC)
        changed['nodes'][0]['label'] = 'Not in the original'
        with self.assertRaisesRegex(ValueError, 'match its saved source'):
            visuals.capture(self.cid, self.content, self.raw, changed)

    def test_widget_parameters_match_the_available_controls(self):
        for parameters in [{'principal': 0}, {'years': 1.5}, {'rate': 3}, {'input': 1}]:
            with self.subTest(parameters=parameters), self.assertRaises(ValueError):
                visuals.validate(dict(schema_version=1, type='widget', title='Growth', widget='compound-growth', parameters=parameters))

    def test_fields_cannot_cross_visual_types(self):
        widget = dict(schema_version=1, type='widget', title='Growth', widget='compound-growth')
        for extra in [{'steps': 'not diagram steps'}, {'nodes': []}, {'attachment_id': str(uuid4())}]:
            with self.subTest(extra=extra), self.assertRaisesRegex(ValueError, 'Unsupported visual schema'):
                visuals.validate({**widget, **extra})
        with self.assertRaises(ValueError):
            visuals.validate(dict(schema_version=1, type=[], title='Invalid'))

    def test_image_reference_requires_a_saved_image_in_the_same_chat(self):
        from io import BytesIO
        from PIL import Image
        output = BytesIO()
        Image.new('RGB', (8, 8), 'blue').save(output, 'PNG')
        draft = str(uuid4())
        item = attachments.create('example.png', base64.b64encode(output.getvalue()).decode(), draft, self.cid)
        with self.assertRaises(ValueError):
            visuals.capture_image(self.cid, item['id'])
        attachments.resolve([item['id']], self.cid, draft)
        conversations.save_turn(self.cid, 'Inspect image', 'A blue square', attachment_ids=[item['id']])
        document = visuals.capture_image(self.cid, item['id'])
        self.assertEqual(document, visuals.capture_image(self.cid, item['id']))
        self.assertEqual(document['versions'][0]['spec']['attachment_id'], item['id'])
        other = conversations.create_conversation('Other')['id']
        with self.assertRaises(ValueError):
            visuals.capture_image(other, item['id'])
        with self.assertRaises(ValueError):
            visuals.revise(document['id'], other, 1, 1, document['versions'][0]['spec'])

    def test_wrong_conversation_and_unsaved_content_rejected(self):
        other = conversations.create_conversation('Other')['id']
        for cid, content in [(other, self.content), (self.cid, 'not saved')]:
            with self.assertRaises(ValueError):
                visuals.capture(cid, content, self.raw, SPEC)
        with self.assertRaises(ValueError):
            visuals.get(self.capture()['id'], other)

    def test_revision_restore_preserves_original_and_branches(self):
        doc = self.capture()
        changed = copy.deepcopy(SPEC)
        changed['nodes'][0]['label'] = 'Edited'
        visuals.revise(doc['id'], self.cid, 1, 1, changed)
        restored = visuals.revise(doc['id'], self.cid, 2, 1, SPEC)
        self.assertEqual([v['parent'] for v in restored['versions']], [None, 1, 1])
        self.assertEqual(restored['versions'][1]['spec']['nodes'][0]['label'], 'Edited')
        self.assertEqual(restored['versions'][0]['spec'], SPEC)

    def test_stale_save_does_not_overwrite(self):
        doc = self.capture()
        visuals.revise(doc['id'], self.cid, 1, 1, SPEC)
        with self.assertRaisesRegex(ValueError, 'another window'):
            visuals.revise(doc['id'], self.cid, 1, 1, SPEC)
        self.assertEqual(len(visuals.get(doc['id'], self.cid)['versions']), 2)

    def test_delete_conversation_removes_versions(self):
        doc = self.capture()
        with closing(conversations._get_conn()) as db, db:
            db.execute('DELETE FROM conversations WHERE id=?', (self.cid,))
        with closing(visuals.connection()) as db:
            self.assertEqual(db.execute('SELECT count(*) FROM visual_versions WHERE document_id=?', (doc['id'],)).fetchone()[0], 0)

    def test_invalid_graphs_and_unbounded_numbers_rejected(self):
        variants = []
        for transform in [lambda s: s['nodes'].append(s['nodes'][0]), lambda s: s['edges'][0].update(target='missing'),
                          lambda s: s['nodes'][0].update(x=float('nan')), lambda s: s.update(nodes=s['nodes']*50),
                          lambda s: s['steps'][0].update(node='missing')]:
            value = copy.deepcopy(SPEC)
            transform(value)
            variants.append(value)
        for value in variants:
            with self.subTest(value=value), self.assertRaises(ValueError):
                visuals.validate(value)

    def test_executable_and_remote_payloads_rejected(self):
        for payload in ['fetch("https://evil.test")', 'window.__TAURI_INTERNALS__.invoke("read_file")', 'while(true){}', 'localStorage.getItem("key")']:
            with self.assertRaises(ValueError):
                visuals.validate(dict(schema_version=1, type='html', title='App', source=payload))
        for payload in ['flowchart LR\nclick A "https://evil.test"', '%%{init:{}}%%\ngraph LR\nA-->B', 'graph LR\nA[<script>alert(1)</script>]', 'graph LR\nA@{img:"https://evil.test"}']:
            with self.assertRaises(ValueError):
                visuals.validate(dict(schema_version=1, type='mermaid', title='Diagram', source=payload))

    def test_plain_mermaid_and_widget_configuration(self):
        visuals.validate(dict(schema_version=1, type='mermaid', title='Diagram', source='flowchart LR\nA[Input] --> B[Output]'))
        visuals.validate(dict(schema_version=1, type='widget', title='Network', widget='backpropagation'))
        with self.assertRaises(ValueError):
            visuals.validate(dict(schema_version=1, type='widget', title='Network', widget='backpropagation', parameters={'script': 1}))

    def test_chart_requires_provenance_and_synthetic_flag(self):
        chart = dict(schema_version=1, type='chart', title='Trend', chart='line', source='Example values', synthetic=True,
                     x_label='Days', y_label='Items', series=[dict(name='A', points=[[1, 4], [2, 7]])])
        visuals.validate(chart)
        del chart['synthetic']
        with self.assertRaises(ValueError):
            visuals.validate(chart)


    def test_sequence_diagram_message_and_return_arrows(self):
        source = 'sequenceDiagram\n    Alice->>Bob: Request\n    Bob-->>Alice: Response'
        visuals.validate(dict(schema_version=1, type='mermaid', title='Sequence', source=source))


class DocumentTests(unittest.TestCase):
    setUp = VisualTests.setUp

    def test_worker_platform_limits_do_not_break_text_extraction(self):
        from io import BytesIO, StringIO
        from types import SimpleNamespace
        from unittest.mock import Mock
        from app.document_attachments import worker

        request = json.dumps({'data': base64.b64encode(b'Plain text').decode(), 'suffix': '.txt'}).encode()
        for platform in ('darwin', 'win32', 'linux'):
            with self.subTest(platform=platform):
                resource = SimpleNamespace(RLIMIT_AS=9, setrlimit=Mock())
                if platform != 'linux':
                    resource.setrlimit.side_effect = ValueError('Unsupported address-space limit')
                output = StringIO()
                with patch('app.document_attachments.sys.platform', platform), \
                        patch.dict('sys.modules', {'resource': resource}), \
                        patch('sys.stdin', SimpleNamespace(buffer=BytesIO(request))), \
                        patch('sys.stdout', output):
                    worker()
                payload = json.loads(output.getvalue())
                self.assertNotIn('error', payload)
                self.assertEqual(payload['result']['pages'][0]['text'], 'Plain text')
                if platform == 'linux':
                    resource.setrlimit.assert_called_once_with(9, (768*1024*1024, 768*1024*1024))
                else:
                    resource.setrlimit.assert_not_called()

    def test_linux_limit_failure_does_not_continue_extraction(self):
        from io import StringIO
        from types import SimpleNamespace
        from unittest.mock import Mock
        from app.document_attachments import worker

        resource = SimpleNamespace(RLIMIT_AS=9, setrlimit=Mock(side_effect=OSError('Cannot set limit')))
        output = StringIO()
        with patch('app.document_attachments.sys.platform', 'linux'), \
                patch.dict('sys.modules', {'resource': resource}), \
                patch('app.document_attachments.extract') as extract_document, \
                patch('sys.stdout', output):
            worker()
        self.assertIn('error', json.loads(output.getvalue()))
        extract_document.assert_not_called()

    def test_text_worker_starts_without_server_dependencies(self):
        import subprocess
        import sys
        from pathlib import Path

        entry = Path(__file__).resolve().parents[1] / 'main.py'
        # Disable site-packages and inherited dependency paths in the real child.
        environment = {key: value for key, value in os.environ.items() if key.upper() != 'PYTHONPATH'}
        result = subprocess.run(
            [sys.executable, '-S', str(entry), '--extract-attachment'],
            input=json.dumps({'data': base64.b64encode(b'Plain text').decode(), 'suffix': '.txt'}),
            capture_output=True, text=True, encoding='utf-8', timeout=20, env=environment,
            creationflags=subprocess.CREATE_NO_WINDOW if sys.platform == 'win32' else 0,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        payload = json.loads(result.stdout)
        self.assertNotIn('error', payload)
        self.assertEqual(payload['result']['pages'][0]['text'], 'Plain text')

    def test_text_excerpt_is_exact_bounded_and_labelled(self):
        data = extract(('A'*14000).encode(), '.txt')
        self.assertEqual(data['pages'][0]['text'], 'A'*12000)
        self.assertTrue(data['pages'][0]['truncated'])
        self.assertEqual(data['mode'], 'extracted-text')

    def test_pdf_keeps_page_numbers_and_identifies_blank_pages(self):
        from io import BytesIO
        from pypdf import PdfWriter
        from pypdf.generic import DictionaryObject, NameObject, DecodedStreamObject
        writer = PdfWriter()
        page = writer.add_blank_page(width=300, height=300)
        font = DictionaryObject({NameObject('/Type'): NameObject('/Font'), NameObject('/Subtype'): NameObject('/Type1'), NameObject('/BaseFont'): NameObject('/Helvetica')})
        page[NameObject('/Resources')] = DictionaryObject({NameObject('/Font'): DictionaryObject({NameObject('/F1'): font})})
        contents = DecodedStreamObject()
        contents.set_data(b'BT /F1 12 Tf 20 200 Td (Synthetic parcel target: 30) Tj ET')
        page[NameObject('/Contents')] = contents
        writer.add_blank_page(width=300, height=300)
        output = BytesIO()
        writer.write(output)
        result = extract(output.getvalue(), '.pdf')
        self.assertEqual(result['pages'][0]['page'], 1)
        self.assertIn('parcel target: 30', result['pages'][0]['text'])
        self.assertEqual(result['pages'][1]['page'], 2)
        self.assertTrue(result['pages'][1]['scanned'])

    def test_document_worker_and_chat_ownership(self):
        raw = base64.b64encode(b'First line\nSecond line').decode()
        # Real worker, including source and frozen-compatible entry routing.
        self.assertEqual(json.loads(normalize(raw, '.txt'))['pages'][0]['text'], 'First line\nSecond line')
        draft = str(uuid4())
        item = attachments.create('notes.txt', raw, draft, self.cid)
        self.assertEqual(item['kind'], 'document')
        self.assertEqual(attachments.resolve([item['id']], self.cid, draft), [])
        refs = attachments.document_references([item['id']], self.cid, draft, [])
        self.assertIn('[notes.txt p.1]', refs[0]['content'])
        self.assertEqual(attachments.preview(item['id'], self.cid)['pages'][0]['text'], 'First line\nSecond line')
        with self.assertRaises(ValueError):
            attachments.document_references([item['id']], conversations.create_conversation('Other')['id'], draft, [])

    def test_binary_encrypted_and_scan_only_rejected(self):
        from pypdf import PdfWriter
        from io import BytesIO
        for raw, suffix in [(b'\x00binary', '.txt'), (b'not a pdf', '.pdf')]:
            with self.assertRaises(Exception):
                extract(raw, suffix)
        writer = PdfWriter()
        writer.add_blank_page(width=100, height=100)
        output = BytesIO()
        writer.write(output)
        with self.assertRaisesRegex(ValueError, 'no extractable text'):
            extract(output.getvalue(), '.pdf')
        writer.encrypt('password')
        output = BytesIO()
        writer.write(output)
        with self.assertRaisesRegex(ValueError, 'Unlock'):
            extract(output.getvalue(), '.pdf')

    def test_worker_timeout_is_a_file_error(self):
        import subprocess
        with patch('app.document_attachments.subprocess.run', side_effect=subprocess.TimeoutExpired('worker', 20)):
            with self.assertRaisesRegex(ValueError, 'chat is still available'):
                normalize('YQ==', '.pdf')
