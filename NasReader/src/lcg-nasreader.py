#!/usr/bin/env python3
"""LTFS NAS Reader - reads files on the NAS for LTFSCopyGui2NAS, in the order the tape write needs them.

Read-only: files are only ever opened for reading, inside the shares listed in /etc/samba/smb.share.conf.
Python 3.8+, standard library only.

Subcommands:
  serve --var DIR [--share NAME=PATH ...] [--port N]   the service (the DSM package runs this)
  key --var DIR [--new]                                 print (or replace) the key the app needs
  bench <folder> [options]                              read a folder like a tape write and print the rate
  evict <folder>                                        drop a folder's cached file data (for cold timings)

Protocol (TCP, little-endian), one write job per connection:
  server: "LNR1", u16 version, 16-byte nonce
  client: HMAC-SHA256(key, nonce + b"client")          server: HMAC-SHA256(key, nonce + b"server")
  client: u8 1, u32 count, u16 name length, name, then per entry: u32 index, u64 offset, u64 length,
          u16 path length, path ("Share/dir/file", UTF-8)
  server, per entry in the client's order: DATA frames (u8 1, u32 index, u32 n, n bytes) as the file is read,
          then END (u8 2, u32 index, u8 status, u64 bytes sent); after the last entry u8 3.
  status: 0 read (bytes sent = min(length, file size - offset)), 1 not found, 2 read error (bytes sent so far are
  good), 3 refused (unknown share or a path outside it). The client reads anything not 0 over SMB itself.
"""
import argparse
import collections
import hashlib
import hmac
import json
import logging
import logging.handlers
import os
import re
import secrets
import socket
import struct
import subprocess
import sys
import threading
import time

VERSION = '@VERSION@'               # set by build_spk.py
CHUNK = 8 << 20                     # read size and the largest DATA frame
MAGIC, PROTO = b'LNR1', 1
DEFAULTS = {'port': 7171, 'threads': 16, 'window_mib': 1024}
SHARE_CONF = '/etc/samba/smb.share.conf'
SKIP_DIRS = {'@eaDir', '#recycle', '#snapshot', '@tmp', '@sharebin'}   # Synology folders SMB does not show
OK, NOT_FOUND, READ_ERROR, REFUSED = 0, 1, 2, 3

HELLO = struct.Struct('<4sH16s')
ENTRY = struct.Struct('<IQQH')      # index, offset, length, path length
DATA = struct.Struct('<BII')        # 1, index, n
END = struct.Struct('<BIBQ')        # 2, index, status, bytes sent
log = logging.getLogger('lcg-nasreader')


def now_iso(t=None):
    return time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(t if t is not None else time.time()))


# ---------------------------------------------------------------------------------------------------------------
# Shares


def read_share_conf(conf=SHARE_CONF):
    """{share name: path} from Samba's share list ([name] ... path=/volume1/name)."""
    shares, name = {}, None
    try:
        with open(conf, encoding='utf-8', errors='replace') as f:
            lines = f.read().splitlines()
    except OSError:
        return shares
    for line in lines:
        m = re.match(r'^\s*\[(.+)\]\s*$', line)
        if m:
            name = m.group(1)
            continue
        m = re.match(r'^\s*path\s*=\s*(.+?)\s*$', line)
        if m and name and name.lower() != 'homes':
            shares[name] = m.group(1)
    return shares


