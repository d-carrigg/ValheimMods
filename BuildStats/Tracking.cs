using System;
using HarmonyLib;

namespace BuildStats;

/// <summary>Counts pieces the local player places and the materials they spend.</summary>
[HarmonyPatch]
internal static class Tracking
{
    [HarmonyPatch(typeof(Player), "PlacePiece"), HarmonyPostfix]
    private static void OnPlacePiece(Player __instance, object[] __args) => Guard.Run("counting a placed piece", () => CountPiece(__instance, __args));

    private static void CountPiece(Player __instance, object[] __args)
    {
        // Arguments by position, so a renamed parameter in a game update doesn't break the hook.
        if (__instance != Player.m_localPlayer || __args.Length == 0 || __args[0] is not Piece piece) return;
        // Hoe and pickaxe terrain edits are pieces too, but cost nothing; they aren't "built".
        if (piece.m_resources == null || piece.m_resources.Length == 0) return;
        StatsSync.Record(Category.Built, piece.m_name, 1);
    }

    // What InventoryGui.DoCrafting is making, while it runs. Private in the game.
    private static readonly AccessTools.FieldRef<InventoryGui, Recipe> CraftRecipe = AccessTools.FieldRefAccess<InventoryGui, Recipe>("m_craftRecipe");
    private static readonly AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData> CraftUpgradeItem = AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_craftUpgradeItem");
    private static Recipe _crafting;
    private static bool _upgrading;

    [HarmonyPatch(typeof(InventoryGui), "DoCrafting"), HarmonyPrefix]
    private static void BeforeCrafting(InventoryGui __instance) => Guard.Run("noting what is being crafted", () =>
    {
        _crafting = CraftRecipe(__instance);
        _upgrading = CraftUpgradeItem(__instance) != null;
    });

    // A finalizer runs even if crafting throws, so a stale recipe never sticks around.
    [HarmonyPatch(typeof(InventoryGui), "DoCrafting"), HarmonyFinalizer]
    private static void AfterCrafting() => _crafting = null;

    [HarmonyPatch(typeof(Player), "ConsumeResources"), HarmonyPrefix]
    private static void OnConsumeResources(Player __instance, object[] __args) => Guard.Run("counting used resources", () => CountResources(__instance, __args));

    private static void CountResources(Player __instance, object[] __args)
    {
        if (__instance != Player.m_localPlayer || __instance.NoCostCheat()) return;
        if (__args.Length < 2 || __args[0] is not Piece.Requirement[] requirements || __args[1] is not int qualityLevel) return;
        int multiplier = __args.Length > 3 && __args[3] is int m ? m : 1;
        // Crafting and upgrading happen with the inventory open; placing pieces with it closed.
        bool crafting = InventoryGui.IsVisible();
        if (crafting && !Plugin.CountCrafting.Value) return;
        Category category = crafting ? Category.CraftMaterials : Category.BuildMaterials;
        foreach (Piece.Requirement req in requirements)
        {
            if (req?.m_resItem == null) continue;
            int amount = req.GetAmount(qualityLevel) * (multiplier < 1 ? 1 : multiplier);
            StatsSync.Record(category, req.m_resItem.m_itemData.m_shared.m_name, amount);
        }

        // Resources are only taken once the craft goes through, so this is a finished craft.
        if (_crafting?.m_item != null)
        {
            string group = _upgrading ? "Upgrades" : Group(_crafting.m_item.m_itemData.m_shared.m_itemType.ToString());
            int count = _upgrading ? 1 : Math.Max(1, _crafting.m_amount) * Math.Max(1, multiplier);
            StatsSync.Record(Category.Crafted, group, count);
        }
    }

    /// <summary>Puts an item type into one of the groups on the Crafted tab. By name, so new types land in "Other".</summary>
    internal static string Group(string itemType) => itemType switch
    {
        "OneHandedWeapon" or "TwoHandedWeapon" or "TwoHandedWeaponLeft" or "Bow" or "Attach_Atgeir" => "Weapons",
        "Shield" => "Shields",
        "Helmet" or "Chest" or "Legs" or "Shoulder" or "Hands" => "Armor",
        "Ammo" or "AmmoNonEquipable" => "Ammo",
        "Tool" or "Torch" => "Tools",
        "Consumable" => "Food & potions",
        "Utility" or "Trinket" => "Utility & trinkets",
        _ => "Other",
    };
}
