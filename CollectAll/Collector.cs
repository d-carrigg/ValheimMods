using System;
using System.Collections.Generic;
using System.Linq;
using ChestShared;

namespace CollectAll
{
    /// <summary>A chest items can be taken from.</summary>
    internal sealed class CollectSource
    {
        public Inventory Inventory;
        public Func<bool> IsBusy;   // someone has it open
        public float Distance;      // from the target chest; closer chests are emptied first

        public bool Busy
        {
            get
            {
                if (IsBusy == null) return false;
                try { return IsBusy(); }
                catch { return true; } // if we can't tell, don't touch it
            }
        }
    }

    internal sealed class CollectResult
    {
        public string ItemName;          // the item's name token, e.g. "$item_finewood"
        public int Moved;                // units moved into the target
        public int ChestsTakenFrom;
        public int LeftBehind;           // units still in other chests (target ran out of room)
        public int BusySkipped;          // chests with the item that were open, so left alone
        public int Errors;
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
        /// into <paramref name="target"/>, nearest chest first, until the target is full.
        /// </summary>
        public static CollectResult Collect(Inventory target, ItemDrop.ItemData example, IEnumerable<CollectSource> sources,
            Action<string, Exception> warn)
        {
            var result = new CollectResult { ItemName = example?.m_shared?.m_name };
            if (target == null || result.ItemName == null) return result;

            Action<string, Exception> fail = (what, e) =>
            {
                result.Errors++;
                try { warn?.Invoke(what, e); } catch { }
            };

            foreach (CollectSource source in sources.Where(s => s?.Inventory != null && !ReferenceEquals(s.Inventory, target))
                                                    .OrderBy(s => s.Distance))
            {
                List<ItemDrop.ItemData> matches;
                try
                {
                    matches = source.Inventory.GetAllItems().Where(i => i.m_shared.m_name == result.ItemName).ToList();
                }
                catch (Exception e)
                {
                    fail("Reading a chest", e);
                    continue;
                }
                if (matches.Count == 0) continue;

                if (source.Busy)
                {
                    result.BusySkipped++;
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
    }
}
