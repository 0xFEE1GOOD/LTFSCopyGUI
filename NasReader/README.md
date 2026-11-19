# LTFS NAS Reader

A DSM 7 package for a Synology NAS. When LTFSCopyGui2NAS writes files that sit on this NAS, the reader on the NAS
reads them and sends them to the app, instead of the app reading every file over SMB.

## How it works

The app still adds folders over the share as before, so names, times and the tape index don't change.

1. When a write starts, the app sends the NAS the list of files that are on it.
2. The NAS reads them with many reads at once on its own disks.
3. It sends the files back in write order.

Any file the NAS can't send is read over the share as before. That covers a file it can't find or read, a share it
doesn't know, a lost connection, and a wrong key.

Measured on a DS3615xs over 10G (cold office folder, 7,126 files, 5.9 GB):

| Method | MB/s | files/s |
|---|---|---|
| SMB, one reader | 57 | 69 |
| SMB, 4 readers over 3 connections (LTFSCopyGui2 1.011) | 132 | 159 |
| LTFS NAS Reader | ~500 | ~600 |

With the NAS reader, no second dropped below 300 MB/s. Since 2.003 the NAS reads large files in 8 MiB pieces with
several readers at once (two cold 12–15 GiB .pst files: 564 MB/s through the reader). Read-ahead is capped at its
window for every file, including the one being sent.

In LTFSCopyGui2NAS, the **NAS** light in the writer's status line shows whether the reader is ready:
- **Blue:** set up, answering, and the key is accepted.
- **Grey:** `LTFSWriter_NasReader` is empty.
- **Red:** set up but not ready. The tooltip says why and which shares it can read.

The writer log gets a "Source progress" line every 10 s. The NAS log gets a line per write every 10 s with the rate
and what the NAS waited for: its disks, or the app (the tape side).

## Install

1. In DSM, open Package Center › Manual Install and choose `LtfsNasReader-<version>.spk`. DSM warns that the
   publisher is unknown; continue.
2. Give the package's user read access to the shares you write from. Open Control Panel › Shared Folder, choose the
   share (e.g. Documents), then Edit › Permissions › System internal user › `sc-ltfsnasreader` › Read only. The
   package can read nothing else and can't change any file.
3. Open **LTFS NAS Reader** from the DSM main menu (administrators only), then use Key › Copy.
4. In LTFSCopyGui2NAS settings, set:
   - `LTFSWriter_NasReader` to the NAS address, e.g. `192.168.3.5` (or `192.168.3.5:7171`).
   - `LTFSWriter_NasReaderKey` to the key.

The writer log then shows "Source reads go through the NAS reader at 192.168.3.5:7171: N of M files". At the end of
the write it shows how many files the NAS read.

## The page

The page shows:

- **Writing now:** files and GB done, MB/s and files/s, the read-ahead, and any files sent back to the share.
- **Disks:** how busy each RAID member is, with a warning while a RAID check or resync is running. Reads are slower
  then.
- **Shares:** which shares the package can read.
- **Recent writes**, **Events**, **Settings** (port, readers, read-ahead) and **Key** (Show, Copy, New key).

## Update and remove

- To update, install the newer `.spk` the same way. The settings and the key are kept.
- To remove it, use Package Center › Uninstall. Nothing stays behind.

## Files

| Path | What it is |
|---|---|
| `src/lcg-nasreader.py` | The service. Python 3.8+ standard library, runs on DSM's own Python. The protocol is described at the top. |
| `package/ui/` | The DSM page: `index.html` plus `cgi/` (DSM sign-in check, administrators only). |
| `spk/` | Package scripts and `conf/` (runs as its own unprivileged user; port 7171 registered for the firewall). |
| `build_spk.py` | Builds the `.spk`. CI builds it with the app (artifact `LtfsNasReader-spk-<commit>`). |

For development, run `lcg-nasreader.py bench <folder> --evict --threads 1,16` on the NAS. It shows how fast the NAS
itself reads a folder with the file data cold, and needs no root.
