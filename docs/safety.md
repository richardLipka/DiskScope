# Safety

DiskScope deletes files, often many at once, sometimes while running as administrator. This page describes
what protects your data and your system.

## What can never be deleted

Every deletion passes through one final check (`DeletionGuard`). It doesn't matter whether the deletion comes
from the tree, a search result, the cleanup catalog or duplicates: the check runs right before each item is
deleted.

**Never deleted themselves, nor any folder that contains them:**

- drive roots (`C:\`, `D:\`, and so on);
- `%SystemRoot%` (Windows), `%ProgramFiles%`, `%ProgramFiles(x86)%`, `%ProgramData%`;
- `C:\Users`, every user profile folder, and each profile's `AppData`, `AppData\Local`, `AppData\Roaming`,
  `AppData\LocalLow`, `Desktop`, `Documents`, `Downloads`, `Pictures`, `Music`, `Videos` and `OneDrive` folders.

You can still delete what is *inside* those folders, with one exception. Inside Windows, only these places may
be emptied:

`Windows\Temp`, `Windows\Minidump`, `Windows\MEMORY.DMP`, `Windows\LiveKernelReports`,
`Windows\SoftwareDistribution\Download`, `Windows\Logs`.

**Links can't be used to sneak around this.** The check is applied to the path as shown and also to where it
really leads: if a junction or symbolic link in a folder above makes `C:\Windows` appear as
`D:\Stuff\old`, the real target is checked and the deletion is refused. Deleting a link itself is
allowed, because that removes only the link.

## Deleting the right thing

- **A file that changed since the scan is not deleted.** Before deleting a file, DiskScope compares its current
  size and last-write time with the scan. If either differs, the file is skipped and marked "the file changed
  since the scan".
- **Junctions and symbolic links are never followed** when deleting permanently: the link is removed and its
  target is left alone. Scanning doesn't follow them either.
- **Names ending in a dot or a space** (`report.`, `data `) are legal in NTFS but are silently trimmed by the
  normal Windows path rules, so a careless delete could hit a look-alike file. All of DiskScope's file access
  uses extended `\\?\` paths, which keep the name exact. The Recycle Bin can't take such items safely, so
  DiskScope refuses to send them there and asks you to delete them permanently.
- **Long paths** (over 260 characters) work everywhere for the same reason.
- **A failed delete never looks like a success.** DiskScope tells "the item is gone" apart from "the disk can't
  answer", such as a disk error, a drive that was unplugged or access denied. An item is removed from the tree
  only when it is really gone.

## Errors don't take the app down

Deleting, scanning and every analysis are built to survive a failing disk, a drive unplugged in the middle of a
scan, locked files, access denied, extremely deep folder trees and damaged file system data:

- A folder that can't be read is counted as an access error. The scan goes on, and a scan whose source
  disappears is reported as failed instead of as a falsely complete scan.
- One locked file doesn't stop the deletion of a folder. The rest is deleted, and the folder is reported as
  partly deleted and rescanned.
- Deep trees are walked without recursion, so 30,000 nested folders can't overflow the stack.
- Unexpected errors are written to `data\error.log` and shown, instead of closing the window.

These cases are covered by automated tests that simulate disk errors and unplugged drives. Other tests run
analyses while a scan or a deletion changes the tree at the same time, and check trees of 30 million entries.
See [Development](development.md).

## Untrusted data

DiskScope builds deletion paths from the names it has stored, so it treats every source of names as untrusted:

- **Raw MFT data** (fast scan) is validated record by record. Damaged records, cycles in the folder structure
  and impossible sizes or times are dropped or treated as unknown. They never crash the scan or produce
  absurd totals.
- **Saved scan files** (`.dscan`) are checked when loaded. A name containing `\`, `/`, `:` or control
  characters, or a name that is `.` or `..`, makes the file invalid, so a crafted file can't make a node point
  at a different path. Unknown flags are dropped.
- **The cleanup catalog** can only suggest places. It can't override the rules above.

## Administrator mode

Running as administrator lets DiskScope read the MFT and folders a normal user can't. Two precautions apply:

- Files you **open** from DiskScope are opened through Explorer. The programs they start therefore run with your
  normal rights, not as administrator. The Windows context menu shown from an elevated DiskScope is marked as
  running as administrator.
- The deletion rules above are the same with or without administrator rights.

## Privacy

DiskScope makes no network connections. It sends nothing anywhere, and has no telemetry or update check.
**Copy question for AI** only puts text on the clipboard. Note that the text contains paths and file names, so
read it before pasting it anywhere.

Everything DiskScope writes stays in its `data` folder (settings, saved scans, your catalog, the error log).
The only exception is the optional Explorer menu item, which is a per-user registry entry (HKCU) that you can
remove again from the ⚙ menu.

## Reporting a problem

If you find a way to make DiskScope delete something it shouldn't, please open an issue on GitHub with the
steps to reproduce it.
