using System;
using System.Collections.Generic;

namespace BuildStats;

/// <summary>
/// Runs mod code that the game calls into (hooks, network messages, UI) so a bug in BuildStats
/// is logged and skipped instead of interrupting what the game was doing.
/// </summary>
internal static class Guard
{
    private static readonly HashSet<string> Reported = new();

    internal static void Run(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            // Log each failing spot once; it may run every frame.
            if (Reported.Add(what)) Plugin.Log.LogError($"BuildStats: {what} failed and was skipped: {e}");
        }
    }
}
