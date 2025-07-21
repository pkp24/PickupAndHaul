namespace PickUpAndHaul.Cache;

public static class CacheManager
{
	private static readonly ConcurrentBag<ICache> _registeredCaches = [];
	private static int _lastMapChangeTick;
	private static int _lastGameResetTick;
	private static Map _lastMap;

	public static void RegisterCache(ICache cache)
	{
		if (cache != null && !_registeredCaches.Contains(cache))
		{
			_registeredCaches.Add(cache);
			Log.Message($"Registered cache: {cache.GetType().Name}");
		}
	}

	public static void CheckForGameChanges()
	{
		try
		{
			var currentTick = Find.TickManager?.TicksGame ?? 0;
			if (currentTick % 60 != 0) // Values below 60 ticks heavily impact performance
				return;
			var currentMap = Find.CurrentMap;

			if (currentMap != _lastMap) // Check if map has changed
			{
				if (_lastMap != null)
				{
					Log.Message($"Map changed from {_lastMap} to {currentMap}, triggering cache cleanup");
					GetDebugInfo();
					Task.Run(() => CleanupAllCaches([_lastMap]));
				}

				_lastMap = currentMap;
				_lastMapChangeTick = currentTick;
				return;
			}
			else if (currentTick < _lastGameResetTick) // then game restared
			{
				Log.Message($"Game reset detected (tick {currentTick} < {_lastGameResetTick}), triggering cache cleanup");
				GetDebugInfo();
				Task.Run(() => CleanupAllCaches(Find.Maps));
				_lastMap = null;
				_lastGameResetTick = currentTick;
				return;
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex.ToString());
		}
	}

	private static void CleanupAllCaches(List<Map> maps)
	{
		foreach (var cache in _registeredCaches)
			cache.ForceCleanup();

		foreach (var (map, pawn, job) in from map in maps
										 from pawn in map.mapPawns.FreeColonistsAndPrisonersSpawned
										 from job in pawn.jobs.AllJobs().Where(t => t.def.IsModSpecificJob())
										 select (map, pawn, job))
		{
			job.GetCachedDriverDirect.EndJobWith(JobCondition.QueuedNoLongerValid);
			map.reservationManager.ReleaseClaimedBy(pawn, job);
			Log.Message($"Canceled mod-specific {job} job for {pawn}");
		}
	}

	private static void GetDebugInfo()
	{
		Log.Message("Registered caches:");

		foreach (var cache in _registeredCaches)
			Log.Message($"{cache.GetType().Name}: {cache.GetDebugInfo()}");

		Log.Message($"Last map change: {_lastMapChangeTick}");
		Log.Message($"Last game reset: {_lastGameResetTick}");
		Log.Message($"Current map: {_lastMap}");
	}
}