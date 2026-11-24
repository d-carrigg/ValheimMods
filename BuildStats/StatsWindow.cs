using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BuildStats;

/// <summary>
/// The popup table: one row per piece or material, one column per player, plus a total.
/// Opened and closed with <see cref="Plugin.ToggleKey"/>; Esc also closes it.
/// </summary>
[HarmonyPatch]
internal static class StatsWindow
{
    private enum Tab { Built, Materials, Crafted }
    private enum MaterialFilter { All, Building, Crafting }

    private sealed class Row
    {
        public string Name;
        public int[] PerPlayer;
        public int Total;
    }

    private const int WindowId = 0x42_53_54;
    private const float ItemWidth = 230f;
    private const float NumberWidth = 96f;

    private static bool _open;
    private static Tab _tab;
    private static MaterialFilter _filter;
    private static Tab? _nextTab;
    // Sorting: SortTotal, SortName or a player's key; clicking a header sets _nextSort.
    private const string SortTotal = "#total", SortName = "#name", SearchControl = "BuildStatsSearch";
    private static string _sortBy = SortTotal;
    private static bool _sortDescending = true;
    private static string _nextSort;
    private static string _search = "";
    private static bool _searchFocused;
    private static MaterialFilter? _nextFilter;
    private static Vector2 _scroll;
    private static Rect _rect;
    private static bool _tooWide;
    private static float _scale = 1f;

    private static string[] _players = Array.Empty<string>();
    private static string[] _labels = Array.Empty<string>();
    private static List<Row> _rows = new();
    private static Row _totals;
    private static readonly Dictionary<string, string> Localized = new();

    internal static bool IsOpen => _open;

    internal static bool SearchFocused => _searchFocused;

    // ------------------------------------------------------------------ open / close

    internal static void Tick()
    {
        if (_open && Player.m_localPlayer == null) _open = false;
        if (!_open) _searchFocused = false;
        if (_searchFocused || !Input.GetKeyDown(Plugin.ToggleKey.Value)) return; // typing: only Esc closes
        if (_open) _open = false;
        else if (CanOpen()) Open();
    }

    private static bool CanOpen()
    {
        if (Player.m_localPlayer == null) return false;
        if (Console.IsVisible() || TextInput.IsVisible() || Menu.IsVisible()) return false;
        if (Chat.instance != null && Chat.instance.HasFocus()) return false;
        return true;
    }

    private static void Open()
    {
        _open = true;
        _scroll = Vector2.zero;
        if (_rect.width <= 0f) _rect = new Rect(0f, 0f, 760f, 520f);
    }

    // While the window is up, the player and camera ignore input and the cursor stays free.

    [HarmonyPatch(typeof(Player), "TakeInput"), HarmonyPostfix]
    private static void BlockPlayerInput(ref bool __result)
    {
        if (_open) __result = false;
    }

    [HarmonyPatch(typeof(PlayerController), "TakeInput"), HarmonyPostfix]
    private static void BlockControllerInput(ref bool __result)
    {
        if (_open) __result = false;
    }

    [HarmonyPatch(typeof(GameCamera), "UpdateMouseCapture"), HarmonyPrefix]
    private static bool FreeCursor()
    {
        if (!_open) return true;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        return false;
    }

    // Esc closes the window instead of opening the game menu.
    [HarmonyPatch(typeof(Menu), "Update"), HarmonyPrefix]
    private static bool EscCloses()
    {
        if (!_open) return true;
        if (Input.GetKeyDown(KeyCode.Escape)) _open = false;
        return false;
    }

    // ------------------------------------------------------------------ drawing

