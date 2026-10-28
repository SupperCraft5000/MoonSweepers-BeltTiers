// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using MoonCleaners;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BeltTiers;

/// <summary>Hotkeys, crosshair belt lookup and the small on-screen readout.</summary>
public class TierController : MonoBehaviour
{
    const float AimReach = 15f;

    public static int BuildTier { get; set; } = TierStore.MinTier;

    static string notice;
    static float noticeUntil;

    int aimedBelt = -1;
    int aimedMachine = -1; // splitter or merger
    GUIStyle style;

    // Diagnostics: once a second, log where the camera is and what belt speed the game asked for.
    public static bool Tracing { get; private set; }
    static float nextTrace;
    static string lastSurface = "none";

    public static int AimedBeltId { get; private set; } = -1;
    static string lastStep = "none";

    public static void TraceStep(string text) => lastStep = text;

    public static void TraceSurface(int id, float vanilla, float modded) =>
        lastSurface = $"belt {id} Mk{TierStore.Get(id)} vanilla {vanilla:0.00} m/s -> {modded:0.00} m/s";

    static void Trace()
    {
        if (!Tracing || Time.unscaledTime < nextTrace) return;
        nextTrace = Time.unscaledTime + 1f;
        var camera = Camera.main;
        string at = camera != null ? camera.transform.position.ToString("F2") : "?";
        Plugin.Logger.LogInfo($"[trace] camera {at} surface: {lastSurface} | belt {AimedBeltId} {lastStep} | machine routes boosted {Machines.BoostedRoutes} | emits: {EmitStats.Flush()} | catchers: {OutputBuffers.FlushCatchStats(Patches.World)}");
        lastSurface = "none";
        lastStep = "none";
    }

    public TierController(IntPtr ptr) : base(ptr) { }

    public static void Notice(string text, float seconds = 4f)
    {
        notice = text;
        noticeUntil = Time.unscaledTime + seconds;
        Plugin.Logger.LogInfo(text);
    }

    public static bool Held(Key key)
    {
        var control = Keyboard.current?.get_Item(key);
        return control != null && control.isPressed;
    }

