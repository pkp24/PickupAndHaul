using Verse;

namespace PickUpAndHaul
{
    public static class StorageContainerReservationManager
    {
        /// <summary>
        /// Attempts to reserve storage space for a thing in a container.
        /// </summary>
        /// <param name="container">The container to reserve space in</param>
        /// <param name="thing">The thing to reserve space for</param>
        /// <returns>True if reservation is successful, false otherwise</returns>
        public static bool TryReserve(Thing container, Thing thing)
        {
            // Fix Bug 2: Return false (failure) when container or thing parameters are null
            if (container == null || thing == null)
            {
                return false;
            }

            // Get the container's position for capacity calculation
            var storeCell = container.Position;
            var map = container.Map;

            // First, try to get capacity from HoldMultipleThings_Support
            if (HoldMultipleThings_Support.CapacityAt(thing, storeCell, map, out var capacity))
            {
                // Successfully got capacity from external support
                return capacity >= thing.stackCount;
            }

            // Fix Bug 1: Check for inner ThingOwner instead of defaulting to thing.def.stackLimit
            var innerThingOwner = container.TryGetInnerInteractableThingOwner();
            if (innerThingOwner != null)
            {
                // Use the container's actual capacity
                capacity = innerThingOwner.GetCountCanAccept(thing);
                return capacity >= thing.stackCount;
            }

            // If no inner ThingOwner found, apply no limit (consistent with JobDriver_UnloadYourHauledInventory.FindTargetOrDrop)
            // This is the key fix - don't fall back to thing.def.stackLimit
            return true;
        }
    }
}