using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using PickUpAndHaul; // for WorkGiver_HaulToInventory

namespace PickUpAndHaul.Cache
{
    public static class CacheManager
    {
        /// <summary>
        /// Populate the unreachable cache for a map
        /// </summary>
        public static void PopulateUnreachableCache(Map map)
        {
            if (map == null) return;

            var haulableCache = PUAHHaulCaches.GetHaulableCache(map);
            var unreachableCache = PUAHHaulCaches.GetUnreachableCache(map);
            
            // Clear existing unreachable cache
            unreachableCache.Clear();

            foreach (var thing in haulableCache.ToList())
            {
                if (thing == null || thing.Destroyed) continue;

                // Check if any pawn can reach this thing
                bool canReach = false;
                foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
                {
                    if (pawn == null || pawn.Dead || pawn.Downed) continue;

                    // Check if pawn can reach the thing
                    if (pawn.CanReach(thing, PathEndMode.Touch, Danger.Deadly))
                    {
                        canReach = true;
                        break;
                    }
                }

                if (!canReach)
                {
                    unreachableCache.Add(thing);
                }
            }
        }

        /// <summary>
        /// Reclassify all items in the too heavy cache to see if they can now be carried
        /// </summary>
        public static void ReclassifyTooHeavyItems(Map map)
        {
            if (map == null) return;

            var tooHeavyCache = PUAHHaulCaches.GetTooHeavyCache(map);
            var itemsToReclassify = tooHeavyCache.ToList();

            foreach (var thing in itemsToReclassify)
            {
                if (thing == null || thing.Destroyed) continue;

                // Check if any pawn can now carry this thing
                bool canCarry = false;
                foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
                {
                    if (pawn == null || pawn.Dead || pawn.Downed) continue;

                    if (CanPawnCarryThing(pawn, thing))
                    {
                        canCarry = true;
                        break;
                    }
                }

                if (canCarry)
                {
                    // Move from too heavy to haulable cache
                    PUAHHaulCaches.RemoveFromTooHeavyCache(map, thing);
                    PUAHHaulCaches.AddToHaulableCache(map, thing);
                }
            }
        }

        /// <summary>
        /// Reclassify all items in the haulable cache to see if they're now too heavy
        /// </summary>
        public static void ReclassifyHaulableItems(Map map)
        {
            if (map == null) return;

            var haulableCache = PUAHHaulCaches.GetHaulableCache(map);
            var itemsToReclassify = haulableCache.ToList();

            foreach (var thing in itemsToReclassify)
            {
                if (thing == null || thing.Destroyed) continue;

                // Check if any pawn can still carry this thing
                bool canCarry = false;
                foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
                {
                    if (pawn == null || pawn.Dead || pawn.Downed) continue;

                    if (CanPawnCarryThing(pawn, thing))
                    {
                        canCarry = true;
                        break;
                    }
                }

                if (!canCarry)
                {
                    // Move from haulable to too heavy cache
                    PUAHHaulCaches.RemoveFromHaulableCache(map, thing);
                    PUAHHaulCaches.AddToTooHeavyCache(map, thing);
                }
            }
        }

        /// <summary>
        /// Clean up stale storage location cache entries
        /// </summary>
        public static void CleanupStorageLocationCache(Map map)
        {
            if (map == null) return;

            var storageLocationCache = PUAHHaulCaches.GetStorageLocationCache(map);
            var staleEntries = new List<Thing>();

            foreach (var entry in storageLocationCache)
            {
                var thing = entry.Key;
                var cache = entry.Value;

                // Check if the thing still exists and is valid
                if (thing == null || thing.Destroyed || !thing.Spawned)
                {
                    staleEntries.Add(thing);
                    continue;
                }

                // Check if cache is too old (more than 10 seconds)
                if (Find.TickManager.TicksGame - cache.TickCreated > 2500)
                {
                    staleEntries.Add(thing);
                    continue;
                }

                // Check if the haul destination is still valid
                if (cache.HaulDestination is Thing destinationThing && (destinationThing.Destroyed || !destinationThing.Spawned))
                {
                    staleEntries.Add(thing);
                    continue;
                }
            }

            // Remove stale entries
            foreach (var thing in staleEntries)
            {
                PUAHHaulCaches.RemoveFromStorageLocationCache(map, thing);
            }

            if (staleEntries.Count > 0 && Settings.EnableDebugLogging)
            {
                Log.Message($"Cleaned up {staleEntries.Count} stale storage location cache entries for map {map.uniqueID}");
            }
        }

