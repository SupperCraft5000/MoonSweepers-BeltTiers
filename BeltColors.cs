// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using MoonCleaners;
using UnityEngine;

namespace BeltTiers;

/// <summary>
/// Tints the body of upgraded belts by tier (Mk2 green, Mk3 blue, Mk4 orange). Each belt view has one
/// renderer for its frame; it gets tinted copies of the game's materials. The moving slats are drawn
/// in shared batches for all belts and keep their look.
/// </summary>
internal static class BeltColors
{
    static readonly Color[] Tints =
    {
        Color.white, Color.white,
        new(0.55f, 1f, 0.55f),   // Mk2
        new(0.5f, 0.75f, 1f),    // Mk3
        new(1f, 0.6f, 0.25f),    // Mk4
    };

    static readonly Dictionary<(IntPtr source, int tier), Material> tinted = new();
    static readonly Dictionary<IntPtr, Material> sourceOf = new();
    static readonly Dictionary<IntPtr, int> applied = new();
    static int appliedRevision = -1;
    static float nextCheck;
    static bool failed;

    /// <summary>Called every frame; repaints at most twice a second.</summary>
    public static void Tick()
    {
        if (failed || !Plugin.ShowTierColors.Value || Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + 0.5f;
        var views = Patches.World?.ConveyorViews?.views;
        if (views == null) return;
        try
        {
            bool all = appliedRevision != TierStore.Revision;
            appliedRevision = TierStore.Revision;
            foreach (var entry in views)
            {
                var renderer = entry.Value?.renderer;
                if (renderer == null || renderer.WasCollected) continue;
                int tier = TierStore.Get(entry.Key);
                if (!all && applied.TryGetValue(renderer.Pointer, out int done) && done == tier && Painted(renderer, tier)) continue;
                Paint(renderer, tier);
                applied[renderer.Pointer] = tier;
            }
        }
        catch (Exception e)
        {
            failed = true;
            Plugin.Logger.LogError($"Belt colours turned off after error: {e}");
        }
    }

    /// <summary>False when the game has put its own materials back (e.g. after rebuilding the belt).</summary>
    static bool Painted(MeshRenderer renderer, int tier)
    {
        var first = renderer.sharedMaterial;
        if (first == null) return true;
        bool ours = sourceOf.ContainsKey(first.Pointer);
        return tier <= TierStore.MinTier ? !ours : ours;
    }

    static void Paint(MeshRenderer renderer, int tier)
    {
        var materials = renderer.sharedMaterials;
        if (materials == null) return;
        bool changed = false;
        for (int i = 0; i < materials.Length; i++)
        {
            var current = materials[i];
            if (current == null) continue;
            var source = sourceOf.TryGetValue(current.Pointer, out var s) ? s : current;
            var wanted = tier <= TierStore.MinTier ? source : Tinted(source, tier);
            if (wanted.Pointer == current.Pointer) continue;
            materials[i] = wanted;
            changed = true;
        }
        if (changed) renderer.sharedMaterials = materials;
    }

    static Material Tinted(Material source, int tier)
    {
        if (tinted.TryGetValue((source.Pointer, tier), out var material) && material != null && !material.WasCollected) return material;
        material = new Material(source) { name = $"{source.name} Mk{tier}" };
        material.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var tint = Tints[tier];
        foreach (string property in new[] { "_BaseColor", "_Color" })
            if (material.HasProperty(property)) material.SetColor(property, source.GetColor(property) * tint);
        tinted[(source.Pointer, tier)] = material;
        sourceOf[material.Pointer] = source;
        Plugin.Logger.LogInfo($"Belt colour Mk{tier}: tinted {source.name} ({source.shader?.name})");
        return material;
    }
}
