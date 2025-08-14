using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using PickUpAndHaul;

namespace PickUpAndHaul.Cache
{
    public static class CacheInitializer
    {
        /// <summary>
        /// Initialize caches for all existing maps (called when mod loads)
        /// </summary>
        public static void InitializeAllCaches()
        {
            if (Current.Game?.Maps == null) return;

            foreach (var map in Current.Game.Maps)
            {
                InitializeMapCaches(map);
            }
        }

        /// <summary>
        /// Initialize caches for a specific map
        /// </summary>
        public static void InitializeMapCaches(Map map)
        {
            if (map?.listerHaulables?.ThingsPotentiallyNeedingHauling() == null) return;

            // Get all haulable things from the map
            var haulableThings = map.listerHaulables.ThingsPotentiallyNeedingHauling();

            foreach (var thing in haulableThings)
            {
                if (!HaulUtils.ThingIsValid(thing)) continue;

                // Check if the thing is too heavy for any pawn
                if (HaulUtils.IsTooHeavyForAnyPawn(map, thing))
                {
                    PUAHHaulCaches.AddToTooHeavyCache(map, thing);
                }
                else
                {
                    PUAHHaulCaches.AddToHaulableCache(map, thing);
                }
            }

            // Initialize storage location cache for existing haulable items
            InitializeStorageLocationCache(map);

            // Ensure cache updater is added to the map
            CacheUpdaterHelper.EnsureCacheUpdater(map);
        }

        /// <summary>
        /// Initialize storage location cache for existing haulable items
        /// </summary>
        private static void InitializeStorageLocationCache(Map map)
        {
            if (map == null) return;

            var haulableThings = PUAHHaulCaches.GetHaulableCache(map);
            var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => p != null && !p.Dead && !p.Downed).ToList();

            if (!pawns.Any()) return;

            // Use the first available pawn for storage location calculation
            var pawn = pawns.First();

            foreach (var thing in haulableThings)
            {
                if (!HaulUtils.ThingIsValid(thing)) continue;

                var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
                
                // Try to find and cache storage location
                if (StoreUtility.TryFindBestBetterStorageFor(thing, pawn, map, currentPriority, pawn.Faction, out var targetCell, out var haulDestination, true))
                {
                    ThingOwner innerInteractableThingOwner = null;
                    if (haulDestination is Thing destinationThing)
                    {
                        innerInteractableThingOwner = destinationThing.TryGetInnerInteractableThingOwner();
                    }
                    
                    PUAHHaulCaches.AddToStorageLocationCache(map, thing, targetCell, haulDestination, innerInteractableThingOwner);
                }
            }

            if (Settings.EnableDebugLogging)
            {
                Log.Message($"Initialized storage location cache for {haulableThings.Count} items on map {map.uniqueID}");
            }
        }

        /// <summary>
        /// Check if a thing is too heavy for any pawn on the map
        /// </summary>
        // moved to HaulUtils
    }
} 