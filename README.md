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

- `.github/workflows/buildstats-package.yml` builds it on every PR and push to `main` that touches it; the `dcarrigg-BuildStats-<version>` artifact is the upload zip.
- Publishing is manual: run the workflow on `main` from the Actions tab with **publish** ticked. It publishes `version_number` from `package/BuildStats/manifest.json` to Thunderstore and creates a `buildstats-v<version>` release.
- Locally: `pwsh package/build-buildstats.ps1`. Keep `version_number` and `Version` in `BuildStats/Plugin.cs` in step.
