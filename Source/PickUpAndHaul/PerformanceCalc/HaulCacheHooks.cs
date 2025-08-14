
// HaulCacheHooks.cs -- hooks into ListerHaulables to keep custom caches in sync
using HarmonyLib;
using RimWorld;
using Verse;
using PickUpAndHaul.Cache;

namespace PickUpAndHaul.Performance
{
    [StaticConstructorOnStartup]
    public static class HaulCacheHooks
    {
        static HaulCacheHooks()
        {
            var h = new Harmony("puah.cache");
            h.Patch(
                AccessTools.Method(typeof(ListerHaulables), nameof(ListerHaulables.CheckAdd)),
                postfix: new HarmonyMethod(typeof(HaulCacheHooks), nameof(Post_Add)));

            h.Patch(
                AccessTools.Method(typeof(ListerHaulables), nameof(ListerHaulables.TryRemove)),
                postfix: new HarmonyMethod(typeof(HaulCacheHooks), nameof(Post_Remove)));
        }

        // ---------------------------------------------------------------
        // Called after vanilla adds a thing to Map.listerHaulables
        // ---------------------------------------------------------------
        public static void Post_Add(ListerHaulables __instance, Thing t)
        {
            Map map = __instance.map;

            // Check if the thing is too heavy for any pawn before adding to haulable cache
            if (IsTooHeavyForAnyPawn(map, t))
            {
                PUAHHaulCaches.AddToTooHeavyCache(map, t);
            }
            else
            {
                PUAHHaulCaches.AddToHaulableCache(map, t);
            }

            // keep live haulable counter
            HaulablesCounter.NotifyAdd(map);
        }

        // ---------------------------------------------------------------
        // Called after vanilla removes a thing from Map.listerHaulables
        // ---------------------------------------------------------------
        public static void Post_Remove(ListerHaulables __instance, Thing t)
        {
            Map map = __instance.map;

            // Remove from all caches
            PUAHHaulCaches.RemoveFromHaulableCache(map, t);
            PUAHHaulCaches.RemoveFromTooHeavyCache(map, t);

            HaulablesCounter.NotifyRemove(map);
        }

        /// <summary>
        /// Check if a thing is too heavy for any pawn on the map
        /// </summary>
        private static bool IsTooHeavyForAnyPawn(Map map, Thing thing)
        {
            if (map == null || thing == null) return false;

            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn == null || pawn.Dead || pawn.Downed) continue;

                // Check if pawn can carry the thing
                if (CanPawnCarryThing(pawn, thing))
                {
                    return false; // At least one pawn can carry it
                }
            }

            return true; // No pawn can carry it
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
    }
}
