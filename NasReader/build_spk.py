"""Builds the LTFS NAS Reader DSM package (LtfsNasReader-<version>-<build>.spk) from NasReader\\.

    python build_spk.py [--version 2.002] [--build 1] [--out DIR]

The version defaults to the app's (Common\\AppVersion.vb). DSM 7, arch noarch, no compiled code, so the package is
just two tar files: package.tgz (what DSM unpacks to /var/packages/LtfsNasReader/target) inside the .spk tar with INFO,
scripts/, conf/ and the icons. Text files get LF line endings and Unix modes here, whatever the checkout has.
"""
import argparse
import gzip
import hashlib
import io
import os
import re
import struct
import tarfile
import time
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
PKG = 'LtfsNasReader'
SCRIPTS = ('preinst', 'postinst', 'preuninst', 'postuninst', 'preupgrade', 'postupgrade')


def app_version():
    with open(os.path.join(HERE, '..', 'Common', 'AppVersion.vb'), encoding='utf-8') as f:
        return re.search(r'Number As String = "([\d.]+)"', f.read()).group(1)


def text(path, replace=None):
    with open(path, 'rb') as f:
        data = f.read().replace(b'\r\n', b'\n')
    for a, b in (replace or {}).items():
        data = data.replace(a, b)
    return data


# ---------------------------------------------------------------------------------------------------------------
# Icon: a white tape cartridge on a blue rounded square, drawn here (4x4 supersampling) so the repo holds no images


def _rounded(x, y, x0, y0, x1, y1, r):
    if not (x0 <= x <= x1 and y0 <= y <= y1):
        return False
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def _sample(u, v):
    """RGBA at (u, v) in 0..1."""
    if not _rounded(u, v, 0.02, 0.02, 0.98, 0.98, 0.2):
        return (0, 0, 0, 0)
    t = v
    bg = (int(0x3B + (0x1D - 0x3B) * t), int(0x82 + (0x4E - 0x82) * t), int(0xF6 + (0xD8 - 0xF6) * t), 255)
    if _rounded(u, v, 0.15, 0.25, 0.85, 0.75, 0.06):                     # cartridge
        for cx in (0.36, 0.64):
            d = (u - cx) ** 2 + (v - 0.53) ** 2
            if d <= 0.035 ** 2:
                return (255, 255, 255, 255)                                 # hub
            if d <= 0.11 ** 2:
                return (0x1E, 0x29, 0x3B, 255)                              # reel
        if _rounded(u, v, 0.3, 0.3, 0.7, 0.38, 0.015):
            return (0xCB, 0xD5, 0xE1, 255)                                  # label
        return (255, 255, 255, 255)
    return bg


def png(size):
    rows, ss = [], 4
    for y in range(size):
        row = bytearray(b'\x00')
        for x in range(size):
            acc = [0, 0, 0, 0]
            for sy in range(ss):
                for sx in range(ss):
                    r, g, b, a = _sample((x + (sx + 0.5) / ss) / size, (y + (sy + 0.5) / ss) / size)
                    acc[0] += r * a
                    acc[1] += g * a
                    acc[2] += b * a
                    acc[3] += a
            a = acc[3]
            row += bytes((acc[0] // a, acc[1] // a, acc[2] // a, a // (ss * ss))) if a else b'\x00\x00\x00\x00'
        rows.append(bytes(row))

    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xFFFFFFFF)

    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)) +
            chunk(b'IDAT', zlib.compress(b''.join(rows), 9)) + chunk(b'IEND', b''))


# ---------------------------------------------------------------------------------------------------------------


def add(tar, name, data, mode=0o644):
    info = tarfile.TarInfo(name)
    info.size, info.mode, info.mtime = len(data), mode, int(time.time())
    info.uid = info.gid = 0
    info.uname = info.gname = 'root'
    tar.addfile(info, io.BytesIO(data))


