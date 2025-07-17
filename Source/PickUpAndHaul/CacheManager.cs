namespace PickUpAndHaul;

/// <summary>
/// Manages cleanup of all caches in the mod to prevent memory leaks
/// </summary>
public static class CacheManager
{
	private static readonly List<ICache> _registeredCaches = [];
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
				Log.Message($"[CacheManager] Registered cache: {cache.GetType().FullName}");
			}
		}
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
				Log.Warning($"[CacheManager] Error cleaning cache {cache.GetType().FullName}: {ex.Message}");
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
}