using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BuildStats;

/// <summary>
/// One-time count of items crafted before the Crafted tab existed. Crafted weapons, armor and
/// tools remember who made them and how far they were upgraded, so the count comes from items
/// that still exist: in chests, on item and armor stands, on the ground, and in players' bags.
/// Food, potions and anything sold, lost or broken can't be found.
/// </summary>
internal static class CraftBackfill
{
    /// <summary>Flag saved with the world's stats once the server has scanned the world.</summary>
    internal const string WorldFlag = "crafted-backfill";

    /// <summary>Flag saved with the world's stats once a player's inventory was counted.</summary>
    internal static string PlayerFlag(long playerId) => "crafted-backfill-player:" + playerId;

    // Static helpers on ItemDrop that read an item saved on an object (item stand, armor stand slot, dropped item).
    private static readonly MethodInfo LoadFromZdo = AccessTools.Method(typeof(ItemDrop), "LoadFromZDO", new[] { typeof(ItemDrop.ItemData), typeof(ZDO) });
    private static readonly MethodInfo LoadSlotFromZdo = AccessTools.Method(typeof(ItemDrop), "LoadFromZDO", new[] { typeof(int), typeof(ItemDrop.ItemData), typeof(ZDO) });

    private static readonly int ItemsKey = "items".GetStableHashCode();
    private static readonly int ItemKey = "item".GetStableHashCode();

    /// <summary>True once the item list needed to read saved items is loaded.</summary>
    internal static bool Ready => Backfill.Ready && ObjectDB.instance != null && ObjectDB.instance.m_items != null && ObjectDB.instance.m_items.Count > 0;

    /// <summary>Counts crafted items stored anywhere in the world. Each kind of object is counted on its own, so one that can't be read is skipped.</summary>
    internal static void ScanWorld(StatsStore store)
    {
        var objects = Backfill.WorldObjects();
        if (objects == null)
        {
            Plugin.Log.LogWarning("Could not read the world's objects; skipping the count of past crafts.");
            return;
        }

        int items = 0, failed = 0;
        foreach (object value in objects)
        {
            if (value is not ZDO zdo) continue;
            try
            {
                items += ScanObject(store, zdo);
            }
            catch (Exception)
            {
                failed++;
            }
        }
        Plugin.Log.LogInfo($"Counted {items} crafted items already in this world." + (failed > 0 ? $" {failed} objects could not be read and were skipped." : ""));
    }

    private static int ScanObject(StatsStore store, ZDO zdo)
    {
        int found = 0;

        // Chests, carts, ships, tombstones: a saved inventory.
        string saved = zdo.GetString(ItemsKey, "");
        if (!string.IsNullOrEmpty(saved))
        {
            var inventory = new Inventory("BuildStats scan", null, 64, 64);
            inventory.Load(new ZPackage(saved));
            foreach (ItemDrop.ItemData item in inventory.GetAllItems()) found += Count(store, item);
        }

        GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
        if (prefab == null) return found;

        // An item lying on the ground.
        if (prefab.TryGetComponent(out ItemDrop drop) && LoadFromZdo != null)
        {
            ItemDrop.ItemData item = drop.m_itemData.Clone();
            LoadFromZdo.Invoke(null, new object[] { item, zdo });
            found += Count(store, item);
        }

        // An item hung on an item stand.
        if (prefab.TryGetComponent(out ItemStand _) && LoadFromZdo != null)
            found += CountSaved(store, zdo, zdo.GetString(ItemKey, ""), item => LoadFromZdo.Invoke(null, new object[] { item, zdo }));

        // Armor and weapons on an armor stand, one per slot.
        if (prefab.TryGetComponent(out ArmorStand stand) && LoadSlotFromZdo != null && stand.m_slots != null)
            for (int i = 0; i < stand.m_slots.Count; i++)
            {
                int slot = i;
                found += CountSaved(store, zdo, zdo.GetString(slot + "_item", ""), item => LoadSlotFromZdo.Invoke(null, new object[] { slot, item, zdo }));
            }

        return found;
    }

    private static int CountSaved(StatsStore store, ZDO zdo, string itemName, Action<ItemDrop.ItemData> load)
    {
        if (string.IsNullOrEmpty(itemName)) return 0;
        GameObject itemPrefab = ObjectDB.instance.GetItemPrefab(itemName);
        if (itemPrefab == null || !itemPrefab.TryGetComponent(out ItemDrop drop)) return 0;
        ItemDrop.ItemData item = drop.m_itemData.Clone();
        load(item);
        return Count(store, item);
    }

    /// <summary>Counts what a player carries. Runs in the player's game; the server adds it once per player.</summary>
    internal static StatsStore ScanInventory(Player player)
    {
        var store = new StatsStore();
        Inventory inventory = player?.GetInventory();
        if (inventory == null) return store;
        foreach (ItemDrop.ItemData item in inventory.GetAllItems()) Count(store, item);
        return store;
    }

    /// <summary>Adds one item to its crafter's counts, plus its upgrades. Returns 1 if it was crafted by a player.</summary>
    private static int Count(StatsStore store, ItemDrop.ItemData item)
    {
        if (item?.m_shared == null || item.m_crafterID == 0L) return 0;
        string crafter = !string.IsNullOrEmpty(item.m_crafterName) ? item.m_crafterName : Backfill.IdPrefix + item.m_crafterID;
        store.Add(crafter, Category.Crafted, Tracking.Group(item.m_shared.m_itemType.ToString()), Math.Max(1, item.m_stack));
        // Quality 1 is as crafted; each level above it was an upgrade. Credited to the crafter, the best guess there is.
        if (item.m_quality > 1) store.Add(crafter, Category.Crafted, "Upgrades", item.m_quality - 1);
        return 1;
    }
}
