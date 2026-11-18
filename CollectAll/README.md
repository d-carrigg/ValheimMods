# CollectAll

A BepInEx mod for Valheim. Open a chest, click **Collect**, click an item: every other stack of that item in chests nearby moves into the chest you have open.

![CollectAll mock-up](../docs/screenshots/collectall.png)

*(Mock-up of the chest window; the item counts are real output of the mod's code. See [`docs/collectall.html`](../docs/collectall.html).)*

## Using it

1. Open the chest you want things gathered into.
2. Click **Collect** in the top-right corner of the chest panel, next to the chest's name. It changes to **Pick item...**
3. Click an item, in the chest or in your own inventory. All other stacks of that item (any quality) in chests within 30 m move into this chest. The nearest chests are emptied first, and existing stacks are topped up before new slots are used.

Shortcut: **Alt + click** an item in the chest does it in one step.

Right-click or Esc cancels pick mode. The message in the middle of the screen says what happened:

- `Collected 120 Fine wood from 3 chests`
- `Chest is full - 45 more still elsewhere`
- `No other Fine wood in chests within 30 m`
- `2 chests skipped (open, or another player's)`

Your own inventory is never touched; only other chests are emptied.

The button is pinned to the top-right corner of the chest panel, so it stays put whatever buttons the game or other mods (QuickStackStore etc.) add elsewhere. `Button.OffsetX` / `OffsetY` nudge it.

## What it won't touch

- Chests someone has open
- The Obliterator, carts, ships, tombstones
- Chests you don't have access to (other players' private chests, chests inside a ward you aren't on)
- In multiplayer: chests another player's game controls (always fine in single player; usually fine when you're alone in your base). `ClaimChestOwnership` lets it take control of those (never ones someone has open).

## Config

`BepInEx/config/local.collectall.cfg` (r2modman: Config editor):

| Setting | Default | |
|---|---|---|
| `Radius` | 30 | How far from the open chest to look (m) |
| `AltClickShortcut` | on | Alt + click collects straight away |
| `ClaimChestOwnership` | off | Multiplayer only, see above |
| `Button.Text` | Collect | Button label |
| `Button.OffsetX` / `OffsetY` | 0 | Nudge the button if it overlaps another mod's button |

## Safety

Every move uses the game's own inventory code, through the same move-and-recount code as SignSorter (`Shared/InventoryOps.cs`). After each move, both chests are re-counted and any difference is repaired, so items are never duplicated or lost, even if another mod's save hook throws an error. Tests run the collect logic against the game's real `Inventory` class, including 3,000 random scenarios. They check that every item is accounted for and that nothing that would fit is left behind.

## Building

```
dotnet build CollectAll -c Release
```

Copy only `CollectAll/bin/Release/CollectAll.dll` into `BepInEx/plugins/`. Paths to Valheim and BepInEx are at the top of `CollectAll.csproj`. Tests live in `SignSorter.Tests` (the `collect:` tests); run `dotnet SignSorter.Tests/bin/Release/SignSorter.Tests.dll`, or `--collect-demo` for the example above.

## Not verified yet

Built and tested outside the game only. Things to check in game:

- That its label reads "Collect", not "Place stacks".
- That Alt + click doesn't clash with another mod you use.
