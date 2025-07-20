namespace PickUpAndHaul;

/// <summary>
/// Manages job queue operations atomically to prevent desync issues
/// </summary>
public static class JobQueueManager
{

	/// <summary>
	/// Atomically adds items to a job's queues
	/// </summary>
	public static bool AddItemsToJob(Pawn pawn, Job job, Thing thing, int count, IntVec3 targetCell, IHaulDestination haulDestination)
	{
		job.targetQueueA ??= [];
		job.countQueue ??= [];
		job.targetQueueB ??= [];

		// Store initial queue counts for rollback
		var initialTargetQueueACount = job.targetQueueA.Count;
		var initialCountQueueCount = job.countQueue.Count;
		var initialTargetQueueBCount = job.targetQueueB.Count;

		try
		{
			if (thing == null)
			{
				Log.Warning($"Skipping null thing");
				return false;
			}

			if (count <= 0)
			{
				Log.Warning($"Skipping non-positive count {count}");
				return false;
			}
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

			// Add all valid items atomically
			var target = new LocalTargetInfo(thing);
			job.targetQueueA.Add(target);
			job.targetA = target;
			job.countQueue.Add(count);
			job.targetQueueB.Add(storeTarget);

			if (!ValidateJobQueues(job, pawn))
			{
				Log.Error($"Job failed final validation for {pawn}");
				return false;
			}
			return true;
		}
		catch (Exception ex)
		{
			Log.Error($"Exception occurred: {ex.Message}");
			// Rollback to initial state
			RollbackJobQueues(job, initialTargetQueueACount, initialCountQueueCount, initialTargetQueueBCount);
			return false;
		}
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
	/// Validates job queue synchronization
	/// </summary>
	private static bool ValidateJobQueues(Job job, Pawn pawn)
	{
		if (job == null)
			return false;

		try
		{
			if (job.targetQueueA == null || job.countQueue == null || job.targetQueueB == null)
			{
				Log.Error($"Job queues are null for {pawn}");
				return false;
			}

			if (job.targetQueueA.Count != job.countQueue.Count || job.targetQueueA.Count != job.targetQueueB.Count)
			{
				Log.Error($"Queue synchronization failure for {pawn} - targetQueueA.Count ({job.targetQueueA.Count}) != countQueue.Count ({job.countQueue.Count}) != targetQueueB.Count ({job.targetQueueB.Count})");
				return false;
			}

			// Check for invalid targets in targetQueueA
			for (var i = 0; i < job.targetQueueA.Count; i++)
			{
				var target = job.targetQueueA[i];
				if (target.Thing == null)
				{
					Log.Error($"Found target with null Thing at index {i} for {pawn}");
					return false;
				}

				if (target.Thing.Destroyed || !target.Thing.Spawned)
					Log.Warning($"Found destroyed/unspawned target {target.Thing} at index {i} for {pawn}");
			}

			// Check for invalid targets in targetQueueB
			for (var i = 0; i < job.targetQueueB.Count; i++)
			{
				var target = job.targetQueueB[i];
				if (target.Thing == null)
				{
					// If target.Thing is null, it might be a cell target - check if the cell is valid
					if (!target.Cell.IsValid)
					{
						Log.Error($"Found target with null Thing and invalid Cell in targetQueueB at index {i} for {pawn}");
						return false;
					}
					// Cell targets are valid even with null Thing
				}
				else
				{
					// If target.Thing is not null, validate the thing
					if (target.Thing.Destroyed || !target.Thing.Spawned)
						Log.Warning($"Found destroyed/unspawned target {target.Thing} in targetQueueB at index {i} for {pawn}");
				}
			}

			// Check for non-positive counts
			for (var i = 0; i < job.countQueue.Count; i++)
			{
				if (job.countQueue[i] <= 0)
				{
					Log.Error($"Found non-positive count {job.countQueue[i]} at index {i} for {pawn}");
					return false;
				}
			}

			return true;
		}
		catch (Exception ex)
		{
			Log.Error($"Exception occurred: {ex.Message}");
			return false;
		}
	}

	/// <summary>
	/// Rolls back job queues to their initial state
	/// </summary>
	private static void RollbackJobQueues(Job job, int initialTargetQueueACount, int initialCountQueueCount, int initialTargetQueueBCount)
	{
		// Remove excess items from targetQueueA
		while (job.targetQueueA?.Count > initialTargetQueueACount)
			job.targetQueueA.RemoveAt(job.targetQueueA.Count - 1);

		// Remove excess items from countQueue
		while (job.countQueue?.Count > initialCountQueueCount)
			job.countQueue.RemoveAt(job.countQueue.Count - 1);

		// Remove excess items from targetQueueB
		while (job.targetQueueB?.Count > initialTargetQueueBCount)
			job.targetQueueB.RemoveAt(job.targetQueueB.Count - 1);

		Log.Message($"Rolled back job queues to initial state - targetQueueA: {job.targetQueueA?.Count ?? 0}, countQueue: {job.countQueue?.Count ?? 0}, targetQueueB: {job.targetQueueB?.Count ?? 0}");
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