// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using HarmonyLib;
using MoonCleaners;

namespace BeltTiers;

/// <summary>
/// Machines try to put cargo onto their outfeed belt on a fixed beat (0.35 s, inlined in the game), which
/// caps a faster belt at the same throughput as a Mk1 belt. When a machine has just tried to emit onto
/// an upgraded belt, the wait until its next emit is shortened by that belt's speed factor. The
/// game still checks stock and belt space on every emit, so an empty machine simply keeps the
/// normal beat and a full belt only gets more frequent checks.
/// </summary>
internal static class EmitPacing
{
    const float EmitSeconds = 0.35f; // GarageSiloEmitSeconds, PackagerEmitSeconds, ProductEmitSeconds

    /// <summary>Highest speed factor among belts that received cargo during the current step.</summary>
    static float passFactor = 1f;
    static MoonProductionState emittingProduct;
    static readonly Dictionary<IntPtr, (MoonProductionState state, float factor)> boostedProducts = new();

    static bool Enabled => Plugin.BoostMachines.Value;

    /// <summary>
    /// Any attempt onto an upgraded belt counts, refused ones too: a fast belt clears its entrance
    /// within a fraction of the beat, and waiting the full beat after "belt full" halves throughput.
    /// </summary>
    static void Emitted(MoonConveyorSnapshot outfeed)
    {
        if (outfeed == null) return;
        float factor = TierStore.Multiplier(outfeed.id);
        if (factor <= 1f) return;
        passFactor = Math.Max(passFactor, factor);
        var product = emittingProduct;
        if (product == null) return;
        float known = boostedProducts.TryGetValue(product.Pointer, out var b) ? b.factor : 1f;
        boostedProducts[product.Pointer] = (product, Math.Max(known, factor));
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.EmitCapsule))]
    [HarmonyPostfix]
    static void AfterCapsule(MoonConveyorSnapshot outfeed) => Emitted(outfeed);

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.EmitCargo))]
    [HarmonyPostfix]
    static void AfterCargo(MoonConveyorSnapshot outfeed) => Emitted(outfeed);

    /// <summary>Pulls a world-wide emit timer forward if the step just emitted onto a faster belt.</summary>
    static float Hasten(float before, float after)
    {
        float factor = passFactor;
        passFactor = 1f;
        if (!Enabled || factor <= 1f || after <= before) return after;
        return after - EmitSeconds * (1f - 1f / factor);
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.StepCatchers))]
    [HarmonyPrefix]
    static void BeforeCatchers(MoonWorld __instance, out float __state)
    {
        passFactor = 1f;
        __state = __instance.nextCatcherEmit;
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.StepCatchers))]
    [HarmonyPostfix]
    static void AfterCatchers(MoonWorld __instance, float __state) =>
        __instance.nextCatcherEmit = Hasten(__state, __instance.nextCatcherEmit);

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.StepGarageSilos))]
    [HarmonyPrefix]
    static void BeforeSilos(MoonWorld __instance, out float __state)
    {
        passFactor = 1f;
        __state = __instance.nextGarageSiloEmit;
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.StepGarageSilos))]
    [HarmonyPostfix]
    static void AfterSilos(MoonWorld __instance, float __state) =>
        __instance.nextGarageSiloEmit = Hasten(__state, __instance.nextGarageSiloEmit);

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.StepPackagers))]
    [HarmonyPrefix]
    static void BeforePackagers(MoonWorld __instance, out float __state)
    {
        passFactor = 1f;
        __state = __instance.nextPackagerEmit;
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.StepPackagers))]
    [HarmonyPostfix]
    static void AfterPackagers(MoonWorld __instance, float __state) =>
        __instance.nextPackagerEmit = Hasten(__state, __instance.nextPackagerEmit);

    // Robot docks keep their own timer per dock (only docks near a player run this view).

    [HarmonyPatch(typeof(MoonRobotDock), nameof(MoonRobotDock.Update))]
    [HarmonyPrefix]
    static void BeforeDock(MoonRobotDock __instance, out float __state)
    {
        passFactor = 1f;
        __state = __instance.nextEmit;
    }

    [HarmonyPatch(typeof(MoonRobotDock), nameof(MoonRobotDock.Update))]
    [HarmonyPostfix]
    static void AfterDock(MoonRobotDock __instance, float __state) =>
        __instance.nextEmit = Hasten(__state, __instance.nextEmit);

    // Production machines keep their own timer per machine.

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.StepProduction))]
    [HarmonyPrefix]
    static void BeforeProduction() => boostedProducts.Clear();

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.EmitProduct))]
    [HarmonyPrefix]
    static void BeforeProduct(MoonProductionState s) => emittingProduct = s;

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.EmitProduct))]
    [HarmonyPostfix]
    static void AfterProduct() => emittingProduct = null;

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.StepProduction))]
    [HarmonyPostfix]
    static void AfterProduction()
    {
        if (!Enabled) { boostedProducts.Clear(); return; }
        foreach (var (state, factor) in boostedProducts.Values)
            if (state != null && !state.WasCollected) state.nextEmit -= EmitSeconds * (1f - 1f / factor);
        boostedProducts.Clear();
    }
}
