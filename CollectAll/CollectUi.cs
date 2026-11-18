using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CollectAll
{
    /// <summary>
    /// Adds a "Collect" button to the chest window. Click it, then click an item: every other
    /// stack of that item in nearby chests is moved into the open chest. Alt+click an item does
    /// the same in one step.
    /// </summary>
    [HarmonyPatch]
    internal static class CollectUi
    {
        private static readonly FieldInfo CurrentContainerField = AccessTools.Field(typeof(InventoryGui), "m_currentContainer");
        private static readonly FieldInfo ContainerGridField = AccessTools.Field(typeof(InventoryGui), "m_containerGrid");
        private static readonly FieldInfo DragItemField = AccessTools.Field(typeof(InventoryGui), "m_dragItem");

        private static Button _button;
        private static TMP_Text _label;
        private static Color _labelColor = Color.white;
        private static bool _pickMode;

        // ------------------------------------------------------------------ the button

        // Helpers copied along from the "Place stacks" button that would fight our changes:
        // re-translating the label, showing its gamepad key / tooltip, resetting the text colour.
        private static readonly string[] StripComponents = { "Localize", "UIGamePad", "UIInputHint", "UITooltip", "ButtonTextColor" };

        // Fallback size if the template button hasn't been laid out yet.
        private static Vector2 _buttonSize = new Vector2(110f, 34f);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryGui), "Awake")]
        private static void AddButton(InventoryGui __instance)
        {
            try
            {
                Button template = __instance.m_stackAllButton != null ? __instance.m_stackAllButton : __instance.m_takeAllButton;
                if (template == null || __instance.m_container == null)
                {
                    Plugin.Log.LogError("Chest window not found (game update?); the Collect button is not available. Alt+click still works.");
                    return;
                }

                Rect size = ((RectTransform)template.transform).rect;
                if (size.width > 20f && size.height > 10f) _buttonSize = new Vector2(Mathf.Min(size.width, 140f), size.height);

                GameObject go = Object.Instantiate(template.gameObject, __instance.m_container);
                go.name = "CollectAllButton";
                go.SetActive(true); // another mod may have hidden the original

                foreach (Component c in go.GetComponentsInChildren<Component>(true))
                {
                    if (c != null && Array.IndexOf(StripComponents, c.GetType().Name) >= 0) Object.DestroyImmediate(c);
                }
                foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                {
                    if (t != go.transform && IsHint(t, go.transform)) t.gameObject.SetActive(false);
                }

                _button = go.GetComponent<Button>();
                _button.onClick = new Button.ButtonClickedEvent(); // drop the copied "Place stacks" action
                _button.onClick.AddListener(OnButtonClicked);

                _label = null;
                foreach (TMP_Text text in go.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (!IsHint(text.transform, go.transform)) { _label = text; break; }
                }
                if (_label != null) _labelColor = _label.color;
                UpdateLabel();
                Pin(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not add the Collect button: {e}");
            }
        }

        private static bool IsHint(Transform t, Transform root)
        {
            for (Transform p = t; p != null && p != root; p = p.parent)
                if (p.name.IndexOf("hint", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        internal static void OnSettingsChanged()
        {
            UpdateLabel();
            if (InventoryGui.instance != null) Pin(InventoryGui.instance);
        }

        /// <summary>
        /// Pins the button to the top-right corner of the chest panel (the header, next to the
        /// chest's name). Anchored to the panel itself, so it doesn't depend on where the game or
        /// other mods (QuickStackStore etc.) put their buttons, and it follows the panel for every
        /// chest size. Set once; never moved per frame.
        /// </summary>
        private static void Pin(InventoryGui gui)
        {
            if (_button == null || gui == null || gui.m_container == null) return;
            var rt = (RectTransform)_button.transform;
            if (rt.parent != gui.m_container) rt.SetParent(gui.m_container, false);

            // If the panel uses automatic layout, keep it from rearranging us.
            LayoutElement layout = rt.GetComponent<LayoutElement>();
            if (layout == null) layout = rt.gameObject.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;

            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
            rt.sizeDelta = _buttonSize;
            rt.anchoredPosition = new Vector2(-16f + Plugin.ButtonOffsetX.Value, -12f + Plugin.ButtonOffsetY.Value);
            rt.SetAsLastSibling(); // drawn on top of the panel background
        }
        private static void OnButtonClicked()
        {
            InventoryGui gui = InventoryGui.instance;
            if (gui == null || !InventoryGui.IsVisible() || !gui.IsContainerOpen()) return;
            if (DragItemField?.GetValue(gui) != null) return; // holding an item: let the normal drop happen first

            _pickMode = !_pickMode;
            UpdateLabel();
            if (_pickMode)
                Message("Click an item to collect all of it into this chest");
        }

        private static void UpdateLabel()
        {
            if (_label == null) return;
            _label.text = _pickMode ? "Pick item..." : Plugin.ButtonText.Value;
            _label.color = _pickMode ? new Color(1f, 0.82f, 0.35f) : _labelColor;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryGui), "Update")]
        private static void EveryFrame(InventoryGui __instance)
        {

            if (!_pickMode) return;
            // Leave pick mode when the chest closes or on Escape (right-click is handled below).
            if (!InventoryGui.IsVisible() || !__instance.IsContainerOpen() || Input.GetKeyDown(KeyCode.Escape))
            {
                _pickMode = false;
                UpdateLabel();
            }
        }

        // In pick mode a right-click only cancels; otherwise it would also eat/equip the item under the cursor.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryGui), "OnRightClickItem")]
        private static bool OnRightClickItem()
        {
            if (!_pickMode) return true;
            _pickMode = false;
            UpdateLabel();
            return false;
        }
        // ------------------------------------------------------------------ clicking an item

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryGui), "OnSelectedItem")]
        private static bool OnSelectedItem(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item)
        {
            try
            {
                bool altClick = Plugin.AltClickShortcut.Value
                    && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
                    && grid != null && ReferenceEquals(grid, ContainerGridField?.GetValue(__instance));
                if (!_pickMode && !altClick) return true; // normal click: game handles it

                // Holding an item already: let the game finish placing it.
                if (DragItemField?.GetValue(__instance) != null)
                {
                    _pickMode = false;
                    UpdateLabel();
                    return true;
                }
                // Empty slot: in pick mode, ignore the click and keep waiting; an Alt+click on an
                // empty slot is just a normal click.
                if (item == null) return !_pickMode;

                var target = CurrentContainerField?.GetValue(__instance) as Container;
                _pickMode = false;
                UpdateLabel();
                if (target == null) return true;

                CollectInto(target, item);
                return false; // don't also pick the item up
            }
            catch (Exception e)
            {
                _pickMode = false;
                UpdateLabel();
                Plugin.Log.LogError($"Collect failed: {e}");
                return true;
            }
        }

        private static void CollectInto(Container target, ItemDrop.ItemData example)
        {
            Player player = Player.m_localPlayer;
            if (player == null) return;

            ZNetView view = target.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || !view.IsOwner())
            {
                Message("Can't collect into this chest right now");
                return;
            }

            if (target.GetComponent<Incinerator>() != null || target.GetComponentInParent<Incinerator>() != null)
            {
                Message("Not into the Obliterator!");
                return;
            }

            ChestFinder.Found found = ChestFinder.ChestsAround(target, player, Plugin.Radius.Value, example.m_shared.m_name);
            CollectResult result = Collector.Collect(target.GetInventory(), example, found.Sources,
                (what, e) => Plugin.Log.LogWarning($"{what}: {e.GetType().Name}: {e.Message}"),
                Plugin.SkipBiggerPiles.Value);

            Message(Describe(result, found.NotOwned, Plugin.Radius.Value, Localize(result.ItemName)));
            if (result.Moved > 0)
                Plugin.Log.LogInfo($"Collected {result.Moved}x {result.ItemName} from {result.ChestsTakenFrom} chests");
        }

        internal static string Describe(CollectResult r, int notOwned, float radius, string name)
        {
            string text;
            if (r.Moved > 0)
            {
                text = $"Collected {r.Moved} {name} from {r.ChestsTakenFrom} chest{(r.ChestsTakenFrom == 1 ? "" : "s")}";
                if (r.TargetFull) text += $"\nChest is full - {r.LeftBehind} more still elsewhere";
            }
            else if (r.TargetFull)
            {
                text = $"No room in this chest for more {name}";
            }
            else if (r.BiggerSkipped > 0)
            {
                text = $"Other chests have more {name} than this one\nCollect into the biggest pile instead";
            }
            else
            {
                text = $"No other {name} in chests within {radius:0} m";
            }

            if (r.Moved > 0 && r.BiggerSkipped > 0)
                text += $"\n{r.BiggerSkipped} chest{(r.BiggerSkipped == 1 ? "" : "s")} with more {name} left alone";

            int skipped = r.BusySkipped + notOwned;
            if (skipped > 0) text += $"\n{skipped} chest{(skipped == 1 ? "" : "s")} skipped (open, or another player's)";
            if (r.Errors > 0) text += "\nSomething went wrong - see BepInEx log (item counts were checked)";
            return text;
        }

        private static string Localize(string token)
        {
            try
            {
                return Localization.instance != null ? Localization.instance.Localize(token) : token;
            }
            catch
            {
                return token;
            }
        }

        private static void Message(string text)
        {
            Player player = Player.m_localPlayer;
            if (player != null) player.Message(MessageHud.MessageType.Center, text);
        }
    }
}
