namespace PickUpAndHaul.Cache;

/// <summary>
/// Manages cleanup of all caches in the mod to prevent memory leaks
/// </summary>
public static class CacheManager
{
	private static readonly List<ICache> _registeredCaches = [];
	private static int _lastMapChangeTick;
	private static int _lastGameResetTick;
	private static Map _lastMap;

	/// <summary>
	/// Registers a cache for automatic cleanup
	/// </summary>
	public static void RegisterCache(ICache cache)
	{
		if (cache != null && !_registeredCaches.Contains(cache))
		{
			_registeredCaches.Add(cache);
			if (Settings.EnableDebugLogging)
				Log.Message($"Registered cache: {cache.GetType().Name}");
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
			try
			{
				cache.ForceCleanup();
				cleanedCount++;
			}
			catch (Exception ex)
			{
				Log.Warning($"Cleaning cache {cache.GetType().Name}: {ex.Message}");
			}

		// Clean up rollback states
		try
		{
			JobRollbackManager.CleanupRollbackStates();
			cleanedCount++;
		}
		catch (Exception ex)
		{
			Log.Warning($"Cleaning rollback states: {ex.Message}");
		}

		if (Settings.EnableDebugLogging && cleanedCount > 0)
			Log.Message($"Cleaned {cleanedCount} caches at tick {currentTick}");
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
					Log.Message($"Map changed from {_lastMap} to {currentMap}, triggering cache cleanup");
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
				Log.Message($"Game reset detected (tick {currentTick} < {_lastGameResetTick}), triggering cache cleanup");
				GetDebugInfo();
			}
			CleanupAllCaches();
			_lastMap = null;
		}

		_lastGameResetTick = currentTick;
	}

	/// <summary>
	/// Gets debug information about all registered caches
	/// </summary>
	public static void GetDebugInfo()
	{
		Log.Message("Registered caches:");

		foreach (var cache in _registeredCaches)
			try
			{
				Log.Message($"{cache.GetType().Name}: {cache.GetDebugInfo()}");
			}
			catch (Exception ex)
			{
				Log.Error($"{cache.GetType().Name}: Exception {ex.Message}");
			}

		Log.Message($"Last map change: {_lastMapChangeTick}");
		Log.Message($"Last game reset: {_lastGameResetTick}");
		Log.Message($"Current map: {_lastMap}");
	}
}