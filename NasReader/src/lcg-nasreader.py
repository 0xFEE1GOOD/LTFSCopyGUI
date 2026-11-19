#!/usr/bin/env python3
"""LTFS NAS Reader - reads files on the NAS for LTFSCopyGui2NAS (read-only; Python 3.8+, stdlib only).

Subcommands:
  bench <folder> [options]   read a folder the way a tape write would and print files/s and MB/s
"""
import argparse
import fcntl
import os
import struct
import subprocess
import sys
import threading
import time

CHUNK = 8 << 20                     # read size and large-file piece
SKIP_DIRS = {'@eaDir', '#recycle', '#snapshot', '@tmp', '@sharebin'}   # Synology folders SMB does not show

# ---------------------------------------------------------------------------------------------------------------
# File order


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


_FS_IOC_FIEMAP = 0xC020660B
_FIEMAP_HDR = struct.Struct('=QQIIII')          # start, length, flags, mapped_extents, extent_count, reserved
_FIEMAP_EXT = struct.Struct('=QQQQQIIII')       # logical, physical, length, reserved x2, flags, reserved x3
_FIEMAP_EXTENT_DATA_INLINE = 0x200


def disk_position(path):
    """Physical address of the file's first extent; 0 for empty and inline files (they live in the metadata)."""
    buf = bytearray(_FIEMAP_HDR.pack(0, 0xFFFFFFFFFFFFFFFF, 0, 0, 1, 0) + bytes(_FIEMAP_EXT.size))
    fd = os.open(path, os.O_RDONLY)
    try:
        fcntl.ioctl(fd, _FS_IOC_FIEMAP, buf, True)
    finally:
        os.close(fd)
    if _FIEMAP_HDR.unpack_from(buf)[3] == 0:
        return 0
    ext = _FIEMAP_EXT.unpack_from(buf, _FIEMAP_HDR.size)
    return 0 if ext[5] & _FIEMAP_EXTENT_DATA_INLINE else ext[1]


def parallel_map(func, items, threads):
    """func(item) for every item on `threads` threads; results in item order (None where func raised)."""
    results = [None] * len(items)
    nxt = [0]
    lock = threading.Lock()

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


# ---------------------------------------------------------------------------------------------------------------
# Reading


def read_whole(path, buf):
    """Reads the file into buf piece by piece; returns the byte count. Read-only open, nothing else."""
    total = 0
    mv = memoryview(buf)
    with open(path, 'rb', buffering=0) as f:
        if os.fstat(f.fileno()).st_size > CHUNK:
            os.posix_fadvise(f.fileno(), 0, 0, os.POSIX_FADV_SEQUENTIAL)
        while True:
            n = f.readinto(mv)
            if not n:
                return total
            total += n


# ---------------------------------------------------------------------------------------------------------------
# bench


def drop_caches():
    """Empty the NAS page, dentry and inode caches. Needs one sudo rule (dev machines only):
    link ALL=(root) NOPASSWD: /usr/bin/tee /proc/sys/vm/drop_caches"""
    os.sync()
    r = subprocess.run(['sudo', '-n', '/usr/bin/tee', '/proc/sys/vm/drop_caches'], input=b'3\n',
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if r.returncode != 0:
        sys.exit('drop caches failed (sudo -n): ' + r.stdout.decode(errors='replace').strip())


def bench(args):
    configs = [(order, int(t)) for order in args.order.split(',') for t in args.threads.split(',')]
    for order, threads in configs:
        if args.drop:
            drop_caches()
        t0 = time.monotonic()
        files = walk_files(args.folder)
        t_walk = time.monotonic() - t0
        files = files[args.skip:args.skip + args.count] if args.count else files[args.skip:]
        t_order = 0.0
        if order == 'inode':
            files.sort(key=lambda f: f[2])
        elif order == 'disk':
            t1 = time.monotonic()
            pos = parallel_map(lambda f: disk_position(f[0]), files, 16)
            t_order = time.monotonic() - t1
            keyed = sorted(zip(((p if p is not None else 0) for p in pos), range(len(files))))
            files = [files[i] for _, i in keyed]
        bufs = threading.local()

        def one(f):
            b = getattr(bufs, 'b', None)
            if b is None:
                b = bufs.b = bytearray(CHUNK)
            return read_whole(f[0], b)

        t2 = time.monotonic()
        got = parallel_map(one, files, threads)
        t_read = time.monotonic() - t2
        ok = [g for g in got if g is not None]
        nbytes = sum(ok)
        print('%-5s threads=%-2d files=%d (%d failed) %.2f GB: read %.1f s = %.0f files/s, %.0f MB/s'
              '  [walk %.1f s%s]' % (order, threads, len(files), len(files) - len(ok), nbytes / 1e9, t_read,
                                     len(ok) / t_read if t_read else 0, nbytes / 1e6 / t_read if t_read else 0,
                                     t_walk, ', disk order %.1f s' % t_order if order == 'disk' else ''),
              flush=True)


def main():
    p = argparse.ArgumentParser(prog='lcg-nasreader')
    sub = p.add_subparsers(dest='cmd', required=True)
    b = sub.add_parser('bench', help='read a folder like a tape write and print the rate')
    b.add_argument('folder')
    b.add_argument('--threads', default='1,4,16', help='comma list (default 1,4,16)')
    b.add_argument('--order', default='path,disk', help='comma list of path, inode, disk (default path,disk)')
    b.add_argument('--skip', type=int, default=0, help='files to skip at the start (path order)')
    b.add_argument('--count', type=int, default=0, help='files to read after the skipped ones (0 = all)')
    b.add_argument('--drop', action='store_true', help='empty the NAS caches before each run (sudo -n)')
    args = p.parse_args()
    if args.cmd == 'bench':
        bench(args)


if __name__ == '__main__':
    main()
