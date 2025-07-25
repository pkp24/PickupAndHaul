namespace PickUpAndHaul.Cache;

public static class CacheManager
{
	private static readonly ConcurrentBag<ICache> _registeredCaches = [];

	public static void RegisterCache(ICache cache)
	{
		try
		{
			if (cache != null && !_registeredCaches.Contains(cache))
			{
				_registeredCaches.Add(cache);
				Log.Message($"Registered cache: {cache.GetType().Name}");
			}
			else
			{
				Log.Message($"Cache registration skipped - Cache: {cache != null}, Already registered: {_registeredCaches.Contains(cache)}");
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error in RegisterCache");
		}
	}

	public static void CheckForGameChanges(List<Map> maps)
	{
		try
		{
			Log.Message($"CheckForGameChanges called with {maps.Count} maps");

			Task.Run(() =>
			{
				try
				{
					Log.Message("Starting cache cleanup task");

					foreach (var cache in _registeredCaches)
					{
						try
						{
							Log.Message($"Cleaning cache: {cache.GetType().Name}");
							cache.ForceCleanup();
							Log.Message($"Cleaned cache({cache.GetType().Name}): {cache.GetDebugInfo()}");
						}
						catch (Exception ex)
						{
							Log.Error(ex, $"Error cleaning cache {cache.GetType().Name}");
						}
					}

					var totalPawns = 0;
					var canceledJobs = 0;
					var removedComps = 0;

					foreach (var map in maps)
					{
						try
						{
							var mapPawns = map.mapPawns.AllPawns.ToList();
							totalPawns += mapPawns.Count;

							Log.Message($"Processing {mapPawns.Count} pawns in map {map.uniqueID}");

							foreach (var pawn in mapPawns)
							{
								try
								{
									var modJobs = pawn.jobs.AllJobs().Where(t => t.def.defName is "HaulToInventory" or "UnloadYourHauledInventory").ToList();

									foreach (var job in modJobs)
									{
										Log.Message($"Canceling mod-specific job {job.def.defName} for pawn {pawn.Name.ToStringShort}");
										pawn.jobs.EndCurrentOrQueuedJob(job, JobCondition.InterruptForced);
										canceledJobs++;
									}

									var modComps = pawn.AllComps.Where(t => t.props.GetType().FullName.Contains("CompHauledToInventory", StringComparison.InvariantCultureIgnoreCase)).ToList();

									foreach (var comp in modComps)
									{
										Log.Message($"Removing mod-specific component {comp.GetType().Name} from pawn {pawn.Name.ToStringShort}");
										pawn.AllComps.Remove(comp);
										removedComps++;
									}
								}
								catch (Exception ex)
								{
									Log.Error(ex, $"Error processing pawn {pawn?.Name.ToStringShort}");
								}
							}
						}
						catch (Exception ex)
						{
							Log.Error(ex, $"Error processing map {map?.uniqueID}");
						}
					}

					Log.Message($"Cache cleanup completed - Processed {totalPawns} pawns, Canceled {canceledJobs} jobs, Removed {removedComps} components");
				}
				catch (Exception ex)
				{
					Log.Error(ex, "Error in cache cleanup task");
				}
			});

			Log.Message("Performed cleanup");
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error in CheckForGameChanges");
		}
	}
}