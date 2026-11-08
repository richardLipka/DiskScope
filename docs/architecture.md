# Architecture

DiskScope is a C# / .NET 10 WinForms application in two projects:

- **`DiskScope.Core`** is a class library with no UI. It holds the data model, the scanners, saved scans,
  search, the analyses and deleting. Nearly all logic and almost all tests live here.
- **`DiskScope`** is the Windows application: the main window, its panels, shell integration and localization.

```
            ┌────────────────────────── DiskScope (WinForms) ──────────────────────────┐
            │ MainForm ─ TreeListView (virtual, owner-drawn)   panels: Find, Cleanup,  │
            │            TreeRows (visible rows)               Abandoned, Duplicates,  │
            │                                                  Map, Deleting           │
            └───────────────┬───────────────────────────────────────┬──────────────────┘
                            │ reads (any thread, live)              │ jobs
┌──────────────┐  writes  ┌─▼──────────┐   Search, CleanupFinder,   ┌─▼───────────┐
│ ScanSession  ├─────────►│ NodeStore  │◄─ OrphanFinder, Duplicate- │ DeleteQueue ├─► FileDeleter
│  MftScanner  │          │ (the tree) │   Finder, TreemapLayout,   │  (1 worker) │   DeletionGuard
│  Directory-  │          └─┬────────▲─┘   FolderSummary,           └─────────────┘
│  Scanner     │      save  │        │ load TreeComparison
└──────────────┘          ┌─▼────────┴─┐
                          │  Snapshot  │  data\scans\*.dscan
                          └────────────┘
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
- All paths are extended (`\\?\`) paths, so long paths and names ending in a dot or space work.
- A folder that can't be listed counts as an error, and the scan continues.

`Rescan` re-lists one folder in place. It first checks that the folder can still be listed, so an unplugged
drive doesn't wipe that part of the tree.

## The view

`TreeRows` turns the tree into the list of visible rows: the expanded folders, each sorted by the chosen
column, with an optional filter (used for comparisons). It is iterative, so revealing an item 30,000 levels deep
can't overflow the stack. Expanding every folder of a 30-million-entry tree takes about 1.4 s.

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

`TreeComparison` matches a current tree against an older one by path, lazily and per folder. It provides the
size change of each node for the Change column and the filters.

## Analyses

All analyses read the tree only. They run on background threads and work during a scan.

| Class | What it does |
|---|---|
| `Search` | Splits the node table into ranges searched in parallel. Each range keeps a bounded heap of the largest matches. Name patterns use the OS wildcard matcher. |
| `CleanupFinder` + `CleanupCatalog` | Resolves catalog path patterns against the tree (environment variables, `{user}`, wildcards per segment) and finds `folderNames` anywhere. See [Cleanup catalog](cleanup-catalog.md). |
| `OrphanFinder` + `InstalledPrograms` | Compares folders in AppData, ProgramData and Program Files with the uninstall registry keys and installed Store package families. |
| `DuplicateFinder` | Size groups from the scan, then actual sizes, then SHA-256 of the first and last 64 KB, then a full SHA-256. Hard links are recognized by file ID. |
| `TreemapLayout` | Squarified treemap (Bruls, Huizing, van Wijk) over a single reading of the sizes. |
| `FolderSummary` + `AiPrompt` | Condensed description of a folder (largest entries, file types, largest files) for the AI question. |

## Deleting

- **`DeleteQueue`** keeps the jobs and runs them one at a time on a single STA worker thread, as the shell's
  recycle-bin API requires. Waiting jobs are a queue with a counter, so queuing 100,000 items costs O(1) each.
  Job changes are reported as events. The UI collects them and applies them every 200 ms in one batch: one tree
  removal, one reload and one panel refresh per batch, not per item.
- **`FileDeleter`** deletes without dialogs. It uses `SHFileOperation` (confirmations and error dialogs suppressed) for the recycle bin, and for permanent
  deletion it walks the folder itself, bottom-up and iteratively, never entering reparse points and carrying on
  past locked files. `Probe` tells "gone" apart from "can't tell" (disk errors).
- **`DeletionGuard`** is the last check before every deletion. It protects system and profile folders and
  resolves links in parent folders. See [Safety](safety.md).

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
