using System.Collections.Generic;
using Verse;

namespace PickUpAndHaul;

public static class StorageContainerReservationManager
{
    private static readonly Dictionary<Thing, int> Reserved = new();

    public static int GetReserved(Thing container)
    {
        return container != null && Reserved.TryGetValue(container, out var val) ? val : 0;
    }

    public static bool TryReserve(Thing container, Thing thing, int count)
    {
        if (container == null || thing == null)
            return true;

        var map = container.Map;
        int capacity;
        if (!HoldMultipleThings_Support.CapacityAt(thing, container.Position, map, out capacity))
        {
            var owner = container.TryGetInnerInteractableThingOwner();
            capacity = owner?.GetCountCanAccept(thing) ?? thing.def.stackLimit;
        }

        var reserved = GetReserved(container);
        if (reserved + count > capacity)
            return false;

        Reserved[container] = reserved + count;
        return true;
    }

    public static void Release(Thing container, int count)
    {
        if (container == null)
            return;

        if (Reserved.TryGetValue(container, out var val))
        {
            val -= count;
            if (val <= 0)
                Reserved.Remove(container);
            else
                Reserved[container] = val;
        }
    }
}
