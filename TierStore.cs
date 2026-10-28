// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MoonCleaners;

namespace BeltTiers;

/// <summary>
/// Belt id → tier. Belts without an entry are Mk1, so vanilla saves load unchanged. Splitters and
/// mergers share the table under negative keys (see <see cref="MachineKey"/>).
/// </summary>
internal static class TierStore
{
    public const int MinTier = 1;
    public const int MaxTier = 4;
    const string FileName = "belttiers.txt";

    static readonly Dictionary<int, int> tiers = new();

    public static int Count => tiers.Count;
    public static int Revision { get; private set; }

    public static int Get(int id) => tiers.TryGetValue(id, out int tier) ? tier : MinTier;

    public static float Multiplier(int id) => Plugin.Multiplier(Get(id));

    /// <summary>Key of a machine (splitter, merger) in the table; belt ids are never negative.</summary>
    public static int MachineKey(int machine) => -machine - 1;

    static bool IsMachineKey(int key, out int machine)
    {
        machine = -key - 1;
        return key < 0;
    }

    public static bool Set(int id, int tier)
    {
        tier = Math.Clamp(tier, MinTier, MaxTier);
        if (Get(id) == tier) return false;
        if (tier == MinTier) tiers.Remove(id);
        else tiers[id] = tier;
        Revision++;
        return true;
    }

    public static void ReplaceAll(IEnumerable<KeyValuePair<int, int>> entries)
    {
        tiers.Clear();
        foreach (var e in entries)
            if (e.Value > MinTier && e.Value <= MaxTier) tiers[e.Key] = e.Value;
        Revision++;
    }

    public static List<KeyValuePair<int, int>> Snapshot() => new(tiers);

    public static void Clear()
    {
        tiers.Clear();
        Revision++;
    }

    /// <summary>Set once a save's belts have been restored, so pruning never runs against a half-loaded world.</summary>
    public static bool WorldRestored { get; set; }

    /// <summary>Drops tiers of belts and machines that no longer exist.</summary>
    public static void Prune(MoonWorld world)
    {
        var conveyors = world.conveyors;
        var depots = world.depots;
        if (!WorldRestored || conveyors == null || depots == null) return;
        var dead = new List<int>();
        foreach (int id in tiers.Keys)
            if (IsMachineKey(id, out int machine) ? !depots.ContainsKey(machine) : !conveyors.ContainsKey(id)) dead.Add(id);
        foreach (int id in dead) tiers.Remove(id);
        if (dead.Count > 0) Revision++;
    }

    static string SlotPath()
    {
        int slot = MoonSaveSlots.Active;
        if (slot < 0) return null;
        string folder = MoonSaveSlots.SlotFolder(slot);
        return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, FileName);
    }

    public static void Save()
    {
        string path = SlotPath();
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var w = new StreamWriter(path + ".tmp");
            w.WriteLine("# Belt Tiers: belt id (negative: -machine id - 1), tier");
            foreach (var e in tiers) w.WriteLine($"{e.Key} {e.Value}");
            w.Close();
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Saving belt tiers to {path} failed: {e.Message}");
        }
    }

    public static void Load()
    {
        tiers.Clear();
        Revision++;
        string path = SlotPath();
        if (path == null || !File.Exists(path)) return;
        try
        {
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.StartsWith("#")) continue;
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2
                    && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tier))
                    Set(id, tier);
            }
            Plugin.Logger.LogInfo($"Loaded {tiers.Count} upgraded belts and machines from {path}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Loading belt tiers from {path} failed: {e.Message}");
        }
    }
}