        /// <summary>
        /// Invalidate storage location cache for a specific thing
        /// </summary>
        public static void InvalidateStorageLocationCache(Map map, Thing thing)
        {
            if (map == null || thing == null) return;
            PUAHHaulCaches.RemoveFromStorageLocationCache(map, thing);
        }

        /// <summary>
        /// Invalidate storage location cache for all things (when storage priorities change)
        /// </summary>
        public static void InvalidateAllStorageLocationCache(Map map)
        {
            if (map == null) return;
            var storageLocationCache = PUAHHaulCaches.GetStorageLocationCache(map);
            storageLocationCache.Clear();
            
            if (Settings.EnableDebugLogging)
            {
                Log.Message($"Invalidated all storage location cache entries for map {map.uniqueID}");
            }
        }

        /// <summary>
        /// Check if a pawn can carry a specific thing
        /// </summary>
        private static bool CanPawnCarryThing(Pawn pawn, Thing thing)
        {
            if (pawn == null || thing == null) return false;

            // Check if the thing is too heavy for the pawn
            float thingMass = thing.GetStatValue(StatDefOf.Mass);
            float maxCarryMass = pawn.GetStatValue(StatDefOf.CarryingCapacity);

            return thingMass <= maxCarryMass;
        }

        /// <summary>
        /// Update all caches for a map
        /// </summary>
        public static void UpdateAllCaches(Map map)
        {
            PopulateUnreachableCache(map);
            ReclassifyTooHeavyItems(map);
            ReclassifyHaulableItems(map);
            CleanupStorageLocationCache(map);
        }

        /// <summary>
        /// Get all haulable things that are actually accessible and carryable
        /// </summary>
        public static IEnumerable<Thing> GetAccessibleHaulables(Map map)
        {
            if (map == null) return Enumerable.Empty<Thing>();

            var haulableCache = PUAHHaulCaches.GetHaulableCache(map);
            var unreachableCache = PUAHHaulCaches.GetUnreachableCache(map);

            return haulableCache.Where(thing => 
                thing != null && 
                !thing.Destroyed && 
                !unreachableCache.Contains(thing));
        }

        /// <summary>
        /// Get count of accessible haulable items
        /// </summary>
        public static int GetAccessibleHaulableCount(Map map)
        {
            return GetAccessibleHaulables(map).Count();
        }

