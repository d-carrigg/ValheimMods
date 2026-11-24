using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace BuildStats;

/// <summary>
/// Moves counts between players and the server. Each game records its own player's builds and
/// sends them to the server every couple of seconds. The server adds them to the world's totals,
/// saves them to a file, and sends the full table to everyone. A host or single-player game is
/// its own server, so the same path works there.
/// </summary>
/// <remarks>
/// If the server doesn't run BuildStats, it never answers the hello, and the window shows only
/// this player's counts from the current session.
/// </remarks>
[HarmonyPatch]
internal static class StatsSync
{
    private const int Protocol = 2; // 2: added Category.Crafted
    private const string RpcHello = "BuildStats_Hello";
    private const string RpcAdd = "BuildStats_Add";
    private const string RpcSync = "BuildStats_Sync";

    // ZRoutedRpc.Everybody. It's a const in the game but a field in some reference copies of it,
    // and code built against the field fails to load in the real game, so use the value.
    private const long Everybody = 0L;

    private const float SendInterval = 2f;
    private const float BroadcastInterval = 2f;
    private const float SaveInterval = 60f;
    private const float ServerAnswerTimeout = 15f;
    private const float HelloRetry = 5f;
    private const float OnlineCheckInterval = 10f;

    // Client side
    private static readonly StatsStore Pending = new();
    private static readonly StatsStore Session = new();
    private static StatsStore _fromServer = new();
    private static float _helloSentAt = -1f;
    private static float _lastHello;
    private static float _lastSend;
    private static bool _serverHasMod;

    // Private in the game, so called by reflection.
    private static readonly System.Reflection.MethodInfo GetServerPeerIdMethod = AccessTools.Method(typeof(ZRoutedRpc), "GetServerPeerID");

    private static long ServerPeerId(ZRoutedRpc rpc) => (long)GetServerPeerIdMethod.Invoke(rpc, null);

    // Server side
    private static StatsStore _world;
    private static string _worldPath;
    private static bool _changed;
    private static bool _unsaved;
    private static float _lastBroadcast;
    private static float _lastSave;
    private static float _lastOnlineCheck;

    /// <summary>The table the window shows.</summary>
    internal static StatsStore View => _serverHasMod ? _fromServer : Session;

    /// <summary>True once the server has answered, i.e. it runs BuildStats.</summary>
    internal static bool ServerHasMod => _serverHasMod;

    /// <summary>True while still waiting to hear whether the server runs BuildStats.</summary>
    internal static bool Waiting => !_serverHasMod && _helloSentAt >= 0f && Time.realtimeSinceStartup - _helloSentAt < ServerAnswerTimeout;

    /// <summary>Counts something the local player did.</summary>
    internal static void Record(Category category, string token, int amount)
    {
        Player player = Player.m_localPlayer;
        if (player == null) return;
        string name = player.GetPlayerName();
        Pending.Add(name, category, token, amount);
        Session.Add(name, category, token, amount);
    }

    // ------------------------------------------------------------------ setup

    [HarmonyPatch(typeof(ZNet), "Awake"), HarmonyPostfix]
    private static void RegisterRpcs() => Guard.Run("setting up stats sync", Register);

    private static void Register()
    {
        ResetSession();
        ZRoutedRpc rpc = ZRoutedRpc.instance;
        if (rpc == null) return;
        rpc.Register<ZPackage>(RpcHello, (from, pkg) => Guard.Run("answering a player", () => OnHello(from, pkg)));
        rpc.Register<ZPackage>(RpcAdd, (from, pkg) => Guard.Run("adding stats from a player", () => OnAdd(from, pkg)));
        rpc.Register<ZPackage>(RpcSync, (from, pkg) => Guard.Run("reading stats from the server", () => OnSync(from, pkg)));
    }

    [HarmonyPatch(typeof(ZNet), "OnDestroy"), HarmonyPrefix]
    private static void OnLeave() => Guard.Run("saving stats on leaving", () =>
    {
        SaveNow();
        ResetSession();
    });

    private static void ResetSession()
    {
        Pending.Clear();
        Session.Clear();
        _fromServer = new StatsStore();
        _helloSentAt = -1f;
        _serverHasMod = false;
        _world = null;
        _worldPath = null;
        _changed = _unsaved = false;
    }

    // ------------------------------------------------------------------ per frame

