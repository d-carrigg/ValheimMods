using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SignSorter
{
    internal enum SortMode { Auto, Manual, Deep, Deposit }

    internal sealed class SortReport
    {
        public int ItemsMoved;
        public int Swaps;
        public int ChestsChanged;
        public int StacksMerged;
        public int NotOwned;
        public int Errors;
        public int LabeledChests;
        public bool Skipped; // auto run skipped because nothing changed
        public List<string> Leftovers = new List<string>();
        public List<string> UnknownEntries = new List<string>();
        public bool AnythingHappened => ItemsMoved > 0 || Swaps > 0 || StacksMerged > 0;
    }

    /// <summary>The game-facing side: finds chests and signs, handles network ownership and undo.</summary>
    internal static class Sorter
    {
        private sealed class ChestHandle
        {
            public Container Container;
            public ZNetView NView;
        }

        private sealed class UndoEntry
        {
            public Container Container;
            public ZNetView NView;
            public byte[] Before;
            public byte[] After;
        }

        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        // Private game members, looked up once. Every use tolerates them being missing.
        private static readonly MethodInfo CheckForChanges = typeof(Container).GetMethod("CheckForChanges", AnyInstance, null, Type.EmptyTypes, null);
        private static readonly MethodInfo CheckAccess = typeof(Container).GetMethod("CheckAccess", AnyInstance, null, new[] { typeof(long) }, null);
        private static readonly FieldInfo SignTextWidget = typeof(Sign).GetField("m_textWidget", AnyInstance);
        private static readonly FieldInfo SignNView = typeof(Sign).GetField("m_nview", AnyInstance);
        private static readonly FieldInfo HiddenRight = typeof(Humanoid).GetField("m_hiddenRightItem", AnyInstance);
        private static readonly FieldInfo HiddenLeft = typeof(Humanoid).GetField("m_hiddenLeftItem", AnyInstance);

        private static readonly List<Piece> PieceBuffer = new List<Piece>();
        private static List<KeyValuePair<ItemDrop.ItemData, ItemKeys>> _catalog;
        private static int _catalogSize = -1;
        private static long _lastAutoFingerprint;
        private static List<UndoEntry> _undo;
        private static readonly HashSet<string> LoggedOnce = new HashSet<string>();

        /// <summary>Works out what an automatic sort would do here, without changing anything.</summary>
        public static SortReport Preview(Player player) => Run(player, SortMode.Auto, preview: true);

        public static SortReport Run(Player player, SortMode mode) => Run(player, mode, preview: false);

        private static SortReport Run(Player player, SortMode mode, bool preview)
        {
            var report = new SortReport();
            EnsureCatalog();
            bool deep = mode == SortMode.Deep;
            bool verbose = mode != SortMode.Auto;
            float radius = deep ? Plugin.DeepSortRadius.Value : Plugin.ScanRadius.Value;

            List<SortChest> chests = Collect(player, player.transform.position, radius, mode, verbose ? report : null);
            report.LabeledChests = chests.Count(c => c.Labeled);
            if (chests.Count == 0 || report.LabeledChests == 0) return report;

            if (preview)
            {
                var look = NewContext(player, apply: false);
                SortEngine.SortChests(chests, look, 1);
                report.ItemsMoved = look.ItemsMoved;
                report.ChestsChanged = look.Involved.Count;
                return report;
            }

            // Auto runs every few seconds: skip the work when no chest or sign changed since a
            // run that found nothing to do.
            if (mode == SortMode.Auto)
            {
                long fingerprint = Fingerprint(chests);
                if (fingerprint == _lastAutoFingerprint)
                {
                    report.Skipped = true;
                    return report;
                }
                _lastAutoFingerprint = fingerprint;
            }
            else
            {
                _lastAutoFingerprint = 0;
            }

            // Ownership: changes only stick if our game controls the chest on the network.
            // Taking control is opt-in and never done by the automatic sort.
            if (Plugin.ClaimChestOwnership.Value && mode != SortMode.Auto)
            {
                var dry = NewContext(player, apply: false);
                if (mode == SortMode.Deposit) SortEngine.Deposit(player.GetInventory(), DepositFilter, chests, dry);
                else SortEngine.SortChests(chests, dry, 1);
                foreach (SortChest chest in chests.Where(c => c.Labeled)) dry.Involved.Add(chest); // for stack merging
                foreach (SortChest chest in dry.Involved) TryClaim(chest);
            }

            var usable = new List<SortChest>();
            foreach (SortChest chest in chests)
            {
                var handle = (ChestHandle)chest.Tag;
                if (!IsOwner(handle))
                {
                    if (chest.Labeled) report.NotOwned++;
                    continue;
                }
                Refresh(handle);
                // Refreshing can delete an empty auto-destroy container; check again.
                if (IsOwner(handle)) usable.Add(chest);
            }
            if (usable.Count == 0) return report;

            List<UndoEntry> undo = mode == SortMode.Manual || mode == SortMode.Deep ? Snapshot(usable) : null;

            var ctx = NewContext(player, apply: true);
            if (mode == SortMode.Deposit)
                SortEngine.Deposit(player.GetInventory(), DepositFilter, usable, ctx);
            else
                SortEngine.SortChests(usable, ctx, deep ? 20 : 6);

            if (undo != null && ctx.ItemsMoved + ctx.Swaps + ctx.StacksMerged > 0) FinishSnapshot(undo);

            report.ItemsMoved = ctx.ItemsMoved;
            report.Swaps = ctx.Swaps;
            report.StacksMerged = ctx.StacksMerged;
            report.ChestsChanged = ctx.Received.Count;
            report.Errors = ctx.Errors;

            if (verbose)
            {
                // Look across every labeled chest in range (not only ones we control) so an item
                // whose chest belongs to someone else isn't reported as unwanted.
                foreach (ItemDrop.ItemData item in SortEngine.Leftovers(chests))
                {
                    string name = DisplayName(item);
                    if (!report.Leftovers.Contains(name)) report.Leftovers.Add(name);
                }
            }
            return report;
        }

        private static SortContext NewContext(Player player, bool apply) => new SortContext
        {
            Apply = apply,
            PullFromUnlabeled = Plugin.SortFromUnlabeledChests.Value,
            AllowSwaps = Plugin.AllowSwaps.Value,
            MergeStacks = Plugin.MergeStacks.Value,
            Origin = player.transform.position,
            Warn = msg => Plugin.Log.LogWarning(msg),
        };

        private static bool DepositFilter(ItemDrop.ItemData item)
        {
            if (item.m_equipped) return false;
            if (!Plugin.DepositIncludesHotbar.Value && item.m_gridPos.y == 0) return false;
            // A sheathed weapon is unequipped but still "in hand" as far as the game is concerned.
            Player player = Player.m_localPlayer;
            if (player != null)
            {
                if (HiddenRight != null && ReferenceEquals(HiddenRight.GetValue(player), item)) return false;
                if (HiddenLeft != null && ReferenceEquals(HiddenLeft.GetValue(player), item)) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ finding chests

        private static List<SortChest> Collect(Player player, Vector3 center, float radius, SortMode mode, SortReport report)
        {
            float signRange = Plugin.SignRange.Value;
            float arrowRange = Plugin.ArrowRange.Value;
            float reach = Mathf.Max(signRange, arrowRange);
            PieceBuffer.Clear();
            Piece.GetAllPiecesInRadius(center, radius + reach * 2f, PieceBuffer);

            var signs = new List<SignGeometry.SignInfo>();
            var containers = new List<Container>();
            var points = new List<Vector3>();

            foreach (Piece piece in PieceBuffer)
            {
                try
                {
                    if (piece == null) continue;
                    Sign sign = piece.GetComponent<Sign>();
                    if (sign != null && TryReadSign(sign, out SignGeometry.SignInfo info)) signs.Add(info);

                    Container container = piece.GetComponent<Container>();
                    if (container == null || !IsStorageChest(piece, container)) continue;
                    // Every chest near the signs takes part in deciding labels, even ones we
                    // won't sort, so a sign's target doesn't jump around as you walk.
                    if (Vector3.Distance(container.transform.position, center) > radius + reach) continue;
                    containers.Add(container);
                    points.Add(container.transform.position + Vector3.up * 0.4f); // middle of the chest
                }
                catch (Exception e)
                {
                    WarnOnce("piece", $"Skipping a piece that couldn't be read ({e.GetType().Name}: {e.Message}). Further errors like this are not logged.");
                }
            }

            List<List<string>> labels = SignGeometry.AssignLabels(signs, points, signRange, arrowRange);
            MatchSettings settings = Plugin.Settings;
            var catalog = Catalog();
            long playerId = player.GetPlayerID();
            bool onlyMine = mode == SortMode.Auto && Plugin.AutoSortOnlyMyChests.Value;
            var result = new List<SortChest>();

            for (int i = 0; i < containers.Count; i++)
            {
                Container container = containers[i];
                try
                {
                    if (Vector3.Distance(container.transform.position, center) > radius) continue;

                    SignFilter filter = labels[i].Count > 0 ? SignFilter.Parse(string.Join(", ", labels[i].ToArray()), settings) : null;
                    if (filter != null && filter.Ignore) continue;
                    // "Bob's house", "Don't touch": a sign that names nothing protects its chest.
                    if (filter != null && filter.NamesNothing(catalog))
                    {
                        if (report != null) AddRange(report.UnknownEntries, filter.UnknownEntries(catalog));
                        continue;
                    }
                    if (filter != null && filter.IsEmpty) filter = null;

                    if (!IsEligible(container, playerId, labeled: filter != null)) continue;
                    if (onlyMine && container.GetComponent<Piece>()?.GetCreator() != playerId) continue;

                    if (report != null && filter != null) AddRange(report.UnknownEntries, filter.UnknownEntries(catalog));

                    Container captured = container;
                    ZNetView view = container.GetComponent<ZNetView>();
                    result.Add(new SortChest
                    {
                        Inventory = container.GetInventory(),
                        Filter = filter,
                        Position = points[i],
                        IsBusy = () => IsOpenByAnyone(captured, view),
                        Tag = new ChestHandle { Container = container, NView = view },
                    });

                    if (Plugin.VerboseLogging.Value && filter != null && mode != SortMode.Auto)
                        Plugin.Log.LogInfo($"Chest at {points[i]} labeled: {string.Join(" + ", labels[i].ToArray())}");
                }
                catch (Exception e)
                {
                    WarnOnce("chest", $"Skipping a chest that couldn't be read ({e.GetType().Name}: {e.Message}). Further errors like this are not logged.");
                }
            }
            return result;
        }

        /// <summary>Real storage only: not the Obliterator, ships, carts or tombstone-style containers.</summary>
        private static bool IsStorageChest(Piece piece, Container container)
        {
            if (container.m_autoDestroyEmpty) return false;
            if (piece.GetComponent<Incinerator>() != null || piece.GetComponentInParent<Incinerator>() != null) return false;
            if (piece.GetComponent<Ship>() != null || piece.GetComponent<Vagon>() != null || container.m_wagon != null) return false;
            return true;
        }

        private static bool IsEligible(Container container, long playerId, bool labeled)
        {
            ZNetView nview = container.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            if (container.GetInventory() == null) return false;
            // Personal (private) chests are only sorted if you've put a sign on them.
            if (!labeled && container.m_privacy == Container.PrivacySetting.Private) return false;
            if (CheckAccess != null && !(bool)CheckAccess.Invoke(container, new object[] { playerId })) return false;
            if (!PrivateArea.CheckAccess(container.transform.position, 0f, false, false)) return false;
            return true;
        }

        /// <summary>
        /// Container.IsInUse() is only accurate on the client that controls the chest; the
        /// shared network copy says whether anyone at all has it open.
        /// </summary>
        private static bool IsOpenByAnyone(Container container, ZNetView nview)
        {
            if (container == null || nview == null || !nview.IsValid()) return true; // destroyed: hands off
            if (container.IsInUse()) return true;
            ZDO zdo = nview.GetZDO();
            return zdo != null && zdo.GetInt(ZDOVars.s_inUse, 0) == 1;
        }

        private static bool TryReadSign(Sign sign, out SignGeometry.SignInfo info)
        {
            info = default;
            string text = ReadSignText(sign);
            if (string.IsNullOrEmpty(text)) return false;

            Arrow arrow = SignText.ExtractArrow(text, out string body);
            if (body.Length == 0) return false;

            Vector3 right = sign.transform.right;
            if (SignTextWidget != null && SignTextWidget.GetValue(sign) is Component widget && widget != null)
            {
                // The text faces the reader, so its right-hand side is the reader's right.
                // TransformVector includes scale, so a mirrored widget is handled too.
                right = widget.transform.TransformVector(Vector3.right);
            }
            if (Plugin.FlipLeftRight.Value) right = -right;

            info = new SignGeometry.SignInfo
            {
                Position = sign.transform.position,
                Arrow = arrow,
                ReaderRight = right,
                Body = body,
            };
            return true;
        }

        /// <summary>
        /// The text as written, from the network copy. Sign.GetText() returns what's displayed,
        /// which can be censored or hidden depending on platform settings.
        /// </summary>
        private static string ReadSignText(Sign sign)
        {
            try
            {
                ZNetView nview = SignNView?.GetValue(sign) as ZNetView;
                if (nview == null) nview = sign.GetComponent<ZNetView>();
                ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
                if (zdo != null) return zdo.GetString(ZDOVars.s_text, "");
            }
            catch (Exception e)
            {
                WarnOnce("signtext", $"Reading sign text from the network failed, using displayed text instead ({e.Message}).");
            }
            try { return sign.GetText(); }
            catch { return ""; }
        }

        // ------------------------------------------------------------------ ownership

        private static bool IsOwner(ChestHandle handle)
        {
            try
            {
                return handle.Container != null && handle.NView != null && handle.NView.IsValid() && handle.NView.IsOwner();
            }
            catch
            {
                return false;
            }
        }

        private static void TryClaim(SortChest chest)
        {
            var handle = (ChestHandle)chest.Tag;
            try
            {
                if (chest.Busy || handle.NView == null || !handle.NView.IsValid()) return;
                if (!handle.NView.IsOwner()) handle.NView.ClaimOwnership();
            }
            catch (Exception e)
            {
                WarnOnce("claim", $"Could not take control of a chest: {e.Message}");
            }
        }

        /// <summary>Loads the latest network copy of the chest, so we never act on stale contents.</summary>
        private static void Refresh(ChestHandle handle)
        {
            if (CheckForChanges == null)
            {
                WarnOnce("refresh-missing", "Container.CheckForChanges not found (game update?). Chest contents may be up to a second stale.");
                return;
            }
            try
            {
                CheckForChanges.Invoke(handle.Container, null);
            }
            catch (Exception e)
            {
                WarnOnce("refresh", $"Refreshing a chest failed: {e.InnerException?.Message ?? e.Message}");
            }
        }

        // ------------------------------------------------------------------ undo

        private static byte[] Serialize(Inventory inv)
        {
            var pkg = new ZPackage();
            inv.Save(pkg);
            return pkg.GetArray();
        }

        private static List<UndoEntry> Snapshot(List<SortChest> chests)
        {
            var list = new List<UndoEntry>();
            foreach (SortChest chest in chests)
            {
                var handle = (ChestHandle)chest.Tag;
                list.Add(new UndoEntry { Container = handle.Container, NView = handle.NView, Before = Serialize(chest.Inventory) });
            }
            return list;
        }

        private static void FinishSnapshot(List<UndoEntry> entries)
        {
            var changed = new List<UndoEntry>();
            foreach (UndoEntry e in entries)
            {
                if (e.Container == null) continue;
                e.After = Serialize(e.Container.GetInventory());
                if (!e.After.SequenceEqual(e.Before)) changed.Add(e);
            }
            _undo = changed.Count > 0 ? changed : _undo;
        }

        public static bool HasUndo => _undo != null && _undo.Count > 0;

        /// <summary>
        /// Puts every chest the last F8/Shift+F8 changed back the way it was. All or nothing:
        /// if any of those chests changed since (or can't be reached), nothing is undone,
        /// because restoring only some of them could duplicate or lose items.
        /// </summary>
        public static string Undo()
        {
            if (!HasUndo) return "Nothing to undo";
            foreach (UndoEntry e in _undo)
            {
                if (e.Container == null || e.NView == null || !e.NView.IsValid())
                    return "Can't undo: a chest from the last sort is gone or too far away";
                if (!e.NView.IsOwner() || IsOpenByAnyone(e.Container, e.NView))
                    return "Can't undo: a chest from the last sort is open or controlled by another player";
                Refresh(new ChestHandle { Container = e.Container, NView = e.NView });
                if (!Serialize(e.Container.GetInventory()).SequenceEqual(e.After))
                    return "Can't undo: chests were changed after the last sort";
            }

            int restored = 0;
            foreach (UndoEntry e in _undo)
            {
                try
                {
                    e.Container.GetInventory().Load(new ZPackage(e.Before));
                    restored++;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"Undo failed for a chest: {ex}");
                }
            }
            _undo = null;
            _lastAutoFingerprint = 0;
            return $"Undid the last sort ({restored} chest{(restored == 1 ? "" : "s")} restored). Auto-sort paused for this session.";
        }

        // ------------------------------------------------------------------ helpers

        private static long Fingerprint(List<SortChest> chests)
        {
            unchecked
            {
                long h = 17;
                foreach (SortChest chest in chests)
                {
                    var handle = (ChestHandle)chest.Tag;
                    ZDO zdo = handle.NView != null && handle.NView.IsValid() ? handle.NView.GetZDO() : null;
                    h = h * 31 + (zdo != null ? zdo.m_uid.GetHashCode() : 0);
                    h = h * 31 + (zdo != null ? zdo.DataRevision : 0);
                    h = h * 31 + (chest.Filter != null ? chest.Filter.GetHashCode() : 0);
                }
                return h == 0 ? 1 : h;
            }
        }

        private static void AddRange(List<string> into, List<string> from)
        {
            foreach (string s in from)
                if (!into.Contains(s)) into.Add(s);
        }

        private static string DisplayName(ItemDrop.ItemData item)
        {
            string name = item?.m_shared?.m_name ?? "?";
            try
            {
                if (ItemKeys.Localize != null) return ItemKeys.Localize(name) ?? name;
            }
            catch { }
            return name;
        }

        internal static void WarnOnce(string key, string message)
        {
            if (LoggedOnce.Add(key)) Plugin.Log.LogWarning(message);
        }

        /// <summary>Every item in the game, for spotting sign entries that match nothing and typo targets.</summary>
        private static List<KeyValuePair<ItemDrop.ItemData, ItemKeys>> Catalog()
        {
            EnsureCatalog();
            return _catalog ?? new List<KeyValuePair<ItemDrop.ItemData, ItemKeys>>();
        }

        private static void EnsureCatalog()
        {
            ObjectDB db = ObjectDB.instance;
            if (db == null || db.m_items == null) return;
            if (_catalog != null && _catalogSize == db.m_items.Count) return;

            var list = new List<KeyValuePair<ItemDrop.ItemData, ItemKeys>>();
            var names = new HashSet<string>();
            foreach (GameObject go in db.m_items)
            {
                if (go == null) continue;
                ItemDrop drop = go.GetComponent<ItemDrop>();
                if (drop == null || drop.m_itemData?.m_shared == null) continue;
                ItemKeys keys = ItemKeys.Make(drop.m_itemData.m_shared.m_name, go.name);
                list.Add(new KeyValuePair<ItemDrop.ItemData, ItemKeys>(drop.m_itemData, keys));
                foreach (string k in keys.All) names.Add(k);
            }
            _catalog = list;
            _catalogSize = db.m_items.Count;
            Plugin.KnownNames = names.ToList();
            Plugin.RebuildSettings(); // typo matching needs the real names
        }
    }
}