class Shares:
    """Maps "Share/dir/file" as the client sends it to a path on the NAS, and refuses anything outside the share."""

    def __init__(self, extra=None):
        self._extra = dict(extra or {})
        self._lock = threading.Lock()
        self._listings = collections.OrderedDict()
        self.reload()

    def reload(self):
        m = read_share_conf()
        m.update(self._extra)
        self.map = {k.lower(): (k, v) for k, v in m.items()}   # SMB share names ignore case

    def resolve(self, rel):
        parts = [p for p in rel.replace('\\', '/').split('/') if p]
        if len(parts) < 2 or any(p in ('.', '..') or '\0' in p for p in parts):
            raise PermissionError('path not allowed')
        share = self.map.get(parts[0].lower())
        if share is None:
            raise PermissionError('unknown share ' + parts[0])
        root = share[1]
        path = os.path.join(root, *parts[1:])
        if not os.path.lexists(path):
            path = self._ignoring_case(root, parts[1:])      # Windows matches names in any case
        real, real_root = os.path.realpath(path), os.path.realpath(root)
        if not real.startswith(real_root.rstrip(os.sep) + os.sep):
            raise PermissionError('outside the share')
        return real

    def _ignoring_case(self, root, parts):
        cur = root
        for p in parts:
            nxt = os.path.join(cur, p)
            if not os.path.lexists(nxt):
                hits = self._listing(cur).get(p.lower(), [])
                if len(hits) != 1:
                    raise FileNotFoundError(nxt)
                nxt = os.path.join(cur, hits[0])
            cur = nxt
        return cur

    def _listing(self, d):
        with self._lock:
            v = self._listings.get(d)
            if v is not None:
                self._listings.move_to_end(d)
                return v
        try:
            names = os.listdir(d)
        except OSError:
            raise FileNotFoundError(d)
        v = {}
        for n in names:
            v.setdefault(n.lower(), []).append(n)
        with self._lock:
            self._listings[d] = v
            while len(self._listings) > 2048:
                self._listings.popitem(last=False)
        return v

    def readable(self):
        return [{'name': name, 'path': path, 'readable': os.access(path, os.R_OK | os.X_OK)}
                for name, path in sorted(self.map.values(), key=lambda s: s[0].lower())]


# ---------------------------------------------------------------------------------------------------------------
# One write job: readers fill slots ahead (bounded by the window), one sender passes them on in the client's order


class Slot:
    __slots__ = ('chunks', 'done', 'status', 'sent', 'reason')

    def __init__(self):
        self.chunks = collections.deque()
        self.done, self.status, self.sent, self.reason = False, OK, 0, ''


