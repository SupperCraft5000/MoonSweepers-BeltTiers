// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace BeltTiers;

/// <summary>
/// J.O.O.N.A.S. asks MoonWorld.FindCatcher for a target before every launch and then books a
/// landing there. The game picks by room and distance, not by when the catcher is free, so slow
/// catchers built up queues of a minute while a fast one stood idle. When another catcher nearby
/// is free clearly sooner, the capsule goes there instead; on a tie the higher tier wins.
/// </summary>
internal static class CatcherChoice
{
    const float Better = 0.25f;      // seconds a catcher must be free sooner to take over
    const float ExtraReach = 60f;    // how much farther than the game's pick a catcher may be
    const double NearlyFull = 99.0;  // CatcherKilograms is relative to a 100 kg store

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.FindCatcher))]
    [HarmonyPostfix]
    static void FindCatcher(MoonWorld __instance, Vector3 from, ref int id, ref Vector3 mouth, ref Vector3 up, ref bool __result)
    {
        if (!__result || __instance.depots == null) return;
        try
        {
            float now = Time.time;
            float pickedFree = Math.Max(now, __instance.CatchFreeAt(id));
            int pickedTier = TierStore.Get(TierStore.MachineKey(id));
            float reach = Vector3.Distance(from, mouth) * 2f + ExtraReach;

            int best = id;
            float bestFree = pickedFree;
            int bestTier = pickedTier;
            Vector3 bestMouth = mouth, bestUp = up;
            foreach (var entry in __instance.depots)
            {
                var d = entry.Value;
                int c = entry.Key;
                if (d == null || d.kind != Machines.CatcherKind || c == id) continue;
                if (__instance.CatcherKilograms(c) >= NearlyFull) continue;
                if (!__instance.CatcherMouth(c, out Vector3 m, out Vector3 u)) continue;
                if (Vector3.Distance(from, m) > reach) continue;
                float free = Math.Max(now, __instance.CatchFreeAt(c));
                int tier = TierStore.Get(TierStore.MachineKey(c));
                bool sooner = free < bestFree - Better;
                bool tie = Math.Abs(free - bestFree) <= Better && tier > bestTier;
                if (!sooner && !tie) continue;
                best = c; bestFree = free; bestTier = tier; bestMouth = m; bestUp = u;
            }
            if (best == id) return;
            if (TierController.Tracing)
                Plugin.Logger.LogInfo($"Capsule to catcher #{best} Mk{bestTier} (free in {bestFree - now:0.0} s) instead of #{id} Mk{pickedTier} (free in {pickedFree - now:0.0} s)");
            id = best;
            mouth = bestMouth;
            up = bestUp;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Catcher choice failed: {e.Message}");
        }
    }
}
