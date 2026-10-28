// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using MoonCleaners;
using UnityEngine;
using UnityEngine.Rendering;

namespace BeltTiers;

/// <summary>
/// While Ctrl is held in the build tool's belt mode, the belt or machine a click would upgrade glows
/// like the demolish target, in green (amber when it already has the chosen tier). Drawn the way the
/// game draws its demolish glow: the hologram material of the build definitions over the target's meshes.
/// </summary>
internal static class UpgradeHighlight
{
    static readonly Color Green = new(0.3f, 1f, 0.35f, 0.45f);
    static readonly Color Amber = new(1f, 0.7f, 0.2f, 0.45f);

    static Material green, amber;
    static readonly List<(Mesh mesh, Renderer renderer)> parts = new();
    static int partsKey = int.MinValue;
    static bool useDrawMesh, failed;

    /// <summary>Called every frame the build tool handles belt input while Ctrl is held.</summary>
    public static void Draw(Ray ray)
    {
        if (failed) return;
        try
        {
            var world = Patches.World;
            if (world == null) return;
            TierController.Aim(ray, TierController.BuildReach, out int belt, out int machine);
            if (belt < 0 && machine < 0) return;
            int key = belt >= 0 ? belt : TierStore.MachineKey(machine);
            if (belt >= 0 || key != partsKey) Collect(world, belt, machine, key); // belts may be reshaped under the same id
            var material = Material(world, TierStore.Get(key) == TierController.BuildTier);
            if (material == null) return;
            foreach (var (mesh, renderer) in parts)
            {
                if (mesh == null || mesh.WasCollected || renderer == null || renderer.WasCollected || !renderer.enabled) continue;
                var matrix = renderer.localToWorldMatrix;
                for (int i = 0; i < mesh.subMeshCount; i++) Render(mesh, i, matrix, material);
            }
        }
        catch (Exception e)
        {
            failed = true;
            Plugin.Logger.LogError($"Upgrade highlight turned off after error: {e}");
        }
    }

    static void Collect(MoonWorld world, int belt, int machine, int key)
    {
        parts.Clear();
        partsKey = key;
        if (belt >= 0)
        {
            var views = world.ConveyorViews?.views;
            if (views != null && views.TryGetValue(belt, out var view) && view != null)
                parts.Add((view.mesh, view.renderer));
            return;
        }
        var root = Machines.ViewOf(machine);
        if (root == null) return;
        foreach (var component in root.GetComponentsInChildren(Il2CppType.Of<MeshFilter>()))
        {
            var filter = component.TryCast<MeshFilter>();
            var renderer = filter != null ? filter.GetComponent<MeshRenderer>() : null;
            if (filter != null && renderer != null) parts.Add((filter.sharedMesh, renderer));
        }
    }

    static void Render(Mesh mesh, int submesh, Matrix4x4 matrix, Material material)
    {
        if (!useDrawMesh)
        {
            try
            {
                var rp = new RenderParams(material) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
                Graphics.RenderMesh(ref rp, mesh, submesh, matrix, new Il2CppSystem.Nullable<Matrix4x4>());
                return;
            }
            catch (Exception e)
            {
                useDrawMesh = true;
                Plugin.Logger.LogWarning($"Upgrade highlight: RenderMesh failed ({e.Message}), using DrawMesh");
            }
        }
        Graphics.DrawMesh(mesh, matrix, material, 0, null, submesh, null, ShadowCastingMode.Off, false);
    }

    static Material Material(MoonWorld world, bool sameTier)
    {
        if (sameTier ? amber != null && !amber.WasCollected : green != null && !green.WasCollected) return sameTier ? amber : green;
        var hologram = world.DepotDefinition?.previewMaterial;
        if (hologram == null) return null;
        var material = new Material(hologram) { name = sameTier ? "Belt Tiers upgrade (same)" : "Belt Tiers upgrade" };
        material.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var color = sameTier ? Amber : Green;
        var shader = material.shader;
        var names = new List<string>();
        for (int i = 0; shader != null && i < shader.GetPropertyCount(); i++)
        {
            if (shader.GetPropertyType(i) != ShaderPropertyType.Color) continue;
            string name = shader.GetPropertyName(i);
            material.SetColor(name, color);
            names.Add(name);
        }
        Plugin.Logger.LogInfo($"Upgrade highlight material from {hologram.name} ({shader?.name}), colours: {string.Join(", ", names)}");
        if (sameTier) amber = material; else green = material;
        return material;
    }
}
