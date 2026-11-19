using System;
using System.Collections.Generic;
using System.Linq;
using SignSorter;
using UnityEngine;
using ItemData = ItemDrop.ItemData;
using ItemType = ItemDrop.ItemData.ItemType;

namespace SignSorterTests
{
    /// <summary>
    /// Runs the sorting logic against Valheim's own Inventory class (loaded from the game's
    /// DLLs). No test framework: each test is a method; failures throw.
    /// </summary>
    internal static class Program
    {
        private static int _passed, _failed;

        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--demo") return Demo();
            if (args.Length > 0 && args[0] == "--collect-demo") return CollectDemo();

            var tests = new List<KeyValuePair<string, Action>>();
            void T(string name, Action a) => tests.Add(new KeyValuePair<string, Action>(name, a));

            // --- sign text & arrows
            T("arrow: '< Wood' points left", () => Arrow(" < Wood", SignSorter.Arrow.Left, "Wood"));
            T("arrow: 'Wood >' points right", () => Arrow("Wood >", SignSorter.Arrow.Right, "Wood"));
            T("arrow: '^ Wood' points up", () => Arrow("^ Wood", SignSorter.Arrow.Up, "Wood"));
            T("arrow: '^Wood, Stone' points up", () => Arrow("^Wood, Stone", SignSorter.Arrow.Up, "Wood, Stone"));
            T("arrow: 'v Wood' points down", () => Arrow("v Wood", SignSorter.Arrow.Down, "Wood"));
            T("arrow: 'Wood v' points down", () => Arrow("Wood v", SignSorter.Arrow.Down, "Wood"));
            T("arrow: '<- Wood' and 'Wood ->'", () => { Arrow("<- Wood", SignSorter.Arrow.Left, "Wood"); Arrow("Wood ->", SignSorter.Arrow.Right, "Wood"); });
            T("arrow: 'Vines' is not an arrow", () => Arrow("Vines", SignSorter.Arrow.None, "Vines"));
            T("arrow: plain text has none", () => Arrow("Wood, Stone", SignSorter.Arrow.None, "Wood, Stone"));
            T("arrow: color tags stripped, no arrow", () => Arrow("<color=red>Wood</color>", SignSorter.Arrow.None, "Wood"));
            T("arrow: color tags plus arrow", () => Arrow("<b>< Wood</b>", SignSorter.Arrow.Left, "Wood"));
            T("arrow: only an arrow leaves empty body", () => Arrow(">", SignSorter.Arrow.Right, ""));

            // --- matching
            T("match: exact, any case/spacing", () => Eq(SignFilter.ScoreExact, Score("fine WOOD", Item("Fine wood"))));
            T("match: plural", () => { Eq(SignFilter.ScoreExact, Score("Stones", Item("Stone"))); Eq(SignFilter.ScoreExact, Score("Raspberry", Item("Raspberries"))); });
            T("match: typo tolerated", () => Eq(SignFilter.ScoreTypo, Score("Fine wod", Item("Fine wood"))));
            T("match: transposed letters tolerated", () => Eq(SignFilter.ScoreTypo, Score("Fien wood", Item("Fine wood"))));
            T("match: short words need exact", () => Eq(SignFilter.NoMatch, Score("Wod", Item("Wood"))));
            T("match: typos can be turned off", () => Eq(SignFilter.NoMatch, Score("Fine wod", Item("Fine wood"), typos: false)));
            T("match: different words don't match", () => { Eq(SignFilter.NoMatch, Score("Deer hide", Item("Deer meat"))); Eq(SignFilter.NoMatch, Score("Copper", Item("Copper ore"))); });
            T("match: wildcard", () => { Eq(SignFilter.ScoreWildcard, Score("*wood", Item("Core wood"))); Eq(SignFilter.ScoreWildcard, Score("*wood", Item("Wood")) >= SignFilter.ScoreWildcard ? SignFilter.ScoreWildcard : -9); });
            T("match: category food vs mead", () =>
            {
                Eq(SignFilter.ScoreCategory, Score("Food", Item("Cooked meat", ItemType.Consumable, food: 30)));
                Eq(SignFilter.NoMatch, Score("Food", Item("Minor healing mead", ItemType.Consumable)));
                Eq(SignFilter.ScoreCategory, Score("Meads", Item("Minor healing mead", ItemType.Consumable)));
            });
            T("match: catch-all", () => Eq(SignFilter.ScoreCatchAll, Score("*", Item("Anything"))));
            T("match: best rule wins", () => Eq(SignFilter.ScoreExact, Score("*, Materials, *wood, Wood", Item("Wood"))));
            T("match: ignore tag", () => True(SignFilter.Parse("Wood, NoSort", Settings()).Ignore, "ignored"));
            T("match: rich text in sign", () => Eq(SignFilter.ScoreExact, Score("<color=yellow>Wood</color>", Item("Wood"))));

            // --- aliases
            T("alias: expands to items", () =>
            {
                var s = Settings(aliases: "Metal, Bars = Copper, Tin");
                Eq(SignFilter.ScoreExact, SignFilter.Parse("Bars", s).Score(Item("Tin")));
                Eq(SignFilter.NoMatch, SignFilter.Parse("Bars", s).Score(Item("Wood")));
            });
            T("alias: nested and wildcards", () =>
            {
                var s = Settings(aliases: "Lumber = *wood\nBuilding = Lumber, Stone");
                Eq(SignFilter.ScoreWildcard, SignFilter.Parse("Building", s).Score(Item("Fine wood")));
                Eq(SignFilter.ScoreExact, SignFilter.Parse("Building", s).Score(Item("Stone")));
            });
            T("alias: loops don't hang", () =>
            {
                var s = Settings(aliases: "A1 = B1\nB1 = A1, Wood");
                Eq(SignFilter.ScoreExact, SignFilter.Parse("A1", s).Score(Item("Wood")));
            });
            T("alias: bad lines skipped, comments ignored", () =>
            {
                var warnings = new List<string>();
                var table = AliasTable.Parse(new[] { "# comment", "no equals sign", "X =", "Good = Wood # trailing", "" }, warnings.Add);
                Eq(1, table.Count);
                Eq(2, warnings.Count);
            });
            T("alias: unknown entries reported", () =>
            {
                var f = SignFilter.Parse("Wood, Wod, Food, *zzz, *", Settings());
                var catalog = new List<KeyValuePair<ItemData, ItemKeys>>
                {
                    new KeyValuePair<ItemData, ItemKeys>(Item("Wood"), ItemKeys.Make("Wood", "Wood")),
                };
                var unknown = f.UnknownEntries(catalog);
                Eq("Wod|*zzz", string.Join("|", unknown.ToArray()));
            });