def add_dir(tar, name):
    info = tarfile.TarInfo(name)
    info.type, info.mode, info.mtime = tarfile.DIRTYPE, 0o755, int(time.time())
    tar.addfile(info)


def build(version, build_no, out):
    icons = {s: png(s) for s in (16, 24, 32, 48, 64, 72, 256)}
    p = os.path.join(HERE, 'package')

    payload = io.BytesIO()
    with gzip.GzipFile(fileobj=payload, mode='wb', mtime=0) as gz:
        with tarfile.open(fileobj=gz, mode='w', format=tarfile.USTAR_FORMAT) as tar:
            for d in ('bin', 'ui', 'ui/cgi', 'ui/images', 'port_conf'):
                add_dir(tar, d)
            add(tar, 'bin/lcg-nasreader.py',
                text(os.path.join(HERE, 'src', 'lcg-nasreader.py'), {b"'@VERSION@'": ("'%s'" % version).encode()}), 0o755)
            add(tar, 'ui/config', text(os.path.join(p, 'ui', 'config')))
            add(tar, 'ui/index.html', text(os.path.join(p, 'ui', 'index.html')))
            for name in sorted(os.listdir(os.path.join(p, 'ui', 'cgi'))):
                if name.endswith(('.cgi', '.py')):
                    add(tar, 'ui/cgi/' + name, text(os.path.join(p, 'ui', 'cgi', name)), 0o755 if name.endswith('.cgi') else 0o644)
            for s, data in icons.items():
                add(tar, 'ui/images/icon_%d.png' % s, data)
            add(tar, 'port_conf/ltfsnasreader.sc', text(os.path.join(p, 'port_conf', 'ltfsnasreader.sc')))
    payload = payload.getvalue()

    info = '\n'.join([
        'package="%s"' % PKG,
        'version="%s-%d"' % (version, build_no),
        'os_min_ver="7.0-40000"',
        'displayname="LTFS NAS Reader"',
        'description="Reads files on this NAS for LTFSCopyGui2NAS tape writes: the app sends the list of files, the NAS '
        'reads them with many reads at once and sends them back in write order. Read-only; it reads only shares you '
        'give the user sc-ltfsnasreader Read only access to."',
        'maintainer="LTFSCopyGui2NAS"',
        'arch="noarch"',
        'dsmuidir="ui"',
        'dsmappname="com.ltfscopygui2nas.nasreader"',
        'ctl_stop="yes"',
        'checksum="%s"' % hashlib.md5(payload).hexdigest(),
    ]) + '\n'

    spk = os.path.join(out, '%s-%s-%d.spk' % (PKG, version, build_no))
    os.makedirs(out, exist_ok=True)
    with tarfile.open(spk, mode='w', format=tarfile.USTAR_FORMAT) as tar:
        add(tar, 'INFO', info.encode())
        add(tar, 'package.tgz', payload)
        add_dir(tar, 'scripts')
        add(tar, 'scripts/start-stop-status', text(os.path.join(HERE, 'spk', 'scripts', 'start-stop-status')), 0o755)
        for name in SCRIPTS:
            add(tar, 'scripts/' + name, b'#!/bin/sh\nexit 0\n', 0o755)
        add_dir(tar, 'conf')
        add(tar, 'conf/privilege', text(os.path.join(HERE, 'spk', 'conf', 'privilege')))
        add(tar, 'conf/resource', text(os.path.join(HERE, 'spk', 'conf', 'resource')))
        add(tar, 'PACKAGE_ICON.PNG', icons[64])
        add(tar, 'PACKAGE_ICON_256.PNG', icons[256])
    print('built %s (%d KB)' % (spk, os.path.getsize(spk) // 1024))
    return spk


if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('--version', default=None)
    ap.add_argument('--build', type=int, default=1)
    ap.add_argument('--out', default=os.path.join(HERE, 'out'))
    a = ap.parse_args()
    build(a.version or app_version(), a.build, a.out)