    internal static void Draw()
    {
        if (!_open) return;
        Styles.Ensure();

        _scale = Mathf.Max(1f, Screen.height / 1080f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(_scale, _scale, 1f));
        float screenW = Screen.width / _scale, screenH = Screen.height / _scale;

        if (Event.current.type == EventType.Layout)
        {
            if (_nextTab is Tab t && t != _tab) { _tab = t; _scroll = Vector2.zero; }
            if (_nextFilter is MaterialFilter f) _filter = f;
            if (_nextSort != null)
            {
                // Same column again flips the order; a new column starts at the most (names: A to Z).
                if (_nextSort == _sortBy) _sortDescending = !_sortDescending;
                else { _sortBy = _nextSort; _sortDescending = _sortBy != SortName; }
                _nextSort = null;
            }
            _nextTab = null;
            _nextFilter = null;
            BuildRows();
            FitWidth(screenW);
        }
        if (_rect.x <= 0f && _rect.y <= 0f) _rect.position = new Vector2((screenW - _rect.width) / 2f, (screenH - _rect.height) / 2f);
        _rect = GUI.Window(WindowId, _rect, DrawWindow, GUIContent.none, Styles.Window);
        _rect.x = Mathf.Clamp(_rect.x, 0f, screenW - _rect.width);
        _rect.y = Mathf.Clamp(_rect.y, 0f, screenH - _rect.height);
    }

    private static void DrawWindow(int id)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Build stats", Styles.Title);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("x", Styles.Close, GUILayout.Width(28f))) _open = false;
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        TabButton("Things built", Tab.Built);
        TabButton("Resources used", Tab.Materials);
        TabButton("Crafted", Tab.Crafted);
        GUILayout.FlexibleSpace();
        if (_tab == Tab.Materials)
        {
            FilterButton("All", MaterialFilter.All);
            FilterButton("Building", MaterialFilter.Building);
            FilterButton("Crafting", MaterialFilter.Crafting);
        }
        GUILayout.Space(10f);
        GUI.SetNextControlName(SearchControl);
        _search = GUILayout.TextField(_search ?? "", 40, Styles.Search, GUILayout.Width(190f));
        if (string.IsNullOrEmpty(_search) && GUI.GetNameOfFocusedControl() != SearchControl)
            GUI.Label(GUILayoutUtility.GetLastRect(), "Search...", Styles.SearchHint);
        if (Event.current.type == EventType.Repaint) _searchFocused = GUI.GetNameOfFocusedControl() == SearchControl;
        GUILayout.EndHorizontal();
        GUILayout.Space(6f);

        if (_rows.Count == 0)
        {
            GUILayout.FlexibleSpace();
            string empty = !string.IsNullOrEmpty(_search) ? $"Nothing matches \"{_search}\"."
                : _tab == Tab.Built ? "Nothing built yet."
                : _tab == Tab.Crafted ? "Nothing crafted yet. Crafts and upgrades count from when BuildStats was installed."
                : "No resources used yet.";
            GUILayout.Label(empty, Styles.Empty);
            GUILayout.FlexibleSpace();
        }
        else
        {
            // Too many players to fit across the screen: header and totals scroll sideways with the rows.
            string first = _tab == Tab.Built ? "Piece" : _tab == Tab.Crafted ? "Kind" : "Material";
            if (_tooWide) _scroll = GUILayout.BeginScrollView(_scroll, true, false, GUI.skin.horizontalScrollbar, GUI.skin.verticalScrollbar);
            DrawRow(first, _labels, null, "Total", Styles.Header, Styles.HeaderNum);
            if (!_tooWide) _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
            for (int i = 0; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                DrawRow(r.Name, null, r.PerPlayer, r.Total.ToString("N0"), i % 2 == 0 ? Styles.Cell : Styles.CellAlt, i % 2 == 0 ? Styles.Num : Styles.NumAlt, i % 2 == 0 ? Styles.Top : Styles.TopAlt);
            }
            if (!_tooWide) GUILayout.EndScrollView();
            DrawRow("All", null, _totals.PerPlayer, _totals.Total.ToString("N0"), Styles.TotalCell, Styles.TotalNum, Styles.TotalTop);
            if (_tooWide) GUILayout.EndScrollView();
        }

