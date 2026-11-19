using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CollectAll;

/// <summary>The game-facing side: finds the chests around the one you have open.</summary>
internal static class ChestFinder
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    // Private game members, looked up once. Every use tolerates them being missing.
    private static readonly MethodInfo CheckForChanges = typeof(Container).GetMethod("CheckForChanges", AnyInstance, null, Type.EmptyTypes, null);
    private static readonly MethodInfo CheckAccess = typeof(Container).GetMethod("CheckAccess", AnyInstance, null, new[] { typeof(long) }, null);

    private static readonly List<Piece> PieceBuffer = new List<Piece>();
    private static readonly HashSet<string> LoggedOnce = new HashSet<string>();

    /// <summary>The result of <see cref="ChestsAround"/>.</summary>
    public sealed class Found
    {
        /// <summary>Chests that hold the item and can be taken from.</summary>
        public List<CollectSource> Sources = new List<CollectSource>();

        /// <summary>Chests skipped because another player's game controls them.</summary>
        public int NotOwned;
    }

    /// <summary>Chests near <paramref name="target"/> that hold <paramref name="itemName"/> and we may take from.</summary>
    public static Found ChestsAround(Container target, Player player, float radius, string itemName)
    {
        var found = new Found();
        Vector3 center = target.transform.position;
        long playerId = player.GetPlayerID();

        PieceBuffer.Clear();
        Piece.GetAllPiecesInRadius(center, radius, PieceBuffer);

        foreach (Piece piece in PieceBuffer)
        {
            try
            {
                if (piece == null) continue;
                Container container = piece.GetComponent<Container>();
                if (container == null || container == target) continue;
                if (!IsStorageChest(piece, container)) continue;

                ZNetView nview = container.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid() || container.GetInventory() == null) continue;
                if (CheckAccess != null && !(bool)CheckAccess.Invoke(container, new object[] { playerId })) continue;
                if (!PrivateArea.CheckAccess(container.transform.position, 0f, false, false)) continue;
                // Only chests that have the item matter (every client has the contents loaded,
                // so this works before taking control, and keeps us from touching the rest).
                if (Collector.CountOf(container.GetInventory(), itemName) == 0) continue;

                // Changes only save if our game controls the chest on the network.
                if (!nview.IsOwner())
                {
                    found.NotOwned++;
                    continue;
                }
                Refresh(container);
                if (!nview.IsValid() || !nview.IsOwner()) continue;

                found.Sources.Add(new CollectSource
                {
                    Inventory = container.GetInventory(),
                    IsBusy = () => IsOpenByAnyone(container, nview),
                    Distance = Vector3.Distance(center, container.transform.position),
                });
            }
            catch (Exception e)
            {
                WarnOnce("piece", $"Skipping a chest that couldn't be read ({e.GetType().Name}: {e.Message}). Further errors like this are not logged.");
            }
        }
        return found;
    }

    /// <summary>Real storage only: not the Obliterator, ships, carts or tombstone-style containers.</summary>
    private static bool IsStorageChest(Piece piece, Container container)
    {
        if (container.m_autoDestroyEmpty) return false;
        if (IsObliterator(container)) return false;
        if (piece.GetComponent<Ship>() != null || piece.GetComponent<Vagon>() != null || container.m_wagon != null) return false;
        return true;
    }

    /// <summary>The Obliterator: anything put in it is destroyed.</summary>
    internal static bool IsObliterator(Container container) =>
        container.GetComponent<Incinerator>() != null || container.GetComponentInParent<Incinerator>() != null;

    /// <summary>
    /// Container.IsInUse() is only accurate on the client that controls the chest; the shared
    /// network copy says whether anyone at all has it open.
    /// </summary>
    private static bool IsOpenByAnyone(Container container, ZNetView nview)
    {
        if (container == null || nview == null || !nview.IsValid()) return true; // destroyed: hands off
        if (container.IsInUse()) return true;
        ZDO zdo = nview.GetZDO();
        return zdo != null && zdo.GetInt(ZDOVars.s_inUse, 0) == 1;
    }

    /// <summary>Loads the latest network copy of the chest, so we never act on stale contents.</summary>
    private static void Refresh(Container container)
    {
        if (CheckForChanges == null)
        {
            WarnOnce("refresh-missing", "Container.CheckForChanges not found (game update?). Chest contents may be up to a second stale.");
            return;
        }
        try
        {
            CheckForChanges.Invoke(container, null);
        }
        catch (Exception e)
        {
            WarnOnce("refresh", $"Refreshing a chest failed: {e.InnerException?.Message ?? e.Message}");
        }
    }

    /// <summary>Logs a warning the first time <paramref name="key"/> is seen, then stays quiet.</summary>
    internal static void WarnOnce(string key, string message)
    {
        if (LoggedOnce.Add(key)) Plugin.Log.LogWarning(message);
    }
}
