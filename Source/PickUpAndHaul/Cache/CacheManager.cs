namespace PickUpAndHaul.Cache;

public static class CacheManager
{
	private static readonly ConcurrentBag<ICache> _registeredCaches = [];

	public static void RegisterCache(ICache cache)
	{
		if (cache != null && !_registeredCaches.Contains(cache))
		{
			_registeredCaches.Add(cache);
			Log.Message($"Registered cache: {cache.GetType().Name}");
		}
	}

	public static void CheckForGameChanges(List<Map> maps)
	{
		Task.Run(() =>
		{
			foreach (var cache in _registeredCaches)
			{
				cache.ForceCleanup();
				Log.Message($"Cleaned cache({cache.GetType().Name}): {cache.GetDebugInfo()}");
			}

			foreach (var pawn in maps.SelectMany(map => map.mapPawns.AllPawns))
			{
				foreach (var job in pawn.jobs.AllJobs().Where(t => t.def.defName is "HaulToInventory" or "UnloadYourHauledInventory"))
				{
					pawn.jobs.EndCurrentOrQueuedJob(job, JobCondition.InterruptForced);
					Log.Message($"Canceled mod-specific {job} job for {pawn}");
				}

				foreach (var comp in pawn.AllComps.Where(t => t.props.GetType().FullName.Contains("CompHauledToInventory", StringComparison.InvariantCultureIgnoreCase)))
					pawn.AllComps.Remove(comp);
			}
		});
		Log.Message($"Performed cleanup");
	}
}