// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MoonCleaners;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BeltTiers;

/// <summary>
/// Tier picker in the build menu: conveyor, splitter and merger cards get a row of MK1–MK4 chips,
/// the details panel names the chosen tier's speed and price, and the prices the menu shows follow it.
/// The chips sit on the card itself because the details panel reverts to the selected structure
/// as soon as the pointer leaves the card.
/// </summary>
internal static class BuildMenu
{
    const int ConveyorKind = 1000; // MoonDepotBuilder.ConveyorKind
    const float CardW = 260f;      // (BayWidth - 2 * BayInner - 3 * CardGap) / Columns
    const float ChipW = 52f, ChipH = 30f, ChipGap = 6f;

    sealed class Picker
    {
        public Image[] Chips = new Image[TierStore.MaxTier + 1];
        public TMP_Text[] Texts = new TMP_Text[TierStore.MaxTier + 1];
    }

    static readonly List<Picker> pickers = new();
    static TMP_Text info;
    static MoonDepotBuilder owner;

    static bool Alive(UnityEngine.Object o) => o != null && !o.WasCollected;

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.BuildCard))]
    [HarmonyPostfix]
    static void AfterBuildCard(MoonDepotBuilder __instance, MoonDepotBuilder.CatalogEntry entry)
    {
        if (entry == null || !Tiered(entry.kind)) return;
        try
        {
            owner = __instance;
            var cards = __instance.cards;
            if (cards == null || cards.Count == 0) return;
            var card = cards.get_Item(cards.Count - 1);
            if (card == null || card.entry == null || card.entry.Pointer != entry.Pointer) return;
            AddChips(__instance.catalogKit, Alive(card.lift) ? card.lift : card.rect, entry.kind);
            MarkChips();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Tier picker failed: {e}");
        }
    }

    static bool Tiered(int kind) => kind == ConveyorKind || Machines.IsTieredKind(kind);

    static void AddChips(MoonWorkshopKit kit, RectTransform parent, int kind)
    {
        if (kit == null || !Alive(parent)) return;
        var picker = new Picker();
        float x0 = (CardW - (4 * ChipW + 3 * ChipGap)) / 2f;
        for (int tier = TierStore.MinTier; tier <= TierStore.MaxTier; tier++)
        {
            var chip = kit.Chip(parent, x0 + (tier - 1) * (ChipW + ChipGap), Plugin.PickerY.Value, ChipW, ChipH, 1f);
            var text = kit.Text(chip.transform, $"MK{tier}", 0f, 0f, ChipW, ChipH, 16f, MoonWorkshopKit.Cream, kit.caps, TextAlignmentOptions.Center);
            int chosen = tier;
            MoonWorkshopKit.Clickable(chip.rectTransform, DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() => Choose(chosen, kind))), out _);
            picker.Chips[tier] = chip;
            picker.Texts[tier] = text;
        }
        pickers.Add(picker);
    }

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.ShowDetails))]
    [HarmonyPostfix]
    static void AfterShowDetails(MoonDepotBuilder __instance, int kind)
    {
        try
        {
            owner = __instance;
            bool tiered = Tiered(kind);
            if (tiered) EnsureInfo(__instance);
            if (Alive(info)) info.gameObject.SetActive(tiered);
            if (tiered) RefreshDetails(kind);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Tier details failed: {e}");
        }
    }

    // Machine prices: the game formats them from a Decimal (MoonDepotBuilder.Price), on the cards
    // through ShowPrice and in the details panel inside RefreshCatalog, so the tier's extra price is
    // added to the amount before it is formatted.

    static bool inShowPrice;
    static int detailKind = -1;

    static Il2CppSystem.Decimal Plus(Il2CppSystem.Decimal money, decimal extra) =>
        money + (Il2CppSystem.Decimal)(double)extra;

    static decimal Extra(int kind) => Pricing.MachineUpgradeCost(kind, TierStore.MinTier, TierController.BuildTier);

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.ShowPrice))]
    [HarmonyPrefix]
    static void BeforeShowPrice(int kind, ref Il2CppSystem.Decimal money)
    {
        inShowPrice = true;
        if (kind == ConveyorKind || !Machines.IsTieredKind(kind)) return;
        decimal extra = Extra(kind);
        if (extra > 0m) money = Plus(money, extra);
    }

    /// <summary>Per-metre price tags on the conveyor card follow the chosen tier.</summary>
    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.ShowPrice))]
    [HarmonyPostfix]
    static void AfterShowPrice(TMP_Text text, int kind, MoonConveyorKit kit)
    {
        inShowPrice = false;
        if (kind != ConveyorKind || !Alive(text) || kit == null) return;
        text.text = WithPrice(text.text, kit, TierController.BuildTier);
    }

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.RefreshCatalog))]
    [HarmonyPrefix]
    static void BeforeRefreshCatalog(MoonDepotBuilder __instance) =>
        detailKind = Machines.IsTieredKind(__instance.selectedStructure) ? __instance.selectedStructure : -1;

    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.RefreshCatalog))]
    [HarmonyPostfix]
    static void AfterRefreshCatalog() => detailKind = -1;

    /// <summary>The details panel's price of a selected splitter, merger or catcher.</summary>
    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.Price))]
    [HarmonyPrefix]
    static void BeforePrice(ref Il2CppSystem.Decimal dollars)
    {
        if (detailKind < 0 || inShowPrice || Patches.World == null) return;
        var def = Patches.World.BuildDefinition(detailKind);
        if (def == null || Il2CppSystem.Decimal.ToDouble(dollars) != def.priceDollars) return;
        decimal extra = Extra(detailKind);
        if (extra > 0m) dollars = Plus(dollars, extra);
    }

    /// <summary>In belt mode, Ctrl+click upgrades the belt or machine under the build ray instead of building.</summary>
    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.UpdateConveyor))]
    [HarmonyPrefix]
    static bool BeforeUpdateConveyor(MoonDepotBuilder.BuildInput input)
    {
        try
        {
            if (!CtrlHeld(input)) return true;
            UpgradeHighlight.Draw(input.aim);
            if (!input.place) return true;
            TierController.UpgradeAt(input.aim);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Build tool upgrade failed: {e}");
            return true;
        }
        return false;
    }

    static bool CtrlHeld(MoonDepotBuilder.BuildInput input) =>
        input.ctrl || TierController.Held(UnityEngine.InputSystem.Key.LeftCtrl) || TierController.Held(UnityEngine.InputSystem.Key.RightCtrl);

    /// <summary>While placing belts, the prompt names the tier being built.</summary>
    [HarmonyPatch(typeof(MoonDepotBuilder), nameof(MoonDepotBuilder.SetPrompt))]
    [HarmonyPrefix]
    static void BeforeSetPrompt(MoonDepotBuilder __instance, ref string text)
    {
        if (string.IsNullOrEmpty(text) || text.StartsWith("MK")) return;
        if (!__instance.conveyorMode && !Machines.IsTieredKind(__instance.ghostKind) && !Machines.IsTieredKind(__instance.selectedStructure)) return;
        text = $"MK{TierController.BuildTier} · {text}" + (__instance.conveyorMode ? " · STRG+KLICK AUFRÜSTEN" : "");
    }

    /// <summary>A line above the footprint readout with the chosen tier's speed and price.</summary>
    static void EnsureInfo(MoonDepotBuilder builder)
    {
        if (Alive(info)) return;
        var kit = builder.catalogKit;
        var footprint = builder.detailFootprint;
        if (kit == null || !Alive(footprint)) return;
        var anchor = footprint.rectTransform;
        info = kit.Text(anchor.parent, "", 0f, 0f, 520f, 30f, 18f, MoonWorkshopKit.Soft, kit.text, TextAlignmentOptions.Left);
        var rect = info.rectTransform;
        rect.anchorMin = anchor.anchorMin;
        rect.anchorMax = anchor.anchorMin;
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(Plugin.PickerX.Value, anchor.anchoredPosition.y + 44f);
    }

    static void Choose(int tier, int kind)
    {
        TierController.BuildTier = tier;
        MarkChips();
        // Refresh the catalog first: it rewrites the details readouts, which ShowDetails then adjusts.
        if (!Alive(owner)) return;
        owner.RefreshCatalog();
        owner.ShowDetails(kind);
    }

    static void MarkChips()
    {
        int chosen = TierController.BuildTier;
        pickers.RemoveAll(p => !Alive(p.Chips[TierStore.MinTier]));
        foreach (var picker in pickers)
            for (int tier = TierStore.MinTier; tier <= TierStore.MaxTier; tier++)
                MoonWorkshopKit.MarkLabel(picker.Chips[tier], picker.Texts[tier], tier == chosen, false);
    }

    static void RefreshDetails(int kind)
    {
        var kit = MoonConveyorKit.loaded ?? MoonConveyorKit.Load();
        if (kit == null) return;
        int chosen = TierController.BuildTier;
        string speed = $"Mk{chosen}: {Plugin.Multiplier(chosen) * Plugin.VanillaBeltSpeed:0.0} m/s";
        var price = owner != null ? owner.detailPrice : null;
        if (kind == ConveyorKind)
        {
            if (Alive(info)) info.text = $"{speed} · {FormatMoney(PerMetre(kit, chosen))} / m";
            if (Alive(price)) price.text = WithPrice(price.text, kit, chosen);
            return;
        }
        if (kind == Machines.CatcherKind)
        {
            float seconds = OutputBuffers.TierCatchSeconds(chosen);
            speed = $"Mk{chosen}: 1 Kapsel alle {seconds:0.0} s · {OutputBuffers.CatcherCapacityKg(chosen):0} kg";
        }
        decimal extra = Extra(kind);
        if (Alive(info)) info.text = extra > 0m ? $"{speed} · Aufpreis {FormatMoney(extra)}" : speed;
    }

    internal static decimal PerMetre(MoonConveyorKit kit, int tier) =>
        Math.Round((decimal)kit.pricePerMetre * (decimal)Plugin.CostFactor(tier), 2);

    internal static string FormatMoney(decimal dollars) => "$" + dollars.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>The game's own money string, including its rich-text markup.</summary>
    static string GamePrice(decimal dollars) => MoonDepotBuilder.Price((Il2CppSystem.Decimal)(double)dollars);

    /// <summary>Swaps the vanilla per-metre price inside a menu text for the tier's price.</summary>
    static string WithPrice(string text, MoonConveyorKit kit, int tier)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string vanilla = GamePrice(PerMetre(kit, TierStore.MinTier));
        string priced = GamePrice(PerMetre(kit, tier));
        int at = text.IndexOf(vanilla, StringComparison.Ordinal);
        if (at < 0)
        {
            // Some readouts use plain text instead of the game's price markup.
            vanilla = FormatMoney(PerMetre(kit, TierStore.MinTier));
            priced = FormatMoney(PerMetre(kit, tier));
            at = text.IndexOf(vanilla, StringComparison.Ordinal);
        }
        return at < 0 ? text : text.Substring(0, at) + priced + text.Substring(at + vanilla.Length);
    }
}
