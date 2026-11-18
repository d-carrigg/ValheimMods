using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SignSorter
{
    /// <summary>
    /// Remembers which worlds the player has sorted by hand at least once. Automatic sorting
    /// only starts in a world after that, so loading an existing world with old signs on its
    /// chests never reshuffles anything until the player asks for it.
    /// </summary>
    internal static class WorldOptIn
    {
        private static string _path;
        private static HashSet<string> _worlds;

        public static void Init(string path)
        {
            _path = path;
            _worlds = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(path))
                    foreach (string line in File.ReadAllLines(path))
                        if (line.Trim().Length > 0 && !line.TrimStart().StartsWith("#")) _worlds.Add(line.Trim());
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"Could not read {path}: {e.Message}");
            }
        }

        public static bool IsEnabled(string worldKey) => worldKey != null && _worlds != null && _worlds.Contains(worldKey);

        public static void Enable(string worldKey)
        {
            if (worldKey == null || _worlds == null || !_worlds.Add(worldKey)) return;
            try
            {
                var lines = new List<string> { "# Worlds where SignSorter's automatic sorting is turned on (one per line)." };
                lines.AddRange(_worlds.OrderBy(w => w));
                File.WriteAllLines(_path, lines.ToArray());
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"Could not save {_path}: {e.Message}");
            }
        }

        /// <summary>An id for the current world: its name and id when hosting, the server address when joining.</summary>
        public static string CurrentWorldKey()
        {
            try
            {
                ZNet net = ZNet.instance;
                if (net == null) return null;
                string name = net.GetWorldName();
                if (!string.IsNullOrEmpty(name)) return "world:" + name + "#" + net.GetWorldUID();

                ZNetPeer server = net.GetServerPeer();
                string host = server?.m_socket?.GetHostName();
                if (!string.IsNullOrEmpty(host)) return "server:" + host;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogDebug($"World id lookup failed: {e.Message}");
            }
            return "multiplayer";
        }
    }
}