            // --- geometry
            T("geometry: arrow picks chest on that side", () =>
            {
                var chests = new List<Vector3> { new Vector3(-1.5f, 0, 0), new Vector3(1.5f, 0, 0), new Vector3(0, 1.5f, 0), new Vector3(0, -1.2f, 0.1f) };
                Vector3 right = Vector3.right;
                Eq(0, SignGeometry.ChooseArrowTarget(Vector3.zero, SignSorter.Arrow.Left, right, chests, 4f));
                Eq(1, SignGeometry.ChooseArrowTarget(Vector3.zero, SignSorter.Arrow.Right, right, chests, 4f));
                Eq(2, SignGeometry.ChooseArrowTarget(Vector3.zero, SignSorter.Arrow.Up, right, chests, 4f));
                Eq(3, SignGeometry.ChooseArrowTarget(Vector3.zero, SignSorter.Arrow.Down, right, chests, 4f));
            });
            T("geometry: arrow respects facing", () =>
            {
                var chests = new List<Vector3> { new Vector3(0, 0, 2f), new Vector3(0, 0, -2f) };
                // Reader's right is +Z: "Wood >" means the +Z chest.
                Eq(0, SignGeometry.ChooseArrowTarget(Vector3.zero, SignSorter.Arrow.Right, Vector3.forward, chests, 4f));
                Eq(1, SignGeometry.ChooseArrowTarget(Vector3.zero, SignSorter.Arrow.Left, Vector3.forward, chests, 4f));
            });
            T("geometry: arrow ignores chests out of range or behind", () =>
            {
                var chests = new List<Vector3> { new Vector3(10f, 0, 0), new Vector3(-1f, 0, 0) };
                Eq(-1, SignGeometry.ChooseArrowTarget(Vector3.zero, SignSorter.Arrow.Right, Vector3.right, chests, 4f));
            });
            T("geometry: arrow prefers straight-ahead over diagonal", () =>
            {
                var chests = new List<Vector3> { new Vector3(1.2f, 0, 1.0f), new Vector3(1.6f, 0, 0) };
                Eq(1, SignGeometry.ChooseArrowTarget(Vector3.zero, SignSorter.Arrow.Right, Vector3.right, chests, 4f));
            });
            T("geometry: arrow sign beats nearest plain sign", () =>
            {
                // Two chests side by side; one sign between them pointing left, one plain sign right above chest B.
                var chests = new List<Vector3> { new Vector3(-1f, 0, 0), new Vector3(1f, 0, 0) };
                var signs = new List<SignGeometry.SignInfo>
                {
                    new SignGeometry.SignInfo { Position = new Vector3(0, 0.5f, 0), Arrow = SignSorter.Arrow.Left, ReaderRight = Vector3.right, Body = "Wood" },
                    new SignGeometry.SignInfo { Position = new Vector3(1f, 1f, 0), Arrow = SignSorter.Arrow.None, ReaderRight = Vector3.right, Body = "Stone" },
                };
                var labels = SignGeometry.AssignLabels(signs, chests, 2.5f, 4f);
                Eq("Wood", string.Join(",", labels[0].ToArray()));
                Eq("Stone", string.Join(",", labels[1].ToArray()));
            });
            T("geometry: two arrows can label one chest", () =>
            {
                var chests = new List<Vector3> { new Vector3(0, 0, 0) };
                var signs = new List<SignGeometry.SignInfo>
                {
                    new SignGeometry.SignInfo { Position = new Vector3(-1f, 0, 0), Arrow = SignSorter.Arrow.Right, ReaderRight = Vector3.right, Body = "Wood" },
                    new SignGeometry.SignInfo { Position = new Vector3(1f, 0, 0), Arrow = SignSorter.Arrow.Left, ReaderRight = Vector3.right, Body = "Stone" },
                };
                var labels = SignGeometry.AssignLabels(signs, chests, 2.5f, 4f);
                Eq(2, labels[0].Count);
            });

