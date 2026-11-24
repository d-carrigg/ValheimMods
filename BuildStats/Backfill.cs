using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace BuildStats;

/// <summary>
/// One-time count of pieces that were already built before BuildStats was installed. Runs on the
/// server the first time it sees a world (no stats file yet). Each piece remembers who placed it
/// by player id; the id becomes a name the first time that player joins with BuildStats.
/// </summary>
internal static class Backfill
{
    /// <summary>Prefix for stats whose player is known only by id so far.</summary>
    internal const string IdPrefix = "id:";

    // Private in the game.
    private static readonly System.Reflection.FieldInfo ObjectsField = AccessTools.Field(typeof(ZDOMan), "m_objectsByID");

    /// <summary>True once the game objects needed for the scan exist.</summary>
    internal static bool Ready => ZDOMan.instance != null && ZNetScene.instance != null;

    /// <summary>Adds every placed piece in the world, and the materials it cost, to <paramref name="store"/>.</summary>
    internal static void Run(StatsStore store)
    {
        if (ObjectsField?.GetValue(ZDOMan.instance) is not IDictionary objects)
        {
            Plugin.Log.LogWarning("Could not read the world's objects; skipping the count of existing builds.");
            return;
        }

        int pieces = 0;
        foreach (object value in objects.Values)
        {
            if (value is not ZDO zdo) continue;
            long creator = zdo.GetLong("creator");
            if (creator == 0L) continue; // not placed by a player (dungeons, villages, ...)
            GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            Piece piece = prefab != null ? prefab.GetComponent<Piece>() : null;
            if (piece == null || piece.m_resources == null || piece.m_resources.Length == 0) continue;

            string player = IdPrefix + creator;
            store.Add(player, Category.Built, piece.m_name, 1);
            foreach (Piece.Requirement req in piece.m_resources)
                if (req?.m_resItem != null)
                    store.Add(player, Category.BuildMaterials, req.m_resItem.m_itemData.m_shared.m_name, req.m_amount);
            pieces++;
        }
        Plugin.Log.LogInfo($"Counted {pieces} pieces already built in this world.");
    }
}