class Job:
    def __init__(self, entries, shares, window):
        self.entries, self.shares, self.window = entries, shares, window
        self.cv = threading.Condition()
        self.slots = {}
        self.claim = self.sending = self.buffered = 0
        self.cancelled = False
        self.files_done = self.bytes_done = self.not_served = 0
        self.drop_after = 0
        self.current = ''
        self.problems = []              # (path, reason) for entries the client reads over SMB

    def cancel(self):
        with self.cv:
            self.cancelled = True
            self.cv.notify_all()

    def reader(self):
        while True:
            with self.cv:
                while (not self.cancelled and self.claim < len(self.entries) and self.claim > self.sending
                       and self.buffered >= self.window):
                    self.cv.wait()
                if self.cancelled or self.claim >= len(self.entries):
                    return
                pos = self.claim
                self.claim += 1
                slot = self.slots[pos] = Slot()
                self.cv.notify_all()
            self._read(pos, slot)

    def _put(self, pos, slot, data):
        with self.cv:
            while not self.cancelled and pos != self.sending and self.buffered + len(data) > self.window:
                self.cv.wait()
            if self.cancelled:
                return False
            slot.chunks.append(data)
            slot.sent += len(data)
            self.buffered += len(data)
            self.cv.notify_all()
            return True

    def _finish(self, slot, status, reason=''):
        with self.cv:
            slot.status, slot.reason, slot.done = status, reason, True
            self.cv.notify_all()

    def _read(self, pos, slot):
        _, rel, offset, length = self.entries[pos]
        try:
            path = self.shares.resolve(rel)
        except PermissionError as e:
            return self._finish(slot, REFUSED, str(e))
        except FileNotFoundError:
            return self._finish(slot, NOT_FOUND, 'not found')
        try:
            with open(path, 'rb', buffering=0) as f:
                remaining = min(length, max(0, os.fstat(f.fileno()).st_size - offset))
                if offset:
                    f.seek(offset)
                if remaining > CHUNK and hasattr(os, 'posix_fadvise'):
                    os.posix_fadvise(f.fileno(), offset, remaining, os.POSIX_FADV_SEQUENTIAL)
                while remaining > 0:
                    data = f.read(min(CHUNK, remaining))
                    if not data:
                        break                       # shorter now: the client sees fewer bytes, as with SMB
                    if not self._put(pos, slot, data):
                        return
                    remaining -= len(data)
            self._finish(slot, OK)
        except FileNotFoundError:
            self._finish(slot, NOT_FOUND, 'not found')
        except OSError as e:
            self._finish(slot, READ_ERROR, e.strerror or str(e))

    def send_all(self, out):
        """Sends every entry in order; returns normally when done, raises when the client went away."""
        for pos, (index, rel, _, _) in enumerate(self.entries):
            with self.cv:
                self.sending = pos
                self.current = rel
                self.cv.notify_all()
            while True:
                with self.cv:
                    slot = self.slots.get(pos)
                    if slot is None or not (slot.chunks or slot.done):
                        out.flush()                 # nothing ready: let the client have what is buffered
                        while not self.cancelled and (slot is None or not (slot.chunks or slot.done)):
                            self.cv.wait()
                            slot = self.slots.get(pos)
                    if self.cancelled:
                        raise ConnectionAbortedError('cancelled')
                    data = slot.chunks.popleft() if slot.chunks else None
                    if data is not None:
                        self.buffered -= len(data)
                    else:
                        del self.slots[pos]
                    self.cv.notify_all()
                if data is not None:
                    out.write(DATA.pack(1, index, len(data)))
                    out.write(data)
                    self.bytes_done += len(data)
                    if self.drop_after and self.bytes_done >= self.drop_after:   # tests only: --test-drop-after
                        out.flush()
                        raise ConnectionAbortedError('test: connection dropped on purpose')
                    continue
                out.write(END.pack(2, index, slot.status, slot.sent))
                self.files_done += 1
                if slot.status != OK:
                    self.not_served += 1
                    if len(self.problems) < 100:
                        self.problems.append((rel, slot.reason))
                break
        out.write(b'\x03')
        out.flush()


# ---------------------------------------------------------------------------------------------------------------
# Disk view for the status page


class DiskView:
    """Busy % of the RAID members and any RAID check/resync, from /proc (empty elsewhere)."""

    def __init__(self):
        self._last, self._t = {}, 0.0

    @staticmethod
    def _arrays():
        arrays, cur = [], None
        try:
            with open('/proc/mdstat') as f:
                lines = f.read().splitlines()
        except OSError:
            return arrays
        for line in lines:
            m = re.match(r'^(md\d+)\s*:\s*\S+\s+(\S+)\s+(.*)$', line)
            if m:
                cur = {'name': m.group(1), 'level': m.group(2), 'disks': sorted(set(
                    re.sub(r'\d+$', '', d.split('[')[0]) for d in m.group(3).split() if '[' in d)), 'sync': None}
                arrays.append(cur)
                continue
            m = re.search(r'(resync|check|recovery|reshape)\s*=\s*([\d.]+%).*?finish=(\S+)', line)
            if m and cur:
                cur['sync'] = '%s %s, about %s left' % (m.group(1), m.group(2), m.group(3))
        return [a for a in arrays if a['name'] not in ('md0', 'md1')]     # DSM system and swap

    def sample(self):
        arrays = self._arrays()
        try:
            with open('/proc/diskstats') as f:
                stats = {p[2]: (int(p[12]), int(p[5])) for p in (l.split() for l in f) if len(p) > 12}
        except OSError:
            return {'arrays': arrays}
        t = time.monotonic()
        dt = (t - self._t) if self._t else 0
        for a in arrays:
            a['members'] = []
            for d in a['disks']:
                busy = mbps = None
                if d in stats and d in self._last and dt > 0:
                    busy = min(100.0, (stats[d][0] - self._last[d][0]) / (dt * 10))
                    mbps = (stats[d][1] - self._last[d][1]) * 512 / 1e6 / dt
                a['members'].append({'disk': d, 'busy': busy, 'read_mbps': mbps})
        self._last, self._t = stats, t
        return {'arrays': arrays}


