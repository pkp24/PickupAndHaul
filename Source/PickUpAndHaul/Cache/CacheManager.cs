using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

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
    }
} 