            // --- sorting with the game's real Inventory
            T("sort: pulls from unlabeled chest", () =>
            {
                var a = Chest(null, ("Wood", 30), ("Stone", 20));
                var b = Chest("Wood");
                Run(a, b);
                Eq(30, Count(b, "Wood"));
                Eq(0, Count(a, "Wood"));
                Eq(20, Count(a, "Stone"));
            });
            T("sort: unlabeled pulling can be turned off", () =>
            {
                var a = Chest(null, ("Wood", 30));
                var b = Chest("Wood");
                Run(new[] { a, b }, ctx => ctx.PullFromUnlabeled = false);
                Eq(0, Count(b, "Wood"));
            });
            T("sort: overflow into second chest", () =>
            {
                var a = Chest(null, w: 2, h: 2, items: new[] { ("Wood", 50), ("Wood", 50), ("Wood", 50) });
                var b = Chest("Wood", w: 2, h: 1);
                var c = Chest("Wood", w: 2, h: 1);
                Run(a, b, c);
                Eq(150, Count(b, "Wood") + Count(c, "Wood"));
                Eq(0, Count(a, "Wood"));
            });
            T("sort: partial move when space runs out", () =>
            {
                var a = Chest(null, ("Wood", 50), ("Wood", 30));
                var b = Chest("Wood", w: 1, h: 1, items: new[] { ("Wood", 45) });
                Run(a, b);
                Eq(50, Count(b, "Wood"));
                Eq(75, Count(a, "Wood"));
            });
            T("sort: fills existing partial stacks first", () =>
            {
                var a = Chest(null, ("Wood", 10));
                var b = Chest("Wood", w: 2, h: 1, items: new[] { ("Wood", 45), ("Stone", 1) });
                Run(new[] { a, b }, c => c.AllowSwaps = false);
                Eq(50, Count(b, "Wood"));
                Eq(5, Count(a, "Wood"));
            });
            T("sort: ...and with swaps the stray stone makes room (when the source wants stone)", () =>
            {
                var a = Chest("Stone", ("Wood", 10));
                var b = Chest("Wood", w: 2, h: 1, items: new[] { ("Wood", 45), ("Stone", 1) });
                Run(a, b);
                Eq(55, Count(b, "Wood"));
                Eq(1, Count(a, "Stone"));
            });
            T("sort: specific sign beats general sign", () =>
            {
                var star = Chest("*", ("Wood", 10), ("Feathers", 3));
                var mats = Chest("Materials");
                var woods = Chest("*wood");
                var wood = Chest("Wood");
                Run(star, mats, woods, wood);
                Eq(10, Count(wood, "Wood"));
                Eq(3, Count(mats, "Feathers"));
                Eq(0, Count(star, "Wood"));
            });
            T("sort: catch-all takes leftovers from labeled chests", () =>
            {
                var wood = Chest("Wood", ("Feathers", 5));
                var star = Chest("*");
                Run(wood, star);
                Eq(5, Count(star, "Feathers"));
            });
            T("sort: same label = no shuffling", () =>
            {
                var a = Chest("Wood", ("Wood", 20));
                var b = Chest("Wood", ("Wood", 20));
                var ctx = Run(a, b);
                Eq(0, ctx.ItemsMoved);
            });
            T("sort: running twice moves nothing the 2nd time", () =>
            {
                var chests = new[] { Chest(null, ("Wood", 49), ("Wood", 30), ("Stone", 40), ("Resin", 7)), Chest("Wood"), Chest("Stone, Resin"), Chest("*") };
                Run(chests);
                Eq(0, Run(chests).ItemsMoved);
            });
            T("sort: open chest is left alone", () =>
            {
                var a = Chest(null, ("Wood", 30));
                var b = Chest("Wood");
                b.IsBusy = () => true;
                Run(a, b);
                Eq(0, Count(b, "Wood"));
                a.IsBusy = () => true; b.IsBusy = null;
                Run(a, b);
                Eq(0, Count(b, "Wood"));
            });
            T("sort: a broken busy-check counts as busy", () =>
            {
                var a = Chest(null, ("Wood", 30));
                var b = Chest("Wood");
                b.IsBusy = () => throw new Exception("boom");
                Run(a, b);
                Eq(0, Count(b, "Wood"));
            });
            T("sort: swaps two full chests holding each other's items", () =>
            {
                var a = Chest("Wood", w: 2, h: 1, items: new[] { ("Stone", 50), ("Stone", 50) });
                var b = Chest("Stone", w: 2, h: 1, items: new[] { ("Wood", 50), ("Wood", 50) });
                var ctx = Run(a, b);
                Eq(100, Count(a, "Wood"));
                Eq(100, Count(b, "Stone"));
                True(ctx.Swaps >= 2, "swapped");
            });
            T("sort: swap trades when each chest holds the other's item", () =>
            {
                var feathers = Chest("Feathers", w: 1, h: 1, items: new[] { ("Stone", 50) });
                var stone = Chest("Stone", w: 1, h: 1, items: new[] { ("Feathers", 20) });
                Run(feathers, stone);
                Eq(50, Count(stone, "Stone"));
                Eq(20, Count(feathers, "Feathers"));
            });
            T("safe: swaps never push junk into a chest with no sign", () =>
            {
                var portal = Chest(null, w: 1, h: 1, items: new[] { ("Stone", 50) });
                var stone = Chest("Stone", w: 1, h: 1, items: new[] { ("Troll hide", 3) });
                Run(portal, stone);
                Eq(50, Count(portal, "Stone"));
                Eq(0, Count(portal, "Troll hide"));
            });
            T("safe: * and category chests don't drain unlabeled chests", () =>
            {
                var personal = Chest(null, ("Cooked meat", 5), ("Wood", 10), ("Feathers", 3));
                var star = Chest("*");
                var mats = Chest("Materials");
                var wood = Chest("Wood");
                Run(personal, star, mats, wood);
                Eq(10, Count(wood, "Wood"));        // named: moved
                Eq(3, Count(personal, "Feathers")); // only a category/catch-all wants it: stays
                Eq(5, Count(personal, "Cooked meat"));
                Eq(0, star.Inventory.GetAllItems().Count);
            });
            T("safe: wildcard signs do pull from unlabeled chests", () =>
            {
                var a = Chest(null, ("Fine wood", 10));
                var b = Chest("*wood");
                Run(a, b);
                Eq(10, Count(b, "Fine wood"));
            });
            T("sort: swaps can be turned off", () =>
            {
                var a = Chest("Wood", w: 1, h: 1, items: new[] { ("Stone", 50) });
                var b = Chest("Stone", w: 1, h: 1, items: new[] { ("Wood", 50) });
                Run(new[] { a, b }, ctx => ctx.AllowSwaps = false);
                Eq(50, Count(a, "Stone"));
            });
            T("sort: messy base converges", () =>
            {
                // Every chest full of the wrong things, no free slots anywhere.
                var wood = Chest("Wood", w: 3, h: 1, items: new[] { ("Stone", 50), ("Resin", 50), ("Flint", 50) });
                var stone = Chest("Stone", w: 3, h: 1, items: new[] { ("Wood", 50), ("Flint", 50), ("Resin", 50) });
                var misc = Chest("Resin, Flint", w: 4, h: 1, items: new[] { ("Wood", 50), ("Wood", 50), ("Stone", 50), ("Stone", 50) });
                var ctx = Run(new[] { wood, stone, misc }, null, 12);
                Eq(150, Count(wood, "Wood"));
                Eq(150, Count(stone, "Stone"));
                Eq(200, Count(misc, "Resin") + Count(misc, "Flint"));
                True(ctx.Errors == 0, "no errors");
            });
            T("sort: dry run changes nothing", () =>
            {
                var a = Chest(null, ("Wood", 30));
                var b = Chest("Wood");
                var ctx = Run(new[] { a, b }, c => c.Apply = false);
                Eq(30, Count(a, "Wood"));
                Eq(30, ctx.ItemsMoved);
                True(ctx.Involved.Contains(a) && ctx.Involved.Contains(b), "involved");
            });
            T("sort: unstackable gear moves and keeps its data", () =>
            {
                var axe = Item("Bronze axe", ItemType.OneHandedWeapon, max: 1);
                axe.m_durability = 37f;
                axe.m_quality = 3;
                axe.m_customData["note"] = "mine";
                var a = Chest("*");
                a.Inventory.AddItem(axe);
                var b = Chest("Weapons");
                Run(a, b);
                var moved = b.Inventory.GetAllItems().Single();
                Eq(37f, moved.m_durability);
                Eq(3, moved.m_quality);
                Eq("mine", moved.m_customData["note"]);
            });
            T("sort: different quality stacks kept apart", () =>
            {
                var a = Chest(null);
                var low = Item("Arrow", ItemType.Ammo, max: 100); low.m_stack = 10; low.m_quality = 1;
                var high = Item("Arrow", ItemType.Ammo, max: 100); high.m_stack = 10; high.m_quality = 2;
                a.Inventory.AddItem(low); a.Inventory.AddItem(high);
                var b = Chest("Arrow");
                Run(a, b);
                Eq(2, b.Inventory.GetAllItems().Count);
            });
            T("merge: partial stacks combined", () =>
            {
                var a = Chest("Wood", ("Wood", 10), ("Wood", 20), ("Wood", 45), ("Stone", 3));
                var ctx = new SortContext();
                SortEngine.MergeStacks(a.Inventory, ctx);
                Eq(75, Count(a, "Wood"));
                Eq(3, a.Inventory.GetAllItems().Count); // 50 + 25 wood, 3 stone
            });
            T("deposit: from player, skipping hotbar and * chests", () =>
            {
                var player = new Inventory("player", null, 8, 4);
                // Full hotbar stack so the game doesn't merge the bag wood into it.
                var hotbarWood = Item("Wood"); hotbarWood.m_stack = 50; player.AddItem(hotbarWood, new Vector2i(0, 0));
                var bagWood = Item("Wood"); bagWood.m_stack = 20; player.AddItem(bagWood, new Vector2i(0, 1));
                var bagFeathers = Item("Feathers"); bagFeathers.m_stack = 5; player.AddItem(bagFeathers, new Vector2i(1, 1));
                var wood = Chest("Wood");
                var star = Chest("*");
                var ctx = new SortContext();
                SortEngine.Deposit(player, i => i.m_gridPos.y != 0, new List<SortChest> { wood, star }, ctx);
                Eq(20, Count(wood, "Wood"));
                Eq(50, CountInv(player, "Wood"));
                Eq(5, CountInv(player, "Feathers"));
                Eq(0, Count(star, "Feathers"));
            });
            T("deposit: category signs don't take your food and arrows", () =>
            {
                var player = new Inventory("player", null, 8, 4);
                var meat = Item("Cooked meat", ItemType.Consumable, food: 30, max: 20); meat.m_stack = 5; player.AddItem(meat, new Vector2i(0, 1));
                var arrows = Item("Wood arrow", ItemType.Ammo, max: 100); arrows.m_shared.m_ammoType = "$ammo_arrows"; arrows.m_stack = 40; player.AddItem(arrows, new Vector2i(1, 1));
                var food = Chest("Food");
                var ammo = Chest("Ammo");
                SortEngine.Deposit(player, i => i.m_gridPos.y != 0, new List<SortChest> { food, ammo }, new SortContext());
                Eq(5, CountInv(player, "Cooked meat"));
                Eq(40, CountInv(player, "Wood arrow"));
            });
            T("leftovers: reported when no sign wants an item", () =>
            {
                var wood = Chest("Wood", ("Feathers", 5), ("Wood", 5));
                var stone = Chest("Stone");
                var left = SortEngine.Leftovers(new List<SortChest> { wood, stone });
                Eq(1, left.Count);
                Eq("Feathers", left[0].m_shared.m_name);
            });
            T("transfer: stale item reference is a no-op", () =>
            {
                var a = Chest(null, ("Wood", 10));
                var b = Chest("Wood");
                var item = a.Inventory.GetAllItems()[0];
                a.Inventory.RemoveItem(item);
                Eq(0, SortEngine.Transfer(a.Inventory, b.Inventory, item, 10, new SortContext()));
                Eq(0, Count(b, "Wood"));
            });

