// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace BeltTiers;

/// <summary>
/// Tiered machines. Splitters and mergers have a belt of their own (MoonMachineBelt); the game
/// moves cargo through them like on belts, under negative route ids (MoonWorld.MachineRoute), so an
/// upgraded one gets the same extra distance per step as an upgraded belt. Capsule catchers take
/// in capsules faster (OutputBuffers.ReserveCatch).
/// </summary>
internal static class Machines
{
    static readonly HashSet<int> withBelt = new();
    static readonly List<int> routes = new();
    static MoonDepotConstruction construction;

    public const int CatcherKind = 100; // MoonWorld.CapsuleCatcherKind

    /// <summary>Routes boosted in the last step (for the trace).</summary>
    public static int BoostedRoutes { get; private set; }

    [HarmonyPatch(typeof(MoonMachineBelt), nameof(MoonMachineBelt.Setup))]
    [HarmonyPostfix]
    static void Setup(int machine)
    {
        if (withBelt.Add(machine)) Plugin.Logger.LogInfo($"Machine with belt: #{machine}");
    }

    public static string Name(MoonWorld world, int machine)
    {
        if (world?.depots == null || !world.depots.TryGetValue(machine, out var d) || d == null) return "Maschine";
        var def = world.BuildDefinition(d.kind);
        return def != null && !string.IsNullOrEmpty(def.displayName) ? def.displayName : $"Maschine {d.kind}";
    }

    // The structure views. FindObjectOfType and friends are stripped from the game, so the
    // instance is picked up whenever it spawns a view.
    [HarmonyPatch(typeof(MoonDepotConstruction), nameof(MoonDepotConstruction.Spawn))]
    [HarmonyPrefix]
    static void Spawning(MoonDepotConstruction __instance) => construction = __instance;

    public static GameObject ViewOf(int id) =>
        construction == null || construction.WasCollected ? null : construction.ViewOf(id);

    /// <summary>The splitter or merger a collider belongs to, or -1.</summary>
    public static int At(Collider collider)
    {
        if (collider == null || construction == null || construction.WasCollected) return -1;
        int id = construction.StructureAt(collider);
        return id >= 0 && (withBelt.Contains(id) || KindOf(Patches.World, id) is int kind && IsTieredKind(kind)) ? id : -1;
    }

    // Kinds with conveyor ports that are plain machines, not splitters or mergers.
    static readonly HashSet<int> NotRouting = new() { 10, 91, 92, 96, 97, 98, 100 };
    static readonly Dictionary<int, bool> routingKinds = new();

    /// <summary>
    /// Splitter or merger: a structure with conveyor inputs and outputs (as in MoonWorld.Routes)
    /// that is none of the production machines.
    /// </summary>
    public static bool IsRoutingKind(int kind)
    {
        if (routingKinds.TryGetValue(kind, out bool known)) return known;
        var world = Patches.World;
        if (world == null) return false;
        if (NotRouting.Contains(kind)) return routingKinds[kind] = false;
        var def = world.BuildDefinition(kind);
        if (def == null) return false;
        bool input = false, output = false;
        var ports = def.conveyorPorts;
        if (ports != null)
            foreach (var port in ports)
                if (port.output) output = true; else input = true;
        bool routing = input && output;
        routingKinds[kind] = routing;
        Plugin.Logger.LogInfo($"Structure kind {kind} ({def.displayName}): {(routing ? "splitter/merger, gets tiers" : "no tiers")}");
        return routing;
    }

    /// <summary>Kinds that get tiers: splitters, mergers and capsule catchers.</summary>
    public static bool IsTieredKind(int kind) => kind == CatcherKind || IsRoutingKind(kind);

    public static int? KindOf(MoonWorld world, int machine) =>
        world?.depots != null && world.depots.TryGetValue(machine, out var d) && d != null ? d.kind : null;

    // ---------- building with a tier ----------

    static int building;

