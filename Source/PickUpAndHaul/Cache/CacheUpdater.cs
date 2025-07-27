using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PickUpAndHaul.Cache
{
    public class CacheUpdater : MapComponent
    {
        private int _tickCounter = 0;
        private const int UPDATE_INTERVAL = 1000; // Update every 1000 ticks (about 16 seconds)

        public CacheUpdater(Map map) : base(map)
        {
        }

        public override void MapComponentTick()
        {
            _tickCounter++;
            
            if (_tickCounter >= UPDATE_INTERVAL)
            {
                _tickCounter = 0;
                UpdateCaches();
            }
        }

        private void UpdateCaches()
        {
            if (map == null) return;

            // Reclassify items between haulable and too heavy caches
            // This handles cases where pawns get stronger/weaker or pawns are added/removed
            CacheManager.ReclassifyTooHeavyItems(map);
            CacheManager.ReclassifyHaulableItems(map);
            
            // Update unreachable cache
            CacheManager.PopulateUnreachableCache(map);
        }

        public override void MapComponentOnGUI()
        {
            // Optional: Add debug info to the UI if needed
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref _tickCounter, "tickCounter", 0);
        }
    }

    /// <summary>
    /// Static helper to ensure cache updater is added to maps
    /// </summary>
    public static class CacheUpdaterHelper
    {
        /// <summary>
        /// Ensure a map has a cache updater component
        /// </summary>
        public static void EnsureCacheUpdater(Map map)
        {
            if (map == null) return;

            if (map.GetComponent<CacheUpdater>() == null)
            {
                map.components.Add(new CacheUpdater(map));
            }
        }

        /// <summary>
        /// Remove cache updater from a map (when map is destroyed)
        /// </summary>
        public static void RemoveCacheUpdater(Map map)
        {
            if (map == null) return;

            var updater = map.GetComponent<CacheUpdater>();
            if (updater != null)
            {
                map.components.Remove(updater);
            }
        }
    }
} 