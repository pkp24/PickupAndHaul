using System.Collections.Generic;
using Verse;

namespace PickUpAndHaul.Cache
{
    public static class PUAHHaulCaches
    {
        // Cache for haulable items per map
        private static readonly Dictionary<int, HashSet<Thing>> _haulableCache = new();
        
        // Cache for unreachable items per map
        private static readonly Dictionary<int, HashSet<Thing>> _unreachableCache = new();
        
        // Cache for too heavy items per map
        private static readonly Dictionary<int, HashSet<Thing>> _tooHeavyCache = new();

        /// <summary>
        /// Get the haulable cache for a specific map
        /// </summary>
        public static HashSet<Thing> GetHaulableCache(Map map)
        {
            if (map == null) return new HashSet<Thing>();
            
            if (!_haulableCache.TryGetValue(map.uniqueID, out var cache))
            {
                cache = new HashSet<Thing>();
                _haulableCache[map.uniqueID] = cache;
            }
            return cache;
        }

        /// <summary>
        /// Get the unreachable cache for a specific map
        /// </summary>
        public static HashSet<Thing> GetUnreachableCache(Map map)
        {
            if (map == null) return new HashSet<Thing>();
            
            if (!_unreachableCache.TryGetValue(map.uniqueID, out var cache))
            {
                cache = new HashSet<Thing>();
                _unreachableCache[map.uniqueID] = cache;
            }
            return cache;
        }

        /// <summary>
        /// Get the too heavy cache for a specific map
        /// </summary>
        public static HashSet<Thing> GetTooHeavyCache(Map map)
        {
            if (map == null) return new HashSet<Thing>();
            
            if (!_tooHeavyCache.TryGetValue(map.uniqueID, out var cache))
            {
                cache = new HashSet<Thing>();
                _tooHeavyCache[map.uniqueID] = cache;
            }
            return cache;
        }

        /// <summary>
        /// Add a thing to the haulable cache
        /// </summary>
        public static void AddToHaulableCache(Map map, Thing thing)
        {
            if (map == null || thing == null) return;
            GetHaulableCache(map).Add(thing);
        }

        /// <summary>
        /// Remove a thing from the haulable cache
        /// </summary>
        public static void RemoveFromHaulableCache(Map map, Thing thing)
        {
            if (map == null || thing == null) return;
            GetHaulableCache(map).Remove(thing);
        }

        /// <summary>
        /// Add a thing to the unreachable cache
        /// </summary>
        public static void AddToUnreachableCache(Map map, Thing thing)
        {
            if (map == null || thing == null) return;
            GetUnreachableCache(map).Add(thing);
        }

        /// <summary>
        /// Remove a thing from the unreachable cache
        /// </summary>
        public static void RemoveFromUnreachableCache(Map map, Thing thing)
        {
            if (map == null || thing == null) return;
            GetUnreachableCache(map).Remove(thing);
        }

        /// <summary>
        /// Add a thing to the too heavy cache
        /// </summary>
        public static void AddToTooHeavyCache(Map map, Thing thing)
        {
            if (map == null || thing == null) return;
            GetTooHeavyCache(map).Add(thing);
        }

        /// <summary>
        /// Remove a thing from the too heavy cache
        /// </summary>
        public static void RemoveFromTooHeavyCache(Map map, Thing thing)
        {
            if (map == null || thing == null) return;
            GetTooHeavyCache(map).Remove(thing);
        }

        /// <summary>
        /// Clear all caches for a specific map (called when map is destroyed)
        /// </summary>
        public static void ClearMapCaches(Map map)
        {
            if (map == null) return;
            
            _haulableCache.Remove(map.uniqueID);
            _unreachableCache.Remove(map.uniqueID);
            _tooHeavyCache.Remove(map.uniqueID);
        }

        /// <summary>
        /// Get count of haulable items for a map
        /// </summary>
        public static int GetHaulableCount(Map map)
        {
            return GetHaulableCache(map).Count;
        }

        /// <summary>
        /// Get count of unreachable items for a map
        /// </summary>
        public static int GetUnreachableCount(Map map)
        {
            return GetUnreachableCache(map).Count;
        }

        /// <summary>
        /// Get count of too heavy items for a map
        /// </summary>
        public static int GetTooHeavyCount(Map map)
        {
            return GetTooHeavyCache(map).Count;
        }

        /// <summary>
        /// Check if a thing is in the haulable cache
        /// </summary>
        public static bool IsInHaulableCache(Map map, Thing thing)
        {
            return GetHaulableCache(map).Contains(thing);
        }

        /// <summary>
        /// Check if a thing is in the unreachable cache
        /// </summary>
        public static bool IsInUnreachableCache(Map map, Thing thing)
        {
            return GetUnreachableCache(map).Contains(thing);
        }

        /// <summary>
        /// Check if a thing is in the too heavy cache
        /// </summary>
        public static bool IsInTooHeavyCache(Map map, Thing thing)
        {
            return GetTooHeavyCache(map).Contains(thing);
        }
    }
} 