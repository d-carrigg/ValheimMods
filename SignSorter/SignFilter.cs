using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SignSorter
{
    /// <summary>Settings that change how sign text is understood.</summary>
    internal sealed class MatchSettings
    {
        public AliasTable Aliases = AliasTable.Empty;
        public bool TypoTolerance = true;
        public string IgnoreTag = "nosort";
        /// <summary>
        /// Every real item name (compacted). Typo matching needs it: a sign entry that is already
        /// a real name never fuzzy-matches another item, and a typo must point at exactly one
        /// item. When null, typo matching is off.
        /// </summary>
        public List<string> KnownNames;
    }

    /// <summary>The names an item can be referred to by, compacted (lowercase letters/digits).</summary>
    internal sealed class ItemKeys
    {
        /// <summary>Turns "$item_wood" into "Wood". Set by the plugin at runtime; null in tests.</summary>
        public static Func<string, string> Localize;

        // Keyed by the item's SharedData object: the game shares one per item type, so this is
        // a cheap reference lookup instead of building strings for every item every sort.
        private static readonly Dictionary<ItemDrop.ItemData.SharedData, ItemKeys> Cache =
            new Dictionary<ItemDrop.ItemData.SharedData, ItemKeys>();
        public readonly List<string> All = new List<string>(3);

        public static ItemKeys Make(string sharedName, string prefabName)
        {
            var keys = new ItemKeys();
            string localized = null;
            if (Localize != null && !string.IsNullOrEmpty(sharedName))
            {
                try { localized = Localize(sharedName); }
                catch { localized = null; }
            }
            keys.Add(localized);
            keys.Add(prefabName);
            keys.Add(sharedName); // e.g. "$item_wood", or a plain name in tests
            return keys;
        }

        public static ItemKeys For(ItemDrop.ItemData item)
        {
            ItemDrop.ItemData.SharedData shared = item?.m_shared;
            if (shared == null) return new ItemKeys();
            if (Cache.TryGetValue(shared, out var keys)) return keys;

            string prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : "";
            keys = Make(shared.m_name, prefab);
            if (Cache.Count > 5000) Cache.Clear();
            Cache[shared] = keys;
            return keys;
        }

        public static void ClearCache() => Cache.Clear();

        private void Add(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            string c = SignFilter.Compact(s);
            if (c.Length > 0 && !All.Contains(c)) All.Add(c);
        }
    }

    /// <summary>
    /// The parsed text of one or more signs. Each comma/semicolon/newline separated entry is a
    /// rule. Score() says how specifically the sign asks for an item, so a chest whose sign
    /// names an item exactly beats a misspelled name, which beats a wildcard, which beats a
    /// category, which beats a "*" catch-all.
    /// </summary>
    internal sealed class SignFilter
    {
        public const int NoMatch = -1;
        public const int ScoreCatchAll = 0;
        public const int ScoreCategory = 1;
        public const int ScoreWildcard = 2;
        public const int ScoreTypo = 3;
        public const int ScoreExact = 4;

        private const int MaxAliasDepth = 8;
        // '&', '/' and '+' too, so "Wood & Stone" and "Wood/Stone" mean two entries.
        private static readonly char[] Separators = { ',', ';', '\n', '\r', '|', '&', '/', '+' };

        private enum Kind { Exact, Wildcard, CatchAll, CategoryOnly }

        private sealed class Rule
        {
            public Kind Kind;
            public string Raw;       // as written, for error messages
            public string Key;       // compacted
            public Regex Pattern;    // wildcards
            public Func<ItemDrop.ItemData, bool> Category;
            public string TypoTarget; // the one real item name this misspelling stands for
        }

        private readonly List<Rule> _rules = new List<Rule>();
        // Score depends only on the item type, so remember it per SharedData.
        private readonly Dictionary<ItemDrop.ItemData.SharedData, int> _scoreCache = new Dictionary<ItemDrop.ItemData.SharedData, int>();
        private List<string> _unknownCache;
        private int _unknownCatalogSize = -1;

        public bool Ignore { get; private set; }
        public bool IsEmpty => _rules.Count == 0;

        private static readonly Dictionary<string, SignFilter> ParseCache = new Dictionary<string, SignFilter>();

        public static void ClearCache()
        {
            ParseCache.Clear();
        }

        /// <summary>Parses sign text (already stripped of any arrow). Results are cached by text.</summary>
        public static SignFilter Parse(string text, MatchSettings settings)
        {
            text = text ?? "";
            if (ParseCache.TryGetValue(text, out var cached)) return cached;

            var filter = new SignFilter();
            string ignore = Compact(settings.IgnoreTag ?? "");
            string clean = SignText.StripRichText(text);

            foreach (string part in clean.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                string raw = part.Trim();
                if (raw.Length == 0) continue;
                filter.AddToken(raw, settings, ignore, 0, null);
            }

            if (ParseCache.Count > 2000) ParseCache.Clear(); // signs can be edited forever; keep it bounded
            ParseCache[text] = filter;
            return filter;
        }

        private void AddToken(string raw, MatchSettings settings, string ignore, int depth, HashSet<string> seenAliases)
        {
            string token = Compact(raw, keepStar: true);
            if (token.Length == 0) return;

            if (ignore.Length > 0 && token == ignore)
            {
                Ignore = true;
                return;
            }

            if (token.Trim('*').Length == 0)
            {
                _rules.Add(new Rule { Kind = Kind.CatchAll, Raw = raw, Key = token });
                return;
            }

            if (token.IndexOf('*') < 0 && depth < MaxAliasDepth
                && settings.Aliases != null && settings.Aliases.TryGet(token, out var targets))
            {
                seenAliases = seenAliases ?? new HashSet<string>();
                if (seenAliases.Add(token))
                {
                    foreach (string target in targets)
                        AddToken(target, settings, ignore, depth + 1, seenAliases);
                    seenAliases.Remove(token);
                    return;
                }
                // Alias loop: fall through and treat it as a plain name.
            }

            if (token.IndexOf('*') >= 0)
            {
                string pattern = "^" + Regex.Escape(token).Replace("\\*", ".*") + "$";
                _rules.Add(new Rule
                {
                    Kind = Kind.Wildcard,
                    Raw = raw,
                    Key = token,
                    Pattern = new Regex(pattern, RegexOptions.CultureInvariant),
                });
                return;
            }

            Categories.TryGetValue(token, out var category);
            _rules.Add(new Rule
            {
                Kind = Kind.Exact,
                Raw = raw,
                Key = token,
                Category = category,
                TypoTarget = settings.TypoTolerance && category == null ? FindTypoTarget(token, settings.KnownNames) : null,
            });
        }

        /// <summary>
        /// If <paramref name="token"/> isn't a real item name but is a near-miss of exactly one,
        /// returns that name. "Fine wod" -> "finewood"; "Boar meat" -> null (it's a real item, and
        /// must not also grab "Bear meat").
        /// </summary>
        internal static string FindTypoTarget(string token, List<string> known)
        {
            if (known == null || known.Count == 0 || token.Length < 5) return null;
            foreach (string name in known)
                if (SameWord(token, name)) return null;

            int allowed = token.Length >= 10 ? 2 : 1;
            int best = allowed + 1;
            string target = null;
            bool tie = false;
            foreach (string name in known)
            {
                if (Math.Abs(name.Length - token.Length) > allowed || name.Length < 4) continue;
                int d = EditDistance(token, name, allowed);
                if (d > allowed) continue;
                if (d < best)
                {
                    best = d;
                    target = name;
                    tie = false;
                }
                else if (d == best && name != target)
                {
                    tie = true;
                }
            }
            return tie ? null : target;
        }

        public int Score(ItemDrop.ItemData item)
        {
            ItemDrop.ItemData.SharedData shared = item?.m_shared;
            if (shared == null) return NoMatch;
            if (_scoreCache.TryGetValue(shared, out int cached)) return cached;
            int score = Score(item, ItemKeys.For(item));
            if (_scoreCache.Count > 5000) _scoreCache.Clear();
            _scoreCache[shared] = score;
            return score;
        }

        public int Score(ItemDrop.ItemData item, ItemKeys keys)
        {
            int best = NoMatch;
            foreach (Rule rule in _rules)
            {
                int s = Match(rule, item, keys, includeCategory: true);
                if (s > best)
                {
                    best = s;
                    if (best == ScoreExact) break;
                }
            }
            return best;
        }

        private static int Match(Rule rule, ItemDrop.ItemData item, ItemKeys keys, bool includeCategory)
        {
            switch (rule.Kind)
            {
                case Kind.CatchAll:
                    return ScoreCatchAll;

                case Kind.Wildcard:
                    foreach (string key in keys.All)
                        if (rule.Pattern.IsMatch(key)) return ScoreWildcard;
                    return NoMatch;

                case Kind.Exact:
                    foreach (string key in keys.All)
                        if (SameWord(rule.Key, key)) return ScoreExact;
                    if (rule.TypoTarget != null)
                        foreach (string key in keys.All)
                            if (key == rule.TypoTarget) return ScoreTypo;
                    if (includeCategory && rule.Category != null && item != null)
                    {
                        try
                        {
                            if (rule.Category(item)) return ScoreCategory;
                        }
                        catch
                        {
                            return NoMatch;
                        }
                    }
                    return NoMatch;

                default:
                    return NoMatch;
            }
        }

        /// <summary>
        /// Entries on this sign that don't name any known item (and aren't categories or "*").
        /// Used to tell the player about typos the matcher couldn't rescue.
        /// </summary>
        public List<string> UnknownEntries(IList<KeyValuePair<ItemDrop.ItemData, ItemKeys>> catalog)
        {
            if (catalog == null || catalog.Count == 0) return new List<string>();
            if (_unknownCache != null && _unknownCatalogSize == catalog.Count) return _unknownCache;
            var unknown = new List<string>();
            foreach (Rule rule in _rules)
            {
                if (rule.Kind == Kind.CatchAll) continue;
                if (rule.Category != null) continue;
                bool found = false;
                foreach (var entry in catalog)
                {
                    if (Match(rule, entry.Key, entry.Value, includeCategory: false) > NoMatch)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found && !unknown.Contains(rule.Raw)) unknown.Add(rule.Raw);
            }
            _unknownCache = unknown;
            _unknownCatalogSize = catalog.Count;
            return unknown;
        }

        /// <summary>
        /// True when nothing on the sign names a real item, category or "*" - e.g. "Bob's house"
        /// or "Don't touch". Such a chest is left alone rather than treated as labeled.
        /// </summary>
        public bool NamesNothing(IList<KeyValuePair<ItemDrop.ItemData, ItemKeys>> catalog)
        {
            if (_rules.Count == 0 || catalog == null || catalog.Count == 0) return false;
            foreach (Rule rule in _rules)
                if (rule.Kind == Kind.CatchAll || rule.Category != null) return false;
            return UnknownEntries(catalog).Count >= CountDistinctRaw();
        }

        private int CountDistinctRaw()
        {
            var seen = new HashSet<string>();
            foreach (Rule rule in _rules) seen.Add(rule.Raw);
            return seen.Count;
        }

        /// <summary>Same word, allowing simple plurals: Stone/Stones, Torch/Torches, Raspberry/Raspberries.</summary>
        internal static bool SameWord(string token, string key)
        {
            if (token == key) return true;
            return IsPluralOf(token, key) || IsPluralOf(key, token);
        }

        private static bool IsPluralOf(string plural, string singular)
        {
            int p = plural.Length, s = singular.Length;
            if (s == 0) return false;
            // singular + "s"
            if (p == s + 1 && plural[p - 1] == 's' && string.CompareOrdinal(plural, 0, singular, 0, s) == 0) return true;
            // singular + "es"
            if (p == s + 2 && plural[p - 2] == 'e' && plural[p - 1] == 's' && string.CompareOrdinal(plural, 0, singular, 0, s) == 0) return true;
            // "berry" -> "berries"
            if (p == s + 2 && singular[s - 1] == 'y' && plural[p - 3] == 'i' && plural[p - 2] == 'e' && plural[p - 1] == 's'
                && string.CompareOrdinal(plural, 0, singular, 0, s - 1) == 0) return true;
            return false;
        }

        /// <summary>Allows one typo for words of 5+ letters, two for 10+.</summary>
        internal static bool CloseEnough(string token, string key)
        {
            int allowed = token.Length >= 10 ? 2 : token.Length >= 5 ? 1 : 0;
            if (allowed == 0 || key.Length < 4) return false;
            if (Math.Abs(token.Length - key.Length) > allowed) return false;
            return EditDistance(token, key, allowed) <= allowed;
        }

        [ThreadStatic] private static int[] _d0, _d1, _d2;

        /// <summary>Optimal-string-alignment distance, bailing out once it exceeds <paramref name="max"/>.</summary>
        internal static int EditDistance(string a, string b, int max)
        {
            int n = a.Length, m = b.Length;
            if (_d0 == null || _d0.Length < m + 1)
            {
                int size = Math.Max(64, m + 1);
                _d0 = new int[size]; _d1 = new int[size]; _d2 = new int[size];
            }
            int[] prev2 = _d0, prev = _d1, cur = _d2;
            for (int j = 0; j <= m; j++) prev[j] = j;

            for (int i = 1; i <= n; i++)
            {
                cur[0] = i;
                int rowMin = cur[0];
                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                        v = Math.Min(v, prev2[j - 2] + 1);
                    cur[j] = v;
                    if (v < rowMin) rowMin = v;
                }
                if (rowMin > max) return max + 1;
                var tmp = prev2; prev2 = prev; prev = cur; cur = tmp;
            }
            return prev[m];
        }

        /// <summary>Lowercase, letters and digits only (optionally keeping '*').</summary>
        internal static string Compact(string s, bool keepStar = false)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (keepStar && c == '*') sb.Append('*');
            }
            return sb.ToString();
        }

        private static bool TypeIs(ItemDrop.ItemData item, params string[] types)
        {
            // Compare by name so enum values that differ between game versions don't break the build.
            string t = item.m_shared.m_itemType.ToString();
            foreach (string s in types)
                if (t == s) return true;
            return false;
        }

        // CopperOre, IronScrap, FlametalOreNew... but not LeatherScraps or TrophyForestTroll.
        private static readonly Regex OrePrefab = new Regex("(Ore|Scrap)(New)?$", RegexOptions.CultureInvariant);

        private static bool IsOre(ItemDrop.ItemData item)
        {
            if (!TypeIs(item, "Material")) return false;
            string prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : "";
            return OrePrefab.IsMatch(prefab);
        }

        private static readonly Dictionary<string, Func<ItemDrop.ItemData, bool>> Categories = BuildCategories();

        internal static bool IsCategoryWord(string compact) => Categories.ContainsKey(compact);

        private static Dictionary<string, Func<ItemDrop.ItemData, bool>> BuildCategories()
        {
            var map = new Dictionary<string, Func<ItemDrop.ItemData, bool>>();
            void Add(Func<ItemDrop.ItemData, bool> predicate, params string[] names)
            {
                foreach (string n in names) map[n] = predicate;
            }

            Add(i => TypeIs(i, "Consumable") && i.m_shared.m_food > 0f, "food", "foods");
            Add(i => TypeIs(i, "Consumable") && i.m_shared.m_food <= 0f, "potion", "potions", "mead", "meads");
            Add(i => TypeIs(i, "Consumable"), "consumable", "consumables");
            Add(i => TypeIs(i, "Material"), "material", "materials", "mats");
            Add(i => TypeIs(i, "Trophy"), "trophy", "trophies");
            Add(i => TypeIs(i, "OneHandedWeapon", "TwoHandedWeapon", "TwoHandedWeaponLeft", "Bow"), "weapon", "weapons");
            Add(i => TypeIs(i, "Helmet", "Chest", "Legs", "Shoulder", "Hands"), "armor", "armour", "armors", "armours");
            Add(i => TypeIs(i, "Shield"), "shield", "shields");
            Add(i => TypeIs(i, "Ammo", "AmmoNonEquipable"), "ammo");
            Add(i => TypeIs(i, "Ammo", "AmmoNonEquipable") && i.m_shared.m_ammoType == "$ammo_arrows", "arrow", "arrows");
            Add(i => TypeIs(i, "Ammo", "AmmoNonEquipable") && i.m_shared.m_ammoType == "$ammo_bolts", "bolt", "bolts");
            Add(i => TypeIs(i, "Tool", "Torch"), "tool", "tools");
            Add(i => TypeIs(i, "Fish"), "fish");
            Add(i => TypeIs(i, "Utility", "Trinket"), "utility", "trinket", "trinkets");
            Add(i => TypeIs(i, "Misc"), "misc");
            Add(IsOre, "ore", "ores");
            Add(i => i.m_shared.m_teleportable == false, "unteleportable", "noteleport");
            return map;
        }
    }
}
