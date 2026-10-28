// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using HarmonyLib;
using MoonCleaners;

namespace BeltTiers;

/// <summary>
/// Larger output buffers, so machines keep working while their belt catches up:
/// production machines (pressure chamber, stone press) get a bigger output slot, robot docks a
/// bigger dust store, and capsule catchers hold more. The packager's 100 kg limit is a
/// constant inside the game's machine code and stays as it is.
/// </summary>
internal static class OutputBuffers
{
    static float Factor => Math.Max(1f, Plugin.OutputBufferMultiplier.Value);

    static bool specsScaled;
    static readonly Dictionary<IntPtr, double> dockScaledTo = new();

    // ---------- production machines ----------

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.StepProduction))]
    [HarmonyPrefix]
    static void ScaleSpecs()
    {
        if (specsScaled) return;
        specsScaled = true;
        try
        {
            Scale(MoonWorld.ChamberSpec, "pressure chamber");
            Scale(MoonWorld.PressSpec, "stone press");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Scaling machine output buffers failed: {e.Message}");
        }
    }

    static void Scale(MoonWorld.ProcessorSpec spec, string name)
    {
        var capacity = spec?.slotCapacity;
        if (capacity == null || spec.output < 0 || spec.output >= capacity.Length) return;
        double before = capacity[spec.output];
        capacity[spec.output] = before * Factor;
        Plugin.Logger.LogInfo($"Output buffer of {name} ({spec.product}): {before:0.##} -> {capacity[spec.output]:0.##} kg");
    }

    // ---------- robot docks ----------

    [HarmonyPatch(typeof(MoonRobotDock), nameof(MoonRobotDock.Update))]
    [HarmonyPrefix]
    static void ScaleDock(MoonRobotDock __instance)
    {
        // The dock may reset its capacity (e.g. from its definition); scale whatever it holds now.
        double current = __instance.capacityKilograms;
        if (dockScaledTo.TryGetValue(__instance.Pointer, out double scaled) && Math.Abs(current - scaled) < 1e-9) return;
        double target = current * Factor;
        __instance.capacityKilograms = target;
        dockScaledTo[__instance.Pointer] = target;
    }

    // Docks out of sight are simulated without a view (MoonWorld.StepFarRobot); their store is the
    // per-kind FarRobotSpec.bin, cached by the game, so each spec is scaled once.
    static readonly HashSet<IntPtr> farSpecsScaled = new();

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.FarSpec))]
    [HarmonyPostfix]
    static void ScaleFarSpec(MoonWorld.FarRobotSpec __result)
    {
        if (__result == null || !farSpecsScaled.Add(__result.Pointer)) return;
        double before = __result.bin;
        __result.bin = before * Factor;
        Plugin.Logger.LogInfo($"Store of robot docks out of sight: {before:0.##} -> {__result.bin:0.##} kg");
    }

    // ---------- capsule catchers ----------
    //
    // The game stores any amount in a catcher, but its 100 kg limit is compiled into every place
    // that reads the fill level (choosing a catcher, "all full", the gauge, the info panel). All
    // of them read CatcherKilograms, so it reports the level relative to the larger store; the
    // info panel's kilogram texts are scaled back to real kilograms. The store is 100 kg ×
    // OutputBufferMultiplier for Mk1 and grows by CatcherStorePerTierKg with each tier above.

    const double CatcherStoreKg = 100; // MoonWorld.CatcherStoreKg

    public static double CatcherCapacityKg(int tier) =>
        CatcherStoreKg * Factor + Math.Max(0, tier - TierStore.MinTier) * Plugin.CatcherStorePerTierKg.Value;

    static double CatcherScale(int catcher) =>
        CatcherCapacityKg(TierStore.Get(TierStore.MachineKey(catcher))) / CatcherStoreKg;

    static int panelCatcher = -1;

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.CatcherKilograms))]
    [HarmonyPostfix]
    static void CatcherKilograms(int catcher, ref double __result)
    {
        double scale = CatcherScale(catcher);
        if (scale > 1) __result /= scale;
    }

    [HarmonyPatch(typeof(MoonCatcherLook), nameof(MoonCatcherLook.Show))]
    [HarmonyPrefix]
    static void BeforeCatcherPanel(int id) => panelCatcher = id;

    [HarmonyPatch(typeof(MoonCatcherLook), nameof(MoonCatcherLook.Show))]
    [HarmonyPostfix]
    static void AfterCatcherPanel() => panelCatcher = -1;

    [HarmonyPatch(typeof(MoonCatcherLook), nameof(MoonCatcherLook.Kg))]
    [HarmonyPrefix]
    static void CatcherPanelKg(ref double kg)
    {
        if (panelCatcher >= 0) kg *= CatcherScale(panelCatcher);
    }

    // Each landing books the catcher for CatchSpacing (9 s, inlined); the booking is set to the
    // catcher tier's interval instead ([Catchers] Mk1–Mk4). Robots prefer catchers with fewer
    // bookings, so faster catchers also get more of the capsules.

    const float CatchSpacing = 9f; // MoonWorld.CatchSpacing

    /// <summary>Seconds between landings at a catcher.</summary>
    public static float CatchSeconds(int catcher) => TierCatchSeconds(TierStore.Get(TierStore.MachineKey(catcher)));

    public static float TierCatchSeconds(int tier) => Plugin.CatchSeconds[Math.Clamp(tier, TierStore.MinTier, TierStore.MaxTier)].Value;

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.ReserveCatch))]
    [HarmonyPostfix]
    static void ReserveCatch(MoonWorld __instance, int id, float earliest, float __result)
    {
        if (TierController.Tracing) Stat(id).Add(__result - earliest);
        float seconds = CatchSeconds(id);
        var slots = __instance.catchSlots;
        if (Math.Abs(seconds - CatchSpacing) < 0.001f || slots == null) return;
        slots.set_Item(id, __result + seconds);
    }

    // ---------- diagnostics (F10) ----------
    //
    // Per catcher and second: landings booked, how long capsules had to wait for a free slot
    // (catcher too slow) and capsules stored. Few bookings with no waiting means too little supply.

    sealed class CatchStat
    {
        public int Booked, Waited, Stored;
        public float WaitSeconds;
        public void Add(float wait)
        {
            Booked++;
            if (wait > 0.01f) { Waited++; WaitSeconds += wait; }
        }
    }

    static readonly Dictionary<int, CatchStat> catchStats = new();

    static CatchStat Stat(int id)
    {
        if (!catchStats.TryGetValue(id, out var stat)) catchStats[id] = stat = new CatchStat();
        return stat;
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.DepositCatch))]
    [HarmonyPostfix]
    static void Deposited(int catcher, double __result)
    {
        if (TierController.Tracing && __result > 0) Stat(catcher).Stored++;
    }

    public static string FlushCatchStats(MoonWorld world)
    {
        if (catchStats.Count == 0) return "none";
        var text = new System.Text.StringBuilder();
        foreach (var (id, s) in catchStats)
        {
            int tier = TierStore.Get(TierStore.MachineKey(id));
            double kg = world != null ? world.CatcherKilograms(id) * CatcherScale(id) : -1;
            text.Append($"#{id} Mk{tier} booked {s.Booked} waited {s.Waited} ({s.WaitSeconds:0.0} s) stored {s.Stored} at {kg:0}/{CatcherCapacityKg(tier):0} kg; ");
        }
        catchStats.Clear();
        return text.ToString();
    }
}
