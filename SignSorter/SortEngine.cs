using System;
using System.Collections.Generic;
using System.Linq;
using ChestShared;
using UnityEngine;

namespace SignSorter
{
    /// <summary>A chest as the sorting logic sees it. No Unity/network objects needed.</summary>
    internal sealed class SortChest
    {
        public Inventory Inventory;
        public SignFilter Filter;          // null when no sign labels this chest
        public Vector3 Position;
        public Func<bool> IsBusy;          // someone has it open
        public object Tag;                 // the Unity-side handle

        public bool Labeled => Filter != null && !Filter.IsEmpty;

        public bool Busy
        {
            get
            {
                if (IsBusy == null) return false;
                try { return IsBusy(); }
                catch { return true; } // if we can't tell, don't touch it
            }
        }

        public int Score(ItemDrop.ItemData item) => Labeled ? Filter.Score(item) : SignFilter.NoMatch;
    }

    internal sealed class SortContext
    {
        /// <summary>False = only work out what would move (nothing changes).</summary>
        public bool Apply = true;
        public bool PullFromUnlabeled = true;
        public bool AllowSwaps = true;
        /// <summary>Combine partial stacks in labeled chests each pass, freeing slots.</summary>
        public bool MergeStacks = true;
        /// <summary>
        /// A chest with no sign only gives up items to chests that name them (wildcard or better),
        /// never to "*" or category chests - so a catch-all can't drain your personal chests.
        /// </summary>
        public int MinScoreFromUnlabeled = SignFilter.ScoreWildcard;
        /// <summary>Where the player stands; breaks ties between equally good chests on deposit.</summary>
        public Vector3 Origin = Vector3.zero;

        public readonly HashSet<SortChest> Involved = new HashSet<SortChest>();
        public readonly HashSet<SortChest> Received = new HashSet<SortChest>();
        public int ItemsMoved;
        public int Swaps;
        public int StacksMerged;
        public int Errors;
        public Action<string> Warn;

        internal void Fail(string what, Exception e)
        {
            Errors++;
            try { Warn?.Invoke($"{what}: {e.GetType().Name}: {e.Message}"); } catch { }
        }
    }

    /// <summary>
    /// The sorting rules, written against the game's own Inventory class so they can be tested
    /// outside the game. An item only ever moves to a chest whose sign wants it MORE than the
    /// chest it's in, so items can't bounce back and forth and every pass makes progress.
    /// </summary>
    internal static class SortEngine
    {
        /// <summary>Runs passes until nothing changes (or maxPasses). Returns passes run.</summary>
        public static int SortChests(List<SortChest> chests, SortContext ctx, int maxPasses)
        {
            int passes = 0;
            for (int pass = 0; pass < Math.Max(1, maxPasses); pass++)
            {
                passes++;
                int before = ctx.ItemsMoved + ctx.Swaps + ctx.StacksMerged;
                if (ctx.Apply && ctx.MergeStacks)
                {
                    // Merge first: it frees slots that the moves below can use.
                    foreach (SortChest chest in chests)
                        if (chest.Labeled && chest.Inventory != null && !chest.Busy)
                            if (MergeStacks(chest.Inventory, ctx) > 0) ctx.Received.Add(chest);
                }
                OnePass(chests, ctx);
                if (!ctx.Apply) break; // a dry run can't make progress, one look is enough
                if (ctx.ItemsMoved + ctx.Swaps + ctx.StacksMerged == before) break;
            }
            return passes;
        }

        private static void OnePass(List<SortChest> chests, SortContext ctx)
        {
            List<SortChest> destinations = chests.Where(c => c.Labeled).ToList();
            if (destinations.Count == 0) return;

            foreach (SortChest source in chests)
            {
                if (source.Inventory == null || source.Busy) continue;
                if (!source.Labeled && !ctx.PullFromUnlabeled) continue;

                foreach (ItemDrop.ItemData item in source.Inventory.GetAllItems().ToList())
                {
                    try
                    {
                        // An earlier swap or stack merge may have taken it out already.
                        if (!source.Inventory.ContainsItem(item) || item.m_stack <= 0) continue;
                        MoveItem(item, source.Inventory, source, source.Score(item), destinations, ctx, allowSwaps: ctx.AllowSwaps);
                    }
                    catch (Exception e)
                    {
                        ctx.Fail($"Sorting {item?.m_shared?.m_name}", e);
                    }
                }
            }
        }

        /// <summary>
        /// Puts items from a player's inventory into chests whose sign names them (exactly, with a
        /// typo, or by wildcard). Category and "*" chests don't take from the player, so your
        /// food, meads and spare arrows stay with you.
        /// </summary>
        public static void Deposit(Inventory from, Func<ItemDrop.ItemData, bool> eligible, List<SortChest> chests, SortContext ctx)
        {
            if (from == null) return;
            List<SortChest> destinations = chests.Where(c => c.Labeled).ToList();
            if (destinations.Count == 0) return;

            foreach (ItemDrop.ItemData item in from.GetAllItems().ToList())
            {
                try
                {
                    if (!from.ContainsItem(item) || item.m_stack <= 0) continue;
                    if (eligible != null && !eligible(item)) continue;
                    MoveItem(item, from, null, SignFilter.ScoreCategory, destinations, ctx, allowSwaps: false);
                }
                catch (Exception e)
                {
                    ctx.Fail($"Depositing {item?.m_shared?.m_name}", e);
                }
            }
        }

