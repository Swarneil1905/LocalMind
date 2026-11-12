"""Validated visual documents and append-only revisions owned by saved chat messages."""
import json
import math
from contextlib import closing
from uuid import uuid4

from app.db.conversations import _get_conn

MAX_DOCUMENT = 100_000


def validate(value):
    if not isinstance(value, dict) or len(json.dumps(value, allow_nan=False)) > MAX_DOCUMENT:
        raise ValueError('Visual document is too large or invalid.')
    fields = {
        'diagram': {'nodes', 'edges', 'steps'},
        'mermaid': {'source'},
        'chart': {'source', 'chart', 'series', 'x_label', 'y_label', 'synthetic'},
        'widget': {'widget', 'parameters'},
        'image': {'attachment_id'},
    }
    kind = value.get('type')
    if not isinstance(kind, str) or kind not in fields:
        raise ValueError('Unsupported visual type. Generated executable code is not supported.')
    allowed = {'schema_version', 'type', 'title'} | fields[kind]
    if set(value) - allowed or value.get('schema_version') != 1:
        raise ValueError('Unsupported visual schema.')

    def text(item, maximum=200):
        if not isinstance(item, str) or not item.strip() or len(item) > maximum:
            raise ValueError('Visual labels must be bounded text.')

    def number(item):
        if type(item) not in (int, float) or not math.isfinite(item) or abs(item) > 1e9:
            raise ValueError('Visual values must be finite numbers within one billion.')

    text(value.get('title'))
    if kind == 'diagram':
        nodes, edges = value.get('nodes'), value.get('edges')
        if not isinstance(nodes, list) or not 1 <= len(nodes) <= 80 or not isinstance(edges, list) or len(edges) > 160:
            raise ValueError('Use 1–80 nodes and at most 160 edges.')
        ids = set()
        for node in nodes:
            if not isinstance(node, dict) or set(node) != {'id', 'label', 'x', 'y'}:
                raise ValueError('Invalid diagram node.')
            text(node['id'], 60)
            text(node['label'])
            number(node['x'])
            number(node['y'])
            if node['id'] in ids:
                raise ValueError('Node IDs must be unique.')
            ids.add(node['id'])
        edge_ids = set()
        for edge in edges:
            if not isinstance(edge, dict) or set(edge) != {'id', 'source', 'target'}:
                raise ValueError('Invalid diagram edge.')
            text(edge['id'], 60)
            if edge['id'] in edge_ids or edge['source'] not in ids or edge['target'] not in ids:
                raise ValueError('Edges need unique IDs and existing nodes.')
            edge_ids.add(edge['id'])
        steps = value.get('steps', [])
        if not isinstance(steps, list) or len(steps) > 80:
            raise ValueError('Too many explanation steps.')
        for step in steps:
            if not isinstance(step, dict) or set(step) != {'node', 'text'} or step['node'] not in ids:
                raise ValueError('Explanation steps must identify an existing node.')
            text(step['text'], 1000)
    elif kind == 'mermaid':
        text(value.get('source'), 12000)
        source = value['source'].lower()
        if any(token in source for token in ('%%{', '---', 'click ', 'href', '<', '>', 'https:', 'http:', 'javascript:', 'data:', 'style ', 'classdef ', 'linkstyle ', 'image', 'icon', '@{')):
            # Greater-than is used in arrows, so arrows are checked separately below.
            cleaned = source.replace('-->>', '').replace('->>', '').replace('-->', '').replace('-.->', '').replace('==>', '')
            if any(token in cleaned for token in ('%%{', '---', 'click ', 'href', '<', '>', 'https:', 'http:', 'javascript:', 'data:', 'style ', 'classdef ', 'linkstyle ', 'image', 'icon', '@{')):
                raise ValueError('Use plain Mermaid nodes and edges without links, HTML, styling or directives.')
        if not source.lstrip().startswith(('flowchart ', 'graph ', 'sequencediagram')):
            raise ValueError('Supported Mermaid formats: flowchart, graph and sequenceDiagram.')
    elif kind == 'chart':
        if value.get('chart') not in ('line', 'bar', 'scatter') or type(value.get('synthetic')) is not bool:
            raise ValueError('Charts require a supported type and an explicit synthetic-data label.')
        text(value.get('source'), 1000)
        text(value.get('x_label'))
        text(value.get('y_label'))
        series = value.get('series')
        if not isinstance(series, list) or not 1 <= len(series) <= 8:
            raise ValueError('Use 1–8 chart series.')
        for entry in series:
            if not isinstance(entry, dict) or set(entry) != {'name', 'points'}:
                raise ValueError('Invalid series.')
            text(entry['name'])
            if not isinstance(entry['points'], list) or not 1 <= len(entry['points']) <= 200:
                raise ValueError('Use 1–200 points per series.')
            for point in entry['points']:
                if not isinstance(point, list) or len(point) != 2:
                    raise ValueError('Points must be [x,y] pairs.')
                for coordinate in point:
                    number(coordinate)
    elif kind == 'image':
        from app.attachments import identifier
        identifier(value.get('attachment_id'))
    elif kind == 'widget':
        if value.get('widget') not in ('gradient-descent', 'compound-growth', 'backpropagation'):
            raise ValueError('Unknown trusted learning widget.')
        params = value.get('parameters', {})
        if not isinstance(params, dict) or set(params) - {'input', 'weight', 'target', 'rate', 'principal', 'years'}:
            raise ValueError('Unknown widget parameters.')
        for item in params.values():
            number(item)
        ranges = {'input': (-5, 5), 'weight': (-3, 3), 'target': (-5, 5),
                  'rate': (0.01, 0.2 if value['widget'] == 'compound-growth' else 0.9),
                  'principal': (1, 1000000), 'years': (1, 50)}
        supported = {'backpropagation': {'input', 'weight', 'target'},
                     'gradient-descent': {'input', 'rate'},
                     'compound-growth': {'principal', 'rate', 'years'}}[value['widget']]
        if set(params) - supported or any(not ranges[key][0] <= item <= ranges[key][1] for key, item in params.items()):
            raise ValueError('Widget parameters are outside the supported controls.')
        if 'years' in params and type(params['years']) is not int:
            raise ValueError('Years must be a whole number.')
    else:
        raise ValueError('Unsupported visual type. Generated executable code is not supported.')
    return value


