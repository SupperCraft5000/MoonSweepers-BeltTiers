// SPDX-License-Identifier: GPL-3.0-or-later
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace BeltTiers;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "moonsweepers.belttiers";
    public const string Name = "Belt Tiers";
    public const string Version = "0.1.0";

    // MoonWorld.BeltSpeed is a const in the game (inlined), so it is mirrored here.
    public const float VanillaBeltSpeed = 1.1f;

    internal static ManualLogSource Logger;
    internal static ConfigEntry<float>[] SpeedMultipliers;
    internal static ConfigEntry<float>[] CostMultipliers;
    internal static ConfigEntry<float> PickerX;
    internal static ConfigEntry<float> PickerY;
    internal static ConfigEntry<string> KeyTierUp;
    internal static ConfigEntry<string> KeyTierDown;
    internal static ConfigEntry<string> KeyApplyAimed;
    internal static ConfigEntry<string> KeyDebugAimed;
    internal static ConfigEntry<string> KeyTrace;
    internal static ConfigEntry<bool> ShowHud;
    internal static ConfigEntry<bool> ShowTierColors;
    internal static ConfigEntry<bool> BoostCargo;
    internal static ConfigEntry<bool> BoostMachines;
    internal static ConfigEntry<float> OutputBufferMultiplier;
    internal static ConfigEntry<float>[] CatchSeconds;
    internal static ConfigEntry<float> MachineBeltMetres;
    internal static ConfigEntry<float> CatcherBeltMetres;
    internal static ConfigEntry<float> CatcherStorePerTierKg;

    public override void Load()
    {
        Logger = Log;

        float[] defaults = { 1f, 2f, 3.5f, 5f };
        SpeedMultipliers = new ConfigEntry<float>[TierStore.MaxTier + 1];
        for (int tier = TierStore.MinTier; tier <= TierStore.MaxTier; tier++)
        {
            SpeedMultipliers[tier] = Config.Bind("Speeds", $"Mk{tier}", defaults[tier - 1],
                new ConfigDescription($"Speed multiplier for Mk{tier} belts (1 = vanilla {VanillaBeltSpeed} m/s).",
                    new AcceptableValueRange<float>(0.25f, 20f)));
        }

        float[] costs = { 1f, 4f, 10f, 25f };
        CostMultipliers = new ConfigEntry<float>[TierStore.MaxTier + 1];
        for (int tier = TierStore.MinTier; tier <= TierStore.MaxTier; tier++)
        {
            CostMultipliers[tier] = Config.Bind("Costs", $"Mk{tier}", costs[tier - 1],
                new ConfigDescription($"Price multiplier for Mk{tier} belts (1 = vanilla price per metre).",
                    new AcceptableValueRange<float>(0f, 1000f)));
        }

        KeyTierUp = Config.Bind("Keys", "BuildTierUp", "", "Optional key to raise the build tier (Unity InputSystem key name). The tier is normally picked in the build menu.");
        KeyTierDown = Config.Bind("Keys", "BuildTierDown", "", "Optional key to lower the build tier.");
        KeyApplyAimed = Config.Bind("Keys", "ApplyToAimedBelt", "Insert", "Upgrade the belt in the crosshair to the tier picked in the build menu (costs the price difference).");
        KeyDebugAimed = Config.Bind("Keys", "DebugAimedBelt", "F9", "Log the cargo queue of the belt in the crosshair (for diagnostics).");
        KeyTrace = Config.Bind("Keys", "ToggleTrace", "F10", "Log camera position and belt speeds once a second (for diagnostics).");
        ShowTierColors = Config.Bind("General", "ShowTierColors", true, "Tint upgraded belts by tier: Mk2 green, Mk3 blue, Mk4 orange.");
        ShowHud = Config.Bind("General", "ShowHud", true, "Show the selected tier and the aimed belt's tier on screen.");
        PickerX = Config.Bind("Layout", "PickerX", 0f, "Horizontal position of the tier info line in the build menu details panel.");
        PickerY = Config.Bind("Layout", "PickerY", 155f, "Distance of the MK1-MK4 chips from the top of the conveyor card.");
        BoostMachines = Config.Bind("General", "BoostMachines", true, "Let catchers, garages, packagers and production machines emit faster onto upgraded belts.");
        OutputBufferMultiplier = Config.Bind("General", "OutputBufferMultiplier", 3f,
            new ConfigDescription("Output buffer size of machines (production machines, robot docks, capsule catchers) as a multiple of vanilla. Takes effect after a restart.",
                new AcceptableValueRange<float>(1f, 20f)));
        float[] catchSeconds = { 15f, 8f, 4f, 1f };
        CatchSeconds = new ConfigEntry<float>[TierStore.MaxTier + 1];
        for (int tier = TierStore.MinTier; tier <= TierStore.MaxTier; tier++)
        {
            CatchSeconds[tier] = Config.Bind("Catchers", $"Mk{tier}", catchSeconds[tier - 1],
                new ConfigDescription($"Seconds between capsule landings at an Mk{tier} catcher (vanilla 9).",
                    new AcceptableValueRange<float>(0.3f, 60f)));
        }
        MachineBeltMetres = Config.Bind("Costs", "MachineBeltMetres", 4f,
            new ConfigDescription("Splitters and mergers are priced like a belt of this many metres when upgraded.",
                new AcceptableValueRange<float>(0f, 100f)));
        CatcherStorePerTierKg = Config.Bind("General", "CatcherStorePerTierKg", 150f,
            new ConfigDescription("Extra store of a capsule catcher per tier above Mk1 (Mk1 holds 100 kg x OutputBufferMultiplier).",
                new AcceptableValueRange<float>(0f, 1000f)));
        CatcherBeltMetres = Config.Bind("Costs", "CatcherBeltMetres", 10f,
            new ConfigDescription("Capsule catchers are priced like a belt of this many metres when upgraded.",
                new AcceptableValueRange<float>(0f, 100f)));
        BoostCargo = Config.Bind("General", "BoostCargo", true, "Move cargo faster on higher-tier belts. Turn off if cargo misbehaves.");

        RvaDump.Run();
        var harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(Patches));
        harmony.PatchAll(typeof(Pricing));
        harmony.PatchAll(typeof(BuildMenu));
        harmony.PatchAll(typeof(EmitStats));
        harmony.PatchAll(typeof(EmitPacing));
        harmony.PatchAll(typeof(OutputBuffers));
        harmony.PatchAll(typeof(Machines));
        harmony.PatchAll(typeof(CatcherChoice));
        AddComponent<TierController>();

        Logger.LogInfo($"{Name} {Version} loaded");
    }

    internal static float Multiplier(int tier)
    {
        if (tier < TierStore.MinTier || tier > TierStore.MaxTier) return 1f;
        return SpeedMultipliers[tier].Value;
    }

    internal static float CostFactor(int tier)
    {
        if (tier < TierStore.MinTier || tier > TierStore.MaxTier) return 1f;
        return CostMultipliers[tier].Value;
    }
}