            T("safety: transfer survives a save hook that throws", () =>
            {
                var a = Chest(null, ("Wood", 30));
                var b = Chest("Wood");
                b.Inventory.m_onChanged += () => throw new Exception("another mod's save hook");
                var ctx = new SortContext();
                SortEngine.Transfer(a.Inventory, b.Inventory, a.Inventory.GetAllItems()[0], 30, ctx);
                Eq(30, Count(a, "Wood") + Count(b, "Wood"), "no duplication");
                True(ctx.Errors > 0, "error reported");
            });
            T("safety: swap survives a save hook that throws", () =>
            {
                var a = Chest("Stone", w: 1, h: 1, items: new[] { ("Wood", 10) });
                var b = Chest("Wood", w: 1, h: 1, items: new[] { ("Stone", 7) });
                b.Inventory.m_onChanged += () => throw new Exception("another mod's save hook");
                var ctx = new SortContext();
                SortEngine.Swap(a.Inventory, a.Inventory.GetAllItems()[0], b.Inventory, b.Inventory.GetAllItems()[0], ctx);
                Eq(10, Count(a, "Wood") + Count(b, "Wood"), "wood kept");
                Eq(7, Count(a, "Stone") + Count(b, "Stone"), "stone kept");
            });
            T("safety: swap refuses a slot outside the other chest", () =>
            {
                // A chest that shrank (e.g. after a game update) can hold an item outside its grid.
                var a = Chest("Stone", w: 3, h: 1);
                var wood = Item("Wood"); wood.m_stack = 10;
                a.Inventory.AddItem(wood, new Vector2i(5, 0));
                var b = Chest("Wood", w: 1, h: 1, items: new[] { ("Stone", 7) });
                Eq(false, SortEngine.Swap(a.Inventory, wood, b.Inventory, b.Inventory.GetAllItems()[0], new SortContext()));
                Eq(10, Count(a, "Wood"));
                Eq(7, Count(b, "Stone"));
            });

            // --- findings from the review team, against the real list of 1071 Valheim item names
            T("names: a real item name never typo-matches a different item", () =>
            {
                var collisions = new List<string>();
                foreach (string name in RealNames)
                {
                    string target = SignFilter.FindTypoTarget(SignFilter.Compact(name), KnownNames);
                    if (target != null) collisions.Add($"{name} -> {target}");
                }
                Eq("", string.Join("; ", collisions.Take(10)));
            });
            T("names: reviewer's examples no longer cross-match", () =>
            {
                Eq(SignFilter.NoMatch, Score("Boar meat", Item("Bear Meat")));
                Eq(SignFilter.NoMatch, Score("Leather scraps", Item("Leather Straps")));
                Eq(SignFilter.NoMatch, Score("Minor healing mead", Item("Major Healing Mead")));
                Eq(SignFilter.NoMatch, Score("Birch seeds", Item("Beech Seeds")));
                Eq(SignFilter.NoMatch, Score("Bronze mace", Item("Bronze Axe")));
            });
            T("names: a typo picks only an unambiguous item", () =>
            {
                Eq(SignFilter.ScoreTypo, Score("Leather scrapz", Item("Leather Scraps")));
                Eq(SignFilter.NoMatch, Score("Leather scrapz", Item("Leather Straps")));
            });
            T("names: default aliases only name real items", () =>
            {
                var table = AliasTable.Parse(AliasFile.DefaultContents.Split('\n'), null);
                var bad = new List<string>();
                foreach (string line in AliasFile.DefaultContents.Split('\n'))
                {
                    int eq = line.IndexOf('=');
                    if (line.TrimStart().StartsWith("#") || eq < 0) continue;
                    foreach (string target in line.Substring(eq + 1).Split(','))
                    {
                        string t = target.Trim();
                        if (t.Contains("*")) continue;
                        if (!KnownNames.Any(k => SignFilter.SameWord(SignFilter.Compact(t), k))) bad.Add(t);
                    }
                }
                Eq("", string.Join(", ", bad));
            });
            T("names: arrows and bolts are different categories", () =>
            {
                var arrow = Item("Wood arrow", ItemType.Ammo, max: 100); arrow.m_shared.m_ammoType = "$ammo_arrows";
                var bolt = Item("Bone bolt", ItemType.Ammo, max: 100); bolt.m_shared.m_ammoType = "$ammo_bolts";
                Eq(SignFilter.ScoreCategory, Score("Arrows", arrow));
                Eq(SignFilter.NoMatch, Score("Arrows", bolt));
                Eq(SignFilter.ScoreCategory, Score("Bolts", bolt));
                Eq(SignFilter.ScoreCategory, Score("Ammo", bolt));
            });
            T("names: & / + separate entries", () =>
            {
                Eq(SignFilter.ScoreExact, Score("Wood & Stone", Item("Stone")));
                Eq(SignFilter.ScoreExact, Score("Wood/Stone", Item("Wood")));
            });
            T("names: a sign naming nothing protects its chest", () =>
            {
                var catalog = RealNames.Take(400).Select(n => new KeyValuePair<ItemData, ItemKeys>(Item(n), ItemKeys.Make(n, ""))).ToList();
                True(SignFilter.Parse("Bob's house", Settings()).NamesNothing(catalog), "Bob's house protected");
                True(SignFilter.Parse("Don't touch!", Settings()).NamesNothing(catalog), "Don't touch protected");
                True(!SignFilter.Parse("Amber, Bob", Settings()).NamesNothing(catalog), "Amber, Bob is a real label");
                True(!SignFilter.Parse("*", Settings()).NamesNothing(catalog), "* is a real label");
            });
            T("text: more TextMeshPro tags stripped", () => Arrow("<page>Wood", SignSorter.Arrow.None, "Wood"));
            T("geometry: a plain sign labels only its closest chest", () =>
            {
                var chests = new List<Vector3> { new Vector3(0, 0, 0), new Vector3(1.6f, 0, 0), new Vector3(3.2f, 0, 0) };
                var signs = new List<SignGeometry.SignInfo> { new SignGeometry.SignInfo { Position = new Vector3(0, 1f, 0), Body = "Wood" } };
                var labels = SignGeometry.AssignLabels(signs, chests, 2.5f, 4f);
                Eq(1, labels[0].Count);
                Eq(0, labels[1].Count);
            });
            T("geometry: ^ sign on a chest's front picks the chest above, not its own", () =>
            {
                // Chest middles at y=0.4 (own chest) and y=1.4 (chest above); sign low on the front of the bottom one.
                var chests = new List<Vector3> { new Vector3(0, 0.4f, 0), new Vector3(0, 1.4f, 0) };
                Eq(1, SignGeometry.ChooseArrowTarget(new Vector3(0, 0.6f, 0.3f), SignSorter.Arrow.Up, Vector3.right, chests, 4f));
            });

