// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace BeltTiers;

internal static class Patches
{
    internal static MoonWorld World;
    static bool cargoBoostFailed;

    // ---------- building ----------

    internal static bool IsLocalBuilder(MoonWalker builder)
    {
        var session = MoonCrewSession.Instance;
        var local = session != null ? session.local : null;
        return builder == null || local == null || builder.Pointer == local.Pointer;
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryBuildConveyor))]
    [HarmonyPostfix]
    static void BuiltConveyor(MoonWorld __instance, MoonWalker builder, bool __result, ref int id)
    {
        World = __instance;
        if (!__result || !IsLocalBuilder(builder)) return;
        int tier = TierController.BuildTier;
        if (TierStore.Set(id, tier)) Net.AnnounceSet(id, tier);
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryBuildConveyorRoute))]
    [HarmonyPostfix]
    static void BuiltConveyorRoute(MoonWorld __instance, MoonWalker builder, bool __result,
        Il2CppSystem.Collections.Generic.List<int> ids)
    {
        World = __instance;
        if (!__result || ids == null || !IsLocalBuilder(builder)) return;
        int tier = TierController.BuildTier;
        var changes = new List<KeyValuePair<int, int>>();
        for (int i = 0; i < ids.Count; i++)
            if (TierStore.Set(ids.get_Item(i), tier)) changes.Add(new KeyValuePair<int, int>(ids.get_Item(i), tier));
        Net.AnnounceSet(changes);
    }

    // Cutting or shifting a belt replaces it with new ids; carry the tier over.

    static HashSet<int> BeltIds(MoonWorld world)
    {
        var ids = new HashSet<int>();
        var conveyors = world.conveyors;
        if (conveyors == null) return ids;
        foreach (var entry in conveyors) ids.Add(entry.Key);
        return ids;
    }

    static void CarryTiers(MoonWorld world, HashSet<int> before)
    {
        if (before == null) return;
        var after = BeltIds(world);
        int tier = TierStore.MinTier;
        foreach (int id in before)
            if (!after.Contains(id)) tier = Math.Max(tier, TierStore.Get(id));
        if (tier == TierStore.MinTier) return;
        var changes = new List<KeyValuePair<int, int>>();
        foreach (int id in after)
            if (!before.Contains(id) && TierStore.Set(id, tier)) changes.Add(new KeyValuePair<int, int>(id, tier));
        Net.AnnounceSet(changes);
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.CutBelt))]
    [HarmonyPrefix]
    static void BeforeCut(MoonWorld __instance, out HashSet<int> __state) => __state = BeltIds(__instance);

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.CutBelt))]
    [HarmonyPostfix]
    static void AfterCut(MoonWorld __instance, HashSet<int> __state) => CarryTiers(__instance, __state);

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.ShiftBelt))]
    [HarmonyPrefix]
    static void BeforeShift(MoonWorld __instance, out HashSet<int> __state) => __state = BeltIds(__instance);

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.ShiftBelt))]
    [HarmonyPostfix]
    static void AfterShift(MoonWorld __instance, HashSet<int> __state) => CarryTiers(__instance, __state);

    // ---------- speed ----------

    /// <summary>Walking or driving on a belt: scale the surface velocity by the belt's tier.</summary>
    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.BeltSurfaceVelocity))]
    [HarmonyPostfix]
    static void SurfaceVelocity(MoonWorld __instance, Collider collider, ref Vector3 velocity, bool __result)
    {
        if (!__result || TierStore.Count == 0 && !TierController.Tracing) return;
        var views = __instance.ConveyorViews;
        if (views == null) return;
        int id = views.BeltAt(collider);
        float m = TierStore.Multiplier(id);
        if (TierController.Tracing) TierController.TraceSurface(id, velocity.magnitude, velocity.magnitude * m);
        if (m != 1f) velocity *= m;
    }

    /// <summary>
    /// The game keeps, per belt, how far its cargo still has to move (beltOwed) and moves the
    /// riders by that amount with its own spacing and hand-off rules. Before each step, upgraded
    /// belts get the extra distance added, so the game itself moves their cargo faster.
    /// </summary>
    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.StepConveyors))]
    [HarmonyPrefix]
    static void BeforeStep(MoonWorld __instance, float dt)
    {
        World = __instance;
        if (TierController.Tracing) { traceFront = FrontOfTraced(__instance); traceOwed = OwedOfTraced(__instance); }
        if (cargoBoostFailed || !Plugin.BoostCargo.Value || TierStore.Count == 0 || dt <= 0f) return;
        try
        {
            AddOwed(__instance, dt);
            Machines.AddRouteOwed(__instance, dt);
        }
        catch (Exception e)
        {
            cargoBoostFailed = true;
            Plugin.Logger.LogError($"Cargo boost disabled after error: {e}");
        }
        if (TierController.Tracing) traceOwedBoosted = OwedOfTraced(__instance);
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.StepConveyors))]
    [HarmonyPostfix]
    static void AfterStep(MoonWorld __instance, float dt)
    {
        if (TierController.Tracing)
            TierController.TraceStep($"dt {dt:0.0000}: owed {traceOwed:0.0000} -> boosted {traceOwedBoosted:0.0000} -> after {OwedOfTraced(__instance):0.0000}; front {traceFront:0.000} -> {FrontOfTraced(__instance):0.000}");
    }

    static void AddOwed(MoonWorld world, float dt)
    {
        var owed = world.beltOwed;
        var queues = world.beltQueues;
        if (owed == null || queues == null) return;
        foreach (var entry in TierStore.Snapshot())
        {
            float m = Plugin.Multiplier(entry.Value);
            if (m == 1f || entry.Key < 0) continue; // negative keys are machines (Machines.AddRouteOwed)
            AddOwed(owed, queues, entry.Key, m, dt);
        }
    }

    /// <summary>Owed distance above this means the cargo is stuck; adding more would only pile up.</summary>
    const float MaxOwed = 0.3f;

    /// <summary>
    /// Adds a belt's (or machine route's) extra distance for one step. The game only works off the
    /// owed distance of belts that carry cargo; on an empty or blocked belt it would pile up and
    /// make the next capsule jump the whole amount at once (seen as teleporting in belt loops).
    /// </summary>
    internal static bool AddOwed(Il2CppSystem.Collections.Generic.Dictionary<int, float> owed,
        Il2CppSystem.Collections.Generic.Dictionary<int, Il2CppSystem.Collections.Generic.List<Il2CppSystem.ValueTuple<float, int>>> queues,
        int key, float m, float dt)
    {
        bool known = owed.TryGetValue(key, out float current);
        bool carrying = queues.TryGetValue(key, out var queue) && queue != null && queue.Count > 0;
        if (!carrying)
        {
            // Clear what earlier versions piled up on empty belts.
            if (known && current > MaxOwed) owed.set_Item(key, 0f);
            return false;
        }
        if (current >= MaxOwed) return false;
        owed.set_Item(key, current + (m - 1f) * Plugin.VanillaBeltSpeed * dt);
        return true;
    }

    static float traceFront, traceOwed, traceOwedBoosted;

    static float OwedOfTraced(MoonWorld world)
    {
        int id = TierController.AimedBeltId;
        return id >= 0 && world.beltOwed != null && world.beltOwed.TryGetValue(id, out float v) ? v : float.NaN;
    }

    /// <summary>Distance of the queue's first rider on the belt being traced (the aimed one), or -1.</summary>
    static float FrontOfTraced(MoonWorld world)
    {
        int id = TierController.AimedBeltId;
        if (id < 0 || world.beltQueues == null || !world.beltQueues.TryGetValue(id, out var queue)) return -1f;
        float[] d = null;
        int[] r = null;
        return BeltQueue.Read(queue, ref d, ref r) > 0 ? d[0] : -1f;
    }

    // ---------- save / load ----------

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.SnapshotConveyors))]
    [HarmonyPostfix]
    static void Snapshot(MoonWorld __instance)
    {
        World = __instance;
        if (Net.IsClient) return;
        TierStore.Prune(__instance);
        TierStore.Save();
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.RestoreConveyors))]
    [HarmonyPostfix]
    static void Restore(MoonWorld __instance)
    {
        World = __instance;
        Net.ResetSession();
        if (Net.IsClient) TierStore.Clear();
        else TierStore.Load();
        TierStore.WorldRestored = true;
    }

    // ---------- network ----------

    [HarmonyPatch(typeof(MoonCrewSession), nameof(MoonCrewSession.Handle))]
    [HarmonyPrefix]
    static bool Handle(MoonCrewSession __instance, ulong sender, int size)
    {
        try
        {
            return !Net.TryHandle(__instance, sender, size);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Belt tier message handling failed: {e.Message}");
            return true;
        }
    }
}
