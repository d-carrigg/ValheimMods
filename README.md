# ValheimMods

BepInEx mods for Valheim.

| Mod | What it does |
|---|---|
| [CollectAll](CollectAll/README.md) | Alt+click an item in a chest to gather all of it from nearby chests. |
| [BuildStats](BuildStats/README.md) | Press F7 for a per-player table of things built and resources used. Runs on dedicated servers. |
| [SignSorter](SignSorter/README.md) | Label chests with signs; items sort themselves into the matching chest. (Not currently in use.) |

## Layout

- `CollectAll/`, `BuildStats/`, `SignSorter/`: the mods
- `Shared/InventoryOps.cs`: item moves used by both mods. Every move re-counts both inventories and repairs any difference, so items are never duplicated or lost.
- `SignSorter.Tests/`: tests for both mods, run against the game's real `Inventory` class
- `package/CollectAll/`, `package/BuildStats/`: Thunderstore / r2modman package files (manifest, icon)
- `docs/`: illustrated guides and screenshots

## Building

Needs the .NET SDK, Valheim, and a BepInEx install (an r2modman profile works). Paths are set at the top of each `.csproj` (`ValheimDir`, `BepInExDir`); override them with `-p:ValheimDir=...`.

```
dotnet build ValheimMods.slnx -c Release
dotnet SignSorter.Tests/bin/Release/SignSorter.Tests.dll
```

## Packaging CollectAll for r2modman / Thunderstore

- GitHub Actions (`.github/workflows/collectall-package.yml`) builds CollectAll on every PR and push to `main` that touches it, with no game install needed. Download the `dcarrigg-CollectAll-<version>` artifact from the run: that zip is the Thunderstore upload.
- Each push to `main` with a new `version_number` publishes it to Thunderstore (via `tcli`, config in `package/CollectAll/thunderstore.toml`, token in the `TCLI_AUTH_TOKEN` repo secret) and creates a GitHub release (`collectall-v<version>`) with `dcarrigg-CollectAll-<version>.zip` attached.
- Locally: `pwsh package/build-collectall.ps1` builds and writes `dist/dcarrigg-CollectAll-<version>.zip`.
- The `dcarrigg-` prefix is the Author-Name-Version name r2modman reads when importing a local zip, so the author shows correctly.
- For each release, bump `version_number` in `package/CollectAll/manifest.json` and `Version` in `CollectAll/Plugin.cs` together; the build fails if they differ.

## Packaging BuildStats

Same setup as CollectAll, with its own workflow (`.github/workflows/buildstats-package.yml`), package folder (`package/BuildStats/`) and release tags (`buildstats-v<version>`).

- Each workflow only runs for its own mod's files, so changing one mod never rebuilds or publishes the other.
- A push to `main` with a new `version_number` in `package/BuildStats/manifest.json` publishes it to Thunderstore. Keep `Version` in `BuildStats/Plugin.cs` in step.
- Locally: `pwsh package/build-buildstats.ps1`.
