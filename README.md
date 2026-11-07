# DiskScope

**[Česky](#česky) · [English](#english)**

---

## Česky

DiskScope je portable aplikace pro Windows, která proskenuje disk nebo složku, spočítá rekurzivně
velikosti všech složek a umožní rychle najít, kde zabírají místo velké soubory a složky.

### Funkce

- Sken celého disku nebo vybrané složky, na libovolném souborovém systému (NTFS, exFAT, FAT32, ReFS).
- Paralelní procházení složek; velikosti se zobrazují průběžně, strom jde procházet už během skenu.
- Zobrazuje primárně **velikost na disku** (alokovanou), vedle ní logickou velikost, počty souborů a složek.
- Rychlý virtuální strom zvládne i desítky milionů položek; řazení podle libovolného sloupce.
- Junctions a symbolické odkazy se nenásledují (nic se nezapočítá dvakrát), cloudové soubory OneDrive se nestahují.
- Rozhraní v češtině a angličtině.

### Ovládání

| Klávesa / akce | Funkce |
|---|---|
| ← / → | sbalit / rozbalit, přejít na nadřazenou / první podsložku |
| Enter | vstoupit do složky (u souboru otevřít) |
| Backspace | o úroveň výš |
| dvojklik | rozbalit složku / otevřít soubor |
| klik na záhlaví | řazení |

### Spuštění

Stáhněte `DiskScope.exe` a spusťte, nic se neinstaluje. Nastavení se ukládá do složky `data` vedle exe
(pokud do ní nelze zapisovat, do `%LOCALAPPDATA%\DiskScope`).

### Sestavení

Vyžaduje .NET 10 SDK.

```
dotnet test
dotnet publish src/DiskScope -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

---

## English

DiskScope is a portable Windows application that scans a drive or folder, computes recursive sizes
of all folders and lets you quickly find where large files and folders take up space.

### Features

- Scans a whole drive or a chosen folder on any file system (NTFS, exFAT, FAT32, ReFS).
- Parallel directory enumeration; sizes update live and the tree can be browsed while the scan runs.
- Shows **size on disk** (allocated) first, alongside logical size and file/folder counts.
- A fast virtual tree handles tens of millions of entries; sort by any column.
- Junctions and symbolic links are not followed (nothing is counted twice); OneDrive cloud files are not downloaded.
- Czech and English user interface.

### Controls

| Key / action | Function |
|---|---|
| ← / → | collapse / expand, go to parent / first child |
| Enter | zoom into folder (open a file) |
| Backspace | up one level |
| double-click | expand folder / open file |
| header click | sort |

### Running

Download `DiskScope.exe` and run it; nothing gets installed. Settings are stored in a `data` folder next
to the exe (or in `%LOCALAPPDATA%\DiskScope` when that is not writable).

### Building

Requires the .NET 10 SDK.

```
dotnet test
dotnet publish src/DiskScope -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

## License

[MIT](LICENSE) © 2026 Richard Lipka