            // --- CollectAll: "get all the other Fine wood into this chest"
            T("collect: pulls every stack from other chests, nearest first", () =>
            {
                var target = Chest(null, ("Fine wood", 10));
                var near = Src(Chest(null, ("Fine wood", 30), ("Stone", 20)), 2f);
                var far = Src(Chest(null, ("Fine wood", 45), ("Fine wood", 5)), 9f);
                var r = Collect(target, "Fine wood", near, far);
                Eq(90, Count(target, "Fine wood"));
                Eq(80, r.Moved);
                Eq(2, r.ChestsTakenFrom);
                Eq(20, CountInv(near.Inventory, "Stone"), "other items stay");
                Eq(0, r.LeftBehind);
            });
            T("collect: stops when the chest is full and says how many are left", () =>
            {
                var target = Chest(null, w: 2, h: 1, items: new[] { ("Fine wood", 40), ("Resin", 1) });
                var a = Src(Chest(null, ("Fine wood", 30)), 1f);
                var b = Src(Chest(null, ("Fine wood", 25)), 5f);
                var r = Collect(target, "Fine wood", a, b);
                Eq(50, Count(target, "Fine wood"));
                Eq(10, r.Moved);
                Eq(45, r.LeftBehind);
                True(r.TargetFull, "target full");
            });
            T("collect: open chests are left alone", () =>
            {
                var target = Chest(null);
                var open = Src(Chest(null, ("Fine wood", 30)), 1f);
                open.IsBusy = () => true;
                var r = Collect(target, "Fine wood", open);
                Eq(0, r.Moved);
                Eq(1, r.BusySkipped);
                Eq(30, CountInv(open.Inventory, "Fine wood"));
            });
            T("collect: every quality of the item, gear keeps its data", () =>
            {
                var target = Chest(null);
                var src = Chest(null);
                var low = Item("Arrow", ItemType.Ammo, max: 100); low.m_stack = 10; low.m_quality = 1;
                var high = Item("Arrow", ItemType.Ammo, max: 100); high.m_stack = 10; high.m_quality = 2;
                var axe = Item("Bronze axe", ItemType.OneHandedWeapon, max: 1); axe.m_durability = 12f;
                src.Inventory.AddItem(low); src.Inventory.AddItem(high); src.Inventory.AddItem(axe);
                var r = Collect(target, "Arrow", Src(src, 1f));
                Eq(20, r.Moved);
                Eq(2, target.Inventory.GetAllItems().Count);
                Eq(1, src.Inventory.GetAllItems().Count, "axe stays");
                var axeTarget = Chest(null);
                Collect(axeTarget, "Bronze axe", Src(src, 1f));
                Eq(12f, axeTarget.Inventory.GetAllItems().Single().m_durability);
            });
            T("collect: the open chest itself is never a source", () =>
            {
                var target = Chest(null, ("Fine wood", 10));
                var r = Collect(target, "Fine wood", new CollectAll.CollectSource { Inventory = target.Inventory, Distance = 0f });
                Eq(0, r.Moved);
                Eq(10, Count(target, "Fine wood"));
            });
            T("collect: nothing nearby", () =>
            {
                var target = Chest(null, ("Fine wood", 10));
                var r = Collect(target, "Fine wood", Src(Chest(null, ("Stone", 5)), 1f));
                Eq(0, r.Moved);
                Eq(0, r.LeftBehind);
            });
            T("collect: survives save hooks that throw", () =>
            {
                var target = Chest(null);
                var a = Src(Chest(null, ("Fine wood", 30)), 1f);
                target.Inventory.m_onChanged += () => throw new Exception("another mod's save hook");
                var r = Collect(target, "Fine wood", a);
                Eq(30, Count(target, "Fine wood") + CountInv(a.Inventory, "Fine wood"), "no duplication or loss");
                True(r.Errors > 0, "error reported");
            });
            T("bigger pile: a chest with more of the item is left alone", () =>
            {
                var target = Chest(null, ("Bone fragments", 30));
                var stash = Src(Chest(null, items: new[] { ("Bone fragments", 50), ("Bone fragments", 50) }), 2f);
                var r = CollectWith(true, target, "Bone fragments", stash);
                Eq(0, r.Moved);
                Eq(1, r.BiggerSkipped);
                Eq(100, CountInv(stash.Inventory, "Bone fragments"));
            });
            T("bigger pile: smaller piles are pulled, and the chest grows as it goes", () =>
            {
                // Target 20. Piles: 15 (far), 10 (near), 40. Takes 10 -> 30, 15 -> 45, then 40 < 45 -> takes it too.
                var target = Chest(null, ("Bone fragments", 20));
                var near = Src(Chest(null, ("Bone fragments", 10)), 1f);
                var far = Src(Chest(null, ("Bone fragments", 15)), 20f);
                var big = Src(Chest(null, ("Bone fragments", 40)), 5f);
                var r = CollectWith(true, target, "Bone fragments", near, far, big);
                Eq(85, Count(target, "Bone fragments"));
                Eq(3, r.ChestsTakenFrom);
                Eq(0, r.BiggerSkipped);
            });
            T("bigger pile: collecting in two chests in turn never shuffles items", () =>
            {
                var a = Chest(null, items: new[] { ("Bone fragments", 50), ("Bone fragments", 50), ("Bone fragments", 50) });
                var b = Chest(null, ("Bone fragments", 20), ("Resin", 5));
                for (int i = 0; i < 4; i++)
                {
                    CollectWith(true, b, "Bone fragments", Src(a, 1f));
                    CollectWith(true, a, "Bone fragments", Src(b, 1f));
                }
                Eq(170, Count(a, "Bone fragments"));
                Eq(0, Count(b, "Bone fragments"));
                Eq(5, Count(b, "Resin"));
            });
            T("bigger pile: two equal piles can still be combined, once", () =>
            {
                var a = Chest(null, ("Bone fragments", 20));
                var b = Chest(null, ("Bone fragments", 20));
                Eq(20, CollectWith(true, b, "Bone fragments", Src(a, 1f)).Moved);
                Eq(0, CollectWith(true, a, "Bone fragments", Src(b, 1f)).Moved);
            });
            T("full chests: a chest full of only that item is left alone", () =>
            {
                var target = Chest(null, ("Bone fragments", 30));
                var stash = Src(Chest(null, w: 2, h: 1, items: new[] { ("Bone fragments", 50), ("Bone fragments", 50) }), 2f);
                var r = CollectWith2(false, true, target, "Bone fragments", stash);
                Eq(0, r.Moved);
                Eq(1, r.FullSkipped);
                Eq(100, CountInv(stash.Inventory, "Bone fragments"));
            });
            T("full chests: one stray item means it's collected from", () =>
            {
                var target = Chest(null, ("Bone fragments", 30));
                var mixed = Src(Chest(null, w: 2, h: 1, items: new[] { ("Bone fragments", 50), ("Resin", 1) }), 2f);
                var r = CollectWith2(false, true, target, "Bone fragments", mixed);
                Eq(50, r.Moved);
                Eq(1, CountInv(mixed.Inventory, "Resin"));
            });
            T("full chests: a partial stack or empty slot means it's collected from", () =>
            {
                var target = Chest(null, ("Bone fragments", 30));
                var partial = Src(Chest(null, w: 2, h: 1, items: new[] { ("Bone fragments", 50), ("Bone fragments", 49) }), 2f);
                var roomy = Src(Chest(null, w: 3, h: 1, items: new[] { ("Bone fragments", 50), ("Bone fragments", 50) }), 3f);
                Eq(199, CollectWith2(false, true, target, "Bone fragments", partial, roomy).Moved);
            });
            T("full chests: off still empties a full stash", () =>
            {
                var target = Chest(null, ("Bone fragments", 30));
                var stash = Src(Chest(null, w: 1, h: 1, items: new[] { ("Bone fragments", 50) }), 2f);
                Eq(50, CollectWith2(false, false, target, "Bone fragments", stash).Moved);
            });
            T("bigger pile: off pulls from every chest", () =>
            {
                var target = Chest(null, ("Bone fragments", 30));
                var stash = Src(Chest(null, ("Bone fragments", 50)), 2f);
                Eq(50, CollectWith(false, target, "Bone fragments", stash).Moved);
            });
            T("fuzz: 3000 random collects conserve items and take everything that fits", () => FuzzCollect(3000));

