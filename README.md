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
- **Rychlý režim MFT** pro NTFS disky (při spuštění jako správce): čte přímo Master File Table, celý disk
  za desítky sekund. Pevné odkazy (hardlinky) se započítají jen jednou. Bez práv správce nebo na jiném
  souborovém systému se automaticky použije procházení složek.
- Junctions a symbolické odkazy se nenásledují (nic se nezapočítá dvakrát), cloudové soubory OneDrive se nestahují.
- **Mazání** do koše nebo trvale, zobrazení v Průzkumníku, otevření výchozí aplikací, kopírování cesty,
  nový sken složky a plné kontextové menu Windows (Shift + pravé tlačítko).
- **Ukládání skenů**: dokončené skeny se ukládají a při dalším spuštění se poslední hned otevře. V okně
  Uložená data jde každý sken nebo všechny najednou smazat.
- **Porovnání se starším skenem**: sloupec Změna, řazení podle přírůstku, filtr „jen narostlé“.
  Ukáže, co od minula přibylo.
- **Hledání** (Ctrl+F): největší soubory, složky podle vlastního obsahu, filtr podle jména (`*.iso; *.zip`),
  velikosti a stáří, v celém stromu nebo jen v otevřené složce.
- Rozhraní v češtině a angličtině.

### Ovládání

| Klávesa / akce | Funkce |
|---|---|
| ← / → | sbalit / rozbalit, přejít na nadřazenou / první podsložku |
| Enter | vstoupit do složky (u souboru otevřít) |
| Backspace | o úroveň výš |
| dvojklik | rozbalit složku / otevřít soubor |
| Del / Shift+Del | přesunout do koše / smazat trvale |
| F5 | znovu skenovat složku |
| Ctrl+C | kopírovat cestu |
| Ctrl+F | hledání a největší soubory |
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
- **Fast MFT mode** for NTFS drives (when run as administrator): reads the Master File Table directly,
  a whole drive in tens of seconds. Hard links are counted once. Without administrator rights or on other
  file systems, folder enumeration is used automatically.
- Junctions and symbolic links are not followed (nothing is counted twice); OneDrive cloud files are not downloaded.
- **Delete** to the Recycle Bin or permanently, show in Explorer, open with the default app, copy path,
  rescan a folder and the full Windows context menu (Shift + right-click).
- **Saved scans**: finished scans are saved and the last one opens instantly on the next start. The Saved
  data window deletes any or all of them.
- **Compare with an earlier scan**: a Change column, sorting by growth and an "only grown" filter show
  what has been added since.
- **Search** (Ctrl+F): largest files, folders by their own content, filters by name (`*.iso; *.zip`),
  size and age, in the whole tree or only the open folder.
- Czech and English user interface.

### Controls

| Key / action | Function |
|---|---|
| ← / → | collapse / expand, go to parent / first child |
| Enter | zoom into folder (open a file) |
| Backspace | up one level |
| double-click | expand folder / open file |
| Del / Shift+Del | move to Recycle Bin / delete permanently |
| F5 | rescan folder |
| Ctrl+C | copy path |
| Ctrl+F | search and largest files |
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