# ---------------------------------------------------------------------------------------------------------------
# Service


def load_key(var, new=False):
    path = os.path.join(var, 'key')
    if new or not os.path.exists(path):
        fd = os.open(path + '.tmp', os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, 'w') as f:
            f.write(secrets.token_hex(32) + '\n')
        os.replace(path + '.tmp', path)
    with open(path) as f:
        return f.read().strip()


def load_settings(var):
    s = dict(DEFAULTS)
    try:
        with open(os.path.join(var, 'settings.json')) as f:
            s.update({k: int(v) for k, v in json.load(f).items() if k in DEFAULTS})
    except (OSError, ValueError):
        pass
    s['threads'] = max(1, min(64, s['threads']))
    s['window_mib'] = max(64, min(8192, s['window_mib']))
    return s


def recv_exact(f, n):
    data = f.read(n)
    if data is None or len(data) != n:
        raise ConnectionError('connection closed')
    return data


class Service:
    def __init__(self, var, extra_shares, port=None, drop_after=0):
        self.var = var
        self.drop_after = drop_after
        os.makedirs(var, exist_ok=True)
        self.settings = load_settings(var)
        if port:
            self.settings['port'] = port
        self.port = self.settings['port']
        self.key_hex = load_key(var)
        self._key_mtime = os.path.getmtime(os.path.join(var, 'key'))
        self.shares = Shares(extra_shares)
        self.started = time.time()
        self.lock = threading.Lock()
        self.live = {}
        self.history = collections.deque(maxlen=20)
        self.events = collections.deque(maxlen=50)
        self.disks = DiskView()

    def event(self, text):
        log.info(text)
        with self.lock:
            self.events.appendleft({'time': now_iso(), 'text': text})

    def key(self):
        path = os.path.join(self.var, 'key')
        try:
            m = os.path.getmtime(path)
            if m != self._key_mtime:                    # "New key" on the status page
                self.key_hex, self._key_mtime = load_key(self.var), m
                self.event('Key replaced; the app needs the new key.')
        except OSError:
            pass
        return bytes.fromhex(self.key_hex)

    def run(self):
        threading.Thread(target=self._status_loop, daemon=True).start()
        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind(('0.0.0.0', self.port))
        srv.listen(8)
        self.event('Started %s, listening on port %d, %d readers, %d MiB read-ahead.'
                   % (VERSION, self.port, self.settings['threads'], self.settings['window_mib']))
        while True:
            conn, addr = srv.accept()
            threading.Thread(target=self._client, args=(conn, addr[0]), daemon=True).start()

    def _client(self, conn, peer):
        job, info = None, None
        try:
            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)   # no SO_SNDBUF: a fixed size ends auto-tuning
            conn.settimeout(30)
            rf, wf = conn.makefile('rb', buffering=1 << 20), conn.makefile('wb', buffering=1 << 20)
            nonce = secrets.token_bytes(16)
            wf.write(HELLO.pack(MAGIC, PROTO, nonce))
            wf.flush()
            key = self.key()
            if not hmac.compare_digest(recv_exact(rf, 32), hmac.new(key, nonce + b'client', hashlib.sha256).digest()):
                self.event('Refused %s: wrong key.' % peer)
                return
            wf.write(hmac.new(key, nonce + b'server', hashlib.sha256).digest())
            wf.flush()
            if recv_exact(rf, 1) != b'\x01':
                return
            count, name_len = struct.unpack('<IH', recv_exact(rf, 6))
            name = recv_exact(rf, name_len).decode('utf-8', 'replace')
            conn.settimeout(300)
            entries = []
            for _ in range(count):
                index, offset, length, plen = ENTRY.unpack(recv_exact(rf, ENTRY.size))
                entries.append((index, recv_exact(rf, plen).decode('utf-8', 'surrogateescape'), offset, length))
            conn.settimeout(None)
            self.shares.reload()
            settings = load_settings(self.var)
            job = Job(entries, self.shares, settings['window_mib'] << 20)
            job.drop_after = self.drop_after
            info = {'client': peer, 'name': name, 'started': time.time(), 'files_total': count,
                    'bytes_total': sum(e[3] for e in entries), 'job': job, 'readers': settings['threads'],
                    'last': (time.monotonic(), 0, 0), 'now': (0.0, 0.0)}
            with self.lock:
                self.live[id(job)] = info
            self.event('%s (%s) started a write of %d files, %.1f GB.' % (name, peer, count, info['bytes_total'] / 1e9))
            readers = [threading.Thread(target=job.reader, daemon=True) for _ in range(settings['threads'])]
            for t in readers:
                t.start()
            job.send_all(wf)
            self._done(info, 'finished')
        except (ConnectionError, OSError) as e:
            if info is not None:
                self._done(info, 'stopped by the app' if isinstance(e, (ConnectionAbortedError, ConnectionResetError,
                                                                         BrokenPipeError)) else 'stopped: %s' % e)
        except Exception:                                   # never let one client take the service down
            log.exception('client %s failed', peer)
            if info is not None:
                self._done(info, 'failed (see the log)')
        finally:
            if job is not None:
                job.cancel()
            try:
                conn.close()
            except OSError:
                pass

    def _done(self, info, how):
        job = info['job']
        secs = max(0.001, time.time() - info['started'])
        rec = {'client': info['client'], 'name': info['name'], 'started': now_iso(info['started']),
               'seconds': round(secs, 1), 'files': job.files_done, 'files_total': info['files_total'],
               'gb': round(job.bytes_done / 1e9, 2), 'mbps': round(job.bytes_done / 1e6 / secs, 1),
               'not_served': job.not_served, 'problems': job.problems[:20], 'result': how}
        with self.lock:
            self.live.pop(id(job), None)
            self.history.appendleft(rec)
        self.event('%s: %s after %d of %d files, %.1f GB, %.0f MB/s%s.'
                   % (info['name'], how, job.files_done, info['files_total'], job.bytes_done / 1e9, rec['mbps'],
                      ', %d read over SMB instead' % job.not_served if job.not_served else ''))

    def _status_loop(self):
        shares, shares_t = [], 0.0
        while True:
            try:
                t = time.monotonic()
                if t - shares_t > 10:
                    shares, shares_t = self.shares.readable(), t
                jobs = []
                with self.lock:
                    live = list(self.live.values())
                for info in live:
                    job = info['job']
                    t0, f0, b0 = info['last']
                    if t - t0 >= 2:
                        info['now'] = ((job.bytes_done - b0) / 1e6 / (t - t0), (job.files_done - f0) / (t - t0))
                        info['last'] = (t, job.files_done, job.bytes_done)
                    secs = max(0.001, time.time() - info['started'])
                    jobs.append({'client': info['client'], 'name': info['name'], 'started': now_iso(info['started']),
                                 'files_done': job.files_done, 'files_total': info['files_total'],
                                 'bytes_done': job.bytes_done, 'bytes_total': info['bytes_total'],
                                 'current': job.current, 'mbps_now': round(info['now'][0], 1),
                                 'files_now': round(info['now'][1], 1),
                                 'mbps_avg': round(job.bytes_done / 1e6 / secs, 1),
                                 'readahead_mib': round(job.buffered / 1048576), 'readers': info['readers'],
                                 'not_served': job.not_served, 'problems': job.problems[-10:]})
                with self.lock:
                    history, events = list(self.history), list(self.events)
                status = {'version': VERSION, 'updated': now_iso(), 'started': now_iso(self.started),
                          'uptime_s': int(time.time() - self.started), 'port': self.port,
                          'settings': load_settings(self.var), 'shares': shares, 'jobs': jobs,
                          'history': history, 'events': events, 'disks': self.disks.sample()}
                tmp = os.path.join(self.var, 'status.json.%d.tmp' % os.getpid())
                with open(tmp, 'w', encoding='utf-8') as f:
                    json.dump(status, f, ensure_ascii=False)
                os.replace(tmp, os.path.join(self.var, 'status.json'))
            except Exception:
                log.exception('status update failed')
            time.sleep(1)


