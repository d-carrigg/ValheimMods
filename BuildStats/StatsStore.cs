using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BuildStats;

/// <summary>What a count is about.</summary>
internal enum Category
{
    /// <summary>Pieces placed, keyed by piece name token.</summary>
    Built = 0,
    /// <summary>Materials spent placing pieces, keyed by item name token.</summary>
    BuildMaterials = 1,
    /// <summary>Materials spent crafting and upgrading items, keyed by item name token.</summary>
    CraftMaterials = 2,
    /// <summary>Items crafted and upgraded, keyed by group ("Weapons", "Armor", ..., "Upgrades").</summary>
    Crafted = 3,
}

/// <summary>Per-player counts, keyed by player name, then category, then name token (e.g. "$item_wood").</summary>
internal sealed class StatsStore
{
    internal const int CategoryCount = 4;

    private readonly Dictionary<string, Dictionary<string, int>[]> _players = new(StringComparer.Ordinal);

    // Server-side notes saved with the world's stats, such as which one-time scans already ran.
    // Saved to the file only; never sent to players.
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    internal bool HasFlag(string flag) => _flags.Contains(flag);

    internal void SetFlag(string flag) => _flags.Add(Clean(flag));

    internal IEnumerable<string> Players => _players.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

    internal bool IsEmpty => _players.Count == 0;

    internal void Add(string player, Category category, string token, int amount)
    {
        if (string.IsNullOrEmpty(player) || string.IsNullOrEmpty(token) || amount <= 0) return;
        if (!_players.TryGetValue(player, out var cats))
        {
            cats = new Dictionary<string, int>[CategoryCount];
            for (int i = 0; i < CategoryCount; i++) cats[i] = new Dictionary<string, int>(StringComparer.Ordinal);
            _players[player] = cats;
        }
        var counts = cats[(int)category];
        counts.TryGetValue(token, out int have);
        counts[token] = have + amount;
    }

    internal void AddAll(StatsStore other)
    {
        foreach (var p in other._players)
            for (int c = 0; c < CategoryCount; c++)
                foreach (var kv in p.Value[c]) Add(p.Key, (Category)c, kv.Key, kv.Value);
    }

    /// <summary>Moves one player's counts onto another name. Returns false if there was nothing to move.</summary>
    internal bool Rename(string from, string to)
    {
        if (from == to || !_players.TryGetValue(from, out var cats)) return false;
        _players.Remove(from);
        for (int c = 0; c < CategoryCount; c++)
            foreach (var kv in cats[c]) Add(to, (Category)c, kv.Key, kv.Value);
        return true;
    }

    internal int Get(string player, Category category, string token) =>
        _players.TryGetValue(player, out var cats) && cats[(int)category].TryGetValue(token, out int n) ? n : 0;

    internal IEnumerable<string> Tokens(Category category) =>
        _players.Values.SelectMany(c => c[(int)category].Keys).Distinct();

    internal void Clear() => _players.Clear();

    // ------------------------------------------------------------------ network form

    internal void Write(ZPackage pkg)
    {
        pkg.Write(_players.Count);
        foreach (var p in _players)
        {
            pkg.Write(p.Key);
            for (int c = 0; c < CategoryCount; c++)
            {
                pkg.Write(p.Value[c].Count);
                foreach (var kv in p.Value[c])
                {
                    pkg.Write(kv.Key);
                    pkg.Write(kv.Value);
                }
            }
        }
    }

    internal static StatsStore Read(ZPackage pkg)
    {
        var store = new StatsStore();
        int players = pkg.ReadInt();
        for (int i = 0; i < players; i++)
        {
            string name = pkg.ReadString();
            for (int c = 0; c < CategoryCount; c++)
            {
                int n = pkg.ReadInt();
                for (int j = 0; j < n; j++)
                {
                    string token = pkg.ReadString();
                    int amount = pkg.ReadInt();
                    store.Add(name, (Category)c, token, amount);
                }
            }
        }
        return store;
    }

    // ------------------------------------------------------------------ file form: player<TAB>category<TAB>token<TAB>count

    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string tmp = path + ".tmp";
        using (var w = new StreamWriter(tmp))
        {
            w.WriteLine("# BuildStats: player, category (Built, BuildMaterials, CraftMaterials, Crafted), name, count");
            foreach (string flag in _flags) w.WriteLine(FlagPrefix + flag);
            foreach (var p in _players)
                for (int c = 0; c < CategoryCount; c++)
                    foreach (var kv in p.Value[c])
                        w.WriteLine($"{Clean(p.Key)}\t{(Category)c}\t{Clean(kv.Key)}\t{kv.Value}");
        }
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    internal static StatsStore Load(string path)
    {
        var store = new StatsStore();
        if (!File.Exists(path)) return store;
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.StartsWith(FlagPrefix)) store._flags.Add(line.Substring(FlagPrefix.Length));
            if (line.StartsWith("#")) continue;
            string[] f = line.Split('\t');
            if (f.Length != 4) continue;
            if (!Enum.TryParse(f[1], out Category cat) || !int.TryParse(f[3], out int n)) continue;
            store.Add(f[0], cat, f[2], n);
        }
        return store;
    }

    // Older versions skip these lines as comments.
    private const string FlagPrefix = "#! ";

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