    static void BeforeBuild(MoonWorld world, int kind, out HashSet<int> state)
    {
        state = null;
        if (building++ > 0 || TierController.BuildTier <= TierStore.MinTier) return;
        Patches.World = world;
        if (!IsTieredKind(kind) || world.depots == null) return;
        state = new HashSet<int>();
        foreach (var entry in world.depots) state.Add(entry.Key);
    }

    static void AfterBuild(MoonWorld world, MoonWalker builder, bool built, HashSet<int> before)
    {
        building--;
        if (!built || before == null || !Patches.IsLocalBuilder(builder)) return;
        int tier = TierController.BuildTier;
        var added = new List<int>();
        foreach (var entry in world.depots)
            if (!before.Contains(entry.Key) && entry.Value != null && IsTieredKind(entry.Value.kind)) added.Add(entry.Key);
        foreach (int machine in added)
        {
            decimal extra = Pricing.MachineUpgradeCost(KindOf(world, machine) ?? -1, TierStore.MinTier, tier);
            if (!TierController.Charge(extra))
            {
                TierController.Notice($"Kein Geld für den Mk{tier}-Aufpreis ({BuildMenu.FormatMoney(extra)}), gebaut als Mk1", 3f);
                continue;
            }
            int key = TierStore.MachineKey(machine);
            if (TierStore.Set(key, tier)) Net.AnnounceSet(key, tier);
            Plugin.Logger.LogInfo($"Built machine {machine} ({Name(world, machine)}) as Mk{tier} for {extra} extra");
        }
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryBuildStructure))]
    [HarmonyPrefix]
    static void BeforeStructure(MoonWorld __instance, MoonWorld.StructurePlan plan, out HashSet<int> __state) =>
        BeforeBuild(__instance, plan != null ? plan.kind : -1, out __state);

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryBuildStructure))]
    [HarmonyPostfix]
    static void AfterStructure(MoonWorld __instance, MoonWalker builder, bool __result, HashSet<int> __state) =>
        AfterBuild(__instance, builder, __result, __state);

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryBuildDepot))]
    [HarmonyPrefix]
    static void BeforeDepot(MoonWorld __instance, int kind, out HashSet<int> __state) =>
        BeforeBuild(__instance, kind, out __state);

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryBuildDepot))]
    [HarmonyPostfix]
    static void AfterDepot(MoonWorld __instance, MoonWalker builder, bool __result, HashSet<int> __state) =>
        AfterBuild(__instance, builder, __result, __state);

    /// <summary>Demolishing refunds the tier's extra price at the game's refund rate.</summary>
    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.StructureRefund))]
    [HarmonyPostfix]
    static void Refund(MoonWorld __instance, int id, ref Il2CppSystem.Decimal __result)
    {
        int tier = TierStore.Get(TierStore.MachineKey(id));
        if (tier <= TierStore.MinTier) return;
        decimal extra = Pricing.MachineUpgradeCost(KindOf(__instance, id) ?? -1, TierStore.MinTier, tier);
        if (extra > 0m) __result = __result + MoonWorld.Refund((Il2CppSystem.Decimal)(double)extra);
    }

    // ---------- speed ----------

    /// <summary>Adds the extra distance for cargo inside upgraded machines (see Patches.AddOwed).</summary>
    public static void AddRouteOwed(MoonWorld world, float dt)
    {
        BoostedRoutes = 0;
        var owed = world.beltOwed;
        var queues = world.beltQueues;
        if (owed == null || queues == null) return;

        routes.Clear();
        foreach (var entry in owed)
            if (entry.Key < 0) routes.Add(entry.Key);
        foreach (var entry in queues)
            if (entry.Key < 0 && entry.Value != null && entry.Value.Count > 0 && !owed.ContainsKey(entry.Key)) routes.Add(entry.Key);

        foreach (int route in routes)
        {
            int machine = MoonWorld.RouteMachine(route);
            float m = TierStore.Multiplier(TierStore.MachineKey(machine));
            if (m == 1f) continue;
            if (Patches.AddOwed(owed, queues, route, m, dt)) BoostedRoutes++;
        }
    }
}
