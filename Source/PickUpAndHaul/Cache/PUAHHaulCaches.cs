using System.Collections.Generic;
using Verse;
using RimWorld;

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

        // Cache for storage locations per map
        private static readonly Dictionary<int, Dictionary<Thing, StorageLocationCache>> _storageLocationCache = new();

        /// <summary>
        /// Storage location cache entry
        /// </summary>
        public class StorageLocationCache
        {
            public IntVec3 TargetCell { get; set; }
            public IHaulDestination HaulDestination { get; set; }
            public ThingOwner InnerInteractableThingOwner { get; set; }
            public int TickCreated { get; set; }
            public bool IsValid { get; set; }

            public StorageLocationCache(IntVec3 targetCell, IHaulDestination haulDestination, ThingOwner innerInteractableThingOwner)
            {
                TargetCell = targetCell;
                HaulDestination = haulDestination;
                InnerInteractableThingOwner = innerInteractableThingOwner;
                TickCreated = Find.TickManager.TicksGame;
                IsValid = true;
            }
        }

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
        /// Get the storage location cache for a specific map
        /// </summary>
        public static Dictionary<Thing, StorageLocationCache> GetStorageLocationCache(Map map)
        {
            if (map == null) return new Dictionary<Thing, StorageLocationCache>();
            
            if (!_storageLocationCache.TryGetValue(map.uniqueID, out var cache))
            {
                cache = new Dictionary<Thing, StorageLocationCache>();
                _storageLocationCache[map.uniqueID] = cache;
            }
            return cache;
        }

        /// <summary>
        /// Add a thing to the haulable cache
        /// </summary>
        public static void AddToHaulableCache(Map map, Thing thing)
        {
            if (map == null || thing == null) return;
            var cache = GetHaulableCache(map);
            if (!cache.Contains(thing))
            {
                cache.Add(thing);
                if (Settings.EnableDebugLogging)
                {
                    Log.Message($"Added {thing} to haulable cache for map {map.uniqueID}");
                }
            }
        }

        /// <summary>
        /// Remove a thing from the haulable cache
        /// </summary>
        public static void RemoveFromHaulableCache(Map map, Thing thing)
        {
            if (map == null || thing == null) return;
            var cache = GetHaulableCache(map);
            if (cache.Remove(thing))
            {
                if (Settings.EnableDebugLogging)
                {
                    Log.Message($"Removed {thing} from haulable cache for map {map.uniqueID}");
                }
            }
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
        /// Add a storage location to the cache
        /// </summary>
        public static void AddToStorageLocationCache(Map map, Thing thing, IntVec3 targetCell, IHaulDestination haulDestination, ThingOwner innerInteractableThingOwner)
        {
            if (map == null || thing == null) return;
            var cache = GetStorageLocationCache(map);
            var storageCache = new StorageLocationCache(targetCell, haulDestination, innerInteractableThingOwner);
            cache[thing] = storageCache;
            
            if (Settings.EnableDebugLogging)
            {
                Log.Message($"Added storage location for {thing} to cache for map {map.uniqueID}: {targetCell}");
            }
        }

        /// <summary>
        /// Remove a storage location from the cache
        /// </summary>
        public static void RemoveFromStorageLocationCache(Map map, Thing thing)
        {
            if (map == null || thing == null) return;
            var cache = GetStorageLocationCache(map);
            if (cache.Remove(thing))
            {
                if (Settings.EnableDebugLogging)
                {
                    Log.Message($"Removed storage location for {thing} from cache for map {map.uniqueID}");
                }
            }
        }

        /// <summary>
        /// Get a cached storage location for a thing
        /// </summary>
        public static StorageLocationCache GetCachedStorageLocation(Map map, Thing thing)
        {
            if (map == null || thing == null) return null;
            
            var cache = GetStorageLocationCache(map);
            if (cache.TryGetValue(thing, out var storageCache))
            {
                // Check if cache is still valid (not too old)
                if (storageCache.IsValid && Find.TickManager.TicksGame - storageCache.TickCreated < 2500) // 10 seconds at 250 TPS
                {
                    return storageCache;
                }
                else
                {
                    // Cache is stale, remove it
                    cache.Remove(thing);
                }
            }
            return null;
        }

        /// <summary>
        /// Invalidate a specific storage location cache entry
        /// </summary>
        public static void InvalidateStorageLocationCache(Map map, Thing thing)
        {
            if (map == null || thing == null) return;
            
            var cache = GetStorageLocationCache(map);
            cache.Remove(thing);
            
            if (Settings.EnableDebugLogging)
            {
                Log.Message($"Invalidated storage location cache for {thing}");
            }
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
            _storageLocationCache.Remove(map.uniqueID);
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
        /// Get count of cached storage locations for a map
        /// </summary>
        public static int GetStorageLocationCount(Map map)
        {
            return GetStorageLocationCache(map).Count;
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

        /// <summary>
        /// Check if a thing has a cached storage location
        /// </summary>
        public static bool HasCachedStorageLocation(Map map, Thing thing)
        {
            return GetCachedStorageLocation(map, thing) != null;
        }

        /// <summary>
        /// Get cache statistics for debugging
        /// </summary>
        public static string GetCacheStatistics(Map map)
        {
            if (map == null) return "No map provided";
            
            var haulableCount = GetHaulableCount(map);
            var unreachableCount = GetUnreachableCount(map);
            var tooHeavyCount = GetTooHeavyCount(map);
            var storageLocationCount = GetStorageLocationCount(map);
            var accessibleCount = CacheManager.GetAccessibleHaulableCount(map);
            
            return $"Map {map.uniqueID}: Haulable={haulableCount}, Unreachable={unreachableCount}, TooHeavy={tooHeavyCount}, StorageLocations={storageLocationCount}, Accessible={accessibleCount}";
        }
    }
} 