using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace PickUpAndHaul
{
    /// <summary>
    /// Generic cache for pawn-based data using weak references to prevent memory leaks
    /// </summary>
    /// <typeparam name="T">The type of data to cache per pawn</typeparam>
    public class PawnCache<T> : ICache
    {
        private readonly Dictionary<System.WeakReference<Pawn>, T> _cache = new();
        private readonly object _lockObject = new object();
        private int _lastCleanupTick = 0;
        private const int CLEANUP_INTERVAL_TICKS = 1000; // Clean up every ~16 seconds at 60 TPS
        
        /// <summary>
        /// Sets a value for a pawn in the cache
        /// </summary>
        public void Set(Pawn pawn, T value)
        {
            if (pawn == null) return;
            
            lock (_lockObject)
            {
                TryCleanupDeadReferences();
                
                // Find existing entry for this pawn and update it
                foreach (var kvp in _cache)
                {
                    if (kvp.Key.TryGetTarget(out var cachedPawn) && cachedPawn == pawn)
                    {
                        _cache[kvp.Key] = value;
                        return;
                    }
                }
                
                // If no existing entry found, create a new one
                _cache[new System.WeakReference<Pawn>(pawn)] = value;
            }
        }
        
        /// <summary>
        /// Tries to get a value for a pawn from the cache
        /// </summary>
        public bool TryGet(Pawn pawn, out T value)
        {
            value = default(T);
            if (pawn == null) return false;
            
            lock (_lockObject)
            {
                TryCleanupDeadReferences();
                
                foreach (var kvp in _cache)
                {
                    if (kvp.Key.TryGetTarget(out var cachedPawn) && cachedPawn == pawn)
                    {
                        value = kvp.Value;
                        return true;
                    }
                }
                
                return false;
            }
        }
        
        /// <summary>
        /// Removes a specific pawn from the cache
        /// </summary>
        public void Remove(Pawn pawn)
        {
            if (pawn == null) return;
            
            lock (_lockObject)
            {
                var keysToRemove = new List<System.WeakReference<Pawn>>();
                
                foreach (var kvp in _cache)
                {
                    if (kvp.Key.TryGetTarget(out var cachedPawn) && cachedPawn == pawn)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }
                
                foreach (var key in keysToRemove)
                {
                    _cache.Remove(key);
                }
            }
        }
        
        /// <summary>
        /// Clears all entries from the cache
        /// </summary>
        public void Clear()
        {
            lock (_lockObject)
            {
                _cache.Clear();
            }
        }
        
        /// <summary>
        /// Gets the current number of entries in the cache
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lockObject)
                {
                    TryCleanupDeadReferences();
                    return _cache.Count;
                }
            }
        }
        
        /// <summary>
        /// Attempts to clean up dead references if enough time has passed
        /// </summary>
        private void TryCleanupDeadReferences()
        {
            var currentTick = Find.TickManager?.TicksGame ?? 0;
            if (currentTick - _lastCleanupTick < CLEANUP_INTERVAL_TICKS)
            {
                return;
            }
            
            CleanupDeadReferences();
            _lastCleanupTick = currentTick;
        }
        
        /// <summary>
        /// Cleans up all dead references from the cache
        /// </summary>
        private void CleanupDeadReferences()
        {
            var keysToRemove = new List<System.WeakReference<Pawn>>();
            
            foreach (var kvp in _cache)
            {
                // If the weak reference is dead (pawn was garbage collected)
                if (!kvp.Key.TryGetTarget(out _))
                {
                    keysToRemove.Add(kvp.Key);
                }
            }
            
            if (keysToRemove.Count > 0)
            {
                foreach (var key in keysToRemove)
                {
                    _cache.Remove(key);
                }
                
                if (Settings.EnableDebugLogging)
                {
                    Log.Message($"[PawnCache] Cleaned up {keysToRemove.Count} dead references from cache");
                }
            }
        }
        
        /// <summary>
        /// Forces a cleanup of dead references (for testing or manual cleanup)
        /// </summary>
        public void ForceCleanup()
        {
            lock (_lockObject)
            {
                CleanupDeadReferences();
            }
        }
        
        /// <summary>
        /// Gets debug information about the cache
        /// </summary>
        public string GetDebugInfo()
        {
            lock (_lockObject)
            {
                TryCleanupDeadReferences();
                return $"PawnCache<{typeof(T).Name}>: {_cache.Count} entries, last cleanup: {_lastCleanupTick}";
            }
        }
    }
} 