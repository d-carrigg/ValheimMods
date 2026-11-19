using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace CollectAll;

/// <summary>BepInEx entry point: reads the config and installs the Harmony patches.</summary>
[BepInPlugin(Guid, Name, Version)]
public class Plugin : BaseUnityPlugin
{
    /// <summary>BepInEx plugin id.</summary>
    public const string Guid = "dcarrigg.collectall";
    /// <summary>Display name.</summary>
    public const string Name = "CollectAll";
    /// <summary>Plugin version; keep in step with the Thunderstore manifest.</summary>
    public const string Version = "1.0.5";

    internal static ManualLogSource Log;

    internal static ConfigEntry<float> Radius;
    internal static ConfigEntry<bool> AltClickShortcut;
    internal static ConfigEntry<bool> SkipBiggerPiles;
    internal static ConfigEntry<bool> SkipFullChests;
    internal static ConfigEntry<bool> ClaimChestOwnership;
    internal static ConfigEntry<string> ButtonText;
    internal static ConfigEntry<float> ButtonOffsetX;
    internal static ConfigEntry<float> ButtonOffsetY;

    private Harmony _harmony;

    private void Awake()
    {
        Log = Logger;

        Radius = Config.Bind("General", "Radius", 60f, new ConfigDescription(
            "How far (meters) from the open chest to look for more of the item.",
            new AcceptableValueRange<float>(2f, 100f)));
        AltClickShortcut = Config.Bind("General", "AltClickShortcut", true,
            "Alt + click an item in the chest to collect it straight away, without pressing the button first.");
        SkipBiggerPiles = Config.Bind("General", "SkipBiggerPiles", false,
            "Bigger pile wins: never take from a chest that holds more of the item than the chest you're collecting " +
            "into. Items only flow toward your biggest pile, so collecting in two chests can't shuffle them back and " +
            "forth. Turn off to pull from every chest regardless (e.g. to move a whole stash into a new chest).");
        SkipFullChests = Config.Bind("General", "SkipFullChests", true,
            "Never take from a chest that is completely full and holds nothing but that item (a finished stash). " +
            "A chest with any other item, a partial stack or an empty slot is still collected from. " +
            "Works on its own or together with SkipBiggerPiles.");
        ClaimChestOwnership = Config.Bind("General", "ClaimChestOwnership", false,
            "Multiplayer only. Off: only chests your game controls are collected from (always true in single player, " +
            "and usually when nobody else is nearby). On: also take control of other chests that hold the item (never " +
            "ones someone has open). Risky with lag: if a friend changed that chest a moment ago and it hasn't reached " +
            "you yet, items can be duplicated or lost.");
        ButtonText = Config.Bind("Button", "Text", "Collect", "Label on the button in the chest window.");
        ButtonOffsetX = Config.Bind("Button", "OffsetX", 0f, "Nudge the button sideways (pixels) if it overlaps something, e.g. another mod's button.");
        ButtonOffsetY = Config.Bind("Button", "OffsetY", 0f, "Nudge the button up/down (pixels).");

        ButtonText.SettingChanged += (_, __) => CollectUi.OnSettingsChanged();
        ButtonOffsetX.SettingChanged += (_, __) => CollectUi.OnSettingsChanged();
        ButtonOffsetY.SettingChanged += (_, __) => CollectUi.OnSettingsChanged();

        try
        {
            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{Name} {Version} loaded");
        }
        catch (Exception e)
        {
            // Most likely a game update renamed something we hook. The vanilla chest window is unaffected.
            Log.LogError($"{Name} could not hook into the chest window (game update?) and is disabled: {e}");
            try { _harmony?.UnpatchSelf(); } catch { }
        }
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }
}
