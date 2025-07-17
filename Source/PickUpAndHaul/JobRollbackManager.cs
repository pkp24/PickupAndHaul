using System.Collections.Concurrent;
using System.Linq;
using System.Text;

namespace PickUpAndHaul;

/// <summary>
/// Manages job rollback operations to restore consistent state when jobs fail
/// </summary>
public static class JobRollbackManager
{
	private static readonly ConcurrentDictionary<Job, JobRollbackState> _rollbackStates = new();
	private static readonly object _lockObject = new();

	/// <summary>
	/// Creates a rollback checkpoint for a job
	/// </summary>
	public static void CreateRollbackPoint(Job job, Pawn pawn)
	{
		if (job == null)
			return;

		var state = new JobRollbackState
		{
			TargetQueueACount = job.targetQueueA?.Count ?? 0,
			TargetQueueBCount = job.targetQueueB?.Count ?? 0,
			CountQueueCount = job.countQueue?.Count ?? 0,
			CreatedTick = Find.TickManager.TicksGame,
			Pawn = pawn
		};
		if (_rollbackStates.TryGetValue(job, out var oldState))
			_rollbackStates.TryUpdate(job, state, oldState);

		if (Settings.EnableDebugLogging)
			Log.Message($"[JobRollbackManager] Created rollback point for {pawn} - queues: A={state.TargetQueueACount}, B={state.TargetQueueBCount}, C={state.CountQueueCount}");
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
				_rollbackStates.Remove(job, out _);
			}
		}
	}

	/// <summary>
	/// Cleans up rollback states for completed or abandoned jobs
	/// </summary>
	public static void CleanupRollbackStates()
	{
		var currentTick = Find.TickManager.TicksGame;
		var initialRollbackStates = _rollbackStates.Count;
		foreach (var state in _rollbackStates)
			if (currentTick - state.Value.CreatedTick > 1000) // Remove states older than 1000 ticks (about 16 seconds)
				_rollbackStates.Remove(state.Key, out _);

		var diff = initialRollbackStates - _rollbackStates.Count;
		if (diff > 0 && Settings.EnableDebugLogging)
			Log.Message($"[JobRollbackManager] Rollback states Before cleanup {initialRollbackStates} and after {_rollbackStates.Count}, diff: {diff}");
	}

	/// <summary>
	/// Gets debug information about rollback states
	/// </summary>
	public static string GetRollbackDebugInfo()
	{
		lock (_lockObject)
		{
			var info = new StringBuilder();
			info.AppendLine($"[JobRollbackManager] Rollback states: {_rollbackStates.Count}");

			foreach (var kvp in _rollbackStates.Take(5)) // Show first 5
			{
				var job = kvp.Key;
				var state = kvp.Value;
				info.AppendLine($"  Job for {state.Pawn}: A={state.TargetQueueACount}, B={state.TargetQueueBCount}, C={state.CountQueueCount}, Age={Find.TickManager.TicksGame - state.CreatedTick}");
			}

			return info.ToString();
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