    static bool Pressed(string keyName)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null || !Enum.TryParse(keyName, true, out Key key) || key == Key.None) return false;
        var control = keyboard.get_Item(key);
        return control != null && control.wasPressedThisFrame;
    }

    void Update()
    {
        try
        {
            if (!MoonSession.InGame)
            {
                aimedBelt = -1;
                aimedMachine = -1;
                return;
            }
            Net.Tick();
            Aim(out aimedBelt, out aimedMachine);
            AimedBeltId = aimedBelt;

            if (Pressed(Plugin.KeyTierUp.Value)) ChangeBuildTier(+1);
            if (Pressed(Plugin.KeyTierDown.Value)) ChangeBuildTier(-1);
            if (Pressed(Plugin.KeyApplyAimed.Value)) ApplyToAimed();
            if (Pressed(Plugin.KeyDebugAimed.Value)) DebugAimed();
            if (Pressed(Plugin.KeyTrace.Value))
            {
                Tracing = !Tracing;
                Notice(Tracing ? "Belt Tiers: Messprotokoll an" : "Belt Tiers: Messprotokoll aus", 2f);
            }
            BeltColors.Tick();
            Trace();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"TierController.Update: {e}");
        }
    }

    static void ChangeBuildTier(int delta)
    {
        BuildTier = Math.Clamp(BuildTier + delta, TierStore.MinTier, TierStore.MaxTier);
        Notice($"Neue Förderbänder: Mk{BuildTier} ({Plugin.Multiplier(BuildTier):0.##}x)", 2f);
    }

    void ApplyToAimed() => Upgrade(aimedBelt, aimedMachine);

    /// <summary>Build tool: Ctrl+click in belt mode upgrades what the build ray points at.</summary>
    public static void UpgradeAt(Ray ray)
    {
        Aim(ray, BuildReach, out int belt, out int machine);
        Upgrade(belt, machine);
    }

    internal const float BuildReach = 40f;

    /// <summary>Sets a belt (or else a splitter, merger or catcher) to the build tier, charging the difference.</summary>
    static void Upgrade(int aimedBelt, int aimedMachine)
    {
        if (aimedBelt < 0 && aimedMachine < 0)
        {
            Notice("Kein Förderband, Splitter, Merger oder Fänger im Fadenkreuz", 2f);
            return;
        }
        var world = Patches.World;
        bool machine = aimedBelt < 0;
        int key = machine ? TierStore.MachineKey(aimedMachine) : aimedBelt;
        string what = machine ? Machines.Name(world, aimedMachine) : "Band";
        int from = TierStore.Get(key);
        if (from == BuildTier)
        {
            Notice($"{what} ist schon Mk{BuildTier}", 2f);
            return;
        }
        decimal cost = world == null ? 0m
            : machine ? Pricing.MachineUpgradeCost(Machines.KindOf(world, aimedMachine) ?? -1, from, BuildTier)
            : Pricing.UpgradeCost(aimedBelt, from, BuildTier, world);
        if (!Charge(cost))
        {
            Notice($"Upgrade auf Mk{BuildTier} kostet {BuildMenu.FormatMoney(cost)}, nicht genug Geld", 3f);
            return;
        }
        Plugin.Logger.LogInfo($"Upgrade {(machine ? $"machine {aimedMachine}" : $"belt {aimedBelt}")} Mk{from}->Mk{BuildTier} for {cost}");
        if (TierStore.Set(key, BuildTier)) Net.AnnounceSet(key, BuildTier);
        // Downgrades are free and refund nothing.
        Notice(cost > 0m ? $"{what} auf Mk{BuildTier} aufgerüstet (-{BuildMenu.FormatMoney(cost)})" : $"{what} auf Mk{BuildTier} gesetzt", 3f);
    }

    /// <summary>Takes money from the local player's wallet; false if there is not enough.</summary>
    public static bool Charge(decimal cost)
    {
        var world = Patches.World;
        if (cost <= 0m || world == null) return true;
        string player = MoonWorld.LocalPlayer;
        double wallet = Il2CppSystem.Decimal.ToDouble(world.WalletDollars(player));
        if (wallet < (double)cost) return false;
        // ChargeBuild takes the change to the wallet, so a cost is negative.
        world.ChargeBuild(player, (Il2CppSystem.Decimal)(double)(-cost));
        return true;
    }

    void DebugAimed()
    {
        var world = Patches.World;
        if (world == null) return;
        if (aimedBelt < 0)
        {
            // Nothing aimed at: list every belt the world knows.
            string all = $"Belts in world ({world.conveyors?.Count ?? 0}):";
            if (world.conveyors != null)
                foreach (var entry in world.conveyors)
                {
                    var p = world.BeltPathOf(entry.Key);
                    all += $" #{entry.Key} Mk{TierStore.Get(entry.Key)} {(p != null ? p.Length : -1f):0.0}m {entry.Value?.owner};";
                }
            Plugin.Logger.LogInfo(all);
            Notice("Alle Bänder: Details im Log", 2f);
            return;
        }
        var path = world.BeltPathOf(aimedBelt);
        string text = $"Belt {aimedBelt} tier Mk{TierStore.Get(aimedBelt)} length {(path != null ? path.Length : -1f):0.00}";
        if (world.beltQueues != null && world.beltQueues.TryGetValue(aimedBelt, out var queue) && queue != null)
        {
            float[] distance = null;
            int[] rider = null;
            int n = BeltQueue.Read(queue, ref distance, ref rider);
            text += $" queue({n}):";
            for (int i = 0; i < n; i++) text += $" [{distance[i]:0.00} #{rider[i]}]";
        }
        Plugin.Logger.LogInfo(text);
        Notice($"Band #{aimedBelt}: Details im Log", 2f);
    }

    /// <summary>The belt, or else the splitter or merger, in the crosshair (-1 for none).</summary>
    static void Aim(out int belt, out int machine)
    {
        belt = machine = -1;
        var camera = Camera.main;
        if (camera == null) return;
        Aim(new Ray(camera.transform.position, camera.transform.forward), AimReach, out belt, out machine);
    }

    internal static void Aim(Ray ray, float reach, out int belt, out int machine)
    {
        belt = machine = -1;
        var world = Patches.World;
        if (world == null) return;
        if (!Physics.Raycast(ray, out RaycastHit hit, reach)) return;
        var views = world.ConveyorViews;
        int id = views != null ? views.BeltAt(hit.collider) : -1;
        if (id >= 0 && world.conveyors != null && world.conveyors.ContainsKey(id)) belt = id;
        else machine = Machines.At(hit.collider);
    }

    void OnGUI()
    {
        if (!Plugin.ShowHud.Value || !MoonSession.InGame) return;
        style ??= new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.UpperRight };

        string text = "";
        if (aimedBelt >= 0)
        {
            int tier = TierStore.Get(aimedBelt);
            text = $"Band: Mk{tier} ({Plugin.Multiplier(tier) * Plugin.VanillaBeltSpeed:0.0} m/s)";
        }
        else if (aimedMachine >= 0)
        {
            int tier = TierStore.Get(TierStore.MachineKey(aimedMachine));
            string name = Machines.Name(Patches.World, aimedMachine);
            text = Machines.KindOf(Patches.World, aimedMachine) == Machines.CatcherKind
                ? $"{name}: Mk{tier} (1 Kapsel alle {OutputBuffers.CatchSeconds(aimedMachine):0.0} s, {OutputBuffers.CatcherCapacityKg(tier):0} kg)"
                : $"{name}: Mk{tier} ({Plugin.Multiplier(tier) * Plugin.VanillaBeltSpeed:0.0} m/s)";
        }
        if (notice != null && Time.unscaledTime < noticeUntil) text += (text.Length > 0 ? "\n" : "") + notice;
        if (text.Length == 0) return;

        float width = 420f;
        GUI.Label(new Rect(Screen.width - width - 20f, 20f, width, 90f), text, style);
    }
}
