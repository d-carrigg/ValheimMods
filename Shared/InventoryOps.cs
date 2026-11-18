using System;
using System.Collections.Generic;
using System.Linq;

namespace ChestShared
{
    /// <summary>
    /// Safe item moves between two inventories, shared by SignSorter and CollectAll.
    ///
    /// The game's Inventory.AddItem/RemoveItem change the item list first and only then run save
    /// hooks (Container.Save, and anything other mods attached). If a hook throws, the change has
    /// happened but the caller never finishes. So every move here is followed by a re-count of
    /// both inventories, and any difference is repaired: items are never duplicated or lost.
    /// </summary>
    internal static class InventoryOps
    {
        /// <summary>Same stack kind as far as the game's stacking goes: name, quality, world level.</summary>
        public static bool IsSameStack(ItemDrop.ItemData a, ItemDrop.ItemData b) =>
            a.m_shared.m_name == b.m_shared.m_name && a.m_quality == b.m_quality && a.m_worldLevel == b.m_worldLevel;

        /// <summary>Units of this exact stack kind in an inventory.</summary>
        public static int Units(Inventory inv, ItemDrop.ItemData kind)
        {
            int n = 0;
            foreach (ItemDrop.ItemData i in inv.GetAllItems())
                if (IsSameStack(i, kind)) n += i.m_stack;
            return n;
        }

        /// <summary>How many of this item fit, mirroring Inventory.AddItem's stacking rules.</summary>
        public static int FreeSpaceFor(Inventory inv, ItemDrop.ItemData item)
        {
            int max = Math.Max(1, item.m_shared.m_maxStackSize);
            long space = (long)Math.Max(0, inv.GetEmptySlots()) * max;
            if (max > 1)
            {
                foreach (ItemDrop.ItemData other in inv.GetAllItems())
                {
                    if (IsSameStack(other, item) && other.m_stack < max)
                        space += max - other.m_stack;
                }
            }
            return (int)Math.Min(int.MaxValue, space);
        }

        /// <summary>
        /// Moves up to <paramref name="amount"/> units. Returns how many actually landed; exactly
        /// that many are removed from the source, so nothing is duplicated or lost.
        /// </summary>
        public static int Transfer(Inventory from, Inventory to, ItemDrop.ItemData item, int amount, Action<string, Exception> fail)
        {
            if (from == null || to == null || item == null) return 0;
            if (amount <= 0 || item.m_stack <= 0 || !from.ContainsItem(item)) return 0;
            amount = Math.Min(amount, item.m_stack);
            if (ReferenceEquals(from, to)) return 0;

            ItemDrop.ItemData kind = item.Clone();
            int total = Units(from, kind) + Units(to, kind);
            int inDestBefore = Units(to, kind);

            try
            {
                ItemDrop.ItemData copy = item.Clone();
                copy.m_stack = amount;
                bool ok = to.AddItem(copy);
                // AddItem tops up existing stacks one at a time, then drops the rest into a free
                // slot. On failure, copy.m_stack holds the part that didn't fit.
                int added = ok ? amount : amount - copy.m_stack;
                if (added > 0 && !from.RemoveItem(item, added))
                    Fail(fail, "Transfer", new InvalidOperationException($"could not remove {added}x {item.m_shared.m_name} from source"));
            }
            catch (Exception e)
            {
                Fail(fail, "Transfer", e);
            }

            // Whatever happened above, the two inventories must still hold the same total.
            // If not, put it right (prefer leaving the items in the source).
            Reconcile(from, to, kind, total, preferred: from, fail);
            return Math.Max(0, Units(to, kind) - inDestBefore);
        }

        /// <summary>
        /// Makes sure the two inventories together still hold exactly <paramref name="expected"/>
        /// units of <paramref name="kind"/>; adds back missing units or removes duplicated ones.
        /// Returns true if a repair was needed.
        /// </summary>
        public static bool Reconcile(Inventory a, Inventory b, ItemDrop.ItemData kind, int expected, Inventory preferred, Action<string, Exception> fail)
        {
            bool repaired = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int actual = Units(a, kind) + Units(b, kind);
                if (actual == expected) return repaired;
                repaired = true;
                try
                {
                    if (actual < expected)
                    {
                        // Put the missing units back wherever there's room, preferred side first.
                        int missing = expected - actual;
                        foreach (Inventory inv in new[] { preferred, preferred == a ? b : a })
                        {
                            int fits = Math.Min(missing, FreeSpaceFor(inv, kind));
                            if (fits <= 0) continue;
                            ItemDrop.ItemData restore = kind.Clone();
                            restore.m_stack = fits;
                            try { inv.AddItem(restore); }
                            catch (Exception e) { Fail(fail, "Repair", e); }
                            missing = expected - (Units(a, kind) + Units(b, kind));
                            if (missing <= 0) break;
                        }
                    }
                    else
                    {
                        int extra = actual - expected;
                        foreach (Inventory inv in new[] { preferred == a ? b : a, preferred })
                        {
                            int take = Math.Min(extra, Units(inv, kind));
                            if (take <= 0) continue;
                            RemoveUnits(inv, kind, take);
                            extra -= take;
                            if (extra <= 0) break;
                        }
                    }
                }
                catch (Exception e)
                {
                    // The list change happens before the hook that threw; re-count and go again.
                    Fail(fail, "Repair", e);
                }
            }
            int final = Units(a, kind) + Units(b, kind);
            if (final != expected)
                Fail(fail, "Repair", new InvalidOperationException($"{kind.m_shared.m_name}: expected {expected}, have {final}"));
            return true;
        }

        private static void RemoveUnits(Inventory inv, ItemDrop.ItemData kind, int count)
        {
            foreach (ItemDrop.ItemData i in inv.GetAllItems().Where(i => IsSameStack(i, kind)).ToList())
            {
                if (count <= 0) return;
                int n = Math.Min(count, i.m_stack);
                inv.RemoveItem(i, n);
                count -= n;
            }
        }

        private static void Fail(Action<string, Exception> fail, string what, Exception e)
        {
            try { fail?.Invoke(what, e); } catch { }
        }

        /// <summary>True if this exact item object is in the inventory.</summary>
        public static bool ContainsItem(this Inventory inv, ItemDrop.ItemData item)
        {
            if (inv == null || item == null) return false;
            foreach (ItemDrop.ItemData i in inv.GetAllItems())
                if (ReferenceEquals(i, item)) return true;
            return false;
        }
    }
}
