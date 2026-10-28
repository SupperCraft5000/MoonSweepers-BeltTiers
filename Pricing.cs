// SPDX-License-Identifier: GPL-3.0-or-later
using HarmonyLib;
using MoonCleaners;

namespace BeltTiers;

/// <summary>
/// Belt prices scale with the tier. MoonConveyorKit.Price has no belt context, so the tier it
/// prices is the selected build tier, except while the game computes a refund for an existing
/// belt, where it is that belt's own tier (otherwise switching to Mk4 before demolishing a Mk1
/// belt would refund the Mk4 price).
/// </summary>
internal static class Pricing
{
    static int refundTier;

    static int PricedTier => refundTier > 0 ? refundTier : TierController.BuildTier;

    [HarmonyPatch(typeof(MoonConveyorKit), nameof(MoonConveyorKit.Price))]
    [HarmonyPostfix]
    static void Price(ref Il2CppSystem.Decimal __result)
    {
        float factor = Plugin.CostFactor(PricedTier);
        if (factor != 1f) __result = __result * (Il2CppSystem.Decimal)(double)factor;
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.ConveyorRefund))]
    [HarmonyPrefix]
    static void BeforeRefund(int id, out int __state)
    {
        __state = refundTier;
        refundTier = TierStore.Get(id);
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.ConveyorRefund))]
    [HarmonyPostfix]
    static void AfterRefund(int __state) => refundTier = __state;

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryRemoveConveyor))]
    [HarmonyPrefix]
    static void BeforeRemove(int id, out int __state)
    {
        __state = refundTier;
        refundTier = TierStore.Get(id);
    }

    [HarmonyPatch(typeof(MoonWorld), nameof(MoonWorld.TryRemoveConveyor))]
    [HarmonyPostfix]
    static void AfterRemove(int __state) => refundTier = __state;

    /// <summary>Cost of raising an existing belt to a higher tier: the price difference for its length.</summary>
    public static decimal UpgradeCost(int id, int fromTier, int toTier, MoonWorld world)
    {
        if (toTier <= fromTier) return 0m;
        var kit = MoonConveyorKit.loaded ?? MoonConveyorKit.Load();
        var path = world.BeltPathOf(id);
        if (kit == null || path == null) return 0m;
        decimal perMetre = (decimal)kit.pricePerMetre * (decimal)(Plugin.CostFactor(toTier) - Plugin.CostFactor(fromTier));
        return System.Math.Round(perMetre * (decimal)path.Length, 2);
    }

    /// <summary>
    /// Cost of raising a splitter, merger or catcher: priced like a belt of MachineBeltMetres
    /// (CatcherBeltMetres for catchers).
    /// </summary>
    public static decimal MachineUpgradeCost(int kind, int fromTier, int toTier)
    {
        if (toTier <= fromTier) return 0m;
        var kit = MoonConveyorKit.loaded ?? MoonConveyorKit.Load();
        if (kit == null) return 0m;
        decimal perMetre = (decimal)kit.pricePerMetre * (decimal)(Plugin.CostFactor(toTier) - Plugin.CostFactor(fromTier));
        float metres = kind == Machines.CatcherKind ? Plugin.CatcherBeltMetres.Value : Plugin.MachineBeltMetres.Value;
        return System.Math.Round(perMetre * (decimal)metres, 2);
    }
}