        GUILayout.Space(4f);
        GUILayout.BeginHorizontal();
        GUILayout.Label(StatusText(), Styles.Footer);
        GUILayout.FlexibleSpace();
        GUILayout.Label($"{Plugin.ToggleKey.Value} or Esc to close", Styles.Footer);
        GUILayout.EndHorizontal();

        GUI.DragWindow(new Rect(0f, 0f, 10000f, 40f));
    }

    // Widens the window to fit every player column, up to the screen width.
    private static void FitWidth(float screenW)
    {
        int columns = _players.Length + (_players.Length > 1 ? 1 : 0);
        float needed = 32f + ItemWidth + 2f + columns * (NumberWidth + 2f) + 24f;
        float max = Mathf.Max(560f, screenW - 40f);
        _tooWide = needed > max;
        _rect.width = Mathf.Clamp(needed, 560f, max);
    }

    private static void DrawRow(string name, string[] heads, int[] values, string total, GUIStyle cell, GUIStyle num, GUIStyle top = null)
    {
        int n = _players.Length;
        // Header: long names wrap onto more lines; every cell in the row gets the tallest height.
        GUILayoutOption height = null;
        if (heads != null)
        {
            float h = cell.CalcHeight(new GUIContent(name), ItemWidth);
            for (int i = 0; i < n; i++) h = Mathf.Max(h, num.CalcHeight(new GUIContent(heads[i]), NumberWidth));
            height = GUILayout.Height(h);
        }
        // Highlight whoever has the most in this row (all of them on a tie).
        int best = 0;
        if (values != null && top != null && n > 1)
            for (int i = 0; i < n; i++) best = Mathf.Max(best, values[i]);

        GUILayout.BeginHorizontal();
        if (heads != null)
        {
            // Header cells are buttons: click to sort by that column, again to flip.
            HeaderButton(name, SortName, cell, Options(ItemWidth, height));
            for (int i = 0; i < n; i++) HeaderButton(heads[i], _players[i], num, Options(NumberWidth, height));
            if (n > 1) HeaderButton(total, SortTotal, num, Options(NumberWidth, height));
        }
        else
        {
            GUILayout.Label(name, cell, Options(ItemWidth, height));
            for (int i = 0; i < n; i++)
            {
                string text = values[i] == 0 ? "-" : values[i].ToString("N0");
                GUIStyle style = best > 0 && values[i] == best ? top : num;
                GUILayout.Label(text, style, Options(NumberWidth, height));
            }
            if (n > 1) GUILayout.Label(total, num, Options(NumberWidth, height));
        }
        GUILayout.Label(GUIContent.none, cell, height == null ? new[] { GUILayout.ExpandWidth(true) } : new[] { GUILayout.ExpandWidth(true), height });
        GUILayout.EndHorizontal();
    }

    private static void HeaderButton(string text, string sortKey, GUIStyle style, GUILayoutOption[] options)
    {
        bool sorted = _sortBy == sortKey;
        if (sorted) text = (style.alignment == TextAnchor.MiddleLeft || style.alignment == TextAnchor.UpperLeft)
            ? text + (_sortDescending ? " \u25BC" : " \u25B2")
            : (_sortDescending ? "\u25BC " : "\u25B2 ") + text;
        if (GUILayout.Button(text, sorted ? (style == Styles.Header ? Styles.HeaderOn : Styles.HeaderNumOn) : style, options)) _nextSort = sortKey;
    }

    private static GUILayoutOption[] Options(float width, GUILayoutOption height) =>
        height == null ? new[] { GUILayout.Width(width) } : new[] { GUILayout.Width(width), height };

    // Clicks are applied on the next layout pass, so one frame's layout and repaint draw the same controls.
    private static void TabButton(string text, Tab tab)
    {
        if (GUILayout.Button(text, _tab == tab ? Styles.TabOn : Styles.Tab)) _nextTab = tab;
    }

    private static void FilterButton(string text, MaterialFilter f)
    {
        if (GUILayout.Button(text, _filter == f ? Styles.FilterOn : Styles.Filter)) _nextFilter = f;
    }

    private static string StatusText()
    {
        if (StatsSync.ServerHasMod) return "All players on this world. Kept by the server.";
        if (StatsSync.Waiting) return "Getting stats from the server...";
        return "The server doesn't run BuildStats: showing only your builds this session.";
    }

    // ------------------------------------------------------------------ table data

    private static void BuildRows()
    {
        StatsStore view = StatsSync.View;
        // Builders known only by id (builds from before BuildStats, not seen since) come last as
        // "Unknown 1", "Unknown 2", ... and get their name once they join with BuildStats.
        var named = view.Players.Where(p => !p.StartsWith(Backfill.IdPrefix)).ToList();
        var unknown = view.Players.Where(p => p.StartsWith(Backfill.IdPrefix)).ToList();
        _players = named.Concat(unknown).ToArray();
        _labels = named.Concat(unknown.Select((_, i) => $"Unknown {i + 1}")).ToArray();
        Category[] cats = _tab == Tab.Built ? new[] { Category.Built }
            : _tab == Tab.Crafted ? new[] { Category.Crafted }
            : _filter == MaterialFilter.Building ? new[] { Category.BuildMaterials }
            : _filter == MaterialFilter.Crafting ? new[] { Category.CraftMaterials }
            : new[] { Category.BuildMaterials, Category.CraftMaterials };

        var rows = new List<Row>();
        _totals = new Row { Name = "All", PerPlayer = new int[_players.Length] };
        foreach (string token in cats.SelectMany(view.Tokens).Distinct())
        {
            var row = new Row { Name = Localize(token), PerPlayer = new int[_players.Length] };
            for (int p = 0; p < _players.Length; p++)
            {
                foreach (Category c in cats) row.PerPlayer[p] += view.Get(_players[p], c, token);
                row.Total += row.PerPlayer[p];
            }
            if (row.Total > 0) rows.Add(row);
        }

        // Search narrows the rows; the totals row adds up what's shown.
        if (!string.IsNullOrEmpty(_search))
            rows = rows.Where(r => r.Name.IndexOf(_search, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
        foreach (Row r in rows)
        {
            for (int p = 0; p < _players.Length; p++) _totals.PerPlayer[p] += r.PerPlayer[p];
            _totals.Total += r.Total;
        }

        int col = Array.IndexOf(_players, _sortBy);
        if (_sortBy != SortTotal && _sortBy != SortName && col < 0) _sortBy = SortTotal; // that player is gone
        int sign = _sortDescending ? -1 : 1;
        rows.Sort((a, b) =>
        {
            int c = _sortBy == SortName ? 0 : (col >= 0 ? a.PerPlayer[col].CompareTo(b.PerPlayer[col]) : a.Total.CompareTo(b.Total)) * sign;
            if (c == 0 && _sortBy == SortName) c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase) * sign;
            if (c == 0 && col >= 0) c = b.Total.CompareTo(a.Total);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        _rows = rows;
    }

    private static string Localize(string token)
    {
        if (Localized.TryGetValue(token, out string s)) return s;
        s = Localization.instance != null ? Localization.instance.Localize(token) : token;
        if (string.IsNullOrEmpty(s)) s = token;
        Localized[token] = s;
        return s;
    }

    // ------------------------------------------------------------------ look

    private static class Styles
    {
        private static readonly Color Gold = new(1f, 0.74f, 0.36f);
        private static readonly Color Text = new(0.93f, 0.90f, 0.84f);
        private static readonly Color Hot = new(1f, 0.62f, 0.2f);
        private static readonly Color Dim = new(0.66f, 0.62f, 0.55f);

        internal static GUIStyle Window, Title, Close, Tab, TabOn, Filter, FilterOn, Header, HeaderNum, HeaderOn, HeaderNumOn, Search, SearchHint;
        internal static GUIStyle Cell, CellAlt, Num, NumAlt, Top, TopAlt, TotalCell, TotalNum, TotalTop, Footer, Empty;
        private static bool _made;

        internal static void Ensure()
        {
            if (_made) return;
            _made = true;
            Font font = Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f => f.name.StartsWith("AveriaSerifLibre-Bold"))
                ?? Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f => f.name.StartsWith("AveriaSerifLibre"));

            Texture2D panel = Solid(new Color(0.09f, 0.075f, 0.06f, 0.95f));
            Texture2D band = Solid(new Color(1f, 1f, 1f, 0.035f));
            Texture2D head = Solid(new Color(0f, 0f, 0f, 0.35f));
            Texture2D tab = Solid(new Color(1f, 1f, 1f, 0.06f));
            Texture2D tabOn = Solid(new Color(1f, 0.74f, 0.36f, 0.22f));
            Texture2D total = Solid(new Color(1f, 0.74f, 0.36f, 0.12f));

            Window = new GUIStyle { normal = { background = panel }, padding = new RectOffset(16, 16, 12, 12), border = new RectOffset(2, 2, 2, 2) };
            Title = Make(font, 22, Gold, FontStyle.Bold);
            Close = Make(font, 18, Dim, FontStyle.Normal, TextAnchor.MiddleCenter);
            Close.hover.textColor = Gold;
            Tab = Make(font, 15, Dim, FontStyle.Bold, TextAnchor.MiddleCenter, tab, new RectOffset(14, 14, 6, 6));
            Tab.hover.textColor = Text;
            TabOn = Make(font, 15, Gold, FontStyle.Bold, TextAnchor.MiddleCenter, tabOn, new RectOffset(14, 14, 6, 6));
            Filter = Make(font, 13, Dim, FontStyle.Normal, TextAnchor.MiddleCenter, null, new RectOffset(8, 8, 6, 6));
            Filter.hover.textColor = Text;
            FilterOn = Make(font, 13, Gold, FontStyle.Bold, TextAnchor.MiddleCenter, null, new RectOffset(8, 8, 6, 6));
            Header = Make(font, 14, Gold, FontStyle.Bold, TextAnchor.MiddleLeft, head, new RectOffset(8, 8, 6, 6));
            HeaderNum = Make(font, 14, Gold, FontStyle.Bold, TextAnchor.MiddleRight, head, new RectOffset(8, 8, 6, 6));
            HeaderNum.wordWrap = true;
            Header.wordWrap = true;
            Texture2D headOn = Solid(new Color(1f, 0.74f, 0.36f, 0.20f));
            HeaderOn = new GUIStyle(Header) { normal = { background = headOn, textColor = Gold }, hover = { background = headOn, textColor = Gold } };
            HeaderNumOn = new GUIStyle(HeaderNum) { normal = { background = headOn, textColor = Gold }, hover = { background = headOn, textColor = Gold } };
            Header.hover.textColor = Text;
            HeaderNum.hover.textColor = Text;
            Texture2D field = Solid(new Color(0f, 0f, 0f, 0.45f));
            Search = Make(font, 14, Text, FontStyle.Normal, TextAnchor.MiddleLeft, field, new RectOffset(8, 8, 5, 5));
            Search.focused.background = field;
            Search.focused.textColor = Text;
            Search.clipping = TextClipping.Clip;
            SearchHint = Make(font, 14, Dim, FontStyle.Italic, TextAnchor.MiddleLeft, null, new RectOffset(8, 8, 5, 5));
            Cell = Make(font, 14, Text, FontStyle.Normal, TextAnchor.MiddleLeft, null, new RectOffset(8, 8, 4, 4));
            CellAlt = Make(font, 14, Text, FontStyle.Normal, TextAnchor.MiddleLeft, band, new RectOffset(8, 8, 4, 4));
            Num = Make(font, 14, Text, FontStyle.Normal, TextAnchor.MiddleRight, null, new RectOffset(8, 8, 4, 4));
            NumAlt = Make(font, 14, Text, FontStyle.Normal, TextAnchor.MiddleRight, band, new RectOffset(8, 8, 4, 4));
            TotalCell = Make(font, 14, Gold, FontStyle.Bold, TextAnchor.MiddleLeft, total, new RectOffset(8, 8, 5, 5));
            TotalNum = Make(font, 14, Gold, FontStyle.Bold, TextAnchor.MiddleRight, total, new RectOffset(8, 8, 5, 5));
            // Most in a row: bright orange, bold, on an orange tint.
            Texture2D topBg = Solid(new Color(1f, 0.55f, 0.1f, 0.28f));
            Texture2D totalTopBg = Solid(new Color(1f, 0.55f, 0.1f, 0.40f));
            Top = Make(font, 14, Hot, FontStyle.Bold, TextAnchor.MiddleRight, topBg, new RectOffset(8, 8, 4, 4));
            TopAlt = Top;
            TotalTop = Make(font, 14, Hot, FontStyle.Bold, TextAnchor.MiddleRight, totalTopBg, new RectOffset(8, 8, 5, 5));
            Footer = Make(font, 12, Dim, FontStyle.Normal);
            Empty = Make(font, 15, Dim, FontStyle.Italic, TextAnchor.MiddleCenter);
        }

        private static GUIStyle Make(Font font, int size, Color color, FontStyle style,
            TextAnchor anchor = TextAnchor.MiddleLeft, Texture2D bg = null, RectOffset padding = null)
        {
            var s = new GUIStyle
            {
                font = font,
                fontSize = size,
                fontStyle = style,
                alignment = anchor,
                padding = padding ?? new RectOffset(2, 2, 2, 2),
                margin = new RectOffset(0, 2, 0, 0),
                clipping = TextClipping.Clip,
            };
            s.normal.textColor = color;
            s.normal.background = bg;
            s.hover.textColor = color;
            s.hover.background = bg;
            s.active.textColor = color;
            s.active.background = bg;
            return s;
        }

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }
    }
}

