namespace PickUpAndHaul;

/// <summary>
/// Manages job queue operations atomically to prevent desync issues
/// </summary>
public static class JobQueueManager
{
	private static readonly object _lockObject = new();

	/// <summary>
	/// Atomically adds items to a job's queues
	/// </summary>
	public static bool AddItemsToJob(Job job, List<Thing> things, List<int> counts, List<LocalTargetInfo> targets, Pawn pawn)
	{
		if (job == null || things == null || counts == null || targets == null)
		{
			Log.Error("[JobQueueManager] AddItemsToJob: Null parameters provided");
			return false;
		}

		if (things.Count != counts.Count || things.Count != targets.Count)
		{
			Log.Error($"[JobQueueManager] AddItemsToJob: Count mismatch - things: {things.Count}, counts: {counts.Count}, targets: {targets.Count}");
			return false;
		}

		lock (_lockObject)
		{
			// DEBUG: Log job queues before any modification
			Log.Message($"[JobQueueManager] DEBUG: (Before AddItemsToJob) targetQueueA: {job.targetQueueA?.Count ?? 0}, targetQueueB: {job.targetQueueB?.Count ?? 0}, countQueue: {job.countQueue?.Count ?? 0}");
			Log.Message($"[JobQueueManager] DEBUG: Contents targetQueueA: {string.Join(", ", job.targetQueueA ?? [])}");
			Log.Message($"[JobQueueManager] DEBUG: Contents targetQueueB: {string.Join(", ", job.targetQueueB ?? [])}");
			Log.Message($"[JobQueueManager] DEBUG: Contents countQueue: {string.Join(", ", job.countQueue ?? [])}");
			// Initialize queues if they don't exist
			job.targetQueueA ??= [];
			job.countQueue ??= [];
			job.targetQueueB ??= [];

			// Store initial queue counts for rollback
			var initialTargetQueueACount = job.targetQueueA.Count;
			var initialCountQueueCount = job.countQueue.Count;
			var initialTargetQueueBCount = job.targetQueueB.Count;

			try
			{
				// Validate all items before adding any
				var validItems = new List<(Thing thing, int count, LocalTargetInfo target)>();
				for (var i = 0; i < things.Count; i++)
				{
					var thing = things[i];
					var count = counts[i];
					var target = targets[i];

					if (thing == null)
					{
						Log.Warning($"[JobQueueManager] AddItemsToJob: Skipping null thing at index {i}");
						continue;
					}

					if (count <= 0)
					{
						Log.Warning($"[JobQueueManager] AddItemsToJob: Skipping non-positive count {count} at index {i}");
						continue;
					}

					// Validate LocalTargetInfo - check if the target is valid
					if (target.Thing == null)
					{
						// If target.Thing is null, it might be a cell target - check if the cell is valid
						if (!target.Cell.IsValid)
						{
							Log.Warning($"[JobQueueManager] AddItemsToJob: Skipping target with null Thing and invalid Cell at index {i}");
							continue;
						}
						// Cell targets are valid even with null Thing
					}
					else
					{
						// If target.Thing is not null, validate the thing
						if (target.Thing.Destroyed || !target.Thing.Spawned)
						{
							Log.Warning($"[JobQueueManager] AddItemsToJob: Skipping destroyed/unspawned target {target.Thing} at index {i}");
							continue;
						}
					}

					validItems.Add((thing, count, target));
				}

				// Add all valid items atomically
				foreach (var (thing, count, target) in validItems)
				{
					job.targetQueueA.Add(new LocalTargetInfo(thing));
					Log.Message($"[JobQueueManager] DEBUG: Added {thing} to targetQueueA (new count: {job.targetQueueA.Count})");
					job.countQueue.Add(count);
					Log.Message($"[JobQueueManager] DEBUG: Added {count} to countQueue (new count: {job.countQueue.Count})");
					job.targetQueueB.Add(target);
					Log.Message($"[JobQueueManager] DEBUG: Added {target} to targetQueueB (new count: {job.targetQueueB.Count})");

					// Validate synchronization after each item
					if (job.targetQueueA.Count != job.countQueue.Count || job.targetQueueA.Count != job.targetQueueB.Count)
					{
						Log.Error($"[JobQueueManager] AddItemsToJob: Queue synchronization failed after adding item for {pawn}");
						Log.Message($"[JobQueueManager] DEBUG: (On error) targetQueueA: {job.targetQueueA.Count}, targetQueueB: {job.targetQueueB.Count}, countQueue: {job.countQueue.Count}");
						Log.Message($"[JobQueueManager] DEBUG: Contents targetQueueA: {string.Join(", ", job.targetQueueA)}");
						Log.Message($"[JobQueueManager] DEBUG: Contents targetQueueB: {string.Join(", ", job.targetQueueB)}");
						Log.Message($"[JobQueueManager] DEBUG: Contents countQueue: {string.Join(", ", job.countQueue)}");
						RollbackJobQueues(job, initialTargetQueueACount, initialCountQueueCount, initialTargetQueueBCount);
						return false;
					}
				}

				// Final validation
				if (job.targetQueueA.Count != job.countQueue.Count || job.targetQueueA.Count != job.targetQueueB.Count)
				{
					Log.Error($"[JobQueueManager] AddItemsToJob: Final queue synchronization failed for {pawn}");
					Log.Message($"[JobQueueManager] DEBUG: (On final error) targetQueueA: {job.targetQueueA.Count}, targetQueueB: {job.targetQueueB.Count}, countQueue: {job.countQueue.Count}");
					Log.Message($"[JobQueueManager] DEBUG: Contents targetQueueA: {string.Join(", ", job.targetQueueA)}");
					Log.Message($"[JobQueueManager] DEBUG: Contents targetQueueB: {string.Join(", ", job.targetQueueB)}");
					Log.Message($"[JobQueueManager] DEBUG: Contents countQueue: {string.Join(", ", job.countQueue)}");
					RollbackJobQueues(job, initialTargetQueueACount, initialCountQueueCount, initialTargetQueueBCount);
					return false;
				}

				if (Settings.EnableDebugLogging)
				{
					Log.Message($"[JobQueueManager] Successfully added {validItems.Count} items to job for {pawn}");
				}

				return true;
			}
			catch (Exception ex)
			{
				Log.Error($"[JobQueueManager] AddItemsToJob: Exception occurred: {ex.Message}");
				// Rollback to initial state
				RollbackJobQueues(job, initialTargetQueueACount, initialCountQueueCount, initialTargetQueueBCount);
				return false;
			}
		}
	}

