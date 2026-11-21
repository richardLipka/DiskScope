# User guide

- [Drive overview](#drive-overview)
- [Scanning](#scanning)
- [The tree](#the-tree)
- [Deleting, moving and compressing](#deleting-moving-and-compressing)
- [Finding what to delete](#finding-what-to-delete)
- [Saved scans and comparing](#saved-scans-and-comparing)
- [Settings and files](#settings-and-files)
- [Keyboard and mouse](#keyboard-and-mouse)
- [Command line](#command-line)

## Drive overview

When there is nothing to open, DiskScope starts with the **drive overview**. **🏠 Drives** in the toolbar brings
it back at any time. It lists every drive with:

- how full it is (the bar turns red above 90 %), used, free and total space;
- when it was last scanned;
- **Change since**: how much more (or less) the drive holds now than at that scan. This works for scans made
  with DiskScope 0.2 or later, and it needs no new scan.

Double-click a drive to open its last scan, or to scan it when there is none. **Open last scan** and **Scan** do
the same explicitly.

## Scanning

Choose a drive or folder in the box at the top left (or with **Folder…**) and press **Scan**. The tree fills in
while the scan runs, and you can browse, search and analyze right away. **Stop** ends the scan early and keeps
what has been counted so far, marked as incomplete.

DiskScope has two ways of scanning and picks one automatically:

| Mode | When | How |
|---|---|---|
| **MFT** | A whole NTFS drive, with DiskScope running as administrator | Reads the drive's Master File Table directly. Usually a whole drive takes tens of seconds. Hard links are counted once. |
| **Directory** | Everything else (folders, other file systems, no admin rights) | Lists folders in parallel with native NT calls. Works on NTFS, ReFS, exFAT, FAT32 and network drives. |

**Run as administrator** in the toolbar restarts DiskScope elevated, which enables the MFT mode and lets it read
folders a normal user can't. If an MFT read fails, DiskScope falls back to listing folders by itself.

What is counted:

- **On disk** is the space the item really occupies (allocated size). It is the main column, because it is
  what you get back by deleting. **Size** is the logical size. Compressed, sparse and very small files differ
  between the two.
- Junctions and symbolic links are shown but not followed, so nothing is counted twice.
- OneDrive and other cloud placeholders count with the space they take locally and are never downloaded.
- Files compressed by Windows (by DiskScope's Compress, `compact`, or CompactOS) count with their compressed
  size.
- Folders that can't be read are counted as "access errors" in the status bar. The scan carries on.

## The tree

Columns: name, **On disk**, **% of parent** (with a bar), **Size**, **Files**, **Folders**, **Modified** and
**Accessed**. Click a header to sort by it. For a folder, Modified and Accessed show the newest time of anything
inside, which makes old, untouched folders easy to spot.

Access times are a hint, not proof. Antivirus scans, indexing and backups can update them too, and Windows can
be set not to record them at all.

The line under the tree describes the selected item. The breadcrumb at the top shows where you are. **Enter**
zooms into a folder so that it becomes the top of the tree, and **Backspace** or **Up** goes back out.

**Type to find:** typing letters jumps to the next visible item whose name starts with them. Typing the same
letter again moves on to the next match.

### Filtering the tree (Ctrl+L)

The **Filter** box on the right of the breadcrumb bar narrows the tree:

- **Names**: wildcards or part of a name, several patterns separated by `;`. For example `*.iso; *.vhdx`, or
  `backup`. The tree then shows only matching files and the folders that lead to them. A folder whose own name
  matches (say `node_modules`) is shown with everything inside, so you can still look into each one.
- **Size**: hides everything smaller than the chosen size on disk. Folders count by their total.

**Esc** or **✕** clears the filter. Folder totals in the columns stay the real totals, filtered or not. While a
scan runs, the filter keeps up with new items every few seconds.

### Context menu

Right-click an item for:

- **Open** (with the default app), **Show in Explorer**, **Copy path**
- **Copy question for AI**, **Rescan folder** (F5; use it after changing things outside DiskScope),
  **Find duplicates in this folder**
- **Move to Recycle Bin**, **Delete permanently…**
- **Move to another drive…**, **Compress…**, and for a `.vhdx`/`.vhd` file **Compact virtual disk…**
- **More Windows options**: the item's full Windows context menu (also on **Shift + right-click**)

When DiskScope runs as administrator, files are opened through Explorer, so the programs they start run with
your normal rights, not as administrator.

## Deleting, moving and compressing

None of these block the window. After you confirm, every item goes into a queue, shown on the **Delete & move**
tab, and DiskScope works through it one item at a time in the background:

- You can keep browsing and keep adding items while it works.
- Each item shows its state: waiting, in progress (with a percentage for moving and compressing), done, failed
  (with the reason) or partly done.
- **Pause**, **Resume**, **Cancel waiting** and **Remove finished** control the queue. The status bar shows how
  much is left and how much space has been freed.
- Queued items are greyed in the tree. When an item is done, the tree follows: deleted and moved items disappear
  and the totals above them go down, while compressed folders are rescanned for their new sizes.
- If items are still waiting when you close the window, DiskScope asks first.

### Deleting

**Recycle Bin** deletion uses the Windows recycle bin without its dialogs. **Permanent** deletion walks the folder
itself. A locked file is skipped and the rest is still deleted, and junctions are removed as links without
touching what they point to.

### Moving to another drive

Large things you want to keep, such as a photo archive, old projects or a game library, can be moved to another
drive instead of deleted. **Move to another drive…** asks for the destination folder and shows how much space
it has.

- To another drive, everything is **copied first**. The copy is checked (same files, same sizes), and only then
  is the original deleted. If anything fails or you cancel, the partial copy is removed and the original stays.
  Attributes and times are kept.
- On the same drive it is just a rename, and no space is freed. DiskScope warns about that.
- **Leave a link (junction)** keeps a folder reachable at its old path, so programs that expect it there
  still work. The junction takes no space.
- A folder that itself contains junctions or symbolic links is not moved, because copying would break them or
  duplicate what they lead to.

### Compressing

**Compress…** uses Windows' own file compression (the same as `compact /exe`). Files stay where they are and open
as usual; reading them costs a little CPU. It suits data that is read but rarely changed, such as installed
programs, games and old projects. A file that is written to afterwards is stored uncompressed again.

| Algorithm | When |
|---|---|
| **LZX** | saves the most; slower to read, best for rarely used data |
| **XPRESS 16K** | a good balance |
| **XPRESS 8K** / **4K** | faster, saves less |

It needs NTFS. Drive roots and anything inside Windows are refused; `compact /compactos` is the tool for Windows
itself. Files that are open for writing elsewhere are skipped and counted.

### Compacting virtual disks

The virtual disks of WSL distributions and Docker Desktop (`ext4.vhdx`, `docker_data.vhdx`) grow as data is
written inside them and never shrink by themselves, even after that data is deleted. **Compact virtual disk…**
gives the unused space back:

1. Quit Docker Desktop, and leave **Shut down WSL first** checked for a WSL disk.
2. Windows asks for administrator permission, unless DiskScope already runs as administrator.
3. DiskScope runs Windows' `diskpart` to attach the disk read-only, compact it and detach it. It then reports
   the size before and after.

This works on every Windows edition, Home included.

### What is never touched

Drive roots, Windows, Program Files, ProgramData, user profiles and their main folders are never deleted or
moved, whatever is selected. A file that has changed since the scan is skipped. See [Safety](safety.md) for the
full rules.

## Finding what to delete

### Search (Ctrl+F)

The **Find** tab lists the largest matching items:

- **What**: largest files, folders by their own content (only the files directly inside, which finds folders
  that are big themselves, not just because of a big subfolder), folders by total size, or everything.
- **Name**: wildcards or plain text, several patterns separated by `;`. For example `*.iso; *.zip`, or `backup`.
- **Size**: a minimum size.
- **Age**: *unchanged* or *not opened* for 30 days, 6 months, a year or 3 years. An item whose time is unknown
  never counts as old.
- **Only in the open folder** limits the search to the folder you are in.

Searching tens of millions of items takes well under a second. Results can be deleted, opened or revealed
in the tree straight from the list.

### File types

**Analyses → File types** shows what kinds of files fill the folder selected in the tree. Totals per extension
are grouped by kind (video, images, archives, disk images, programs…), each with its size, number of files and
share. Select an extension to see its largest files on the right, and act on them from their context menu. The
panel follows the tree: select another folder and it counts that one.

### Cleanup

The **Cleanup** tab checks the scan against a [catalog of known places](cleanup-catalog.md) and lists the ones
it finds, with the space they take, grouped by how safe they are:

- **Safe**: caches and temporary files. Deleting them loses nothing that can't be recreated, so they can be
  emptied with one click.
- **With care**: deletable, but at a cost, such as downloading again, losing offline copies or losing
  rollback.
- **Do not delete by hand**: the description names the Windows tool that frees the space properly (for
  example WinSxS or the Installer cache).

**Only caches that can be downloaded again** shows developer and package caches (npm, Yarn, pnpm, Maven,
Gradle, NuGet, pip, Conda, Cargo, Go, Composer, Git LFS…) that their tools fetch again when needed.
**Select re-downloadable caches** selects them all, ready to be emptied in one go.

Select an entry to read what it is and how to clean it up correctly. **Edit catalog…** opens your own copy of
the catalog. Right-click a found place to empty it (to the Recycle Bin or permanently) or to copy a question for
AI.

### Abandoned data

**Analyses → Abandoned data** looks for folders in AppData, ProgramData and Program Files that no installed
program accounts for. It compares them with the programs listed in Windows (Apps & features) and with the
installed Microsoft Store apps. Leftovers of uninstalled Store apps are found reliably. For other folders the
match is by name, so the results are suggestions: anything still written to recently is marked "possibly
abandoned". If the list of installed programs can't be read, the analysis refuses to run instead of reporting
everything as abandoned.

### Duplicates

**Analyses → Duplicate files…** finds files with identical content, in the whole scan or in one folder and its
subfolders. It works in stages, so it reads as little as possible:

1. It groups files by size, using the sizes from the scan. Files that differ in size can't be duplicates.
2. For the candidates, it reads their actual sizes on the disk now.
3. It compares the first and last 64 KB of each file.
4. Only files that still match have their whole content compared (SHA-256).

Hard links to the same file are not reported, and cloud-only files are never downloaded. **Select copies (keep
the oldest)** selects all but one file of each group. You can then delete them, or:

**Replace selected with hard links** keeps every name but stores the content once. Each selected copy becomes a
hard link to the file of its group that is not selected (the oldest one). Before each copy is replaced,
DiskScope checks again that it is on the same drive, unchanged since the scan and still identical at both ends.
The link is created under a temporary name and renamed over the copy, so the copy is never missing.

Keep in mind that hard-linked names are then *one file*: changing it under any name changes it for all of
them. That suits collections that are only read, such as photos, installers and downloaded libraries, not
documents you edit.

### Treemap

The **Map** tab draws the open folder as rectangles: the area is the size on disk and the color is the file
type. Click a rectangle to select the item in the tree, and double-click a folder to open it.

### Copy question for AI (Ctrl+Shift+C)

When you don't know what a folder is, DiskScope can write a question for you to paste into any AI assistant.
It describes the item: its path, sizes, dates, largest entries and file types, or the neighbouring items for a
file. Then it asks what the item belongs to and whether it is safe to delete. The text goes only to the
clipboard: DiskScope never connects to anything.

## Saved scans and comparing

Every finished scan is saved compressed (a few tens of bytes per item). When you start DiskScope again, the
latest scan of the chosen place opens immediately. **Saved scans** lists them by place and date.

**Compare with scan** puts an earlier scan of the same place next to the current one. A **Change** column
shows how much each item has grown or shrunk, and new items are marked. You can sort by it, and the filter
next to it shows all items, only grown or only changed ones. The earlier scan is held in a compact form (about
a third of the memory of a full tree), so even two whole-disk scans fit in memory together.

**Saved scans → All saved scans and data management…** shows how much space the saved scans take. You can
open or delete them one by one or all at once. Two options there choose whether finished scans are saved
automatically and whether the last one opens on start.

## Settings and files

DiskScope is portable. It writes only to a `data` folder next to `DiskScope.exe`, or to
`%LOCALAPPDATA%\DiskScope` when that folder is not writable:

| File | Contents |
|---|---|
| `data\settings.json` | Language, appearance, window layout, last place, options |
| `data\scans\*.dscan` | Saved scans |
| `data\drives.json` | How full each drive was over time, for the drive overview |
| `data\cleanup-catalog.json` | Your changes to the cleanup catalog (only if you created it) |
| `data\error.log` | Details of unexpected errors, if any happened |

In the **⚙** menu you can:

- choose the **Appearance**: same as Windows, light or dark. It takes effect after a restart, which DiskScope
  offers to do;
- add or remove the **Analyze in DiskScope** item in Explorer's context menu for folders and drives. It is
  stored per user (HKCU), so it needs no admin rights.

**Language** switches between English and Czech.

To remove DiskScope, turn off the Explorer integration (if you turned it on) and delete the folder.

## Keyboard and mouse

| Key / action | Function |
|---|---|
| ← / → | collapse / expand; go to the parent / first child |
| Numpad + / − | expand / collapse |
| Letters | jump to the next item starting with them |
| Enter | zoom into a folder; open a file |
| Backspace | up one level |
| Double-click | expand a folder / open a file |
| Del | move to Recycle Bin (after a confirmation, through the queue) |
| Shift+Del | delete permanently (after a confirmation, through the queue) |
| F5 | rescan the selected folder |
| Ctrl+C | copy paths of the selected items |
| Ctrl+Shift+C | copy a question for AI |
| Ctrl+F | search |
| Ctrl+L | filter the tree; Esc clears the filter |
| Shift + right-click | Windows context menu of the item |
| Click on a header | sort by that column |

## Command line

```
DiskScope.exe [--scan <folder>] [--path <folder>] [--lang en|cs] [--theme system|light|dark]
```

`--scan` starts scanning a folder or drive right away, `--path` only selects it, `--lang` overrides the language
and `--theme` the appearance. The Explorer menu item uses `--scan`.