# ---------------------------------------------------------------------------------------------------------------
# bench / evict (development: how fast the NAS itself reads a folder)


def walk_files(root):
    """Files under root in directory order, depth first as met - the order the app adds a folder in."""
    out = []

    def walk(path):
        try:
            with os.scandir(path) as it:
                entries = list(it)
        except OSError:
            return
        for e in entries:
            try:
                if e.is_dir(follow_symlinks=False):
                    if e.name not in SKIP_DIRS:
                        walk(e.path)
                elif e.is_file(follow_symlinks=False):
                    st = e.stat(follow_symlinks=False)
                    out.append((e.path, st.st_size, st.st_ino))
            except OSError:
                pass

    walk(root)
    return out


def disk_position(path):
    """Physical address of the file's first extent (FIEMAP); 0 for empty and inline files."""
    import fcntl
    hdr, ext = struct.Struct('=QQIIII'), struct.Struct('=QQQQQIIII')
    buf = bytearray(hdr.pack(0, 0xFFFFFFFFFFFFFFFF, 0, 0, 1, 0) + bytes(ext.size))
    fd = os.open(path, os.O_RDONLY)
    try:
        fcntl.ioctl(fd, 0xC020660B, buf, True)
    finally:
        os.close(fd)
    if hdr.unpack_from(buf)[3] == 0:
        return 0
    e = ext.unpack_from(buf, hdr.size)
    return 0 if e[5] & 0x200 else e[1]


