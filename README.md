# CairnStoryTracker

A lightweight exploration-assist mod for **Cairn / 孤山独影** (The Game Bakers), built on MelonLoader.

## Markers

| Icon | Meaning |
|---|---|
| `?` | Narrative POI — a lore location worth exploring (hidden caves, note boards, item-backed story spots) |
| `i` | World info — signs, warnings and other environmental readables |
| `◇`→`*` | Standalone collectible (pure exploration item with no lore identity) |

Collectible-backed lore items (e.g. a note board that also contains a collectible) are **merged into their lore marker** — one place, one marker.

## Where markers appear

- **L1 survey view** (勘察岩壁): projected on-screen at the readables' world positions
- **Eagle-eye fast-travel map**: pure-visual markers that never register as warp points and never pollute the native location list

A small progress panel shows per-zone collectible progress (official localized zone names).

## Design principles

- One narrative place = one marker (grouped by the level's `*_Lore` hierarchy)
- Collectible identity (stable persistent ID) outranks lore identity — no `?`+`*` duplicates
- Never writes to the game save; non-persistent readables only track "read" for the current session
- Read/loot events never trigger background rescans — the model refreshes once, when you open a marker surface

## Requirements

- Cairn
- [MelonLoader](https://melonwiki.xyz/) 0.7.x (IL2CPP)
- [CairnAPI](https://www.nexusmods.com/cairn/mods/21)

## Build

Requires the .NET 6 SDK. The build needs the game's generated interop assemblies, so point it at your install:

```bash
dotnet build -c Release -p:GameDir="D:\Path\To\Cairn"
# or set the CAIRN_GAME_DIR environment variable once and just run:
dotnet build -c Release
```

## Install

Copy `bin/Release/CairnStoryTracker.dll` into the game's `Mods/` folder (next to CairnAPI.dll).

## Keys

- `F8` — dump a full diagnostic snapshot to the MelonLoader log
- Eagle-eye / L1 — markers appear automatically

## Configuration

Three toggles (CairnModOptions or `UserData/MelonPreferences.cfg`): show narrative POIs / show world info / show collectibles.