def connection():
    conn = _get_conn()
    conn.executescript('''
        CREATE TABLE IF NOT EXISTS visual_documents (
          id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
          source_message_id TEXT NOT NULL REFERENCES conversation_messages(id) ON DELETE CASCADE,
          source_key TEXT NOT NULL, UNIQUE(source_message_id, source_key));
        CREATE TABLE IF NOT EXISTS visual_versions (
          document_id TEXT NOT NULL REFERENCES visual_documents(id) ON DELETE CASCADE,
          version INTEGER NOT NULL, parent INTEGER, body TEXT NOT NULL,
          created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, PRIMARY KEY(document_id, version));
    ''')
    return conn


def capture(conversation_id, source_content, source_key, spec):
    import hashlib
    spec = validate(spec)
    if spec['type'] == 'image':
        _saved_image(spec['attachment_id'], conversation_id)
    if len(source_content) > 200_000 or len(source_key) > MAX_DOCUMENT:
        raise ValueError('Source is too large.')
    # A user may import a document, but a generated source must actually occur in the saved reply.
    if source_key not in source_content:
        raise ValueError('The visual does not occur in its source message.')
    if spec['type'] == 'mermaid' and source_key == spec['source']:
        pass
    else:
        try:
            original = json.loads(source_key)
        except (ValueError, TypeError):
            raise ValueError('The visual must match its saved source.') from None
        if original != spec:
            raise ValueError('The visual must match its saved source.')
    digest = hashlib.sha256(source_key.encode()).hexdigest()
    with closing(connection()) as conn, conn:
        conn.execute('BEGIN IMMEDIATE')
        message = conn.execute("SELECT id FROM conversation_messages WHERE conversation_id=? AND role='assistant' AND content=? ORDER BY rowid DESC LIMIT 1", (conversation_id, source_content)).fetchone()
        if not message:
            raise ValueError('Wait until this response has been saved before opening the visual.')
        row = conn.execute('SELECT id FROM visual_documents WHERE source_message_id=? AND source_key=?', (message['id'], digest)).fetchone()
        if row:
            return get(row['id'], conversation_id, conn=conn)
        identifier = str(uuid4())
        conn.execute('INSERT INTO visual_documents VALUES (?,?,?,?)', (identifier, conversation_id, message['id'], digest))
        conn.execute('INSERT INTO visual_versions(document_id,version,parent,body) VALUES (?,1,NULL,?)', (identifier, json.dumps(spec)))
        return get(identifier, conversation_id, conn=conn)


