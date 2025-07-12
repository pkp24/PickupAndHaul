using Verse;
using System.Collections.Generic;

namespace PickUpAndHaul;

/// <summary>
/// Tracks temporary reservations for storage capacity so multiple pawns can
/// unload into the same container without overfilling it.
/// </summary>
public static class StorageReservationManager
{
    private static readonly Dictionary<Thing, int> thingReserved = new();
    private static readonly Dictionary<IntVec3, int> cellReserved = new();

    public static int Reserved(Thing container)
        => container != null && thingReserved.TryGetValue(container, out var v) ? v : 0;

    public static int Reserved(IntVec3 cell)
        => cell != default && cellReserved.TryGetValue(cell, out var v) ? v : 0;

    public static void Reserve(Thing container, int count)
    {
        if (container == null || count <= 0) return;
        thingReserved.TryGetValue(container, out var v);
        thingReserved[container] = v + count;
    }

    public static void Reserve(IntVec3 cell, int count)
    {
        if (cell == default || count <= 0) return;
        cellReserved.TryGetValue(cell, out var v);
        cellReserved[cell] = v + count;
    }

    public static void Release(Thing container, int count)
    {
        if (container == null || count <= 0) return;
        if (!thingReserved.TryGetValue(container, out var v)) return;
        v -= count;
        if (v <= 0) thingReserved.Remove(container); else thingReserved[container] = v;
    }

    public static void Release(IntVec3 cell, int count)
    {
        if (cell == default || count <= 0) return;
        if (!cellReserved.TryGetValue(cell, out var v)) return;
        v -= count;
        if (v <= 0) cellReserved.Remove(cell); else cellReserved[cell] = v;
    }
}
