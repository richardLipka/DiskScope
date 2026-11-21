# Architecture

DiskScope is a C# / .NET 10 WinForms application in two projects:

- **`DiskScope.Core`** is a class library with no UI. It holds the data model, the scanners, saved scans,
  search, the analyses and deleting. Nearly all logic and almost all tests live here.
- **`DiskScope`** is the Windows application: the main window, its panels, shell integration and localization.

```
            ┌────────────────────────── DiskScope (WinForms) ──────────────────────────┐
            │ MainForm ─ TreeListView (virtual, owner-drawn)   panels: Find, Cleanup,  │
            │            TreeRows + TreeFilter                 File types, Map,        │
            │            DriveOverviewPanel                    Abandoned, Duplicates,  │
            │                                                  Delete & move           │
            └───────────────┬───────────────────────────────────────┬──────────────────┘
                            │ reads (any thread, live)              │ jobs
┌──────────────┐  writes  ┌─▼──────────┐   Search, CleanupFinder,   ┌─▼───────────┐   FileDeleter
│ ScanSession  ├─────────►│ NodeStore  │◄─ FileTypes, OrphanFinder, │ DeleteQueue ├─► FileMover
│  MftScanner  │          │ (the tree) │   DuplicateFinder,         │  (1 worker) │   FileCompressor
│  Directory-  │          └─┬────────▲─┘   TreemapLayout, ...       └──────┬──────┘   (all behind
│  Scanner     │      save  │        │ load                                │           DeletionGuard)
└──────────────┘          ┌─▼────────┴─┐  load compact  ┌─────────────┐
                          │  Snapshot  ├───────────────►│ CompactTree │◄─ TreeComparison
                          └────────────┘                └─────────────┘
                          data\scans\*.dscan
```

## The tree: `NodeStore`

A whole disk can hold tens of millions of entries, so the tree uses no object per entry. Every file and folder
is a 64-byte struct (`Node`) in large arrays:

| Field | Meaning |
|---|---|
| `Size`, `Length` | Size on disk (allocated) and logical size. For a folder: the sum over its whole subtree. |
| `MTime`, `ATime` | Last write and last access (FILETIME). For a folder: the newest inside. |
| `Parent`, `FirstChild`, `NextSibling` | Links, as indexes into the arrays. |
| `FileCount`, `DirCount` | For a folder: totals over its subtree. |
| `NameOffset`, `NameLen` | Where the name is in the name storage. |
| `Flags`, `Attributes` | Directory, scanned, link, hard link, queued, deleted…; Windows attributes. |

- **Nodes** live in chunks of 65,536 structs. Chunks are created as needed and never move, so a `ref Node` stays
  valid, and adding nodes never copies existing ones.
- **Names** live in 4 MB byte chunks. A name is stored as Latin-1 (one byte per character) when it can be and
  as UTF-16 otherwise, and a bit in `NameLen` says which. Full paths are never stored: they are built by walking
  up the parents.
- **Measured cost:** about 75 bytes per entry in total. 30 million entries take 2.2 GB.

### Concurrency model

Scanning, the UI and the analyses all use the same `NodeStore` at the same time, without locks on the read
path:

- **Allocation is append-only.** Scanner threads reserve indexes with a compare-and-swap. A node's chunk is
  created *before* the count moves past the node, so a reader walking `0..Count` never meets missing storage.
- **Children are published at once.** A scanner thread builds a folder's child list privately and then
  publishes it with one volatile write of `FirstChild`. Readers see either no children or the complete list.
- **Totals are updated atomically:** sizes and counts with `Interlocked.Add` up the parent chain, newest times
  with an interlocked max.
- **Readers never sort by live values.** Sizes keep changing during a scan. A comparison that reads them live
  can contradict itself, and `Array.Sort` then throws. Every sort first reads its keys once (`TreeRows`,
  `NodeStore.SortBySizeDescending`).
- **Only the UI thread removes nodes.** It does so after deletions finish (`RemoveMany`, which rebuilds each
  affected child list once). While a scan or a folder rescan is changing the tree, finished deletions are held
  back and applied when it ends.

`ConcurrencyStressTests` exercise exactly these combinations (see [Development](development.md)).

## Scanning

`ScanSession` runs a scan on a background thread, reports progress and ends as *completed*, *cancelled* or
*failed*. It never reports a lost source as a complete scan.

**`MftScanner`** handles NTFS drive roots and needs administrator rights:

1. It opens the volume and reads `$MFT` sequentially. A reader thread fills buffers while the parser works.
2. `MftParser` decodes each file record: names (preferring the Win32 name over the DOS 8.3 name), sizes from
   the `$DATA` attribute (including non-resident and compressed data), times and hard-link names. Extension
   records are merged into their base record.
