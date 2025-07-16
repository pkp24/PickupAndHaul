using System;
using System.Collections.Generic;
using Verse;

namespace PickUpAndHaul
{
    /// <summary>
    /// Manages cleanup of all caches in the mod to prevent memory leaks
    /// </summary>
    public static class CacheManager
    {
        private static readonly List<ICache> _registeredCaches = new();
        private static int _lastMapChangeTick = 0;
        private static int _lastGameResetTick = 0;
        private static Map _lastMap = null;
        
        /// <summary>
        /// Registers a cache for automatic cleanup
        /// </summary>
        public static void RegisterCache(ICache cache)
        {
            if (cache != null && !_registeredCaches.Contains(cache))
            {
                _registeredCaches.Add(cache);
                if (Settings.EnableDebugLogging)
                {
                    Log.Message($"[CacheManager] Registered cache: {cache.GetType().Name}");
                }
            }
        }
        
        /// <summary>
        /// Unregisters a cache from automatic cleanup
        /// </summary>
        public static void UnregisterCache(ICache cache)
        {
            if (cache != null && _registeredCaches.Contains(cache))
            {
                _registeredCaches.Remove(cache);
                if (Settings.EnableDebugLogging)
                {
                    Log.Message($"[CacheManager] Unregistered cache: {cache.GetType().Name}");
                }
            }
        }
        
        /// <summary>
        /// Gets all registered caches
        /// </summary>
        public static IEnumerable<ICache> GetRegisteredCaches()
        {
            return _registeredCaches.AsReadOnly();
        }
        
        /// <summary>
        /// Performs cleanup of all registered caches
        /// </summary>
        public static void CleanupAllCaches()
        {
            var currentTick = Find.TickManager?.TicksGame ?? 0;
            var cleanedCount = 0;
            
            foreach (var cache in _registeredCaches)
            {
                try
                {
                    cache.ForceCleanup();
                    cleanedCount++;
                }
                catch (Exception ex)
                {
                    Log.Warning($"[CacheManager] Error cleaning cache {cache.GetType().Name}: {ex.Message}");
                }
            }
            
            // Clean up rollback states
            try
            {
                JobRollbackManager.CleanupRollbackStates();
                cleanedCount++;
            }
            catch (Exception ex)
            {
                Log.Warning($"[CacheManager] Error cleaning rollback states: {ex.Message}");
            }
            
            if (Settings.EnableDebugLogging && cleanedCount > 0)
            {
                Log.Message($"[CacheManager] Cleaned {cleanedCount} caches at tick {currentTick}");
            }
        }
        
        /// <summary>
        /// Checks for map changes and triggers cleanup if needed
        /// </summary>
        public static void CheckForMapChange()
        {
            var currentTick = Find.TickManager?.TicksGame ?? 0;
            var currentMap = Find.CurrentMap;
            
            // Check if map has changed
            if (currentMap != _lastMap)
            {
                if (_lastMap != null)
                {
                    if (Settings.EnableDebugLogging)
                    {
                        Log.Message($"[CacheManager] Map changed from {_lastMap} to {currentMap}, triggering cache cleanup");
                    }
                    CleanupAllCaches();
                }
                
                _lastMap = currentMap;
                _lastMapChangeTick = currentTick;
            }
        }
        
        /// <summary>
        /// Checks for game resets and triggers cleanup if needed
        /// </summary>
        public static void CheckForGameReset()
        {
            var currentTick = Find.TickManager?.TicksGame ?? 0;
            
            // If tick counter has reset (new game or save loaded)
            if (currentTick < _lastGameResetTick)
            {
                if (Settings.EnableDebugLogging)
                {
                    Log.Message($"[CacheManager] Game reset detected (tick {currentTick} < {_lastGameResetTick}), triggering cache cleanup");
                }
                CleanupAllCaches();
                _lastMap = null;
            }
            
            _lastGameResetTick = currentTick;
        }
        
        /// <summary>
        /// Gets debug information about all registered caches
        /// </summary>
        public static string GetDebugInfo()
        {
            var info = new System.Text.StringBuilder();
            info.AppendLine("[CacheManager] Registered caches:");
            
            foreach (var cache in _registeredCaches)
            {
                try
                {
                    info.AppendLine($"  {cache.GetType().Name}: {cache.GetDebugInfo()}");
                }
                catch (Exception ex)
                {
                    info.AppendLine($"  {cache.GetType().Name}: Error getting debug info - {ex.Message}");
                }
            }
            
            info.AppendLine($"Last map change: {_lastMapChangeTick}");
            info.AppendLine($"Last game reset: {_lastGameResetTick}");
            info.AppendLine($"Current map: {_lastMap}");
            
            return info.ToString();
        }
        
        /// <summary>
        /// Forces a complete reset of all caches (for testing or emergency cleanup)
        /// </summary>
        public static void ForceResetAllCaches()
        {
            if (Settings.EnableDebugLogging)
            {
                Log.Message("[CacheManager] Force resetting all caches");
            }
            
            CleanupAllCaches();
            _lastMap = null;
            _lastMapChangeTick = 0;
            _lastGameResetTick = 0;
        }
    }
    
    /// <summary>
    /// Interface for caches that can be managed by CacheManager
    /// </summary>
    public interface ICache
    {
        /// <summary>
        /// Forces a cleanup of the cache
        /// </summary>
        void ForceCleanup();
        
        /// <summary>
        /// Gets debug information about the cache
        /// </summary>
        string GetDebugInfo();
    }
} 