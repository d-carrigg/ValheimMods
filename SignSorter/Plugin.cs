using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace SignSorter
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "local.signsorter";
        public const string Name = "SignSorter";
        public const string Version = "2.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> AutoSortInterval;
        internal static ConfigEntry<bool> AutoSortNeedsFirstManualSort;
        internal static ConfigEntry<bool> AutoSortOnlyMyChests;
        internal static ConfigEntry<float> ScanRadius;
        internal static ConfigEntry<float> SignRange;
        internal static ConfigEntry<float> ArrowRange;
        internal static ConfigEntry<bool> FlipLeftRight;
        internal static ConfigEntry<bool> SortFromUnlabeledChests;
        internal static ConfigEntry<bool> AllowSwaps;
        internal static ConfigEntry<bool> MergeStacks;
        internal static ConfigEntry<bool> TypoTolerance;
        internal static ConfigEntry<string> IgnoreTag;
        internal static ConfigEntry<bool> ClaimChestOwnership;
        internal static ConfigEntry<KeyboardShortcut> SortKey;
        internal static ConfigEntry<KeyboardShortcut> DeepSortKey;
        internal static ConfigEntry<KeyboardShortcut> UndoKey;
        internal static ConfigEntry<float> DeepSortRadius;
        internal static ConfigEntry<KeyboardShortcut> DepositKey;
        internal static ConfigEntry<bool> DepositIncludesHotbar;
        internal static ConfigEntry<bool> ShowMessages;
        internal static ConfigEntry<bool> VerboseLogging;

        internal static MatchSettings Settings = new MatchSettings();
        internal static List<string> KnownNames;

        private float _nextAutoSort;
        private float _nextAliasCheck;
        private int _consecutiveErrors;
        private bool _autoPausedByUndo;
        private string _hintShownForWorld;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Turn the mod on or off.");
            AutoSortInterval = Config.Bind("General", "AutoSortInterval", 5f, new ConfigDescription(
                "Seconds between automatic sorts of the chests around you. 0 turns automatic sorting off (hotkeys still work).",
                new AcceptableValueRange<float>(0f, 600f)));
            AutoSortNeedsFirstManualSort = Config.Bind("General", "AutoSortNeedsFirstManualSort", true,
                "Automatic sorting only starts in a world after you've sorted it by hand once (F8 or Shift+F8). " +
                "Protects existing worlds from being reshuffled the moment you load in.");
            AutoSortOnlyMyChests = Config.Bind("General", "AutoSortOnlyMyChests", false,
                "Automatic sorting only touches chests you built (hotkeys still sort any chest you can open). " +
                "Useful on public servers.");
            ScanRadius = Config.Bind("General", "ScanRadius", 20f, new ConfigDescription(
                "How far from you (meters) chests are sorted automatically and with the sort hotkey.",
                new AcceptableValueRange<float>(2f, 100f)));
            ShowMessages = Config.Bind("General", "ShowMessages", true, "Show a message in the corner when items are sorted.");
            VerboseLogging = Config.Bind("General", "VerboseLogging", false, "Log which signs label which chests on hotkey sorts (BepInEx/LogOutput.log).");

            SignRange = Config.Bind("Signs", "SignRange", 2.5f, new ConfigDescription(
                "How close (meters) a plain sign must be to a chest to label it. A sign labels the one chest closest to it.",
                new AcceptableValueRange<float>(0.5f, 10f)));
            ArrowRange = Config.Bind("Signs", "ArrowRange", 4f, new ConfigDescription(
                "How far (meters) an arrow sign (< Wood, Wood >, ^ Wood, v Wood) can point to reach a chest.",
                new AcceptableValueRange<float>(0.5f, 10f)));
            FlipLeftRight = Config.Bind("Signs", "FlipLeftRight", false,
                "If < and > signs pick the chest on the wrong side, turn this on.");
            TypoTolerance = Config.Bind("Signs", "TypoTolerance", true,
                "Let a misspelled sign entry (Resinn, Fine wod) match the one item it's closest to. Never applies when the " +
                "entry is already a real item name, or when two items are equally close.");
            IgnoreTag = Config.Bind("Signs", "IgnoreTag", "nosort",
                "A chest whose sign contains this word is never touched. (Signs that name no items at all, like " +
                "\"Bob's stuff\", also leave their chest alone.)");

            SortFromUnlabeledChests = Config.Bind("Sorting", "SortFromUnlabeledChests", true,
                "Take items out of chests with no sign when another chest's sign names them. (Only named items: " +
                "never for * or category chests, and never your personal chests.)");
            AllowSwaps = Config.Bind("Sorting", "AllowSwaps", true,
                "When the right chest is full of things that belong in this one, trade stacks so both end up in the right place.");
            MergeStacks = Config.Bind("Sorting", "MergeStacks", true, "Combine partial stacks of the same item in labeled chests.");
            ClaimChestOwnership = Config.Bind("Sorting", "ClaimChestOwnership", false,
                "Multiplayer only. Off: only chests your game controls are sorted (always true in single player, and usually " +
                "when you're the only one nearby). On: hotkey sorts take control of other chests too (never ones someone has " +
                "open). Can lose a friend's very recent change if you both touch a chest at the same moment.");

            SortKey = Config.Bind("Hotkeys", "SortNow", new KeyboardShortcut(KeyCode.F8), "Sort the chests around you right now.");
            DeepSortKey = Config.Bind("Hotkeys", "DeepSort", new KeyboardShortcut(KeyCode.F8, KeyCode.LeftShift),
                "Sort your whole base: bigger radius, repeats until settled. Use after setting up signs in an existing world.");
            UndoKey = Config.Bind("Hotkeys", "UndoLastSort", new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl),
                "Put back everything the last F8 / Shift+F8 sort moved (as long as those chests haven't changed since).");
            DeepSortRadius = Config.Bind("Hotkeys", "DeepSortRadius", 60f, new ConfigDescription(
                "Radius (meters) for the deep sort. Chests farther than the game keeps loaded (~64 m) can't be reached.",
                new AcceptableValueRange<float>(5f, 120f)));
            DepositKey = Config.Bind("Hotkeys", "DepositFromInventory", new KeyboardShortcut(KeyCode.F9),
                "Put items from your inventory into chests whose signs name them (not category or * chests).");
            DepositIncludesHotbar = Config.Bind("Hotkeys", "DepositIncludesHotbar", false,
                "Also deposit items in your hotbar (top inventory row). Equipped items are never deposited.");

            TypoTolerance.SettingChanged += (_, __) => RebuildSettings();
            IgnoreTag.SettingChanged += (_, __) => RebuildSettings();

            ItemKeys.Localize = LocalizeSafe;
            AliasFile.Init(Path.Combine(Paths.ConfigPath, "SignSorter.aliases.txt"));
            WorldOptIn.Init(Path.Combine(Paths.ConfigPath, "SignSorter.worlds.txt"));
            RebuildSettings();

            Log.LogInfo($"{Name} {Version} loaded. Aliases: {AliasFile.FilePath}");
        }

        internal static void RebuildSettings()
        {
            Settings = new MatchSettings
            {
                Aliases = AliasFile.Current,
                TypoTolerance = TypoTolerance?.Value ?? true,
                IgnoreTag = IgnoreTag?.Value ?? "nosort",
                KnownNames = KnownNames,
            };
            SignFilter.ClearCache();
        }

        private static string LocalizeSafe(string token)
        {
            Localization loc = Localization.instance;
            return loc != null ? loc.Localize(token) : null;
        }

        private void Update()
        {
            if (Enabled == null || !Enabled.Value) return;

            Player player = Player.m_localPlayer;
            if (player == null || ZNet.instance == null || player.IsDead() || player.IsTeleporting()) return;

            try
            {
                if (Time.time >= _nextAliasCheck)
                {
                    _nextAliasCheck = Time.time + 2f;
                    if (AliasFile.ReloadIfChanged()) RebuildSettings();
                }

                if (!IsTyping())
                {
                    // Most specific first: Ctrl+F8 and Shift+F8 would otherwise also count as F8.
                    if (Pressed(UndoKey.Value)) { RunUndo(player); return; }
                    if (Pressed(DeepSortKey.Value)) { RunSort(player, SortMode.Deep); return; }
                    if (Pressed(DepositKey.Value)) { RunSort(player, SortMode.Deposit); return; }
                    if (Pressed(SortKey.Value)) { RunSort(player, SortMode.Manual); return; }
                }

                float interval = AutoSortInterval.Value;
                if (interval > 0f && Time.time >= _nextAutoSort)
                {
                    _nextAutoSort = Time.time + Mathf.Max(1f, interval);
                    RunAuto(player);
                }
            }
            catch (Exception e)
            {
                // Back off harder each time so a persistent problem can't flood the log or stutter the game.
                _consecutiveErrors++;
                float backoff = Mathf.Min(300f, 10f * _consecutiveErrors);
                _nextAutoSort = Time.time + backoff;
                if (_consecutiveErrors <= 5) Log.LogError($"Sorting failed (retrying in {backoff:0}s): {e}");
                else if (_consecutiveErrors == 6) Log.LogError("Sorting keeps failing; further errors are not logged.");
            }
        }

        /// <summary>
        /// Like KeyboardShortcut.IsDown(), but still works while you're holding movement keys:
        /// the main key was just pressed and every modifier is held.
        /// </summary>
        private static bool Pressed(KeyboardShortcut shortcut)
        {
            if (shortcut.MainKey == KeyCode.None || !Input.GetKeyDown(shortcut.MainKey)) return false;
            foreach (KeyCode modifier in shortcut.Modifiers)
                if (!Input.GetKey(modifier) && !Input.GetKey(OtherSide(modifier))) return false;
            return true;
        }

        private static KeyCode OtherSide(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftShift: return KeyCode.RightShift;
                case KeyCode.RightShift: return KeyCode.LeftShift;
                case KeyCode.LeftControl: return KeyCode.RightControl;
                case KeyCode.RightControl: return KeyCode.LeftControl;
                case KeyCode.LeftAlt: return KeyCode.RightAlt;
                case KeyCode.RightAlt: return KeyCode.LeftAlt;
                default: return key;
            }
        }

        private static bool IsTyping()
        {
            try
            {
                return (Chat.instance != null && Chat.instance.HasFocus())
                    || global::Console.IsVisible()
                    || TextInput.IsVisible()
                    || Menu.IsVisible()
                    || Minimap.IsOpen();
            }
            catch
            {
                return false;
            }
        }

        private void RunAuto(Player player)
        {
            if (_autoPausedByUndo) return;

            string world = WorldOptIn.CurrentWorldKey();
            if (AutoSortNeedsFirstManualSort.Value && !WorldOptIn.IsEnabled(world))
            {
                // Not switched on for this world yet: look, don't touch, and say so once.
                if (_hintShownForWorld == world) return;
                SortReport preview = Sorter.Preview(player);
                if (preview.LabeledChests == 0) return;
                _hintShownForWorld = world;
                if (preview.ItemsMoved > 0 && ShowMessages.Value)
                {
                    player.Message(MessageHud.MessageType.Center,
                        $"SignSorter: {Plural(preview.ItemsMoved, "item")} here can be sorted.\nPress Shift+F8 to sort (Ctrl+F8 undoes). Auto-sort starts after that.");
                }
                return;
            }

            SortReport report = Sorter.Run(player, SortMode.Auto);
            Report(player, report, SortMode.Auto);
            _consecutiveErrors = 0;
        }

        private void RunSort(Player player, SortMode mode)
        {
            SortReport report = Sorter.Run(player, mode);
            _consecutiveErrors = 0;
            _nextAutoSort = Time.time + Mathf.Max(1f, AutoSortInterval.Value); // don't follow up with an auto run straight away

            if (mode == SortMode.Manual || mode == SortMode.Deep)
            {
                _autoPausedByUndo = false;
                if (report.LabeledChests > 0) WorldOptIn.Enable(WorldOptIn.CurrentWorldKey());
            }
            Report(player, report, mode);
        }

        private void RunUndo(Player player)
        {
            string result = Sorter.Undo();
            if (result.StartsWith("Undid")) _autoPausedByUndo = true; // otherwise auto-sort would just redo it
            Log.LogInfo(result);
            player.Message(MessageHud.MessageType.TopLeft, result);
        }

        private static void Report(Player player, SortReport report, SortMode mode)
        {
            if (report.AnythingHappened)
                Log.LogInfo($"{mode}: moved {report.ItemsMoved} items, {report.Swaps} swaps, {report.StacksMerged} stacks merged, {report.ChestsChanged} chests changed");
            if (report.Errors > 0)
                Log.LogWarning($"{mode}: {report.Errors} problems while sorting (see warnings above). Item counts were checked and repaired after each one.");

            if (!ShowMessages.Value) return;
            if (mode == SortMode.Auto && !report.AnythingHappened) return;

            List<string> lines = BuildMessages(report, mode);
            if (lines.Count > 0)
                player.Message(MessageHud.MessageType.TopLeft, string.Join("\n", lines.ToArray()));
        }

        internal static List<string> BuildMessages(SortReport report, SortMode mode)
        {
            var lines = new List<string>();
            bool manual = mode != SortMode.Auto;

            if (report.AnythingHappened)
            {
                string verb = mode == SortMode.Deposit ? "Deposited" : "Sorted";
                string line = $"{verb} {Plural(report.ItemsMoved, "item")} into {Plural(report.ChestsChanged, "chest")}";
                if (report.Swaps > 0) line += $", {Plural(report.Swaps, "swap")}";
                if (report.StacksMerged > 0) line += $", merged {Plural(report.StacksMerged, "stack")}";
                if (mode == SortMode.Manual || mode == SortMode.Deep) line += " (Ctrl+F8 to undo)";
                lines.Add(line);
            }
            else if (manual)
            {
                if (report.LabeledChests == 0) lines.Add("No labeled chests nearby - put a sign next to a chest");
                else if (mode == SortMode.Deposit) lines.Add("Nothing to deposit (no sign names these items, or those chests are full)");
                else lines.Add("Everything is already sorted");
            }

            if (!manual) return lines;

            if (report.UnknownEntries.Count > 0)
                lines.Add("No item called: " + Shorten(report.UnknownEntries) + " (add it to SignSorter.aliases.txt?)");
            if (report.Leftovers.Count > 0 && mode != SortMode.Deposit)
                lines.Add("No sign wants: " + Shorten(report.Leftovers) + " (add a sign, or a * chest)");
            if (report.NotOwned > 0)
                lines.Add($"{Plural(report.NotOwned, "labeled chest")} skipped - another player controls them");
            if (report.Errors > 0)
                lines.Add($"{Plural(report.Errors, "problem")} while sorting - see BepInEx log");
            return lines;
        }

        private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}{(word.EndsWith("h") ? "es" : "s")}";

        private static string Shorten(List<string> items)
        {
            const int max = 5;
            string shown = string.Join(", ", items.Take(max).ToArray());
            return items.Count > max ? $"{shown} +{items.Count - max} more" : shown;
        }
    }
}
