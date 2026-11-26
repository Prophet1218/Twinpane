# Twinpane 1.4.0

*(Korábbi neve: Glass Commander)*

Kétpaneles fájlkezelő Windows 11-re: a Total Commander működése, natív WinUI 3-as felülettel
(Mica / Acrylic háttér, Fluent ikonok, lekerekített kártyák, világos/sötét téma a rendszer szerint).

## Letöltés

A **Releases** oldalon (jobb oldalt a repó főoldalán) minden verzióhoz két fájl van:

- **Twinpane-x.y.z-Setup.exe** – telepítő: Start menü, asztali ikon (választható), eltávolítás a Gépházból.
  Rendszergazdai jog nélkül is települ.
- **Twinpane-x.y.z-portable.zip** – hordozható változat: kicsomagolod, és fut, telepítés nélkül.

## Fordítás

### A) GitHub Actions – nem kell semmit telepíteni
1. Töltsd fel a mappát egy GitHub repóba (pl. `Prophet1218/Glass-Commander`).
2. **Actions** fül → *Build* → lefut magától (vagy *Run workflow*).
3. A végén az **Artifacts** alól töltsd le a `Twinpane-x.y.z` zipet, csomagold ki, és indítsd a `Twinpane.exe`-t.

### B) Saját gépen
- **Visual Studio 2022**: a telepítőben pipáld be a *WinUI application development* (vagy *.NET desktop development* + *Windows App SDK*) részt,
  nyisd meg a `src/GlassCommander/GlassCommander.csproj`-t, platform: **x64**, majd F5.
- **Csak .NET 8 SDK-val**:
  ```
  dotnet publish src/GlassCommander/GlassCommander.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o publish
  ```
  Ezután: `publish\Twinpane.exe`

A program önálló (self-contained), a futtatáshoz nem kell külön .NET vagy Windows App SDK runtime.

## Billentyűk

| Billentyű | Funkció |
|---|---|
| Tab | Váltás a panelek között |
| Ctrl+T / Ctrl+W | Új fül / fül bezárása |
| Ctrl+Tab / Ctrl+Shift+Tab | Következő / előző fül |
| Ctrl+↑ | A kijelölt mappa megnyitása új fülön |
| Gépelés a listában | Gyorskereső: ráugrik az első egyező fájlra |
| Ctrl+Q | Gyorsnézet a másik panelben (képek, szövegek, mappák) |
| Alt+F7 / Ctrl+F | Kereső: név, joker (*.pdf), tartalom; egy mappában vagy minden meghajtón |
| Enter / dupla katt | Megnyitás / belépés a mappába |
| Backspace, Alt+↑ | Szülőmappa |
| Alt+← | Vissza |
| Insert | Kijelölés váltása + következő elem |
| Ctrl+A | Összes kijelölése |
| F2, Shift+F6 | Átnevezés |
| F3 | Megnyitás az alapértelmezett programmal |
| F4 | Szerkesztés Jegyzettömbben |
| F5 | Másolás a másik panelre |
| F6 | Mozgatás a másik panelre |
| F7 | Új mappa |
| F8 / Del | Lomtárba |
| Shift+F8 / Shift+Del | Végleges törlés |
| Ctrl+C / Ctrl+X / Ctrl+V | Vágólap (az Intézővel oda-vissza működik) |
| Ctrl+Shift+C | Elérési út másolása |
| Alt+Enter | Tulajdonságok |
| Alt+F1 / Alt+F2 | Meghajtóválasztó (bal / jobb) |
| Ctrl+L | Ugrás a címsorba |
| Ctrl+R | Frissítés |
| Ctrl+H | Rejtett fájlok be/ki |
| Ctrl+U | Panelek cseréje |
| Ctrl+I | A másik panel is ugyanide lép |
| Ctrl+Shift+T | Windows Terminal az aktuális mappában |
| Ctrl+E | Megnyitás Intézőben |

A panelek tetején a meghajtógombokkal (c, d, …) lehet meghajtót váltani; az aktuális meghajtó gombja a gyökérbe visz.
Új meghajtó (pl. pendrive) csatlakoztatásakor a gomb magától megjelenik.

A másolás/mozgatás/törlés a Windows saját motorjával megy: natív folyamatjelzőt kapsz,
ütközésnél "Csere / Kihagyás" ablakot, a Lomtárba került fájlokat pedig vissza lehet állítani.

Fájlokat egérrel is húzhatsz a panelek között, vagy az Intézőből be és ki
(alapból másol, **Shift**-tel mozgat; mappára ejtve a mappába kerül).

A panelek automatikusan frissülnek, ha a mappa tartalma megváltozik. A megnyitott fülek, az ablakméret,
a háttér és a rejtett fájlok beállítása megmarad (`%LOCALAPPDATA%\Twinpane\settings.json`).