def _saved_image(attachment_id, conversation_id):
    from app.attachments import identifier
    identifier(attachment_id)
    with closing(_get_conn()) as conn:
        row = conn.execute('SELECT * FROM chat_attachments WHERE id=? AND conversation_id=? AND message_id IS NOT NULL', (attachment_id, conversation_id)).fetchone()
        if not row or not row['mime_type'].startswith('image/'):
            raise ValueError('Choose an image saved in this conversation.')
        return dict(row)


def capture_image(conversation_id, attachment_id):
    row = _saved_image(attachment_id, conversation_id)
    spec = dict(schema_version=1, type='image', title=row['filename'], attachment_id=attachment_id)
    key = 'attachment:' + attachment_id
    with closing(connection()) as conn, conn:
        conn.execute('BEGIN IMMEDIATE')
        existing = conn.execute('SELECT id FROM visual_documents WHERE source_message_id=? AND source_key=?', (row['message_id'], key)).fetchone()
        if existing:
            return get(existing['id'], conversation_id, conn=conn)
        identifier = str(uuid4())
        conn.execute('INSERT INTO visual_documents VALUES (?,?,?,?)', (identifier, conversation_id, row['message_id'], key))
        conn.execute('INSERT INTO visual_versions(document_id,version,parent,body) VALUES (?,1,NULL,?)', (identifier, json.dumps(spec)))
        return get(identifier, conversation_id, conn=conn)


def get(identifier, conversation_id, *, conn=None):
    if conn is None:
        with closing(connection()) as db:
            return get(identifier, conversation_id, conn=db)
    row = conn.execute('SELECT * FROM visual_documents WHERE id=? AND conversation_id=?', (identifier, conversation_id)).fetchone()
    if not row:
        raise ValueError('Visual not found in this conversation.')
    result = dict(row)
    result['versions'] = [dict(version=v['version'], parent=v['parent'], created_at=v['created_at'], spec=json.loads(v['body'])) for v in conn.execute('SELECT * FROM visual_versions WHERE document_id=? ORDER BY version', (identifier,))]
    return result


def revise(identifier, conversation_id, expected_version, parent, spec):
    validate(spec)
    if spec['type'] == 'image':
        _saved_image(spec['attachment_id'], conversation_id)
    with closing(connection()) as conn, conn:
        conn.execute('BEGIN IMMEDIATE')
        document = get(identifier, conversation_id, conn=conn)
        latest = document['versions'][-1]['version']
        if expected_version != latest:
            raise ValueError('This visual changed in another window. Reopen it before saving your revision.')
        if type(parent) is not int or parent < 1 or parent > latest:
            raise ValueError('The parent version does not exist.')
        if latest >= 100:
            raise ValueError('This visual has reached its 100-version limit. Export it before continuing.')
        conn.execute('INSERT INTO visual_versions(document_id,version,parent,body) VALUES (?,?,?,?)', (identifier, latest+1, parent, json.dumps(spec)))
        return get(identifier, conversation_id, conn=conn)
