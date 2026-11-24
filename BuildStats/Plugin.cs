using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BuildStats;

/// <summary>BepInEx entry point: reads the config, installs the Harmony patches and drives sync and UI.</summary>
[BepInPlugin(Guid, Name, Version)]
public class Plugin : BaseUnityPlugin
{
    /// <summary>BepInEx plugin id.</summary>
    public const string Guid = "dcarrigg.buildstats";
    /// <summary>Display name.</summary>
    public const string Name = "BuildStats";
    /// <summary>Plugin version; keep in step with the Thunderstore manifest.</summary>
    public const string Version = "1.2.0";

    internal static ManualLogSource Log;

    internal static ConfigEntry<KeyCode> ToggleKey;
    internal static ConfigEntry<bool> CountCrafting;

    private Harmony _harmony;

    private void Awake()
    {
        Log = Logger;

        ToggleKey = Config.Bind("General", "ToggleKey", KeyCode.F7, "Key that opens and closes the stats window.");
        CountCrafting = Config.Bind("General", "CountCrafting", true,
            "Also count materials used at crafting stations (tools, weapons, armor, food, upgrades). " +
            "Applies to what this player sends; the server keeps whatever it receives.");

        _harmony = new Harmony(Guid);
        // Patch each class on its own so a game update that breaks one hook leaves the rest working.
        foreach (Type t in typeof(Plugin).Assembly.GetTypes())
        {
            if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            try
            {
                _harmony.CreateClassProcessor(t).Patch();
            }
            catch (Exception e)
            {
                Log.LogError($"{Name}: could not hook {t.Name} (game update?); that part is disabled. {e.Message}");
            }
        }
        Log.LogInfo($"{Name} {Version} loaded");
    }

    private bool _syncFailed;

    // Separate try blocks: if syncing breaks, the window still opens with what this game recorded.
    private void Update()
    {
        try
        {
            StatsSync.Tick();
        }
        catch (Exception e)
        {
            if (!_syncFailed) Log.LogError($"Stats sync failed: {e}");
            _syncFailed = true;
        }
        Guard.Run("the stats window hotkey", StatsWindow.Tick);
    }

    private void OnGUI() => Guard.Run("drawing the stats window", StatsWindow.Draw);

    private void OnApplicationQuit() => StatsSync.SaveNow();

    private void OnDestroy()
    {
        StatsSync.SaveNow();
        _harmony?.UnpatchSelf();
    }
}
