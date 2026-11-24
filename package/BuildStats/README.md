# BuildStats

Press **F7** in game for a table of what each player has built and the resources they used.

- **Things built**: every piece placed, per player (walls, floors, workbenches, ...)
- **Resources used**: materials spent, per player. Filter by **Building**, **Crafting** or **All**.

Each player gets a column, with a total on the right and a totals row at the bottom. Drag the window by its title. F7 or Esc closes it.

## Multiplayer and dedicated servers

Install BuildStats on the server as well as on each player's game. The server keeps the totals for every player, saves them, and sends the table to everyone who joins.

- Totals are saved per world in `BepInEx/config/BuildStats/<world>.tsv` on the server (or on the host, for a hosted or single-player game).
- Counting starts when the mod is installed. Earlier builds aren't known.
- If the server doesn't have BuildStats, the window still works but shows only your own builds from the current session.

## What counts

- A piece counts when you place it. Terrain edits with the hoe or pickaxe don't count.
- Resources count when they're taken from your inventory: placing pieces, and crafting or upgrading items at a station.
- Nothing counts while no-cost mode is on.

## Install

Use r2modman or Thunderstore Mod Manager. It installs BepInExPack_Valheim for you.

To install by hand, set up [BepInExPack_Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) and drop `BuildStats.dll` into `BepInEx/plugins`, on the server and on each player's game.

## Config

Settings are in `BepInEx/config/dcarrigg.buildstats.cfg`, or the config editor in your mod manager.

- `ToggleKey` (default F7): opens and closes the window.
- `CountCrafting` (default on): also count materials used for crafting.
