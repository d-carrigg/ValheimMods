using System;
using System.Collections.Generic;
using System.Linq;
using ChestShared;

namespace CollectAll;

/// <summary>A chest items can be taken from.</summary>
internal sealed class CollectSource
{
    /// <summary>The chest's contents.</summary>
    public Inventory Inventory;

    /// <summary>True while someone has the chest open.</summary>
    public Func<bool> IsBusy;

    /// <summary>Distance from the target chest; closer chests are emptied first.</summary>
    public float Distance;

    /// <summary><see cref="IsBusy"/>, treating "can't tell" as busy so the chest is left alone.</summary>
    public bool Busy
    {
        get
        {
            try { return IsBusy?.Invoke() ?? false; }
            catch { return true; }
        }
    }
}

/// <summary>What a collect did, for the on-screen message.</summary>
internal sealed class CollectResult
{
    /// <summary>The item's name token, e.g. "$item_finewood".</summary>
    public string ItemName;

    /// <summary>Units moved into the target.</summary>
    public int Moved;

    /// <summary>Chests at least one unit was taken from.</summary>
    public int ChestsTakenFrom;

    /// <summary>Units still in other chests because the target ran out of room.</summary>
    public int LeftBehind;

    /// <summary>Chests holding the item that were open, so left alone.</summary>
    public int BusySkipped;

    /// <summary>Chests holding more of the item than the target, so left alone.</summary>
    public int BiggerSkipped;

    /// <summary>Chests completely full of nothing but this item, so left alone.</summary>
    public int FullSkipped;

    /// <summary>Failed reads or moves (each logged; counts were re-checked).</summary>
    public int Errors;

    /// <summary>The target filled up before everything was moved.</summary>
    public bool TargetFull => LeftBehind > 0;
}

/// <summary>
/// "Collect all of this item into this chest." Written against the game's own Inventory class
/// so it can be tested outside the game; every move goes through ChestShared.InventoryOps,
/// which re-counts and repairs, so nothing is ever duplicated or lost.
/// </summary>
internal static class Collector
{
    /// <summary>
    /// Moves every item named like <paramref name="example"/> (any quality) from the sources
    /// into <paramref name="target"/> until the target is full. Smaller piles go first (then
    /// nearer chests), so the target grows as it goes.
    /// </summary>
    /// <param name="target">The open chest to collect into.</param>
    /// <param name="example">Any stack of the item to collect.</param>
    /// <param name="sources">Chests to take from.</param>
    /// <param name="warn">Called for each failed read or move.</param>
    /// <param name="skipBiggerPiles">
    /// "Bigger pile wins": never take from a chest that holds more of the item than the target
    /// does at that moment. Items then only flow toward the biggest pile, so collecting in
    /// two chests in turn can never shuffle items back and forth.
    /// </param>
    /// <param name="skipFullChests">
    /// Never take from a chest that is completely full and holds nothing but this item (a
    /// finished stash). One stray item, a partial stack or an empty slot means it isn't one.
    /// </param>
    public static CollectResult Collect(Inventory target, ItemDrop.ItemData example, IEnumerable<CollectSource> sources,
        Action<string, Exception> warn, bool skipBiggerPiles, bool skipFullChests)
    {
        var result = new CollectResult { ItemName = example?.m_shared?.m_name };
        if (target == null || result.ItemName == null) return result;

        Action<string, Exception> fail = (what, e) =>
        {
            result.Errors++;
            try { warn?.Invoke(what, e); } catch { }
        };

        // Work out what each chest holds first, so smaller piles can be taken first.
        var piles = new List<KeyValuePair<CollectSource, List<ItemDrop.ItemData>>>();
        foreach (CollectSource source in sources.Where(s => s?.Inventory != null && !ReferenceEquals(s.Inventory, target)))
        {
            try
            {
                var matches = source.Inventory.GetAllItems().Where(i => i.m_shared.m_name == result.ItemName).ToList();
                if (matches.Count > 0) piles.Add(new KeyValuePair<CollectSource, List<ItemDrop.ItemData>>(source, matches));
            }
            catch (Exception e)
            {
                fail("Reading a chest", e);
            }
        }

        foreach (var pile in piles.OrderBy(p => p.Value.Sum(i => i.m_stack)).ThenBy(p => p.Key.Distance))
        {
            CollectSource source = pile.Key;
            List<ItemDrop.ItemData> matches = pile.Value;

            if (source.Busy)
            {
                result.BusySkipped++;
                continue;
            }

            if (skipFullChests && IsFullOfOnly(source.Inventory, result.ItemName))
            {
                result.FullSkipped++;
                continue;
            }

            if (skipBiggerPiles && CountOf(source.Inventory, result.ItemName) > CountOf(target, result.ItemName))
            {
                result.BiggerSkipped++;
                continue;
            }

            bool tookAny = false;
            // Biggest stacks first, so the target ends up with as few partial stacks as possible.
            foreach (ItemDrop.ItemData item in matches.OrderByDescending(i => i.m_stack))
            {
                try
                {
                    if (!source.Inventory.ContainsItem(item) || item.m_stack <= 0) continue;
                    int room = InventoryOps.FreeSpaceFor(target, item);
                    if (room <= 0)
                    {
                        result.LeftBehind += item.m_stack;
                        continue;
                    }
                    int moved = InventoryOps.Transfer(source.Inventory, target, item, Math.Min(room, item.m_stack), fail);
                    if (moved > 0)
                    {
                        result.Moved += moved;
                        tookAny = true;
                    }
                    if (source.Inventory.ContainsItem(item) && item.m_stack > 0) result.LeftBehind += item.m_stack;
                }
                catch (Exception e)
                {
                    fail($"Collecting {result.ItemName}", e);
                }
            }
            if (tookAny) result.ChestsTakenFrom++;
        }
        return result;
    }

    /// <summary>
    /// True when every slot is taken, every item is <paramref name="itemName"/>, and every
    /// stack is at its maximum.
    /// </summary>
    internal static bool IsFullOfOnly(Inventory inv, string itemName)
    {
        if (inv == null || inv.GetEmptySlots() > 0) return false;
        List<ItemDrop.ItemData> items = inv.GetAllItems();
        if (items.Count == 0) return false;
        foreach (ItemDrop.ItemData item in items)
        {
            if (item.m_shared.m_name != itemName) return false;
            if (item.m_stack < Math.Max(1, item.m_shared.m_maxStackSize)) return false;
        }
        return true;
    }

    /// <summary>How many of this item (any quality) an inventory holds.</summary>
    internal static int CountOf(Inventory inv, string itemName) =>
        inv.GetAllItems().Where(i => i.m_shared.m_name == itemName).Sum(i => i.m_stack);
}
