
// HaulCacheHooks.cs -- hooks into ListerHaulables to keep custom caches in sync
using HarmonyLib;
using RimWorld;
using Verse;

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

            // update your other caches (not shown here)
            // ExtraHaulCaches.For(map).NotifyAdded(t);

            // keep live haulable counter
            HaulablesCounter.NotifyAdd(map);
        }

        // ---------------------------------------------------------------
        // Called after vanilla removes a thing from Map.listerHaulables
        // ---------------------------------------------------------------
        public static void Post_Remove(ListerHaulables __instance, Thing t)
        {
            Map map = __instance.map;

            // ExtraHaulCaches.For(map).NotifyRemoved(t);
            HaulablesCounter.NotifyRemove(map);
        }
    }
}