/// <summary>Kept apart from <see cref="StatsWindow"/> so a game update that renames the camera code only loses this.</summary>
[HarmonyPatch]
internal static class ZoomLock
{
    // The mouse wheel scrolls the table, so keep it from zooming the camera while the window is open.
    private static readonly AccessTools.FieldRef<GameCamera, float> CameraDistance = AccessTools.FieldRefAccess<GameCamera, float>("m_distance");

    [HarmonyPatch(typeof(GameCamera), "UpdateCamera"), HarmonyPrefix]
    private static void RememberZoom(GameCamera __instance, out float __state) => __state = CameraDistance(__instance);

    [HarmonyPatch(typeof(GameCamera), "UpdateCamera"), HarmonyPostfix]
    private static void KeepZoom(GameCamera __instance, float __state)
    {
        if (StatsWindow.IsOpen) CameraDistance(__instance) = __state;
    }
}

/// <summary>Stops game hotkeys from firing while typing in the search box.</summary>
[HarmonyPatch]
internal static class TypingLock
{
    // While typing in the search box, letters mustn't open the inventory, map or chat.
    [HarmonyPatch(typeof(InventoryGui), "Update"), HarmonyPrefix]
    private static bool NoInventoryKeysWhileSearching() => !(StatsWindow.IsOpen && StatsWindow.SearchFocused);

    [HarmonyPatch(typeof(Minimap), "Update"), HarmonyPrefix]
    private static bool NoMapKeysWhileSearching() => !(StatsWindow.IsOpen && StatsWindow.SearchFocused);

    [HarmonyPatch(typeof(Chat), "Update"), HarmonyPrefix]
    private static bool NoChatKeysWhileSearching() => !(StatsWindow.IsOpen && StatsWindow.SearchFocused);
}
