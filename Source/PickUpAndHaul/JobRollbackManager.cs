namespace PickUpAndHaul;

/// <summary>
/// Manages job rollback operations to restore consistent state when jobs fail
/// </summary>
public static class JobRollbackManager
{
	private static readonly Dictionary<Job, JobRollbackState> _rollbackStates = [];
	private static readonly object _lockObject = new();

	/// <summary>
	/// Creates a rollback checkpoint for a job
	/// </summary>
	public static void CreateRollbackPoint(Job job, Pawn pawn)
	{
		if (job == null)
			return;

		lock (_lockObject)
		{
			var state = new JobRollbackState
			{
				TargetQueueACount = job.targetQueueA?.Count ?? 0,
				TargetQueueBCount = job.targetQueueB?.Count ?? 0,
				CountQueueCount = job.countQueue?.Count ?? 0,
				CreatedTick = Find.TickManager.TicksGame,
				Pawn = pawn
			};

			_rollbackStates[job] = state;

			if (Settings.EnableDebugLogging)
			{
				Log.Message($"[JobRollbackManager] Created rollback point for {pawn} - queues: A={state.TargetQueueACount}, B={state.TargetQueueBCount}, C={state.CountQueueCount}");
			}
		}
	}

	/// <summary>
	/// Rolls back a job to its last checkpoint
	/// </summary>
	public static bool RollbackJob(Job job, Pawn pawn)
	{
		if (job == null)
			return false;

		lock (_lockObject)
		{
			if (!_rollbackStates.TryGetValue(job, out var state))
			{
				Log.Warning($"[JobRollbackManager] No rollback state found for job of {pawn}");
				return false;
			}

			try
			{
				// Rollback targetQueueA
				if (job.targetQueueA != null && job.targetQueueA.Count > state.TargetQueueACount)
				{
					var itemsToRemove = job.targetQueueA.Count - state.TargetQueueACount;
					for (var i = 0; i < itemsToRemove; i++)
					{
						job.targetQueueA.RemoveAt(job.targetQueueA.Count - 1);
					}
				}

				// Rollback countQueue
				if (job.countQueue != null && job.countQueue.Count > state.CountQueueCount)
				{
					var itemsToRemove = job.countQueue.Count - state.CountQueueCount;
					for (var i = 0; i < itemsToRemove; i++)
					{
						job.countQueue.RemoveAt(job.countQueue.Count - 1);
					}
				}

				// Rollback targetQueueB
				if (job.targetQueueB != null && job.targetQueueB.Count > state.TargetQueueBCount)
				{
					var itemsToRemove = job.targetQueueB.Count - state.TargetQueueBCount;
					for (var i = 0; i < itemsToRemove; i++)
					{
						job.targetQueueB.RemoveAt(job.targetQueueB.Count - 1);
					}
				}

				// Validate synchronization after rollback
				if (job.targetQueueA?.Count != job.countQueue?.Count)
				{
					Log.Error($"[JobRollbackManager] Rollback failed - queue synchronization error for {pawn}");
					Log.Error($"[JobRollbackManager] After rollback - targetQueueA: {job.targetQueueA?.Count ?? 0}, countQueue: {job.countQueue?.Count ?? 0}");
					return false;
				}

				if (Settings.EnableDebugLogging)
				{
					Log.Message($"[JobRollbackManager] Successfully rolled back job for {pawn}");
					Log.Message($"[JobRollbackManager] Rollback result - queues: A={job.targetQueueA?.Count ?? 0}, B={job.targetQueueB?.Count ?? 0}, C={job.countQueue?.Count ?? 0}");
				}

				return true;
			}
			catch (Exception ex)
			{
				Log.Error($"[JobRollbackManager] Exception during rollback for {pawn}: {ex.Message}");
				return false;
			}
			finally
			{
				// Remove the rollback state after use
				_rollbackStates.Remove(job);
			}
		}
	}

	/// <summary>
	/// Cleans up rollback states for completed or abandoned jobs
	/// </summary>
	public static void CleanupRollbackStates()
	{
		lock (_lockObject)
		{
			var currentTick = Find.TickManager.TicksGame;
			var keysToRemove = _rollbackStates.Keys.Where(job =>
			{
				if (_rollbackStates.TryGetValue(job, out var state))
				{
					// Remove states older than 1000 ticks (about 16 seconds)
					return currentTick - state.CreatedTick > 1000;
				}
				return true;
			}).ToList();

			foreach (var key in keysToRemove)
			{
				_rollbackStates.Remove(key);
			}

			if (keysToRemove.Count > 0 && Settings.EnableDebugLogging)
			{
				Log.Message($"[JobRollbackManager] Cleaned up {keysToRemove.Count} old rollback states");
			}
		}
	}

	/// <summary>
	/// Gets debug information about rollback states
	/// </summary>
	public static void GetRollbackDebugInfo()
	{
		Log.Message($"Rollback states: {_rollbackStates.Count}");

		foreach (var kvp in _rollbackStates.Take(5)) // Show first 5
		{
			var state = kvp.Value;
			Log.Message($"Job for {state.Pawn}: A={state.TargetQueueACount}, B={state.TargetQueueBCount}, C={state.CountQueueCount}, Age={Find.TickManager.TicksGame - state.CreatedTick}");
		}
	}

	/// <summary>
	/// Represents the state of a job for rollback purposes
	/// </summary>
	private class JobRollbackState
	{
		public int TargetQueueACount { get; set; }
		public int TargetQueueBCount { get; set; }
		public int CountQueueCount { get; set; }
		public int CreatedTick { get; set; }
		public Pawn Pawn { get; set; }
	}
}