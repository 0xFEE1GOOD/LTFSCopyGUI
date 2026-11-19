"""Shared by the LTFS NAS Reader page's CGIs: DSM sign-in check (administrators only) and JSON replies."""
import json
import os
import subprocess
import sys

VAR = '/var/packages/LtfsNasReader/var'
AUTH = '/usr/syno/synoman/webman/modules/authenticate.cgi'


def reply(code, obj):
    text = {200: 'OK', 400: 'Bad Request', 401: 'Unauthorized', 403: 'Forbidden', 500: 'Internal Server Error'}[code]
    sys.stdout.write('Status: %d %s\r\nContent-Type: application/json; charset=utf-8\r\nCache-Control: no-store\r\n'
                     'X-Content-Type-Options: nosniff\r\n\r\n' % (code, text))
    sys.stdout.write(json.dumps(obj, ensure_ascii=False))
    sys.stdout.flush()


def is_admin(user):
    try:
        with open('/etc/group', encoding='utf-8', errors='replace') as f:
            for line in f:
                parts = line.rstrip('\n').split(':')
                if len(parts) >= 4 and parts[0] == 'administrators':
                    return user in parts[3].split(',')
    except OSError:
        pass
    return False


def require_admin(post=False):
    """The signed-in DSM user (an administrator), or a reply and exit. Changes must be POSTs carrying our header, which
    a page on another site cannot send without the browser asking this server first (it never agrees)."""
    if post and (os.environ.get('REQUEST_METHOD') != 'POST' or os.environ.get('HTTP_X_REQUESTED_WITH') != 'LtfsNasReader'):
        reply(400, {'error': 'Changes need a POST from the LTFS NAS Reader page.'})
        sys.exit(0)
    try:
        out = subprocess.run([AUTH], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=10).stdout
        user = out.decode('utf-8', 'replace').strip().splitlines()[0].strip() if out.strip() else ''
    except (OSError, subprocess.SubprocessError, IndexError):
        user = ''
    if not user:
        reply(401, {'error': 'Not signed in to DSM.', 'login': True})
        sys.exit(0)
    if not is_admin(user):
        reply(403, {'error': 'Only administrators can use LTFS NAS Reader.'})
        sys.exit(0)
    return user


def var_path(name):
    return os.path.join(VAR, name)


def cannot(action, e):
    reply(500, {'error': 'Cannot %s: %s (the page runs as uid %d).' % (action, getattr(e, 'strerror', None) or e, os.geteuid())})
    sys.exit(0)
