// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MoonCleaners;

namespace BeltTiers;

/// <summary>
/// Diagnostics for how often machines put cargo onto belts. While tracing, every emit attempt is
/// counted per source, so the log shows whether a machine is held back by a full belt.
/// </summary>
internal static class EmitStats
{
    sealed class Counter
    {
        public int Ok, Refused;
        public string LastReason;
    }

    static readonly Dictionary<string, Counter> counters = new();

    static void Count(string source, bool ok, string reason)
    {
        if (!TierController.Tracing) return;
        if (!counters.TryGetValue(source, out var c)) counters[source] = c = new Counter();
        if (ok) c.Ok++;
        else { c.Refused++; c.LastReason = reason; }
    }

    /// <summary>One line per source since the last call, then resets.</summary>
    public static string Flush()
    {
        if (counters.Count == 0) return "none";
        string text = string.Join("; ", counters.Select(e =>
            $"{e.Key} ok {e.Value.Ok} refused {e.Value.Refused}{(e.Value.Refused > 0 ? $" ({e.Value.LastReason})" : "")}"));
        counters.Clear();
        return text;
    }

    static string Belt(MoonConveyorSnapshot outfeed) =>
        outfeed != null ? $"belt {outfeed.id} Mk{TierStore.Get(outfeed.id)}" : "belt ?";

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.EmitCapsule))]
    [HarmonyPostfix]
    static void Capsule(MoonConveyorSnapshot outfeed, bool __result, string reason) =>
        Count($"capsule->{Belt(outfeed)}", __result, reason);

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.EmitCargo))]
    [HarmonyPostfix]
    static void Cargo(MoonConveyorSnapshot outfeed, bool __result, string reason) =>
        Count($"cargo->{Belt(outfeed)}", __result, reason);

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.EmitDockCapsule))]
    [HarmonyPostfix]
    static void Dock(int dock, bool __result, string reason) =>
        Count($"dock {dock}", __result, reason);

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.EmitPackagerCapsule))]
    [HarmonyPostfix]
    static void Packager(MoonDepotSnapshot d, bool __result, string reason) =>
        Count($"packager {d?.id}", __result, reason);
}