            // --- randomized: thousands of messy bases, nothing may ever be lost or duplicated.
            T("fuzz: 3000 random bases conserve every item and settle", () => Fuzz(3000));
            T("fuzz: 1500 bases with randomly failing save hooks still conserve items", () => FuzzThrowing(1500));

            foreach (var t in tests)
            {
                try
                {
                    SignFilter.ClearCache();
                    t.Value();
                    _passed++;
                    System.Console.WriteLine("  ok   " + t.Key);
                }
                catch (Exception e)
                {
                    _failed++;
                    System.Console.WriteLine("  FAIL " + t.Key + "\n       " + e.ToString().Replace("\n", "\n       "));
                }
            }
            System.Console.WriteLine($"\n{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- demo

        /// <summary>The "messy world" example from docs/guide.html, run through the real engine.</summary>
        private static int Demo()
        {
            SortChest Messy(string label, params (string, int)[] items)
            {
                var c = Chest(label, 5, 2, null);
                foreach (var (name, count) in items)
                {
                    ItemData it = name == "Cooked meat" || name == "Raspberries"
                        ? Item(name, ItemType.Consumable, food: 20, max: 50)
                        : Item(name);
                    it.m_stack = count;
                    c.Inventory.AddItem(it, NextFree(c.Inventory));
                }
                return c;
            }

            var wood = Messy("Wood", ("Stone", 50), ("Stone", 50), ("Copper ore", 30), ("Wood", 12), ("Feathers", 9),
                ("Wood", 7), ("Cooked meat", 4), ("Flint", 16), ("Wood", 31), ("Tin ore", 22));
            var stone = Messy("Stone, Flint", ("Wood", 50), ("Wood", 50), ("Fine wood", 20), ("Stone", 5), ("Raspberries", 13),
                ("Copper ore", 11), ("Wood", 40), ("Troll hide", 3), ("Wood", 50), ("Wood", 50));
            var ores = Messy("Copper ore, Tin ore", ("Stone", 50), ("Stone", 50), ("Stone", 50), ("Wood", 50), ("Stone", 45),
                ("Wood", 20), ("Flint", 8), ("Stone", 50), ("Stone", 50), ("Stone", 50));
            var star = Messy("*");
            var chests = new List<SortChest> { wood, stone, ores, star };

            var before = Describe(Totals(chests));
            var look = new SortContext { Apply = false };
            SortEngine.SortChests(chests, look, 1);
            System.Console.WriteLine($"preview={look.ItemsMoved}");
            var ctx = new SortContext();
            int passes = SortEngine.SortChests(chests, ctx, 12);
            System.Console.WriteLine($"passes={passes} moved={ctx.ItemsMoved} swaps={ctx.Swaps} merged={ctx.StacksMerged} chests={ctx.Received.Count} errors={ctx.Errors}");
            System.Console.WriteLine("conserved=" + (before == Describe(Totals(chests))));
            foreach (var (name, c) in new[] { ("Wood", wood), ("Stone, Flint", stone), ("Copper ore, Tin ore", ores), ("*", star) })
            {
                System.Console.WriteLine($"[{name}]");
                for (int y = 0; y < 2; y++)
                {
                    var cells = new List<string>();
                    for (int x = 0; x < 5; x++)
                    {
                        var it = c.Inventory.GetItemAt(x, y);
                        cells.Add(it == null ? "-" : $"{it.m_shared.m_name}:{it.m_stack}");
                    }
                    System.Console.WriteLine("  " + string.Join(" | ", cells));
                }
            }
            var left = SortEngine.Leftovers(chests).Select(i => i.m_shared.m_name).Distinct();
            System.Console.WriteLine("leftovers=" + string.Join(", ", left));
            return 0;
        }

        /// <summary>The CollectAll example from docs/collectall.html, run through the real logic.</summary>
        private static int CollectDemo()
        {
            var target = Chest(null, items: new[] { ("Fine wood", 12), ("Core wood", 30), ("Resin", 18) });
            var a = Src(Chest(null, ("Wood", 50), ("Fine wood", 40), ("Stone", 50), ("Fine wood", 7)), 3f);
            var b = Src(Chest(null, ("Fine wood", 50), ("Flint", 20)), 6f);
            var c = Src(Chest(null, ("Feathers", 9), ("Fine wood", 23)), 11f);
            var r = Collect(target, "Fine wood", a, b, c);
            System.Console.WriteLine($"moved={r.Moved} chests={r.ChestsTakenFrom} left={r.LeftBehind} busy={r.BusySkipped} errors={r.Errors}");
            System.Console.WriteLine("message=" + CollectAll_Describe(r));
            void Dump(string name, Inventory inv)
            {
                var cells = new List<string>();
                for (int y = 0; y < inv.GetHeight(); y++)
                    for (int x = 0; x < inv.GetWidth(); x++)
                    {
                        var it = inv.GetItemAt(x, y);
                        cells.Add(it == null ? "-" : $"{it.m_shared.m_name}:{it.m_stack}");
                    }
                System.Console.WriteLine($"[{name}] " + string.Join(" | ", cells));
            }
            Dump("target", target.Inventory); Dump("a", a.Inventory); Dump("b", b.Inventory); Dump("c", c.Inventory);
            return 0;
        }

        private static string CollectAll_Describe(CollectAll.CollectResult r) =>
            $"Collected {r.Moved} Fine wood from {r.ChestsTakenFrom} chests" + (r.TargetFull ? $" / full, {r.LeftBehind} left" : "");

        // ---------------------------------------------------------------- fuzz

        private static readonly string[] FuzzItems = { "Wood", "Fine wood", "Core wood", "Stone", "Resin", "Flint", "Feathers", "Copper", "Tin", "Bronze axe", "Cooked meat", "Minor healing mead", "Arrow" };
        private static readonly string[] FuzzLabels = { null, null, "Wood", "*wood", "Stone", "Resin, Flint", "Materials", "Food", "Meads", "Weapons", "Metal", "*", "Ammo", "Fine wod", "Feathers, Stone" };

        private static void Fuzz(int rounds)
        {
            var rng = new System.Random(12345);
            var settings = Settings(aliases: "Metal = Copper, Tin");
            for (int round = 0; round < rounds; round++)
            {
                int chestCount = rng.Next(1, 7);
                var chests = new List<SortChest>();
                for (int c = 0; c < chestCount; c++)
                {
                    string label = FuzzLabels[rng.Next(FuzzLabels.Length)];
                    var chest = Chest(label, w: rng.Next(1, 5), h: rng.Next(1, 3), settings: settings);
                    chest.Position = new Vector3(rng.Next(-10, 10), 0, rng.Next(-10, 10));
                    if (rng.Next(20) == 0) chest.IsBusy = () => true;
                    int fill = rng.Next(0, chest.Inventory.GetWidth() * chest.Inventory.GetHeight() + 1);
                    for (int k = 0; k < fill; k++)
                    {
                        ItemData item = FuzzItem(rng);
                        if (!chest.Inventory.HaveEmptySlot()) break;
                        chest.Inventory.AddItem(item);
                    }
                    chests.Add(chest);
                }

                var before = Totals(chests);
                var busyBefore = chests.Where(c => c.Busy).Select(c => Snapshot(c.Inventory)).ToList();
                var ctx = new SortContext
                {
                    PullFromUnlabeled = rng.Next(2) == 0,
                    AllowSwaps = rng.Next(4) != 0,
                    MergeStacks = rng.Next(3) != 0,
                    Warn = msg => throw new Exception("warning: " + msg),
                };
                SortEngine.SortChests(chests, ctx, 50);

                var after = Totals(chests);
                string where = $"round {round}";
                Eq(Describe(before), Describe(after), where + " totals");
                Eq(0, ctx.Errors, where + " errors");
                var busyAfter = chests.Where(c => c.Busy).Select(c => Snapshot(c.Inventory)).ToList();
                Eq(string.Join("|", busyBefore.ToArray()), string.Join("|", busyAfter.ToArray()), where + " busy chest untouched");

                foreach (var chest in chests) CheckGrid(chest.Inventory, where);

                // Settled: another full sort finds nothing to do.
                var again = new SortContext { PullFromUnlabeled = ctx.PullFromUnlabeled, AllowSwaps = ctx.AllowSwaps, MergeStacks = ctx.MergeStacks };
                SortEngine.SortChests(chests, again, 50);
                Eq(0, again.ItemsMoved + again.Swaps + again.StacksMerged, where + " settled");
            }
        }

        private static CollectAll.CollectSource Src(SortChest chest, float distance) =>
            new CollectAll.CollectSource { Inventory = chest.Inventory, Distance = distance };

        // Most collect tests check the moving itself, so they run with "bigger pile wins" off.
        private static CollectAll.CollectResult Collect(SortChest target, string itemName, params CollectAll.CollectSource[] sources) =>
            CollectWith(false, target, itemName, sources);

        private static CollectAll.CollectResult CollectWith(bool skipBiggerPiles, SortChest target, string itemName, params CollectAll.CollectSource[] sources) =>
            CollectWith2(skipBiggerPiles, false, target, itemName, sources);

        private static CollectAll.CollectResult CollectWith2(bool skipBiggerPiles, bool skipFullChests, SortChest target, string itemName, params CollectAll.CollectSource[] sources)
        {
            var example = target.Inventory.GetAllItems().FirstOrDefault(i => i.m_shared.m_name == itemName)
                ?? sources.SelectMany(s => s.Inventory.GetAllItems()).FirstOrDefault(i => i.m_shared.m_name == itemName)
                ?? Item(itemName);
            var all = new List<Inventory> { target.Inventory };
            all.AddRange(sources.Select(s => s.Inventory).Where(i => !ReferenceEquals(i, target.Inventory)));
            var before = Describe(TotalsOf(all));
            var r = CollectAll.Collector.Collect(target.Inventory, example, sources, (w, e) => { }, skipBiggerPiles, skipFullChests);
            Eq(before, Describe(TotalsOf(all)), "items conserved");
            foreach (var inv in all) CheckGrid(inv, "after collect");
            return r;
        }

        private static Dictionary<string, int> TotalsOf(IEnumerable<Inventory> invs) =>
            Totals(invs.Select(i => new SortChest { Inventory = i }));

        private static void FuzzCollect(int rounds)
        {
            var rng = new System.Random(4242);
            for (int round = 0; round < rounds; round++)
            {
                var target = Chest(null, w: rng.Next(1, 5), h: rng.Next(1, 3));
                for (int k = rng.Next(0, 6); k > 0 && target.Inventory.HaveEmptySlot(); k--) target.Inventory.AddItem(FuzzItem(rng));
                var sources = new List<CollectAll.CollectSource>();
                for (int c = rng.Next(0, 6); c > 0; c--)
                {
                    var chest = Chest(null, w: rng.Next(1, 5), h: rng.Next(1, 3));
                    for (int k = rng.Next(0, 9); k > 0 && chest.Inventory.HaveEmptySlot(); k--) chest.Inventory.AddItem(FuzzItem(rng));
                    var src = Src(chest, rng.Next(0, 30));
                    if (rng.Next(10) == 0) src.IsBusy = () => true;
                    sources.Add(src);
                }
                string name = FuzzItems[rng.Next(FuzzItems.Length)];
                bool bigger = rng.Next(2) == 0;
                bool full = rng.Next(2) == 0;
                // A few full single-item chests, so the full-chest rule actually gets exercised.
                if (rng.Next(4) == 0)
                {
                    var stash = Chest(null, w: rng.Next(1, 3), h: 1);
                    while (stash.Inventory.HaveEmptySlot())
                    {
                        var it = FuzzItem(rng);
                        it.m_shared = sources.SelectMany(x => x.Inventory.GetAllItems()).Select(i => i.m_shared).FirstOrDefault(sh => sh.m_name == name) ?? it.m_shared;
                        it.m_stack = it.m_shared.m_maxStackSize;
                        stash.Inventory.AddItem(it);
                    }
                    sources.Add(Src(stash, rng.Next(0, 30)));
                }
                var r = CollectWith2(bigger, full, target, name, sources.ToArray());
                string where = $"round {round}";
                Eq(0, r.Errors, where + " errors");

                // Whatever's left in a reachable chest must be something the target has no room for
                // (or, with "bigger pile wins", sit in a chest that still has more than the target).
                int targetHas = CollectAll.Collector.CountOf(target.Inventory, name);
                foreach (var src in sources.Where(s => !s.Busy))
                {
                    if (bigger && CollectAll.Collector.CountOf(src.Inventory, name) > targetHas) continue;
                    if (full && CollectAll.Collector.IsFullOfOnly(src.Inventory, name)) continue;
                    foreach (var left in src.Inventory.GetAllItems().Where(i => i.m_shared.m_name == name))
                        Eq(0, ChestShared.InventoryOps.FreeSpaceFor(target.Inventory, left), $"{where}: {name} left behind although it fits");
                }

                // And with "bigger pile wins", collecting back into a source only moves items toward an
                // equal or bigger pile, and after that collecting into the target again moves nothing.
                if (bigger)
                {
                    foreach (var src in sources.Where(s => !s.Busy))
                    {
                        int srcHad = CollectAll.Collector.CountOf(src.Inventory, name);
                        int targetHad = CollectAll.Collector.CountOf(target.Inventory, name);
                        var back = CollectWith(true, new SortChest { Inventory = src.Inventory }, name, Src(target, 1f));
                        if (back.Moved > 0) True(targetHad <= srcHad, $"{where}: items flowed out of a bigger pile ({targetHad} > {srcHad})");
                        var again = CollectWith(true, target, name, Src(new SortChest { Inventory = src.Inventory }, 1f));
                        if (back.Moved > 0) Eq(0, again.Moved, $"{where}: items shuffled back and forth");
                    }
                }
            }
        }

        private static void FuzzThrowing(int rounds)
        {
            var rng = new System.Random(777);
            var settings = Settings();
            for (int round = 0; round < rounds; round++)
            {
                var chests = new List<SortChest>();
                int chestCount = rng.Next(2, 6);
                for (int c = 0; c < chestCount; c++)
                {
                    var chest = Chest(FuzzLabels[rng.Next(FuzzLabels.Length)], w: rng.Next(1, 4), h: rng.Next(1, 3), settings: settings);
                    int fill = rng.Next(0, chest.Inventory.GetWidth() * chest.Inventory.GetHeight() + 1);
                    for (int k = 0; k < fill && chest.Inventory.HaveEmptySlot(); k++) chest.Inventory.AddItem(FuzzItem(rng));
                    chests.Add(chest);
                }
                var before = Describe(Totals(chests));
                // Hooks installed after setup; each change has a 15% chance to throw.
                foreach (var chest in chests)
                    chest.Inventory.m_onChanged += () => { if (rng.Next(100) < 15) throw new Exception("flaky hook"); };

                bool debug = Environment.GetEnvironmentVariable("FUZZ_DEBUG") == round.ToString();
                var ctx = new SortContext { Warn = m => { if (debug) System.Console.WriteLine("   warn " + m + "   now: " + Describe(Totals(chests))); } };
                if (debug) foreach (var c in chests) System.Console.WriteLine($"   chest[{c.Filter?.IsEmpty}] {Snapshot(c.Inventory)}");
                SortEngine.SortChests(chests, ctx, 20);
                Eq(before, Describe(Totals(chests)), $"round {round} totals");
                foreach (var chest in chests) CheckGrid(chest.Inventory, $"round {round}");
            }
        }

        private static ItemData FuzzItem(System.Random rng)
        {
            string name = FuzzItems[rng.Next(FuzzItems.Length)];
            ItemData item;
            switch (name)
            {
                case "Bronze axe": item = Item(name, ItemType.OneHandedWeapon, max: 1); break;
                case "Cooked meat": item = Item(name, ItemType.Consumable, food: 25, max: 20); break;
                case "Minor healing mead": item = Item(name, ItemType.Consumable, max: 10); break;
                case "Arrow": item = Item(name, ItemType.Ammo, max: 100); item.m_quality = rng.Next(1, 3); break;
                default: item = Item(name); break;
            }
            item.m_stack = rng.Next(1, item.m_shared.m_maxStackSize + 1);
            if (rng.Next(10) == 0) item.m_worldLevel = 1;
            return item;
        }

        private static Dictionary<string, int> Totals(IEnumerable<SortChest> chests)
        {
            var d = new Dictionary<string, int>();
            foreach (var c in chests)
                foreach (var i in c.Inventory.GetAllItems())
                {
                    string k = $"{i.m_shared.m_name}/q{i.m_quality}/w{i.m_worldLevel}";
                    d.TryGetValue(k, out int n);
                    d[k] = n + i.m_stack;
                }
            return d;
        }

        private static string Describe(Dictionary<string, int> d) =>
            string.Join(", ", d.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}").ToArray());

        private static string Snapshot(Inventory inv) =>
            string.Join(";", inv.GetAllItems().Select(i => $"{i.m_shared.m_name}@{i.m_gridPos.x},{i.m_gridPos.y}x{i.m_stack}").ToArray());

        private static void CheckGrid(Inventory inv, string where)
        {
            var seen = new HashSet<string>();
            foreach (var i in inv.GetAllItems())
            {
                True(i.m_stack > 0, $"{where}: empty stack left behind");
                True(i.m_stack <= i.m_shared.m_maxStackSize, $"{where}: overfull stack {i.m_stack}");
                True(i.m_gridPos.x >= 0 && i.m_gridPos.x < inv.GetWidth() && i.m_gridPos.y >= 0 && i.m_gridPos.y < inv.GetHeight(), $"{where}: item off the grid");
                True(seen.Add($"{i.m_gridPos.x},{i.m_gridPos.y}"), $"{where}: two items in one slot");
            }
        }

        // ---------------------------------------------------------------- helpers

        private static readonly Dictionary<string, ItemData.SharedData> Shared = new Dictionary<string, ItemData.SharedData>();

        private static ItemData Item(string name, ItemType type = ItemType.Material, float food = 0f, int max = 50)
        {
            string key = $"{name}|{type}|{food}|{max}";
            if (!Shared.TryGetValue(key, out var shared))
            {
                shared = new ItemData.SharedData { m_name = name, m_itemType = type, m_food = food, m_maxStackSize = max, m_weight = 1f };
                Shared[key] = shared;
            }
            return new ItemData { m_shared = shared, m_stack = 1, m_quality = 1 };
        }

        // The game's real English item names (from its localization), for typo/collision tests.
        private static readonly List<string> RealNames = LoadRealNames();
        private static readonly List<string> KnownNames = RealNames.Select(n => SignFilter.Compact(n)).Distinct().ToList();

        private static List<string> LoadRealNames()
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "valheim_item_names.txt");
            if (!System.IO.File.Exists(path)) path = System.IO.Path.Combine("TestData", "valheim_item_names.txt");
            return System.IO.File.ReadAllLines(path)
                .Select(l => l.Split('\t'))
                .Where(p => p.Length == 2 && p[1].Trim().Length > 0)
                .Select(p => p[1].Trim())
                .Distinct()
                .ToList();
        }

