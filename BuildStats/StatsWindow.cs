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
    private enum Tab { Built, Materials }
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
    private static MaterialFilter? _nextFilter;
    private static Vector2 _scroll;
    private static Rect _rect;
    private static float _scale = 1f;

    private static string[] _players = Array.Empty<string>();
    private static List<Row> _rows = new();
    private static Row _totals;
    private static readonly Dictionary<string, string> Localized = new();

    internal static bool IsOpen => _open;

    // ------------------------------------------------------------------ open / close

    internal static void Tick()
    {
        if (_open && Player.m_localPlayer == null) _open = false;
        if (!Input.GetKeyDown(Plugin.ToggleKey.Value)) return;
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
        if (_rect.x <= 0f && _rect.y <= 0f) _rect.position = new Vector2((screenW - _rect.width) / 2f, (screenH - _rect.height) / 2f);

        if (Event.current.type == EventType.Layout)
        {
            if (_nextTab is Tab t && t != _tab) { _tab = t; _scroll = Vector2.zero; }
            if (_nextFilter is MaterialFilter f) _filter = f;
            _nextTab = null;
            _nextFilter = null;
            BuildRows();
        }
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
        GUILayout.FlexibleSpace();
        if (_tab == Tab.Materials)
        {
            FilterButton("All", MaterialFilter.All);
            FilterButton("Building", MaterialFilter.Building);
            FilterButton("Crafting", MaterialFilter.Crafting);
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(6f);

        if (_rows.Count == 0)
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label(_tab == Tab.Built ? "Nothing built yet." : "No resources used yet.", Styles.Empty);
            GUILayout.FlexibleSpace();
        }
        else
        {
            DrawRow(_tab == Tab.Built ? "Piece" : "Material", _players, null, "Total", Styles.Header, Styles.HeaderNum);
            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
            for (int i = 0; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                DrawRow(r.Name, null, r.PerPlayer, r.Total.ToString("N0"), i % 2 == 0 ? Styles.Cell : Styles.CellAlt, i % 2 == 0 ? Styles.Num : Styles.NumAlt);
            }
            GUILayout.EndScrollView();
            DrawRow("All", null, _totals.PerPlayer, _totals.Total.ToString("N0"), Styles.TotalCell, Styles.TotalNum);
        }

        GUILayout.Space(4f);
        GUILayout.BeginHorizontal();
        GUILayout.Label(StatusText(), Styles.Footer);
        GUILayout.FlexibleSpace();
        GUILayout.Label($"{Plugin.ToggleKey.Value} or Esc to close", Styles.Footer);
        GUILayout.EndHorizontal();

        GUI.DragWindow(new Rect(0f, 0f, 10000f, 40f));
    }

    private static void DrawRow(string name, string[] heads, int[] values, string total, GUIStyle cell, GUIStyle num)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(name, cell, GUILayout.Width(ItemWidth));
        int n = _players.Length;
        for (int i = 0; i < n; i++)
        {
            string text = heads != null ? heads[i] : values[i] == 0 ? "-" : values[i].ToString("N0");
            GUILayout.Label(text, num, GUILayout.Width(NumberWidth));
        }
        if (n > 1) GUILayout.Label(total, num, GUILayout.Width(NumberWidth));
        GUILayout.Label(GUIContent.none, cell, GUILayout.ExpandWidth(true));
        GUILayout.EndHorizontal();
    }

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
        _players = view.Players.ToArray();
        Category[] cats = _tab == Tab.Built ? new[] { Category.Built }
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
                _totals.PerPlayer[p] += row.PerPlayer[p];
            }
            _totals.Total += row.Total;
            if (row.Total > 0) rows.Add(row);
        }
        rows.Sort((a, b) => a.Total != b.Total ? b.Total.CompareTo(a.Total) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
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
        private static readonly Color Dim = new(0.66f, 0.62f, 0.55f);

        internal static GUIStyle Window, Title, Close, Tab, TabOn, Filter, FilterOn, Header, HeaderNum;
        internal static GUIStyle Cell, CellAlt, Num, NumAlt, TotalCell, TotalNum, Footer, Empty;
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
            Cell = Make(font, 14, Text, FontStyle.Normal, TextAnchor.MiddleLeft, null, new RectOffset(8, 8, 4, 4));
            CellAlt = Make(font, 14, Text, FontStyle.Normal, TextAnchor.MiddleLeft, band, new RectOffset(8, 8, 4, 4));
            Num = Make(font, 14, Text, FontStyle.Normal, TextAnchor.MiddleRight, null, new RectOffset(8, 8, 4, 4));
            NumAlt = Make(font, 14, Text, FontStyle.Normal, TextAnchor.MiddleRight, band, new RectOffset(8, 8, 4, 4));
            TotalCell = Make(font, 14, Gold, FontStyle.Bold, TextAnchor.MiddleLeft, total, new RectOffset(8, 8, 5, 5));
            TotalNum = Make(font, 14, Gold, FontStyle.Bold, TextAnchor.MiddleRight, total, new RectOffset(8, 8, 5, 5));
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
