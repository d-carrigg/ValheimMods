# ValheimMods

BepInEx mods for Valheim.

| Mod | What it does |
|---|---|
| [CollectAll](CollectAll/README.md) | Open a chest, click **Collect**, click an item: every other stack of that item in nearby chests moves into this chest. Alt+click does it in one step. |
| [SignSorter](SignSorter/README.md) | Label chests with signs; items sort themselves into the matching chest. (Not currently in use.) |

## Layout

- `CollectAll/`, `SignSorter/`: the mods
- `Shared/InventoryOps.cs`: item moves used by both mods. Every move re-counts both inventories and repairs any difference, so items are never duplicated or lost.
- `SignSorter.Tests/`: tests for both mods, run against the game's real `Inventory` class
- `package/CollectAll/`: Thunderstore / r2modman package files (manifest, icon)
- `docs/`: illustrated guides and screenshots

## Building

Needs the .NET SDK, Valheim, and a BepInEx install (an r2modman profile works). Paths are set at the top of each `.csproj` (`ValheimDir`, `BepInExDir`); override them with `-p:ValheimDir=...`.

```
dotnet build ValheimMods.slnx -c Release
dotnet SignSorter.Tests/bin/Release/SignSorter.Tests.dll
```

## Packaging CollectAll for r2modman / Thunderstore

Copy `CollectAll/bin/Release/CollectAll.dll` and `CollectAll/README.md` into `package/CollectAll/`. Then zip the four files (`manifest.json`, `icon.png`, `README.md`, `CollectAll.dll`). Bump `version_number` in `manifest.json` for every release.