        private static MatchSettings Settings(string aliases = null, bool typos = true) => new MatchSettings
        {
            TypoTolerance = typos,
            KnownNames = KnownNames,
            Aliases = aliases == null ? AliasTable.Empty : AliasTable.Parse(aliases.Split('\n'), null),
        };

        private static int Score(string sign, ItemData item, bool typos = true)
        {
            SignFilter.ClearCache();
            return SignFilter.Parse(sign, Settings(typos: typos)).Score(item);
        }

        private static SortChest Chest(string label, params (string name, int count)[] items) => Chest(label, 5, 2, items);

        private static SortChest Chest(string label, int w = 5, int h = 2, (string name, int count)[] items = null, MatchSettings settings = null)
        {
            var inv = new Inventory("chest", null, w, h);
            if (items != null)
            {
                foreach (var (name, count) in items)
                {
                    var item = Item(name);
                    item.m_stack = count;
                    // Place each stack in its own slot, like a messy chest would have.
                    if (!inv.AddItem(item, NextFree(inv))) throw new Exception("test setup: chest too small");
                }
            }
            return new SortChest
            {
                Inventory = inv,
                Filter = label == null ? null : SignFilter.Parse(label, settings ?? Settings()),
                Position = Vector3.zero,
            };
        }

        private static Vector2i NextFree(Inventory inv)
        {
            for (int y = 0; y < inv.GetHeight(); y++)
                for (int x = 0; x < inv.GetWidth(); x++)
                    if (inv.GetItemAt(x, y) == null) return new Vector2i(x, y);
            return new Vector2i(-1, -1);
        }

