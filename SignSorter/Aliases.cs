using System;
using System.Collections.Generic;
using System.IO;

namespace SignSorter
{
    /// <summary>
    /// "What you write on a sign" → "what it means". Loaded from a text file:
    ///   Metal, Metals = Copper, Tin, Bronze, Iron
    /// Targets may be item names, wildcards, categories or other aliases.
    /// </summary>
    internal sealed class AliasTable
    {
        public static readonly AliasTable Empty = new AliasTable();

        private readonly Dictionary<string, List<string>> _map = new Dictionary<string, List<string>>();

        public int Count => _map.Count;

        public bool TryGet(string compactKey, out List<string> targets) => _map.TryGetValue(compactKey, out targets);

        public static AliasTable Parse(IEnumerable<string> lines, Action<string> warn)
        {
            var table = new AliasTable();
            int lineNo = 0;
            foreach (string rawLine in lines)
            {
                lineNo++;
                string line = rawLine ?? "";
                int comment = line.IndexOf('#');
                if (comment >= 0) line = line.Substring(0, comment);
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                line = line.Trim();
                if (line.Length == 0) continue;

                int eq = line.IndexOf('=');
                if (eq < 0) eq = line.IndexOf(':');
                if (eq <= 0 || eq == line.Length - 1)
                {
                    warn?.Invoke($"Alias line {lineNo} ignored (expected 'name = item, item'): {rawLine}");
                    continue;
                }

                var targets = new List<string>();
                foreach (string t in line.Substring(eq + 1).Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string target = t.Trim();
                    if (target.Length > 0) targets.Add(target);
                }
                if (targets.Count == 0)
                {
                    warn?.Invoke($"Alias line {lineNo} has nothing after '=': {rawLine}");
                    continue;
                }

                foreach (string n in line.Substring(0, eq).Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (n.IndexOf('*') >= 0)
                    {
                        warn?.Invoke($"Alias line {lineNo}: names can't contain '*' ({n.Trim()}); use the wildcard on the right side instead.");
                        continue;
                    }
                    string key = SignFilter.Compact(n);
                    if (key.Length == 0) continue;
                    if (table._map.TryGetValue(key, out var existing))
                    {
                        foreach (string target in targets)
                            if (!existing.Contains(target)) existing.Add(target);
                    }
                    else
                    {
                        table._map[key] = new List<string>(targets);
                    }
                }
            }
            return table;
        }
    }

    /// <summary>Owns the alias file on disk: creates it with examples, reloads it when edited.</summary>
    internal static class AliasFile
    {
        public const string DefaultContents =
@"# SignSorter aliases
#
# Teach the mod extra names for items. Format:
#     name you write on a sign = what it means, what else it means
#
# The right side can be item names, wildcards (*wood), categories (Food, Weapons...)
# or other aliases. Several names can share one line: Metal, Metals, Bars = ...
# Spaces and capital letters don't matter. Lines starting with # are ignored.
# This file is re-read automatically when you save it - no restart needed.
#
# If an alias has the same name as a real item, the alias wins.

Metal, Metals, Bars, Ingots = Copper, Tin, Bronze, Iron, Silver, Black metal, Flametal
Seeds = *seeds, Acorn, Fir cone, Pine cone
Meat, Meats = Raw meat, Boar meat, Deer meat, Wolf meat, Lox meat, Serpent meat, Hare meat, Chicken meat, Seeker meat, Bear meat, Moose meat, Volture meat, Bonemaw meat, Neck tail, Asksvin tail
Hides, Pelts, Leather = *hide, *pelt, Leather scraps
Gems = Ruby, Amber, Amber pearl
Berries = Raspberries, Blueberries, Cloudberries
";

        private static string _path;
        private static DateTime _lastWrite = DateTime.MinValue;
        private static long _lastLength = -1;

        public static AliasTable Current { get; private set; } = AliasTable.Empty;
        public static string FilePath => _path;

        public static void Init(string path)
        {
            _path = path;
            try
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, DefaultContents);
                    Plugin.Log?.LogInfo($"Created alias file: {path}");
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"Could not create alias file {path}: {e.Message}");
            }
            ReloadIfChanged();
        }

        /// <summary>Reloads when the file's timestamp or size changed. Returns true if reloaded.</summary>
        public static bool ReloadIfChanged()
        {
            if (string.IsNullOrEmpty(_path)) return false;
            try
            {
                if (!File.Exists(_path))
                {
                    if (Current.Count > 0 || _lastLength != -1)
                    {
                        Current = AliasTable.Empty;
                        _lastWrite = DateTime.MinValue;
                        _lastLength = -1;
                        SignFilter.ClearCache();
                        return true;
                    }
                    return false;
                }

                var info = new FileInfo(_path);
                if (info.LastWriteTimeUtc == _lastWrite && info.Length == _lastLength) return false;

                string[] lines = File.ReadAllLines(_path);
                Current = AliasTable.Parse(lines, msg => Plugin.Log?.LogWarning(msg));
                _lastWrite = info.LastWriteTimeUtc;
                _lastLength = info.Length;
                SignFilter.ClearCache();
                Plugin.Log?.LogInfo($"Loaded {Current.Count} sign aliases from {_path}");
                return true;
            }
            catch (Exception e)
            {
                // File may be mid-save in an editor; keep the old table and try again later.
                Plugin.Log?.LogWarning($"Could not read alias file {_path}: {e.Message}");
                return false;
            }
        }
    }
}
