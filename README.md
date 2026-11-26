# Glass Commander 1.0.0

Kétpaneles fájlkezelő Windows 11-re: a Total Commander működése, natív WinUI 3-as felülettel
(Mica / Acrylic háttér, Fluent ikonok, lekerekített kártyák, világos/sötét téma a rendszer szerint).

## Fordítás

### A) GitHub Actions – nem kell semmit telepíteni
1. Töltsd fel a mappát egy GitHub repóba (pl. `Prophet1218/Glass-Commander`).
2. **Actions** fül → *Build* → lefut magától (vagy *Run workflow*).
3. A végén az **Artifacts** alól töltsd le a `GlassCommander-win-x64` zipet, csomagold ki, és indítsd a `GlassCommander.exe`-t.

### B) Saját gépen
- **Visual Studio 2022**: a telepítőben pipáld be a *WinUI application development* (vagy *.NET desktop development* + *Windows App SDK*) részt,
  nyisd meg a `src/GlassCommander/GlassCommander.csproj`-t, platform: **x64**, majd F5.
- **Csak .NET 8 SDK-val**:
  ```
  dotnet publish src/GlassCommander/GlassCommander.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o publish
  ```
  Ezután: `publish\GlassCommander.exe`

A program önálló (self-contained), a futtatáshoz nem kell külön .NET vagy Windows App SDK runtime.

## Billentyűk

| Billentyű | Funkció |
|---|---|
| Tab | Váltás a panelek között |
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
| Ctrl+T | Windows Terminal az aktuális mappában |
| Ctrl+E | Megnyitás Intézőben |

A másolás/mozgatás/törlés a Windows saját motorjával megy: natív folyamatjelzőt kapsz,
ütközésnél "Csere / Kihagyás" ablakot, a Lomtárba került fájlokat pedig vissza lehet állítani.

A panelek automatikusan frissülnek, ha a mappa tartalma megváltozik. Az utolsó mappák, az ablakméret,
a háttér és a rejtett fájlok beállítása megmarad (`%LOCALAPPDATA%\GlassCommander\settings.json`).
