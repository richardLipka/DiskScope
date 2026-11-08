# Cleanup catalog

The **Cleanup** tab is driven by a catalog of known places: what each place is, how safe it is to clean, and how
to free the space properly. The catalog is a JSON file, so you can add places, change them or turn them off
without rebuilding DiskScope.

## Where it lives

- The **built-in catalog** ships inside the app:
  [`src/DiskScope.Core/Cleanup/cleanup-catalog.json`](../src/DiskScope.Core/Cleanup/cleanup-catalog.json).
  It has 64 entries.
- **Your catalog** is `data\cleanup-catalog.json` next to `DiskScope.exe`. **Edit catalog…** in the Cleanup tab
  creates it as a copy of the built-in catalog (field explanations included at the top) and opens it in your
  editor. **Reload catalog** applies your changes without restarting.

Your file is laid over the built-in catalog:

- an entry with the same `id` **replaces** the built-in one;
- `"disabled": true` with an existing `id` **removes** that entry;
- a new `id` **adds** an entry.

So your file only needs to contain what you change. Entries you leave in your copy unchanged also override
the built-in ones; delete them from your file if you want a newer DiskScope's updated versions of them. If your file has an error, DiskScope says what is wrong and uses the built-in
catalog alone.

The format allows `//` comments and trailing commas.

## Entry format

```jsonc
{
  "version": 1,
  "entries": [
    {
      "id": "my-render-cache",            // unique; reuse a built-in id to replace that entry
      "category": "app",                  // temp | browser | dev | app | system | user
      "safety": "safe",                   // safe | caution | system
      "action": "deleteContents",         // deleteContents | delete | none
      "paths": [ "{user}\\AppData\\Local\\MyRenderer\\Cache" ],
      "folderNames": [],                  // folder names matched anywhere in the scan
      "redownloadable": false,            // a cache its tool downloads again when needed
      "minSize": 10485760,                // report only matches of at least 10 MB
      "name":        { "en": "MyRenderer cache", "cs": "Mezipaměť MyRenderer" },
      "description": { "en": "Preview frames MyRenderer keeps between sessions." },
      "howTo":       { "en": "Close MyRenderer, then empty the folder." }
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `id` | Unique name of the entry. |
| `category` | Used for grouping: `temp`, `browser`, `dev`, `app`, `system` or `user`. |
| `safety` | `safe`: caches and temporary data; nothing is lost that can't be recreated. `caution`: deletable, but at a cost (downloading again, lost offline copies, lost rollback). `system`: must not be deleted by hand; `howTo` should name the right tool. |
| `action` | What DiskScope offers. `deleteContents` empties the folder and keeps the folder itself. `delete` removes the matched item. `none` shows advice only. |
| `paths` | Full path patterns (see below). |
| `folderNames` | Folder names matched anywhere in the scanned tree, such as `node_modules` or `__pycache__`. A folder nested inside another match is not reported again. A name with backslashes matches the end of a path: `.git\lfs\objects` finds the LFS store of every repository. |
| `redownloadable` | `true` for caches of downloaded files that their tool fetches again. These are shown by **Only caches that can be downloaded again** and selected by **Select re-downloadable caches**. |
| `minSize` | Matches smaller than this many bytes are not listed. |
| `disabled` | Only in your file: removes the built-in entry with the same `id`. |
| `name`, `description`, `howTo` | Texts by language code (`en`, `cs`). English is the fallback when a language is missing. |

### Path patterns

- `%Variable%`: environment variables such as `%SystemRoot%`, `%ProgramData%` or `%LOCALAPPDATA%`.
- `{user}`: every user profile folder (`%SystemDrive%\Users\*`), so one entry covers all accounts on the PC.
- `*` and `?` work as wildcards in any path segment, for example
  `{user}\AppData\Local\Google\Chrome\User Data\*\Cache`.
- Remember to double the backslashes in JSON: `"C:\\Data\\Cache"`.

Only places that are **in the current scan** are found, so scan the drive (or the folder) that contains them.

## Safety

The catalog only suggests. Whatever an entry says, every deletion goes through the same checks as a deletion
from the tree: drive roots, Windows, Program Files, ProgramData, user profiles and their main folders can't be
deleted, and inside Windows only a few known places may be emptied. A wrong or malicious catalog entry can't
get around this. See [Safety](safety.md).

## What the built-in catalog covers

| Category | Entries |
|---|---|
| Temporary files | User and Windows temp folders, application crash dumps, the memory dump after a blue screen, Windows Error Reporting, Explorer thumbnail cache |
| Browsers | Chrome, Edge and Firefox caches; the browser's on-device AI model |
| Developer tools (34) | npm, Yarn, pnpm, Bun, Deno, node-gyp/Electron/Bower, `node_modules`; pip, uv, Poetry/Pipenv/Hatch, conda, Python `__pycache__`; NuGet; Maven, Maven Wrapper, Gradle, Coursier/Ivy/sbt; Cargo; Go; Composer; Dart/Flutter pub; vcpkg; Scoop; Android SDK; Unity; Visual Studio installation packages; VS Code and JetBrains caches; Git LFS objects and large Git repositories; Hugging Face and PyTorch models; Playwright/Cypress/Puppeteer browsers |
| Applications | Docker Desktop and WSL virtual disks (advice on how to compact them), Google Drive, OneDrive, Teams, NVIDIA, Steam shader cache, Adobe media cache, CKAN mods, leftover update installers of GitHub Desktop, Discord, Slack and others |
| System | Downloaded Windows updates, Delivery Optimization, previous Windows installation, restore points, hibernation file, page file, WinSxS, Installer cache. Most are advice only, pointing to the Windows tool that frees the space safely |
| User | Recycle Bin, Downloads |

Corrections and new entries are welcome as pull requests to the built-in catalog.
