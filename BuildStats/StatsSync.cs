using System;
using System.IO;
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
    private const int Protocol = 1;
    private const string RpcHello = "BuildStats_Hello";
    private const string RpcAdd = "BuildStats_Add";
    private const string RpcSync = "BuildStats_Sync";

    private const float SendInterval = 2f;
    private const float BroadcastInterval = 2f;
    private const float SaveInterval = 60f;
    private const float ServerAnswerTimeout = 15f;

    // Client side
    private static readonly StatsStore Pending = new();
    private static readonly StatsStore Session = new();
    private static StatsStore _fromServer = new();
    private static float _helloSentAt = -1f;
    private static float _lastSend;
    private static bool _serverHasMod;

    // Server side
    private static StatsStore _world;
    private static string _worldPath;
    private static bool _changed;
    private static bool _unsaved;
    private static float _lastBroadcast;
    private static float _lastSave;

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
    private static void RegisterRpcs()
    {
        ResetSession();
        ZRoutedRpc rpc = ZRoutedRpc.instance;
        if (rpc == null) return;
        rpc.Register<ZPackage>(RpcHello, OnHello);
        rpc.Register<ZPackage>(RpcAdd, OnAdd);
        rpc.Register<ZPackage>(RpcSync, OnSync);
    }

    [HarmonyPatch(typeof(ZNet), "OnDestroy"), HarmonyPrefix]
    private static void OnLeave()
    {
        SaveNow();
        ResetSession();
    }

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
                rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcSync, Snapshot());
            }
            if (_unsaved && now - _lastSave >= SaveInterval) SaveNow();
        }

        if (Player.m_localPlayer == null) return;

        if (_helloSentAt < 0f)
        {
            _helloSentAt = now;
            var hello = new ZPackage();
            hello.Write(Protocol);
            rpc.InvokeRoutedRPC(rpc.GetServerPeerID(), RpcHello, hello);
        }

        if (_serverHasMod && !Pending.IsEmpty && now - _lastSend >= SendInterval)
        {
            _lastSend = now;
            var add = new ZPackage();
            add.Write(Protocol);
            Pending.Write(add);
            Pending.Clear();
            rpc.InvokeRoutedRPC(rpc.GetServerPeerID(), RpcAdd, add);
        }
    }

    // ------------------------------------------------------------------ server

    private static void EnsureWorldLoaded()
    {
        if (_world != null) return;
        string world = ZNet.instance.GetWorldName();
        if (string.IsNullOrEmpty(world)) return;
        foreach (char c in Path.GetInvalidFileNameChars()) world = world.Replace(c, '_');
        _worldPath = Path.Combine(Path.Combine(Paths.ConfigPath, "BuildStats"), world + ".tsv");
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