        /// <summary>
        /// Get cached storage location for a thing, or find and cache a new one
        /// </summary>
        public static bool TryGetCachedStorageLocation(Thing thing, Pawn pawn, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, out IHaulDestination haulDestination, out ThingOwner innerInteractableThingOwner, WorkGiver_HaulToInventory workGiver = null)
        {
            foundCell = default;
            haulDestination = null;
            innerInteractableThingOwner = null;

            if (thing == null || pawn == null || map == null) return false;

            // Try to get from cache first
            var cachedLocation = PUAHHaulCaches.GetCachedStorageLocation(map, thing);
            if (cachedLocation != null)
            {
                // Validate that the cached location still has capacity AND is not reserved by vanilla reservations
                PUAHReservationSystem.StorageLocation storageLocation;
                LocalTargetInfo reservationTarget;
                if (cachedLocation.InnerInteractableThingOwner != null)
                {
                    storageLocation = new PUAHReservationSystem.StorageLocation((Thing)cachedLocation.HaulDestination);
                    reservationTarget = (Thing)cachedLocation.HaulDestination;
                }
                else
                {
                    storageLocation = new PUAHReservationSystem.StorageLocation(cachedLocation.TargetCell);
                    reservationTarget = cachedLocation.TargetCell;
                }

                var availableCapacity = PUAHReservationSystem.GetAvailableCapacity(storageLocation, thing, map);

                // Check vanilla reservation state and our custom reservation ownership
                var vanillaReservable   = pawn.CanReserve(reservationTarget, 1, -1, null, false);
                var claimantHasPuahRes = PUAHReservationSystem.PawnHasReservation(pawn, storageLocation, map);
                var anyPuahRes         = PUAHReservationSystem.HasAnyReservation(storageLocation, map);

                var puahOkay = !anyPuahRes || claimantHasPuahRes; // allow if none or owned by claimant

                if (availableCapacity > 0 && (vanillaReservable || claimantHasPuahRes) && puahOkay)
                {
                    foundCell = cachedLocation.TargetCell;
                    haulDestination = cachedLocation.HaulDestination;
                    innerInteractableThingOwner = cachedLocation.InnerInteractableThingOwner;
                    return true;
                }
                else
                {
                    // Cached location is no longer valid (full or reserved by other pawn)
                    PUAHHaulCaches.InvalidateStorageLocationCache(map, thing);
                }
            }

            // If not in cache or cache was invalid, find storage location and cache it via PUAH helper (supports ThingOwner out param)
            if (workGiver != null && workGiver.TryFindBestBetterStorageFor(thing, pawn, map, currentPriority, faction, out foundCell, out haulDestination, out innerInteractableThingOwner))
            {
                // Ensure the found location is usable considering both vanilla and PUAH reservations
                PUAHReservationSystem.StorageLocation newLocation = haulDestination is Thing destThing2
                    ? new PUAHReservationSystem.StorageLocation(destThing2)
                    : new PUAHReservationSystem.StorageLocation(foundCell);

                LocalTargetInfo reservationTarget = haulDestination is Thing destThing ? (LocalTargetInfo)destThing : (LocalTargetInfo)foundCell;

                var vanillaReservable   = pawn.CanReserve(reservationTarget, 1, -1, null, false);
                var claimantHasPuahRes = PUAHReservationSystem.PawnHasReservation(pawn, newLocation, map);
                var anyPuahRes         = PUAHReservationSystem.HasAnyReservation(newLocation, map);

                if (!vanillaReservable && !claimantHasPuahRes) // blocked by another vanilla reservation
                {
                    return false;
                }

                if (anyPuahRes && !claimantHasPuahRes) // blocked by another pawn's PUAH reservation
                {
                    return false;
                }
                // Cache the result
                PUAHHaulCaches.AddToStorageLocationCache(map, thing, foundCell, haulDestination, innerInteractableThingOwner);
                return true;
            }
            else if (workGiver == null)
            {
                // Fallback to vanilla StoreUtility when workGiver is not available
                if (StoreUtility.TryFindBestBetterStorageFor(thing, pawn, map, currentPriority, faction, out foundCell, out haulDestination, true))
                {
                    // Ensure location is usable with reservation checks
                    PUAHReservationSystem.StorageLocation newLocation = haulDestination is Thing destThing2
                        ? new PUAHReservationSystem.StorageLocation(destThing2)
                        : new PUAHReservationSystem.StorageLocation(foundCell);

                    LocalTargetInfo reservationTarget = haulDestination is Thing destThing ? (LocalTargetInfo)destThing : (LocalTargetInfo)foundCell;

                    var vanillaReservable   = pawn.CanReserve(reservationTarget, 1, -1, null, false);
                    var claimantHasPuahRes = PUAHReservationSystem.PawnHasReservation(pawn, newLocation, map);
                    var anyPuahRes         = PUAHReservationSystem.HasAnyReservation(newLocation, map);

                    if (!vanillaReservable && !claimantHasPuahRes)
                    {
                        return false;
                    }

                    if (anyPuahRes && !claimantHasPuahRes)
                    {
                        return false;
                    }
                    // Cache the result
                    PUAHHaulCaches.AddToStorageLocationCache(map, thing, foundCell, haulDestination, null);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Invalidate storage locations that are full
        /// </summary>
        public static void InvalidateFullStorageLocations(Map map)
        {
            if (map == null) return;

            var storageCache = PUAHHaulCaches.GetStorageLocationCache(map);
            var toInvalidate = new List<Thing>();

            foreach (var kvp in storageCache)
            {
                var thing = kvp.Key;
                var cachedLocation = kvp.Value;

                if (thing == null || thing.Destroyed || cachedLocation == null) 
                {
                    toInvalidate.Add(thing);
                    continue;
                }

                // Check if the location is full
                PUAHReservationSystem.StorageLocation storageLocation;
                if (cachedLocation.InnerInteractableThingOwner != null)
                {
                    storageLocation = new PUAHReservationSystem.StorageLocation((Thing)cachedLocation.HaulDestination);
                }
                else
                {
                    storageLocation = new PUAHReservationSystem.StorageLocation(cachedLocation.TargetCell);
                }

                var availableCapacity = PUAHReservationSystem.GetAvailableCapacity(storageLocation, thing, map);
                if (availableCapacity <= 0)
                {
                    toInvalidate.Add(thing);
                }
            }

            // Invalidate full locations
            foreach (var thing in toInvalidate)
            {
                PUAHHaulCaches.InvalidateStorageLocationCache(map, thing);
            }
        }
    }
} 