        private static void MoveItem(ItemDrop.ItemData item, Inventory from, SortChest source, int sourceScore,
            List<SortChest> destinations, SortContext ctx, bool allowSwaps)
        {
            Vector3 origin = source != null ? source.Position : ctx.Origin;
            bool fromUnlabeled = source != null && !source.Labeled;
            var candidates = new List<KeyValuePair<SortChest, int>>();
            foreach (SortChest dest in destinations)
            {
                if (dest == source || dest.Inventory == null || dest.Busy) continue;
                int score = dest.Score(item);
                if (score <= sourceScore) continue;
                if (fromUnlabeled && score < ctx.MinScoreFromUnlabeled) continue;
                candidates.Add(new KeyValuePair<SortChest, int>(dest, score));
            }
            if (candidates.Count == 0) return;

            candidates.Sort((a, b) =>
            {
                int c = b.Value.CompareTo(a.Value);
                if (c != 0) return c;
                c = Holds(b.Key.Inventory, item).CompareTo(Holds(a.Key.Inventory, item));
                if (c != 0) return c;
                return (a.Key.Position - origin).sqrMagnitude.CompareTo((b.Key.Position - origin).sqrMagnitude);
            });

            int remaining = item.m_stack;
            foreach (var pair in candidates)
            {
                if (remaining <= 0) break;
                SortChest dest = pair.Key;
                int amount = Math.Min(remaining, FreeSpaceFor(dest.Inventory, item));
                if (amount <= 0) continue;

                if (!ctx.Apply)
                {
                    ctx.Involved.Add(dest);
                    if (source != null) ctx.Involved.Add(source);
                    remaining -= amount;
                    ctx.ItemsMoved += amount;
                    continue;
                }

                int added = Transfer(from, dest.Inventory, item, amount, ctx);
                if (added <= 0) continue;
                ctx.Received.Add(dest);
                ctx.ItemsMoved += added;
                remaining = from.ContainsItem(item) ? item.m_stack : 0;
            }

            // Every chest that wants it is full. If one of them holds something that belongs
            // here at least as much as there, trade places.
            if (remaining > 0 && allowSwaps && source != null)
            {
                foreach (var pair in candidates)
                {
                    if (TrySwap(item, source, sourceScore, pair.Key, pair.Value, ctx)) break;
                }
            }
        }

        private static bool TrySwap(ItemDrop.ItemData x, SortChest source, int xScoreHere, SortChest dest, int xScoreThere, SortContext ctx)
        {
            ItemDrop.ItemData best = null;
            int bestGain = int.MinValue;
            int bestThere = int.MaxValue;
            foreach (ItemDrop.ItemData y in dest.Inventory.GetAllItems())
            {
                if (y == x || y.m_stack <= 0) continue;
                int yThere = dest.Score(y);
                int yHere = source.Score(y);
                // y must have a real home in the source (so junk is never pushed into a chest
                // that doesn't ask for it), belong there at least as much as where it is, and be
                // less at home in dest than x would be - otherwise the swap isn't an improvement.
                if (yHere <= SignFilter.NoMatch || yHere < yThere || yThere >= xScoreThere) continue;
                int gain = yHere - yThere;
                if (gain > bestGain || (gain == bestGain && yThere < bestThere))
                {
                    best = y;
                    bestGain = gain;
                    bestThere = yThere;
                }
            }
            if (best == null) return false;

            if (!ctx.Apply)
            {
                ctx.Involved.Add(dest);
                ctx.Involved.Add(source);
                ctx.Swaps++;
                return true;
            }

            int units = x.m_stack + best.m_stack;
            if (!Swap(source.Inventory, x, dest.Inventory, best, ctx)) return false;
            ctx.Received.Add(dest);
            ctx.Received.Add(source);
            ctx.Swaps++;
            ctx.ItemsMoved += units;
            return true;
        }

