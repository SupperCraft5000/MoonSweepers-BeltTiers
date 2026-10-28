// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using Il2CppSystem.Collections.Generic;

namespace BeltTiers;

/// <summary>
/// Raw access to MoonWorld.beltQueues entries: List&lt;(float distance, int rider)&gt;.
/// The interop indexer for this generic value tuple returns garbage, so the list's backing
/// array is read directly. Layout (IL2CPP, x64):
///   List&lt;T&gt;: header 0x10, _items 0x10, _size 0x18
///   T[]:     header 0x20, then elements of 8 bytes (float at +0, int at +4)
/// </summary>
internal static unsafe class BeltQueue
{
    const int ItemsOffset = 0x10;
    const int SizeOffset = 0x18;
    const int ArrayDataOffset = 0x20;
    const int ArrayLengthOffset = 0x18;
    const int ElementSize = 8;

    /// <summary>Returns the element base pointer, or null when the layout does not match.</summary>
    static byte* Elements(List<Il2CppSystem.ValueTuple<float, int>> queue, out int count)
    {
        count = 0;
        if (queue == null) return null;
        byte* list = (byte*)queue.Pointer;
        if (list == null) return null;
        int size = *(int*)(list + SizeOffset);
        if (size != queue.Count) throw new InvalidOperationException($"List layout mismatch (_size {size} vs Count {queue.Count})");
        byte* items = *(byte**)(list + ItemsOffset);
        if (items == null || size == 0) return null;
        long capacity = *(long*)(items + ArrayLengthOffset);
        if (capacity < size) throw new InvalidOperationException($"Array layout mismatch (length {capacity} < size {size})");
        count = size;
        return items + ArrayDataOffset;
    }

    public static int Read(List<Il2CppSystem.ValueTuple<float, int>> queue, ref float[] distance, ref int[] rider)
    {
        byte* e = Elements(queue, out int count);
        if (e == null) return 0;
        if (distance == null || distance.Length < count) { distance = new float[count]; rider = new int[count]; }
        for (int i = 0; i < count; i++)
        {
            distance[i] = *(float*)(e + i * ElementSize);
            rider[i] = *(int*)(e + i * ElementSize + 4);
        }
        return count;
    }
}