	/// <summary>
	/// Atomically removes items from a job's queues
	/// </summary>
	public static bool RemoveItemsFromJob(Job job, int count, Pawn pawn)
	{
		if (job == null || count <= 0)
		{
			Log.Error("[JobQueueManager] RemoveItemsFromJob: Invalid parameters");
			return false;
		}

		lock (_lockObject)
		{
			try
			{
				if (job.targetQueueA == null || job.countQueue == null || job.targetQueueB == null)
				{
					Log.Error("[JobQueueManager] RemoveItemsFromJob: Job queues are null");
					return false;
				}

				var actualCount = Math.Min(count, job.targetQueueA.Count);

				// Remove items from the end (most recently added)
				for (var i = 0; i < actualCount; i++)
				{
					if (job.targetQueueA.Count > 0)
						job.targetQueueA.RemoveAt(job.targetQueueA.Count - 1);
					if (job.countQueue.Count > 0)
						job.countQueue.RemoveAt(job.countQueue.Count - 1);
					if (job.targetQueueB.Count > 0)
						job.targetQueueB.RemoveAt(job.targetQueueB.Count - 1);
				}

				// Validate synchronization after removing
				if (job.targetQueueA.Count != job.countQueue.Count || job.targetQueueA.Count != job.targetQueueB.Count)
				{
					Log.Error($"[JobQueueManager] RemoveItemsFromJob: Queue synchronization failed after removing items for {pawn}");
					return false;
				}

				if (Settings.EnableDebugLogging)
				{
					Log.Message($"[JobQueueManager] Successfully removed {actualCount} items from job for {pawn}");
				}

				return true;
			}
			catch (Exception ex)
			{
				Log.Error($"[JobQueueManager] RemoveItemsFromJob: Exception occurred: {ex.Message}");
				return false;
			}
		}
	}

