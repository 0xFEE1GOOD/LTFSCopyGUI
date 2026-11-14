# LTFSCopyGui2 versions

LTFSCopyGui2 and LTFSCatalog always carry the same version, set in one place: `Common\AppVersion.vb`.
Every version pushed to GitHub adds 0.001 (1.001, 1.002, 1.003 ...) and gets a line here.

| Version | Date | Changes |
|---|---|---|
| 1.001 | 2026-11-14 | First LTFSCopyGui2 version: this branch of LTFSCopyGUI 3.6.0 with the upstream build number and the "Non-commercial License" title text removed. Includes the work on this branch so far: small-file writing, restore read-ahead, LTFSCatalog with restore hand-off, the Segoe UI facelift, and the usability changes (labelled status lights, steady I/O light, keyboard shortcut windows, menu items that open the other app). New tapes record LTFSCopyGui2 1.001 in the index creator and in the cartridge memory application name/version. |
| 1.002 | 2026-11-14 | Restore no longer stops with "Illegal characters in path". Cause: the index writer put a line break before a name that starts with ' &amp; &lt; &gt; or " (inherited from upstream LTFSCopyGUI 3.6.0; affects tapes written by it and by 1.001). The writer is fixed, and restore (folder, selected files, catalog hand-off, extract as empty files, delete disk files by index) drops line breaks at the ends of a name and replaces characters Windows rejects with _ (logged as "Name changed for Windows"). Each new index generation now records the program that wrote it (it kept the creator of the first index). |
| 1.003 | 2026-11-14 | Device configurator, TapeAlert Response log page (12h): every flag was labelled one number too low (a "Flag 00h" existed and 40h was missing), so e.g. the bit shown as "31h Diminished Native Capacity" was really 32h Lost Statistics. Labels now start at 01h, in the code and in the shipped logpages\0x12.xml. The TapeAlert log page (2Eh) was already right. |
