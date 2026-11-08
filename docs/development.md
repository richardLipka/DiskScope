# Development

## Requirements

- Windows 10 or 11, x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Any editor with .NET 10 support (Visual Studio, Rider, VS Code with C# Dev Kit). The solution is `DiskScope.slnx`.

## Build, run, publish

```
dotnet build
dotnet run --project src/DiskScope -- --lang en
powershell -File tools/publish.ps1
```

`tools/publish.ps1` produces the portable `publish\DiskScope.exe`: a self-contained, compressed single file of
about 50 MB that runs without .NET installed. `tools/make-icon.ps1` regenerates `DiskScope.ico`.

The version, author and copyright are set in `Directory.Build.props`.

## Tests

```
dotnet test
```

The xUnit suite in `tests/DiskScope.Tests` (132 tests, about 20 s) runs against the core library and real
temporary folders:

| Area | Tests |
|---|---|
| Data model | `NodeStoreTests`, `TreeSyncTests`, `DeepTreeTests` (30,000 levels on a 256 KB stack) |
| Scanning | `DirectoryScannerTests`, `MftTests` (parser, cycles, fuzzing damaged records; the live MFT test needs elevation and is skipped without it), `ScanErrorTests`, `AccessTimeTests` |
| Saved scans and comparing | `SnapshotTests` (round trip, damaged and crafted files), `ComparisonTests` |
| Analyses | `SearchTests`, `CleanupTests`, `OrphanTests`, `DuplicateTests`, `TreemapTests` |
| Deleting | `DeletionTests`, `DeletionGuardTests`, `TrailingDotTests`, `MassDeletionTests`, `DiskErrorTests` (simulated disk errors and unplugged drives) |
| App | `LocalizationTests` (every key in both languages with the same placeholders), `ExplorerIntegrationTests` |
| Concurrency | `ConcurrencyStressTests` (see below) |

### Stress tests

Two groups of tests look for failures that normal use rarely shows. They run in a short form in every
`dotnet test` and in a long form on request:

```
set DISKSCOPE_STRESS=1
dotnet test -c Release --filter "FullyQualifiedName~StressTests"
```

**`ConcurrencyStressTests`** run everything the app may do at the same time:

- 8 writer threads build trees the way the scanner does, while other threads walk the node table and run every
  analysis, sort, treemap, comparison and snapshot save. In the long form that is 12 rounds and 17.5 million
  nodes.
- Analyses run during real scans of `C:\Windows`.
- Analyses run while batches of deletions are removed from the tree.
- Sorting, the treemap and folder summaries run while folder totals keep changing.

Afterwards, every folder's totals are checked against its contents. These tests found the
sort crash ("IComparer.Compare() method returns inconsistent results") that occurred when sorting during a
scan.

**`SizeStressTests`** run only with `DISKSCOPE_STRESS=1` and use a disk-like tree: a folder of a million files,
a chain 2,000 levels deep, and the rest as ordinary folders with names in several scripts. Every step is
timed: building, totals, all search kinds, cleanup, abandoned data, treemaps, sorting a million-row folder by
every column, expanding the whole tree, restoring a 100,000-item selection, saving and loading a scan,
removing 500,000 items at once, and comparing. The size is set with `DISKSCOPE_STRESS_NODES`. The default is
12 million, because comparing holds two trees in memory; above 15 million the comparison step is skipped.

Results at 30 million nodes on a 20-thread PC:

| Step | Time |
|---|---|
| Build 30 M nodes (2.2 GB) | 4.8 s |
| Recompute all totals | 0.4 s |
| Search (any kind) | 0.05–1.7 s |
| Cleanup catalog | 0.8 s |
| Sort a 1 M-file folder | 0.07–0.8 s |
| Expand the whole tree (30 M rows) | 1.4 s |
| Save / load scan (824 MB) | 8.9 s / 6.0 s |
| Remove 500,000 items | 0.08 s |

## Automation hooks (Debug builds)

Debug builds of the app accept command-line switches that drive the real window and capture the result. They
are used to check UI changes and to take the screenshots in these docs. They are compiled out of Release builds
(`#if DEBUG`).

```
DiskScope.exe --scan C:\Windows --lang en --screenshot tree.png --expand 1
DiskScope.exe --scan C:\Windows --map-test map.png
DiskScope.exe --scan <folder> --empty-test <subfolder> --out report.txt
```

The full list is at the top of [`src/DiskScope/UI/TestHooks.cs`](../src/DiskScope/UI/TestHooks.cs).
`--messages file.txt` collects the texts of message boxes instead of showing them, so a run is never blocked.

## Conventions

- **Core first.** Logic goes into `DiskScope.Core`, where it can be tested. The WinForms layer stays thin.
- **Every UI text** goes into `Strings.cs` with both Czech and English. `LocalizationTests` fail otherwise.
- **The tree is shared across threads.** Read [the concurrency model](architecture.md#concurrency-model)
  before changing `NodeStore` or anything that sorts or walks it.
- **All file system access on tree paths uses `LongPath.Of(path)`** (extended `\\?\` paths). All deletions go
  through `DeleteQueue` → `DeletionGuard` → `FileDeleter`.
- **No recursion over the tree.** Use explicit stacks; trees can be arbitrarily deep.
- After an `await` in the UI, check `IsDisposed`. Background threads reach the UI only through `PostToUi`.