	/// <summary>
	/// Atomically updates the count of the last item in a job's queues
	/// </summary>
	public static bool UpdateLastItemCount(Job job, int newCount, Pawn pawn)
	{
		if (job == null || newCount <= 0)
		{
			Log.Error("[JobQueueManager] UpdateLastItemCount: Invalid parameters");
			return false;
		}

		lock (_lockObject)
		{
			try
			{
				if (job.targetQueueA == null || job.countQueue == null || job.targetQueueB == null)
				{
					Log.Error("[JobQueueManager] UpdateLastItemCount: Job queues are null");
					return false;
				}

				if (job.countQueue.Count == 0)
				{
					Log.Error("[JobQueueManager] UpdateLastItemCount: countQueue is empty");
					return false;
				}

				// Store the original count for rollback
				var originalCount = job.countQueue[^1];

				// Update the count atomically
				job.countQueue[^1] = newCount;

				// Validate that the queues are still synchronized
				if (job.targetQueueA.Count != job.countQueue.Count || job.targetQueueA.Count != job.targetQueueB.Count)
				{
					Log.Error($"[JobQueueManager] UpdateLastItemCount: Queue synchronization failed after updating count for {pawn}");
					// Rollback the change
					job.countQueue[^1] = originalCount;
					return false;
				}

				if (Settings.EnableDebugLogging)
				{
					Log.Message($"[JobQueueManager] Successfully updated last item count from {originalCount} to {newCount} for {pawn}");
				}

				return true;
			}
			catch (Exception ex)
			{
				Log.Error($"[JobQueueManager] UpdateLastItemCount: Exception occurred: {ex.Message}");
				return false;
			}
		}
	}