3. `MftBuilder` links the records into a tree, counts hard links once and skips NTFS metadata files (such as
   `$BadClus`, which would otherwise count as the whole volume).
4. `DropUnreachable` removes records that don't connect to the root, for example because of cycles in damaged
   data.

If anything fails, the session falls back to the directory scanner.

**`DirectoryScanner`** works on any file system:

- 4–16 worker threads take folders from a shared stack and list each one with `NtQueryDirectoryFile`
  (`FileFullDirectoryInformation`). That returns names, sizes, allocated sizes and times for a whole folder
  without opening any file.
- Reparse points (junctions, symbolic links) are recorded but not entered. Cloud placeholders are never
  hydrated.
- Files compressed by Windows (WOF: `compact /exe`, CompactOS, DiskScope's Compress) list no reparse point and an
  allocation of 0, because their data lives in a hidden stream. A file over 4 KB with nothing allocated and no
  cloud attributes is therefore asked for its real size with `GetCompressedFileSize`. (The MFT scanner sums all
  data streams, so it counts these files correctly anyway.)
- All paths are extended (`\\?\`) paths, so long paths and names ending in a dot or space work.
- A folder that can't be listed counts as an error, and the scan continues.

`Rescan` re-lists one folder in place. It first checks that the folder can still be listed, so an unplugged
drive doesn't wipe that part of the tree.

## The view

`TreeRows` turns the tree into the list of visible rows: the expanded folders, each sorted by the chosen
column, with an optional filter. It is iterative, so revealing an item 30,000 levels deep can't overflow the
stack. Expanding every folder of a 30-million-entry tree takes about 1.4 s.

The row filter combines the comparison filter (grown or changed items) with **`TreeFilter`**, the view filter.
With name patterns, `TreeFilter.Prepare` makes one parallel pass over the node table. It marks the folders that
lead to a match and the folders whose own name matches, taking 0.9 s for 30 million entries. After that,
deciding whether a row is shown is a set lookup. The UI prepares the filter again, at most every three seconds,
while a scan or deletion changes the tree.

`TreeListView` is a virtual WinForms `ListView` drawn by the owner: indentation, expanders, the percentage bar,
the shell icons and greyed queued items. It asks `TreeRows` for row *i* only when that row is painted. While a
scan runs, a timer rebuilds the rows only if `NodeStore.Version` changed. Restoring the selection after a rebuild
takes one pass over the rows, whatever the number of selected items.

## Saved scans: `Snapshot`

A `.dscan` file has two parts:

- a small uncompressed header (magic `DSCN`, format version, root path, scan time and duration, mode,
  complete flag and totals), which the scan list reads without loading the tree;
- the tree in preorder, Deflate-compressed. Each node is stored as its name, flags, sizes, times and child
  count. Folder totals are not stored; they are recomputed on load.

It takes about 27 bytes per entry: 30 million entries are an 824 MB file, saved in 9 s and loaded in 6 s.
Loading validates every name and flag (see [Safety](safety.md)) and rejects a damaged file with a clear error.
`SnapshotCatalog` lists the saved scans per place and deletes them.

### The older scan in a comparison: `CompactTree`

A comparison needs very little from the older scan: names, sizes and the shape of the tree. `Snapshot.LoadCompact`
reads a saved scan into a **`CompactTree`**, which keeps the nodes in preorder and stores for each one only:

- its size on disk;
- the number of nodes in its subtree, with a directory bit;
- its name, encoded as in `NodeStore`.

No links are needed: the first child of node *i* is *i* + 1, and the next sibling of child *c* is
*c* + span(*c*). That is about 19 bytes per entry plus the name. For 30 million entries it takes 906 MB, against
2.2 GB for a full tree, so a whole-disk tree and its comparison baseline fit together in about 3 GB.

`TreeComparison` matches the current tree against the compact one by path, lazily and per folder, indexing large
folders by name on first use. It provides the size change of each node for the Change column and the filters.

### Memory when opening another scan

Opening a saved scan normally keeps the shown tree until the new one has loaded, so it can stay if loading fails.
For a large tree (over 256 MB) that is saved and unchanged since, the UI lets it go first (`ReleaseTree`), and
reopens it from its file if loading fails. Two whole-disk trees therefore never need to fit in memory at once.

## Analyses

All analyses read the tree only. They run on background threads and work during a scan.

| Class | What it does |
|---|---|
| `Search` | Splits the node table into ranges searched in parallel. Each range keeps a bounded heap of the largest matches. Name patterns use the OS wildcard matcher. |
| `CleanupFinder` + `CleanupCatalog` | Resolves catalog path patterns against the tree (environment variables, `{user}`, wildcards per segment) and finds `folderNames` anywhere. See [Cleanup catalog](cleanup-catalog.md). |
| `OrphanFinder` + `InstalledPrograms` | Compares folders in AppData, ProgramData and Program Files with the uninstall registry keys and installed Store package families. |
| `DuplicateFinder` | Size groups from the scan, then actual sizes, then SHA-256 of the first and last 64 KB, then a full SHA-256. Hard links are recognized by file ID. |
| `FileTypes` | Totals per extension of a folder's files, and the largest files of one extension. Extensions are looked up by span, so a 30-million-entry walk allocates almost nothing (1.9 s). Also the extension → kind classification used by the treemap. |
| `TreemapLayout` | Squarified treemap (Bruls, Huizing, van Wijk) over a single reading of the sizes. |
| `DriveUsageLog` | A small JSON log (`data\drives.json`) of how full each drive was, written when a drive scan finishes and when the overview opens (at most hourly). The overview's "change since" is the used space now minus the reading closest to the last scan. |
| `FolderSummary` + `AiPrompt` | Condensed description of a folder (largest entries, file types, largest files) for the AI question. |

## Deleting, moving and compressing

- **`DeleteQueue`** keeps the jobs and runs them one at a time on a single STA worker thread, as the shell's
  recycle-bin API requires. A job is a deletion (recycle bin or permanent), a move or a compression
  (`DeleteMethod`). Waiting jobs are a queue with a counter, so queuing 100,000 items costs O(1) each.
  Job changes, including progress of long moves and compressions (at most a few times a second), are reported
  as events. The UI collects them and applies them every 200 ms in one batch: one tree removal, one reload and
  one panel refresh per batch, not per item. Each job records the space it freed (`Freed`).
- **`FileMover`** moves an item into another folder. On the same volume it renames. To another volume it copies
  the tree iteratively, keeping attributes and times, and verifies the copy (same relative paths and sizes).
  Only then does it delete the original with the permanent-delete walker. Any failure or cancellation before
  that point removes the partial copy. A folder containing reparse points is refused before anything is copied.
  Optionally `Junction.Create` leaves a junction at the old path; it is a mount-point reparse point set with
  `FSCTL_SET_REPARSE_POINT`, which needs no privilege.
- **`FileCompressor`** compresses files in place through WOF (`FSCTL_SET_EXTERNAL_BACKING`, the mechanism behind
  `compact /exe`), with the XPRESS 4K/8K/16K or LZX algorithm. It measures each file's on-disk size before and
  after with `GetCompressedFileSize`. Files below 8 KB are skipped. "Not beneficial" counts as success, and
  files open for writing elsewhere are counted as failed.
- **`VirtualDisk`** compacts `.vhdx`/`.vhd` files with a generated diskpart script (attach read-only, compact,
  detach), optionally after `wsl --shutdown`. diskpart runs elevated through a UAC prompt when DiskScope isn't
  elevated.
- **`HardLinker`** (duplicates) replaces a copy with a hard link to the kept file: same-volume check, size and
  time unchanged since the scan, both ends re-compared, then `CreateHardLink` under a temporary name and
  `MoveFileEx(REPLACE_EXISTING)` over the copy.
- **`FileDeleter`** deletes without dialogs. It uses `SHFileOperation` (confirmations and error dialogs suppressed) for the recycle bin, and for permanent
  deletion it walks the folder itself, bottom-up and iteratively, never entering reparse points and carrying on
  past locked files. `Probe` tells "gone" apart from "can't tell" (disk errors).
- **`DeletionGuard`** is the last check before every deletion, move and hard-link replacement. It protects
  system and profile folders and resolves links in parent folders. `CheckCompress` is its rule for compressing:
  no drive roots and nothing inside Windows, for the path as given and where it really leads. See
  [Safety](safety.md).

## Application shell

- **`Program`** sets up global exception handlers, which write to `data\error.log` and show the error instead
  of closing. Background work reaches the UI only through `PostToUi`, which does nothing once the window is
  gone.
- **`Strings` (`L.T`)** holds the Czech and English texts in one table. A test checks that every key has both
  languages and the same `{n}` placeholders.
- **`ExplorerIntegration`** adds or removes the per-user "Analyze in DiskScope" verb under HKCU.
- **`ShellOpen` / `Shell`** open files (through `explorer.exe` when elevated) and show the native Windows
  context menu.
- **`AppPaths`** uses the `data` folder next to the exe, or `%LOCALAPPDATA%\DiskScope` when that is read-only.
- **`Theme`** applies the light, dark or system appearance through Windows Forms' color mode
  (`Application.SetColorMode`, before the first window). It holds the colors of the custom-drawn parts (bars,
  change column, glyphs) for both modes.
