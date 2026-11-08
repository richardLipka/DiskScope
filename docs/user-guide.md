# User guide

- [Scanning](#scanning)
- [The tree](#the-tree)
- [Deleting](#deleting)
- [Finding what to delete](#finding-what-to-delete)
- [Saved scans and comparing](#saved-scans-and-comparing)
- [Settings and files](#settings-and-files)
- [Keyboard and mouse](#keyboard-and-mouse)
- [Command line](#command-line)

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
- Folders that can't be read are counted as "access errors" in the status bar. The scan carries on.

## The tree

Columns: name, **On disk**, **% of parent** (with a bar), **Size**, **Files**, **Folders**, **Modified** and
**Accessed**. Click a header to sort by it. For a folder, Modified and Accessed show the newest time of anything
inside, which makes old, untouched folders easy to spot.

Access times are a hint, not proof. Antivirus scans, indexing and backups can update them too, and Windows can
be set not to record them at all.

The line under the tree describes the selected item. The breadcrumb at the top shows where you are. **Enter**
zooms into a folder so that it becomes the top of the tree, and **Backspace** or **Up** goes back out.

Right-click an item for:

- **Open** (with the default app), **Show in Explorer**, **Copy path**
- **Move to Recycle Bin**, **Delete permanently…**
- **Rescan folder** (F5). Use it after you have changed things outside DiskScope.
- **Find duplicates in this folder**
- **Copy question for AI**
- **More Windows options**: the item's full Windows context menu (also on **Shift + right-click**)

When DiskScope runs as administrator, files are opened through Explorer, so the programs they start run with
your normal rights, not as administrator.

## Deleting

Deleting never blocks the window. After you confirm what is about to be deleted, every item goes into a queue,
shown on the **Deleting** tab, and DiskScope deletes the items one at a time in the background:

- You can keep browsing and keep adding items while it works.
- Each item shows its state: waiting, deleting, deleted, failed (with the reason) or partly deleted.
- **Pause**, **Resume**, **Cancel waiting** and **Remove finished** control the queue. The status bar shows how
  much is left and how much space has been freed.
- Queued items are greyed in the tree. When an item is gone, it disappears from the tree and the totals above
  it go down. A folder that could be deleted only partly is rescanned.
- If items are still waiting when you close the window, DiskScope asks first.

**Recycle Bin** deletion uses the Windows recycle bin without its dialogs. **Permanent** deletion walks the folder
itself. A locked file is skipped and the rest is still deleted, and junctions are removed as links without
touching what they point to.

Some things are never deleted, whatever is selected: drive roots, Windows, Program Files, ProgramData, user
profiles and their main folders. A file is also skipped if it has changed since the scan. See
[Safety](safety.md) for the full rules.

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
the catalog. Right-click a found place to empty it (to the Recycle Bin or permanently) or to copy a question for AI.

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
the oldest)** marks all but one file of each group for deletion.

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
next to it shows all items, only grown or only changed ones.

**Saved scans → All saved scans and data management…** shows how much space the saved scans take. You can
open or delete them one by one or all at once. Two options there choose whether finished scans are saved
automatically and whether the last one opens on start.

## Settings and files

DiskScope is portable. It writes only to a `data` folder next to `DiskScope.exe`, or to
`%LOCALAPPDATA%\DiskScope` when that folder is not writable:

| File | Contents |
|---|---|
| `data\settings.json` | Language, window layout, last place, options |
| `data\scans\*.dscan` | Saved scans |
| `data\cleanup-catalog.json` | Your changes to the cleanup catalog (only if you created it) |
| `data\error.log` | Details of unexpected errors, if any happened |

In the **⚙** menu you can add or remove the **Analyze in DiskScope** item in Explorer's context menu for
folders and drives. It is stored per user (HKCU), so it needs no admin rights.

**Language** switches between English and Czech.

To remove DiskScope, turn off the Explorer integration (if you turned it on) and delete the folder.

## Keyboard and mouse

| Key / action | Function |
|---|---|
| ← / → | collapse / expand; go to the parent / first child |
| Numpad + / − | expand / collapse |
| Enter | zoom into a folder; open a file |
| Backspace | up one level |
| Double-click | expand a folder / open a file |
| Del | move to Recycle Bin (after a confirmation, through the queue) |
| Shift+Del | delete permanently (after a confirmation, through the queue) |
| F5 | rescan the selected folder |
| Ctrl+C | copy paths of the selected items |
| Ctrl+Shift+C | copy a question for AI |
| Ctrl+F | search |
| Shift + right-click | Windows context menu of the item |
| Click on a header | sort by that column |

## Command line

```
DiskScope.exe [--scan <folder>] [--path <folder>] [--lang en|cs]
```

`--scan` starts scanning a folder or drive right away, `--path` only selects it, and `--lang` overrides the
language. The Explorer menu item uses `--scan`.