        /// <summary>Exchanges two stacks between inventories, each taking the other's slot.</summary>
        internal static bool Swap(Inventory a, ItemDrop.ItemData x, Inventory b, ItemDrop.ItemData y, SortContext ctx)
        {
            if (!a.ContainsItem(x) || !b.ContainsItem(y)) return false;
            Vector2i posX = x.m_gridPos;
            Vector2i posY = y.m_gridPos;
            // Each item takes the other's slot, so both slots must exist in the other chest's grid.
            if (!InGrid(b, posY) || !InGrid(a, posX)) return false;

            // Snapshot unit counts. The game's add/remove change the list first and only then
            // run save hooks, which another mod could make throw; if anything goes wrong we
            // compare against these counts and repair, so items are never lost or duplicated.
            ItemDrop.ItemData xKind = x.Clone(), yKind = y.Clone();
            int xTotal = Units(a, xKind) + Units(b, xKind);
            int yTotal = Units(a, yKind) + Units(b, yKind);

            bool ok = false;
            try
            {
                if (a.RemoveItem(x) && b.RemoveItem(y))
                {
                    bool okX = b.AddItem(x, posY);
                    bool okY = a.AddItem(y, posX);
                    ok = okX && okY;
                    if (!ok) ctx.Fail("Swap", new InvalidOperationException($"slot was not free (x={okX}, y={okY})"));
                }
            }
            catch (Exception e)
            {
                ctx.Fail("Swap", e);
            }

            bool repairedX = Reconcile(a, b, xKind, xTotal, preferred: ok ? b : a, ctx);
            bool repairedY = Reconcile(a, b, yKind, yTotal, preferred: ok ? a : b, ctx);
            return ok && !repairedX && !repairedY;
        }

        private static bool InGrid(Inventory inv, Vector2i pos) =>
            pos.x >= 0 && pos.y >= 0 && pos.x < inv.GetWidth() && pos.y < inv.GetHeight();

        private static int Units(Inventory inv, ItemDrop.ItemData kind) => InventoryOps.Units(inv, kind);

        private static bool Reconcile(Inventory a, Inventory b, ItemDrop.ItemData kind, int expected, Inventory preferred, SortContext ctx) =>
            InventoryOps.Reconcile(a, b, kind, expected, preferred, ctx.Fail);

        private static bool Holds(Inventory inv, ItemDrop.ItemData item)
        {
            foreach (ItemDrop.ItemData other in inv.GetAllItems())
                if (other.m_shared.m_name == item.m_shared.m_name) return true;
            return false;
        }

        /// <summary>How many of this item fit, mirroring Inventory.AddItem's stacking rules.</summary>
        internal static int FreeSpaceFor(Inventory inv, ItemDrop.ItemData item) => InventoryOps.FreeSpaceFor(inv, item);

        private static bool IsSameStack(ItemDrop.ItemData a, ItemDrop.ItemData b) => InventoryOps.IsSameStack(a, b);

        /// <summary>Moves up to <paramref name="amount"/> units; returns how many landed (see InventoryOps).</summary>
        internal static int Transfer(Inventory from, Inventory to, ItemDrop.ItemData item, int amount, SortContext ctx) =>
            InventoryOps.Transfer(from, to, item, amount, ctx.Fail);

        /// <summary>Merges partial stacks of the same item inside one inventory.</summary>
        public static int MergeStacks(Inventory inv, SortContext ctx)
        {
            if (inv == null) return 0;
            int merges = 0;
            try
            {
                List<ItemDrop.ItemData> items = inv.GetAllItems()
                    .Where(i => i.m_shared.m_maxStackSize > 1)
                    .OrderBy(i => i.m_gridPos.y).ThenBy(i => i.m_gridPos.x)
                    .ToList();

                for (int i = 0; i < items.Count; i++)
                {
                    ItemDrop.ItemData target = items[i];
                    int max = target.m_shared.m_maxStackSize;
                    if (!inv.ContainsItem(target)) continue;

                    for (int j = i + 1; j < items.Count && target.m_stack < max; j++)
                    {
                        ItemDrop.ItemData donor = items[j];
                        if (!IsSameStack(target, donor) || !inv.ContainsItem(donor) || donor.m_stack <= 0) continue;
                        int n = Math.Min(max - target.m_stack, donor.m_stack);
                        target.m_stack += n;
                        if (donor.m_cheated) target.m_cheated = true; // same as the game's own stacking
                        if (!inv.RemoveItem(donor, n))
                        {
                            target.m_stack -= n;
                            continue;
                        }
                        merges++;
                    }
                }
            }
            catch (Exception e)
            {
                ctx.Fail("Merging stacks", e);
            }
            ctx.StacksMerged += merges;
            return merges;
        }

        /// <summary>
        /// Items in labeled chests that their own sign doesn't want and no other sign wants
        /// either - they're stuck until a matching (or "*") chest exists.
        /// </summary>
        public static List<ItemDrop.ItemData> Leftovers(List<SortChest> chests)
        {
            var result = new List<ItemDrop.ItemData>();
            List<SortChest> labeled = chests.Where(c => c.Labeled && c.Inventory != null).ToList();
            foreach (SortChest chest in labeled)
            {
                foreach (ItemDrop.ItemData item in chest.Inventory.GetAllItems())
                {
                    if (chest.Score(item) > SignFilter.NoMatch) continue;
                    bool wanted = false;
                    foreach (SortChest other in labeled)
                    {
                        if (other != chest && other.Score(item) > SignFilter.NoMatch)
                        {
                            wanted = true;
                            break;
                        }
                    }
                    if (!wanted) result.Add(item);
                }
            }
            return result;
        }
    }

}