def parallel_map(func, items, threads):
    results, nxt, lock = [None] * len(items), [0], threading.Lock()

    def work():
        while True:
            with lock:
                i = nxt[0]
                nxt[0] += 1
            if i >= len(items):
                return
            try:
                results[i] = func(items[i])
            except OSError:
                results[i] = None

    ts = [threading.Thread(target=work, daemon=True) for _ in range(max(1, threads))]
    for t in ts:
        t.start()
    for t in ts:
        t.join()
    return results


def read_whole(path, buf):
    total, mv = 0, memoryview(buf)
    with open(path, 'rb', buffering=0) as f:
        if os.fstat(f.fileno()).st_size > CHUNK and hasattr(os, 'posix_fadvise'):
            os.posix_fadvise(f.fileno(), 0, 0, os.POSIX_FADV_SEQUENTIAL)
        while True:
            n = f.readinto(mv)
            if not n:
                return total
            total += n


def evict(path):
    """Drop the file's data from the page cache (no root needed; metadata stays cached)."""
    fd = os.open(path, os.O_RDONLY)
    try:
        os.posix_fadvise(fd, 0, 0, os.POSIX_FADV_DONTNEED)
    finally:
        os.close(fd)


def drop_caches():
    """Empty the NAS caches (needs: link ALL=(root) NOPASSWD: /usr/bin/tee /proc/sys/vm/drop_caches)."""
    os.sync()
    r = subprocess.run(['sudo', '-n', '/usr/bin/tee', '/proc/sys/vm/drop_caches'], input=b'3\n',
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if r.returncode != 0:
        sys.exit('drop caches failed (sudo -n): ' + r.stdout.decode(errors='replace').strip())


def bench(args):
    for order, threads in [(o, int(t)) for o in args.order.split(',') for t in args.threads.split(',')]:
        if args.drop:
            drop_caches()
        t0 = time.monotonic()
        files = walk_files(args.folder)
        t_walk = time.monotonic() - t0
        files = files[args.skip:args.skip + args.count] if args.count else files[args.skip:]
        if args.evict:
            parallel_map(lambda f: evict(f[0]), files, 16)
        t_order = 0.0
        if order == 'inode':
            files.sort(key=lambda f: f[2])
        elif order == 'disk':
            t1 = time.monotonic()
            pos = parallel_map(lambda f: disk_position(f[0]), files, 16)
            t_order = time.monotonic() - t1
            files = [files[i] for _, i in sorted(zip(((p or 0) for p in pos), range(len(files))))]
        bufs = threading.local()

        def one(f):
            b = getattr(bufs, 'b', None)
            if b is None:
                b = bufs.b = bytearray(CHUNK)
            return read_whole(f[0], b)

        t2 = time.monotonic()
        got = parallel_map(one, files, threads)
        t_read = max(0.001, time.monotonic() - t2)
        ok = [g for g in got if g is not None]
        print('%-5s threads=%-2d files=%d (%d failed) %.2f GB: read %.1f s = %.0f files/s, %.0f MB/s  [walk %.1f s%s]'
              % (order, threads, len(files), len(files) - len(ok), sum(ok) / 1e9, t_read, len(ok) / t_read,
                 sum(ok) / 1e6 / t_read, t_walk, ', disk order %.1f s' % t_order if order == 'disk' else ''),
              flush=True)


def main():
    p = argparse.ArgumentParser(prog='lcg-nasreader')
    sub = p.add_subparsers(dest='cmd')
    s = sub.add_parser('serve', help='run the service')
    s.add_argument('--var', required=True, help='folder for the key, settings, status and log')
    s.add_argument('--share', action='append', default=[], help='extra share NAME=PATH (tests)')
    s.add_argument('--port', type=int, default=0)
    s.add_argument('--test-drop-after', type=int, default=0, help=argparse.SUPPRESS)   # fault tests: cut each job there
    k = sub.add_parser('key', help='print the key the app needs')
    k.add_argument('--var', required=True)
    k.add_argument('--new', action='store_true', help='replace it first')
    b = sub.add_parser('bench', help='read a folder like a tape write and print the rate')
    b.add_argument('folder')
    b.add_argument('--threads', default='1,4,16', help='comma list (default 1,4,16)')
    b.add_argument('--order', default='path', help='comma list of path, inode, disk (default path)')
    b.add_argument('--skip', type=int, default=0, help='files to skip at the start (path order)')
    b.add_argument('--count', type=int, default=0, help='files to read after the skipped ones (0 = all)')
    b.add_argument('--drop', action='store_true', help='empty the NAS caches before each run (sudo -n)')
    b.add_argument('--evict', action='store_true', help="drop the files' cached data before each run (no root)")
    e = sub.add_parser('evict', help="drop a folder's cached file data")
    e.add_argument('folder')
    args = p.parse_args()
    if args.cmd == 'serve':
        os.makedirs(args.var, exist_ok=True)
        handler = logging.handlers.RotatingFileHandler(os.path.join(args.var, 'lcg-nasreader.log'),
                                                       maxBytes=1 << 20, backupCount=3, encoding='utf-8')
        handler.setFormatter(logging.Formatter('%(asctime)s %(levelname)s %(message)s'))
        logging.basicConfig(level=logging.INFO, handlers=[handler])
        extra = dict(x.split('=', 1) for x in args.share)
        Service(args.var, extra, args.port, args.test_drop_after).run()
    elif args.cmd == 'key':
        os.makedirs(args.var, exist_ok=True)
        print(load_key(args.var, args.new))
    elif args.cmd == 'bench':
        bench(args)
    elif args.cmd == 'evict':
        files = walk_files(args.folder)
        parallel_map(lambda f: evict(f[0]), files, 16)
        print('evicted %d files' % len(files))
    else:
        p.print_help()


if __name__ == '__main__':
    main()
