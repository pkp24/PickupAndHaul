namespace PickUpAndHaul;

/// <summary>
/// Manages job queue operations atomically to prevent desync issues
/// </summary>
public static class JobQueueManager
{

	/// <summary>
	/// Atomically adds items to a job's queues
	/// </summary>
	public static bool AddItemsToJob(Job job, Thing thing, int count, IntVec3 targetCell, IHaulDestination haulDestination)
	{
		job.targetQueueA ??= [];
		job.countQueue ??= [];
		job.targetQueueB ??= [];

		var target = new LocalTargetInfo(thing);
		var storeTarget = new LocalTargetInfo(targetCell);
		if (haulDestination is Thing destinationAsThing)
			storeTarget = new(destinationAsThing);

		// Validate LocalTargetInfo - check if the target is valid. If target.Thing is null, it might be a cell target - check if the cell is valid
		if (storeTarget.Thing == null && !storeTarget.Cell.IsValid)
		{
			Log.Warning($"Skipping target with null Thing and invalid Cell");
			return false;
		}
		// If target.Thing is not null, validate the thing
		if (storeTarget.Thing.Destroyed || !storeTarget.Thing.Spawned)
		{
			Log.Warning($"Skipping destroyed/unspawned target {storeTarget.Thing}");
			return false;
		}

		job.targetQueueA.Add(target);
		job.targetQueueB.Add(storeTarget);
		job.countQueue.Add(count);
		return true;
	}

	/// <summary>
	/// Atomically clears all items from a job's queues
	/// </summary>
	public static void ClearJobQueues(List<Map> maps)
	{
		foreach (var map in maps)
			foreach (var pawn in map.mapPawns.FreeColonistsAndPrisonersSpawned)
				foreach (var job in pawn.jobs.AllJobs())
				{
					job?.targetQueueA?.Clear();
					job?.countQueue?.Clear();
					job?.targetQueueB?.Clear();
					if (Settings.EnableDebugLogging)
						GetJobQueueDebugInfo(job, pawn);
				}

		if (Settings.EnableDebugLogging)
			Log.Message($"Cleared all job queues");
	}

	/// <summary>
	/// Gets debug information about job queues
	/// </summary>
	private static void GetJobQueueDebugInfo(Job job, Pawn pawn)
	{
		if (job == null)
			return;

		Log.Message($"Job queue debug info for {pawn}:");
		Log.Message($"targetQueueA: {job.targetQueueA?.Count ?? 0} items");
		Log.Message($"countQueue: {job.countQueue?.Count ?? 0} items");
		Log.Message($"targetQueueB: {job.targetQueueB?.Count ?? 0} items");

		if (job.targetQueueA != null && job.countQueue != null && job.targetQueueB != null)
		{
			Log.Message($"Synchronized: {job.targetQueueA.Count == job.countQueue.Count && job.targetQueueA.Count == job.targetQueueB.Count}");

			if (job.targetQueueA.Count > 0)
			{
				Log.Message("targetQueueA contents:");
				for (var i = 0; i < Math.Min(job.targetQueueA.Count, 5); i++)
					Log.Message($"[{i}]: {job.targetQueueA[i]}");

				Log.Message("countQueue contents:");
				for (var i = 0; i < Math.Min(job.countQueue.Count, 5); i++)
					Log.Message($"[{i}]: {job.countQueue[i]}");

				Log.Message("targetQueueB contents:");
				for (var i = 0; i < Math.Min(job.targetQueueB.Count, 5); i++)
					Log.Message($"[{i}]: {job.targetQueueB[i]}");
			}
		}
	}
}