    internal static void Tick()
    {
        ZNet net = ZNet.instance;
        ZRoutedRpc rpc = ZRoutedRpc.instance;
        if (net == null || rpc == null) return;
        float now = Time.realtimeSinceStartup;

        if (net.IsServer())
        {
            EnsureWorldLoaded();
            if (_changed && now - _lastBroadcast >= BroadcastInterval)
            {
                _changed = false;
                _lastBroadcast = now;
                rpc.InvokeRoutedRPC(Everybody, RpcSync, Snapshot());
            }
            if (_unsaved && now - _lastSave >= SaveInterval) SaveNow();
            // Name builders from whoever is online, with or without BuildStats.
            if (_world != null && now - _lastOnlineCheck >= OnlineCheckInterval)
            {
                _lastOnlineCheck = now;
                Guard.Run("finding names of online players", () => NameUnknowns(NameFinder.FromOnlinePlayers()));
            }
        }

        Player me = Player.m_localPlayer;
        // Right after spawning, the player's id and name aren't set yet; the hello needs both.
        if (me == null || me.GetPlayerID() == 0L || string.IsNullOrEmpty(me.GetPlayerName())) return;

        // Say hello on arrival, and again every few seconds in case one was lost, until the server answers or we give up.
        if (_helloSentAt < 0f || (!_serverHasMod && now - _lastHello >= HelloRetry && now - _helloSentAt < ServerAnswerTimeout))
        {
            if (_helloSentAt < 0f) _helloSentAt = now;
            _lastHello = now;
            var hello = new ZPackage();
            hello.Write(Protocol);
            hello.Write(Player.m_localPlayer.GetPlayerID());
            hello.Write(Player.m_localPlayer.GetPlayerName());
            rpc.InvokeRoutedRPC(ServerPeerId(rpc), RpcHello, hello);
        }

        if (_serverHasMod && !Pending.IsEmpty && now - _lastSend >= SendInterval)
        {
            _lastSend = now;
            var add = new ZPackage();
            add.Write(Protocol);
            Pending.Write(add);
            Pending.Clear();
            rpc.InvokeRoutedRPC(ServerPeerId(rpc), RpcAdd, add);
        }
    }

    // ------------------------------------------------------------------ server

    private static void EnsureWorldLoaded()
    {
        if (_world != null) return;
        string world = ZNet.instance.GetWorldName();
        if (string.IsNullOrEmpty(world)) return;
        foreach (char c in Path.GetInvalidFileNameChars()) world = world.Replace(c, '_');
        string path = Path.Combine(Path.Combine(Paths.ConfigPath, "BuildStats"), world + ".tsv");
        bool firstTime = !File.Exists(path);
        if (firstTime && !Backfill.Ready) return; // try again next frame
        _worldPath = path;
        try
        {
            _world = StatsStore.Load(_worldPath);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Could not read {_worldPath}; starting with empty stats. {e.Message}");
            _world = new StatsStore();
        }
        _lastSave = Time.realtimeSinceStartup;
        if (firstTime)
        {
            // The file is written right away, so this runs once per world.
            Guard.Run("counting existing builds", () => Backfill.Run(_world));
            _changed = _unsaved = true;
        }
        Guard.Run("finding names in the world", () => NameUnknowns(NameFinder.FromWorld()));
        SaveNow();
    }

    /// <summary>Gives names to builders known only by id. Returns nothing; marks stats changed if any were named.</summary>
    private static void NameUnknowns(Dictionary<long, string> names)
    {
        if (_world == null || names.Count == 0) return;
        int named = 0;
        foreach (string player in _world.Players.ToList())
        {
            if (!player.StartsWith(Backfill.IdPrefix)) continue;
            if (!long.TryParse(player.Substring(Backfill.IdPrefix.Length), out long id)) continue;
            if (names.TryGetValue(id, out string name) && _world.Rename(player, name)) named++;
        }
        if (named == 0) return;
        _changed = _unsaved = true;
        Plugin.Log.LogInfo($"Named {named} builder(s) from the world.");
    }

    private static ZPackage Snapshot()
    {
        var pkg = new ZPackage();
        pkg.Write(Protocol);
        _world.Write(pkg);
        return pkg;
    }

    private static void OnHello(long sender, ZPackage pkg)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
        EnsureWorldLoaded();
        if (_world == null || pkg.ReadInt() != Protocol) return;
        long playerId = pkg.ReadLong();
        string name = pkg.ReadString();
        // Builds from before BuildStats are stored by player id until that player shows up.
        if (!string.IsNullOrEmpty(name) && _world.Rename(Backfill.IdPrefix + playerId, name))
            _changed = _unsaved = true;
        ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcSync, Snapshot());
    }

    private static void OnAdd(long sender, ZPackage pkg)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
        EnsureWorldLoaded();
        if (_world == null || pkg.ReadInt() != Protocol) return;
        _world.AddAll(StatsStore.Read(pkg));
        _changed = _unsaved = true;
    }

    internal static void SaveNow()
    {
        if (_world == null || !_unsaved) return;
        try
        {
            _world.Save(_worldPath);
            _unsaved = false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Could not save {_worldPath}: {e.Message}");
        }
        _lastSave = Time.realtimeSinceStartup;
    }

    // ------------------------------------------------------------------ client

    private static void OnSync(long sender, ZPackage pkg)
    {
        if (ZNet.instance == null || ZNet.instance.IsDedicated()) return;
        if (pkg.ReadInt() != Protocol) return;
        _fromServer = StatsStore.Read(pkg);
        // Anything recorded before this answer is still in Pending and goes out on the next Tick.
        _serverHasMod = true;
    }
}
