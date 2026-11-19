#!/usr/bin/python3
"""GET: the service's status.json, plus how old it is (the service rewrites it every second while it runs)."""
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lnr_cgi as c  # noqa: E402

user = c.require_admin()
path = c.var_path('status.json')
try:
    with open(path, encoding='utf-8') as f:
        status = json.load(f)
    status['stale_s'] = round(time.time() - os.path.getmtime(path))
except FileNotFoundError:
    status = {'stale_s': None}
except ValueError:
    status = {'stale_s': 0, 'busy': True}         # caught mid-replace: the page asks again
except OSError as e:
    c.cannot('read the status', e)
status['user'] = user
c.reply(200, status)
