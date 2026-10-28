// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using MoonCleaners;
using UnityEngine;
using Il2CppBinaryWriter = Il2CppSystem.IO.BinaryWriter;

namespace BeltTiers;

/// <summary>
/// Tier sync over the game's own crew messages, using a message kind the game does not use.
/// The host owns the table: it answers requests and rebroadcasts it periodically; any peer may
/// announce single changes, which the host folds into its table.
/// </summary>
internal static class Net
{
    const byte MessageKind = 200;
    const byte ModProtocol = 1;

    const byte OpSet = 1;
    const byte OpTable = 2;
    const byte OpRequest = 3;

    const float TableEvery = 15f;
    const float RequestEvery = 3f;

    static int headerLength = -1;
    static int kindOffset = -1;

    static bool haveTable;
    static float nextTable;
    static float nextRequest;

    public static bool InCrew => MoonSteamLobby.InLobby && MoonCrewSession.Instance != null;
    public static bool IsClient => InCrew && !MoonSteamLobby.IsHost;

    /// <summary>Learns where the game puts the kind byte by building a probe message.</summary>
    static bool EnsureHeader()
    {
        if (headerLength >= 0) return true;
        try
        {
            var probe = MoonCrewSession.Message((MoonCrewSession.Kind)MessageKind);
            byte[] bytes = probe.ToArray();
            headerLength = bytes.Length;
            kindOffset = Array.LastIndexOf(bytes, MessageKind);
            Plugin.Logger.LogInfo($"Crew message header: [{BitConverter.ToString(bytes)}], kind at {kindOffset}");
            if (kindOffset < 0) Plugin.Logger.LogError("Could not find the kind byte in the message header; tier sync is off.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Probing crew message header failed: {e}");
            headerLength = 0;
            kindOffset = -1;
        }
        return kindOffset >= 0;
    }

    public static void ResetSession()
    {
        haveTable = false;
        nextRequest = 0f;
        nextTable = 0f;
    }

    public static void Tick()
    {
        if (!InCrew || !EnsureHeader()) return;
        float now = Time.unscaledTime;
        if (MoonSteamLobby.IsHost)
        {
            // Single changes go out immediately via AnnounceSet; the full table is the safety net.
            if (now >= nextTable)
            {
                nextTable = now + TableEvery;
                Send(WriteTable, null);
            }
        }
        else if (!haveTable && now >= nextRequest)
        {
            nextRequest = now + RequestEvery;
            Send(w => w.Write(OpRequest), MoonSteamLobby.HostId);
        }
    }

    public static void AnnounceSet(int id, int tier) => AnnounceSet(new[] { new KeyValuePair<int, int>(id, tier) });

    public static void AnnounceSet(IList<KeyValuePair<int, int>> changes)
    {
        if (!InCrew || changes.Count == 0 || !EnsureHeader()) return;
        Send(w =>
        {
            w.Write(OpSet);
            w.Write(changes.Count);
            foreach (var c in changes) { w.Write(c.Key); w.Write((byte)c.Value); }
        }, null);
    }

    static void WriteTable(Il2CppBinaryWriter w)
    {
        var all = TierStore.Snapshot();
        w.Write(OpTable);
        w.Write(all.Count);
        foreach (var e in all) { w.Write(e.Key); w.Write((byte)e.Value); }
    }

    static void Send(Action<Il2CppBinaryWriter> body, ulong? to)
    {
        var session = MoonCrewSession.Instance;
        if (session == null) return;
        try
        {
            var message = MoonCrewSession.Message((MoonCrewSession.Kind)MessageKind);
            var w = new Il2CppBinaryWriter(message);
            w.Write(ModProtocol);
            body(w);
            w.Flush();
            if (to.HasValue) session.SendTo(to.Value, message, 0);
            else session.SendToCrew(message);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Sending belt tiers failed: {e.Message}");
        }
    }

    /// <summary>Called before MoonCrewSession.Handle. Returns true when the message was ours.</summary>
    public static bool TryHandle(MoonCrewSession session, ulong sender, int size)
    {
        if (kindOffset < 0 || size <= headerLength) return false;
        var buffer = session.receiveBuffer;
        if (buffer == null || buffer.Length < size || buffer[kindOffset] != MessageKind) return false;

        var data = new byte[size - headerLength];
        for (int i = 0; i < data.Length; i++) data[i] = buffer[headerLength + i];

        try
        {
            using var r = new BinaryReader(new MemoryStream(data));
            byte protocol = r.ReadByte();
            if (protocol != ModProtocol)
            {
                TierController.Notice($"Belt Tiers: Mitspieler hat eine andere Mod-Version ({protocol} statt {ModProtocol})");
                return true;
            }
            switch (r.ReadByte())
            {
                case OpSet:
                    ReadSet(r, sender);
                    break;
                case OpTable:
                    if (sender == MoonSteamLobby.HostId) ReadTable(r);
                    break;
                case OpRequest:
                    if (MoonSteamLobby.IsHost) Send(WriteTable, sender);
                    break;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Bad belt tier message from {sender}: {e.Message}");
        }
        return true;
    }

    static void ReadSet(BinaryReader r, ulong sender)
    {
        int count = r.ReadInt32();
        if (count < 0 || count > 20000) return;
        var changes = new List<KeyValuePair<int, int>>(count);
        for (int i = 0; i < count; i++)
        {
            int id = r.ReadInt32();
            int tier = r.ReadByte();
            if (TierStore.Set(id, tier)) changes.Add(new KeyValuePair<int, int>(id, tier));
        }
        // The host relays changes so every crewmate gets them even without direct links.
        if (MoonSteamLobby.IsHost && sender != MoonSteamLobby.HostId) AnnounceSet(changes);
    }

    static void ReadTable(BinaryReader r)
    {
        int count = r.ReadInt32();
        if (count < 0 || count > 20000) return;
        var entries = new List<KeyValuePair<int, int>>(count);
        for (int i = 0; i < count; i++) entries.Add(new KeyValuePair<int, int>(r.ReadInt32(), r.ReadByte()));
        TierStore.ReplaceAll(entries);
        if (!haveTable) Plugin.Logger.LogInfo($"Received belt tier table from host ({count} upgraded belts)");
        haveTable = true;
    }
}
