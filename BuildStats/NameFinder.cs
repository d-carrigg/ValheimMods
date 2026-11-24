using System.Collections;
using System.Collections.Generic;

namespace BuildStats;

/// <summary>
/// Finds player names for player ids. Pieces only remember who built them by id, and Valheim keeps
/// no id-to-name list, but some saved objects carry both: beds (owner), wards (who placed it and
/// who is allowed on it), tombstones (owner), and the characters of players who are online.
/// </summary>
internal static class NameFinder
{
    // Saved-object keys that hold an id and its name side by side.
    // Each row: id key, name key. Beds and tombstones (owner), wards (creator), characters (player).
    private static readonly string[][] Pairs =
    {
        new[] { "owner", "ownerName" },
        new[] { "creator", "creatorName" },
        new[] { "playerID", "playerName" },
    };

    /// <summary>Names found on objects saved in the world.</summary>
    internal static Dictionary<long, string> FromWorld()
    {
        var names = new Dictionary<long, string>();
        IEnumerable objects = Backfill.WorldObjects();
        if (objects == null) return names;
        foreach (object value in objects)
        {
            if (value is not ZDO zdo) continue;
            foreach (string[] pair in Pairs) Note(names, zdo.GetLong(pair[0]), zdo.GetString(pair[1]));
            // Ward access list: "permitted" entries, each "pu_id<i>" / "pu_name<i>".
            int permitted = zdo.GetInt("permitted");
            for (int i = 0; i < permitted && i < 100; i++)
                Note(names, zdo.GetLong("pu_id" + i), zdo.GetString("pu_name" + i));
        }
        return names;
    }

    /// <summary>Names of the players online right now, mod or no mod.</summary>
    internal static Dictionary<long, string> FromOnlinePlayers()
    {
        var names = new Dictionary<long, string>();
        if (ZNet.instance == null || ZDOMan.instance == null) return names;
        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
        {
            ZDO character = ZDOMan.instance.GetZDO(peer.m_characterID);
            if (character != null) Note(names, character.GetLong("playerID"), character.GetString("playerName"));
        }
        Player me = Player.m_localPlayer; // the host's own character
        if (me != null) Note(names, me.GetPlayerID(), me.GetPlayerName());
        return names;
    }

    private static void Note(Dictionary<long, string> names, long id, string name)
    {
        if (id != 0L && !string.IsNullOrEmpty(name) && !names.ContainsKey(id)) names[id] = name;
    }
}
