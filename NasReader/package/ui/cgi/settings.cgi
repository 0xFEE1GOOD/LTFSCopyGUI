#!/usr/bin/python3
"""POST {"action": "save", port, threads, window_mib} | {"action": "key"} | {"action": "newkey"}."""
import json
import os
import secrets
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lnr_cgi as c  # noqa: E402

c.require_admin(post=True)
try:
    body = json.loads(sys.stdin.read(min(int(os.environ.get('CONTENT_LENGTH') or 0), 4096)) or '{}')
except ValueError:
    body = {}
action = body.get('action')


def write(name, text, mode=0o644):
    tmp = c.var_path(name + '.tmp')
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, mode)
    with os.fdopen(fd, 'w') as f:
        f.write(text)
    os.replace(tmp, c.var_path(name))


try:
    if action == 'save':
        try:
            port, threads, window = int(body['port']), int(body['threads']), int(body['window_mib'])
        except (KeyError, TypeError, ValueError):
            c.reply(400, {'error': 'Port, readers and read-ahead must be numbers.'})
            sys.exit(0)
        if not (1024 <= port <= 65535 and 1 <= threads <= 64 and 64 <= window <= 8192):
            c.reply(400, {'error': 'Port 1024-65535, readers 1-64, read-ahead 64-8192 MiB.'})
            sys.exit(0)
        old = {}
        try:
            with open(c.var_path('settings.json')) as f:
                old = json.load(f)
        except (OSError, ValueError):
            pass
        write('settings.json', json.dumps({'port': port, 'threads': threads, 'window_mib': window}))
        c.reply(200, {'ok': True, 'restart': int(old.get('port', 7171)) != port})
    elif action == 'key':
        with open(c.var_path('key')) as f:
            c.reply(200, {'key': f.read().strip()})
    elif action == 'newkey':
        key = secrets.token_hex(32)
        write('key', key + '\n', 0o600)
        c.reply(200, {'key': key})
    else:
        c.reply(400, {'error': 'Unknown action.'})
except OSError as e:
    c.cannot('change the settings' if action != 'key' else 'read the key', e)
