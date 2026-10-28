// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace BeltTiers;

/// <summary>
/// Development aid: writes the native address (relative to GameAssembly.dll) of every game method
/// to BepInEx\belttiers-rva.tsv, once per game build, for reading the machine code offline.
/// </summary>
internal static class RvaDump
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string name);

    internal static unsafe void Run()
    {
        try
        {
            string path = Path.Combine(BepInEx.Paths.BepInExRootPath, "belttiers-rva.tsv");
            if (File.Exists(path)) return;
            long module = GetModuleHandle("GameAssembly.dll").ToInt64();
            var text = new StringBuilder();
            var assembly = typeof(MoonCleaners.MoonWorld).Assembly;
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null); }
            foreach (var type in types)
                foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (!field.Name.StartsWith("NativeMethodInfoPtr_") || field.FieldType != typeof(IntPtr)) continue;
                    var info = (IntPtr)field.GetValue(null);
                    if (info == IntPtr.Zero) continue;
                    long code = *(long*)info; // Il2CppMethodInfo.methodPointer
                    if (code == 0) continue;
                    text.Append((code - module).ToString("x")).Append('\t')
                        .Append(type.FullName).Append("::").Append(field.Name.Substring(20)).Append('\n');
                }
            File.WriteAllText(path, text.ToString());
            Plugin.Logger.LogInfo($"Wrote method addresses to {path}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Method address dump failed: {e.Message}");
        }
    }
}