	/// <summary>
	/// Atomically clears all items from a job's queues
	/// </summary>
	public static void ClearJobQueues(Job job, Pawn pawn)
	{
		if (job == null)
			return;

		lock (_lockObject)
		{
			try
			{
				job.targetQueueA?.Clear();
				job.countQueue?.Clear();
				job.targetQueueB?.Clear();

				if (Settings.EnableDebugLogging)
				{
					Log.Message($"[JobQueueManager] Cleared all job queues for {pawn}");
				}
			}
			catch (Exception ex)
			{
				Log.Error($"[JobQueueManager] ClearJobQueues: Exception occurred: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// Validates job queue synchronization
	/// </summary>
	public static bool ValidateJobQueues(Job job, Pawn pawn)
	{
		if (job == null)
			return false;

		lock (_lockObject)
		{
			try
			{
				if (job.targetQueueA == null || job.countQueue == null || job.targetQueueB == null)
				{
					Log.Error($"[JobQueueManager] ValidateJobQueues: Job queues are null for {pawn}");
					return false;
				}

				if (job.targetQueueA.Count != job.countQueue.Count || job.targetQueueA.Count != job.targetQueueB.Count)
				{
					Log.Error($"[JobQueueManager] ValidateJobQueues: Queue synchronization failure for {pawn} - targetQueueA.Count ({job.targetQueueA.Count}) != countQueue.Count ({job.countQueue.Count}) != targetQueueB.Count ({job.targetQueueB.Count})");
					return false;
				}

				// Check for invalid targets in targetQueueA
				for (var i = 0; i < job.targetQueueA.Count; i++)
				{
					var target = job.targetQueueA[i];
					if (target.Thing == null)
					{
						Log.Error($"[JobQueueManager] ValidateJobQueues: Found target with null Thing at index {i} for {pawn}");
						return false;
					}

					if (target.Thing.Destroyed || !target.Thing.Spawned)
					{
						Log.Warning($"[JobQueueManager] ValidateJobQueues: Found destroyed/unspawned target {target.Thing} at index {i} for {pawn}");
					}
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
							Log.Error($"[JobQueueManager] ValidateJobQueues: Found target with null Thing and invalid Cell in targetQueueB at index {i} for {pawn}");
							return false;
						}
						// Cell targets are valid even with null Thing
					}
					else
					{
						// If target.Thing is not null, validate the thing
						if (target.Thing.Destroyed || !target.Thing.Spawned)
						{
							Log.Warning($"[JobQueueManager] ValidateJobQueues: Found destroyed/unspawned target {target.Thing} in targetQueueB at index {i} for {pawn}");
						}
					}
				}

				// Check for non-positive counts
				for (var i = 0; i < job.countQueue.Count; i++)
				{
					if (job.countQueue[i] <= 0)
					{
						Log.Error($"[JobQueueManager] ValidateJobQueues: Found non-positive count {job.countQueue[i]} at index {i} for {pawn}");
						return false;
					}
				}

				return true;
			}
			catch (Exception ex)
			{
				Log.Error($"[JobQueueManager] ValidateJobQueues: Exception occurred: {ex.Message}");
				return false;
			}
		}
	}

	/// <summary>
	/// Rolls back job queues to their initial state
	/// </summary>
	private static void RollbackJobQueues(Job job, int initialTargetQueueACount, int initialCountQueueCount, int initialTargetQueueBCount)
	{
		try
		{
			// Remove excess items from targetQueueA
			while (job.targetQueueA?.Count > initialTargetQueueACount)
			{
				job.targetQueueA.RemoveAt(job.targetQueueA.Count - 1);
			}

			// Remove excess items from countQueue
			while (job.countQueue?.Count > initialCountQueueCount)
			{
				job.countQueue.RemoveAt(job.countQueue.Count - 1);
			}

			// Remove excess items from targetQueueB
			while (job.targetQueueB?.Count > initialTargetQueueBCount)
			{
				job.targetQueueB.RemoveAt(job.targetQueueB.Count - 1);
			}

			Log.Message($"[JobQueueManager] Rolled back job queues to initial state - targetQueueA: {job.targetQueueA?.Count ?? 0}, countQueue: {job.countQueue?.Count ?? 0}, targetQueueB: {job.targetQueueB?.Count ?? 0}");
		}
		catch (Exception ex)
		{
			Log.Error($"[JobQueueManager] RollbackJobQueues: Exception occurred: {ex.Message}");
		}
	}

	/// <summary>
	/// Gets debug information about job queues
	/// </summary>
	public static string GetJobQueueDebugInfo(Job job, Pawn pawn)
	{
		if (job == null)
			return "Job is null";

		lock (_lockObject)
		{
			var info = new System.Text.StringBuilder();
			info.AppendLine($"[JobQueueManager] Job queue debug info for {pawn}:");
			info.AppendLine($"  targetQueueA: {job.targetQueueA?.Count ?? 0} items");
			info.AppendLine($"  countQueue: {job.countQueue?.Count ?? 0} items");
			info.AppendLine($"  targetQueueB: {job.targetQueueB?.Count ?? 0} items");

			if (job.targetQueueA != null && job.countQueue != null && job.targetQueueB != null)
			{
				info.AppendLine($"  Synchronized: {job.targetQueueA.Count == job.countQueue.Count && job.targetQueueA.Count == job.targetQueueB.Count}");

				if (job.targetQueueA.Count > 0)
				{
					info.AppendLine("  targetQueueA contents:");
					for (var i = 0; i < Math.Min(job.targetQueueA.Count, 5); i++)
					{
						info.AppendLine($"    [{i}]: {job.targetQueueA[i]}");
					}

					info.AppendLine("  countQueue contents:");
					for (var i = 0; i < Math.Min(job.countQueue.Count, 5); i++)
					{
						info.AppendLine($"    [{i}]: {job.countQueue[i]}");
					}

					info.AppendLine("  targetQueueB contents:");
					for (var i = 0; i < Math.Min(job.targetQueueB.Count, 5); i++)
					{
						info.AppendLine($"    [{i}]: {job.targetQueueB[i]}");
					}
				}
			}

			return info.ToString();
		}
	}
}