        private static SortContext Run(params SortChest[] chests) => Run(chests, null);

        private static SortContext Run(SortChest[] chests, Action<SortContext> setup, int passes = 4)
        {
            var ctx = new SortContext { Warn = msg => System.Console.WriteLine("       warn: " + msg) };
            setup?.Invoke(ctx);
            var before = Totals(chests);
            SortEngine.SortChests(chests.ToList(), ctx, passes);
            Eq(Describe(before), Describe(Totals(chests)), "items conserved");
            foreach (var c in chests) CheckGrid(c.Inventory, "after sort");
            return ctx;
        }

        private static int Count(SortChest chest, string name) => CountInv(chest.Inventory, name);

        private static int CountInv(Inventory inv, string name) =>
            inv.GetAllItems().Where(i => i.m_shared.m_name == name).Sum(i => i.m_stack);

        private static void Arrow(string text, SignSorter.Arrow expected, string body)
        {
            var got = SignText.ExtractArrow(text, out string b);
            Eq(expected, got, $"arrow of '{text}'");
            Eq(body, b, $"body of '{text}'");
        }

        private static void Eq<T>(T expected, T actual, string what = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"{what ?? "value"}: expected <{expected}> got <{actual}>");
        }

        private static void True(bool cond, string what)
        {
            if (!cond) throw new Exception("expected: " + what);
        }
    }
}
