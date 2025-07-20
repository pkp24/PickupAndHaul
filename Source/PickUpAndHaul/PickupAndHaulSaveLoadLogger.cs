namespace PickUpAndHaul;

public class PickupAndHaulSaveLoadLogger : GameComponent
{
	private static readonly object _jobLock = new();
	private static readonly List<JobInfo> _suspendedJobs = [];
	private static bool _isSaving;
	private static bool _modRemoved;

	public PickupAndHaulSaveLoadLogger() : base() { }

	[SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Reflection")]
	public PickupAndHaulSaveLoadLogger(Game game) : base() { }

	public override void ExposeData()
	{
		// NEVER save any data for this component
		// This ensures the component is never written to save files
		// and prevents any issues when the mod is removed

		if (Scribe.mode == LoadSaveMode.Saving)
		{
			Log.Message("Skipping save of GameComponent data");
			return;
		}

		if (Scribe.mode == LoadSaveMode.LoadingVars)
		{
			Log.Message("Skipping load of GameComponent data");
			return;
		}

		// Only perform operations during normal gameplay, not during save/load
		if (Scribe.mode == LoadSaveMode.Inactive)
		{
			// Perform safety check
			PerformSafetyCheck();

			// Don't save any mod-specific data if the mod is being removed
			if (_modRemoved)
			{
				Log.Warning("Mod removed, skipping operations");
				return;
			}
		}
	}

	public override void FinalizeInit()
	{
		base.FinalizeInit();
		Log.Message("GameComponent: On Load (FinalizeInit)");

		// Perform safety check to ensure mod is active
		PerformSafetyCheck();
	}

	/// <summary>
	/// Temporarily suspends all pickup and haul jobs to prevent save conflicts
	/// </summary>
	public static void SuspendPickupAndHaulJobs()
	{
		lock (_jobLock)
		{
			if (_isSaving)
			{
				Log.Warning("Save operation already in progress, skipping job suspension");
				return;
			}

			_isSaving = true;
			_suspendedJobs.Clear();

			try
			{
				// Get all maps and their pawns - with null checks
				var maps = Find.Maps;
				if (maps == null || maps.Count == 0)
				{
					Log.Warning("No maps found during job suspension");
					return;
				}

				foreach (var map in maps)
				{
					if (map?.mapPawns == null)
					{
						Log.Warning("Map has null mapPawns during job suspension");
						continue;
					}

					var pawns = map.mapPawns.FreeColonistsAndPrisonersSpawned?.ToList();
					if (pawns == null || pawns.Count == 0)
					{
						continue;
					}

					foreach (var pawn in pawns)
					{
						if (pawn?.jobs == null)
							continue;

						// Store the current job reference to avoid race conditions
						var currentJob = pawn.jobs.curJob;
						if (currentJob == null)
							continue;

						// Check if this is a pickup and haul job
						if (IsPickupAndHaulJob(currentJob))
						{
							// Create job info with additional null checks
							var jobQueue = pawn.jobs.jobQueue?.ToList() ?? [];

							var jobInfo = new JobInfo
							{
								Pawn = pawn,
								Job = currentJob,
								JobQueue = jobQueue,
								JobDef = currentJob.def
							};

							_suspendedJobs.Add(jobInfo);

							// End the current job safely
							var curDriver = pawn.jobs.curDriver;
							if (curDriver != null)
							{
								try
								{
									curDriver.EndJobWith(JobCondition.InterruptForced);
								}
								catch (Exception ex)
								{
									Log.Warning($"Error ending job for {pawn.NameShortColored}: {ex.Message}");
								}
							}

							// Clear the job queue safely
							if (pawn.jobs.jobQueue != null)
							{
								try
								{
									pawn.jobs.jobQueue.Clear(pawn, true);
								}
								catch (Exception ex)
								{
									Log.Warning($"Error clearing job queue for {pawn.NameShortColored}: {ex.Message}");
								}
							}

							Log.Message($"Suspended job for {pawn.NameShortColored}: {currentJob.def?.defName ?? "Unknown"}");
						}
					}
				}

				Log.Message($"Suspended {_suspendedJobs.Count} pickup and haul jobs for save operation");
			}
			catch (Exception ex)
			{
				Log.Error($"Error during job suspension: {ex.Message}");
				// Don't reset _isSaving here - it should be handled by the restoration method
				// Emergency cleanup if needed
				EmergencyCleanup();
				throw; // Re-throw to let the caller handle the error
			}
		}
	}

	/// <summary>
	/// Restores previously suspended pickup and haul jobs after save completion
	/// </summary>
	public static void RestorePickupAndHaulJobs()
	{
		lock (_jobLock)
		{
			if (!_isSaving)
			{
				Log.Warning("No save operation in progress, skipping job restoration");
				return;
			}

			try
			{
				var restoredCount = 0;
				var failedCount = 0;

				foreach (var jobInfo in _suspendedJobs)
				{
					if (jobInfo?.Pawn?.Spawned == true && jobInfo.Pawn.Map != null && jobInfo.Pawn.jobs != null)
					{
						// Restore the job queue safely
						if (jobInfo.Pawn.jobs.jobQueue != null)
						{
							try
							{
								jobInfo.Pawn.jobs.jobQueue.Clear(jobInfo.Pawn, true);

								if (jobInfo.JobQueue != null)
								{
									foreach (var queuedJob in jobInfo.JobQueue)
									{
										if (queuedJob?.job != null)
										{
											jobInfo.Pawn.jobs.jobQueue.EnqueueLast(queuedJob.job, queuedJob.tag);
										}
									}
								}
							}
							catch (Exception ex)
							{
								Log.Warning($"Error restoring job queue for {jobInfo.Pawn.NameShortColored}: {ex.Message}");
							}
						}

						// Try to restart the main job if the pawn is available
						if (jobInfo.Pawn.jobs.curJob == null &&
							jobInfo.Job?.targetA != null &&
							jobInfo.JobDef != null &&
							jobInfo.Pawn.CanReserveAndReach(jobInfo.Job.targetA, PathEndMode.ClosestTouch, Danger.Deadly))
						{
							try
							{
								var newJob = JobMaker.MakeJob(jobInfo.JobDef, jobInfo.Job.targetA, jobInfo.Job.targetB);

								// Copy job properties safely
								if (jobInfo.Job.targetQueueA != null)
									newJob.targetQueueA = [.. jobInfo.Job.targetQueueA];
								if (jobInfo.Job.targetQueueB != null)
									newJob.targetQueueB = [.. jobInfo.Job.targetQueueB];
								if (jobInfo.Job.countQueue != null)
									newJob.countQueue = [.. jobInfo.Job.countQueue];

								newJob.count = jobInfo.Job.count;
								newJob.haulMode = jobInfo.Job.haulMode;

								if (newJob.TryMakePreToilReservations(jobInfo.Pawn, false))
								{
									jobInfo.Pawn.jobs.StartJob(newJob, JobCondition.InterruptForced);
									Log.Message($"Restored job for {jobInfo.Pawn.NameShortColored}: {jobInfo.JobDef.defName ?? "Unknown"}");
									restoredCount++;
								}
								else
								{
									Log.Warning($"Failed to restore job for {jobInfo.Pawn.NameShortColored}: reservation failed");
									failedCount++;
								}
							}
							catch (Exception ex)
							{
								Log.Warning($"Error creating new job for {jobInfo.Pawn.NameShortColored}: {ex.Message}");
								failedCount++;
							}
						}
						else
						{
							Log.Message($"Could not restore job for {jobInfo.Pawn.NameShortColored}: pawn unavailable or already has job");
							failedCount++;
						}
					}
					else
					{
						Log.Warning($"Could not restore job: pawn no longer spawned or valid");
						failedCount++;
					}
				}

				Log.Message($"Job restoration complete: {restoredCount} restored, {failedCount} failed");
			}
			catch (Exception ex)
			{
				Log.Error($"Error during job restoration: {ex.Message}");
			}
			finally
			{
				_suspendedJobs.Clear();
				_isSaving = false;
			}
		}
	}

	/// <summary>
	/// Checks if a job is a pickup and haul job that should be suspended during save
	/// </summary>
	private static bool IsPickupAndHaulJob(Job job) => (job?.def?.defName) != null && job.def.defName is "HaulToInventory" or
			   "UnloadYourHauledInventory";

	/// <summary>
	/// Provides a public method to check if a save operation is in progress
	/// </summary>
	public static bool IsSaveInProgress()
	{
		lock (_jobLock)
		{
			return _isSaving;
		}
	}

	/// <summary>
	/// Provides a public method to manually suspend jobs (for external use)
	/// </summary>
	public static void ManualSuspendJobs() => SuspendPickupAndHaulJobs();

	/// <summary>
	/// Provides a public method to manually restore jobs (for external use)
	/// </summary>
	public static void ManualRestoreJobs() => RestorePickupAndHaulJobs();

	/// <summary>
	/// Emergency cleanup method to reset the save state if something goes wrong
	/// </summary>
	public static void EmergencyCleanup()
	{
		lock (_jobLock)
		{
			Log.Warning("Performing emergency cleanup of save state");
			_suspendedJobs.Clear();
			_isSaving = false;
		}
	}

	/// <summary>
	/// Gets the current status of the save system for debugging
	/// </summary>
	public static string SaveStatus
	{
		get
		{
			lock (_jobLock)
			{
				return $"Save in progress: {_isSaving}, Suspended jobs: {_suspendedJobs.Count}, Mod removed: {_modRemoved}";
			}
		}
	}

	/// <summary>
	/// Marks the mod as removed to prevent save data corruption
	/// </summary>
	public static void MarkModAsRemoved()
	{
		lock (_jobLock)
		{
			Log.Warning("Mod marked as removed, preventing save data corruption");
			_modRemoved = true;
			_suspendedJobs.Clear();
			_isSaving = false;
		}
	}

	/// <summary>
	/// Checks if the mod is currently active and available
	/// </summary>
	public static bool IsModActive()
	{
		try
		{
			// Check if our key types are available
			var haulToInventoryDef = DefDatabase<JobDef>.GetNamedSilentFail("HaulToInventory");
			var unloadDef = DefDatabase<JobDef>.GetNamedSilentFail("UnloadYourHauledInventory");

			return haulToInventoryDef != null && unloadDef != null;
		}
		catch (Exception ex)
		{
			Log.Warning($"Error checking mod active status: {ex.Message}");
			return false;
		}
	}

	/// <summary>
	/// Performs a safety check and cleanup if the mod is not active
	/// </summary>
	public static void PerformSafetyCheck()
	{
		if (!IsModActive())
		{
			Log.Warning("Mod appears to be inactive, performing safety cleanup");
			MarkModAsRemoved();

			// Clear any remaining mod-specific jobs from all pawns
			try
			{
				var maps = Find.Maps;
				if (maps == null || maps.Count == 0)
				{
					Log.Warning("No maps found during safety check");
					return;
				}

				foreach (var map in maps)
				{
					if (map?.mapPawns == null)
					{
						Log.Warning("Map has null mapPawns during safety check");
						continue;
					}

					var pawns = map.mapPawns.FreeColonistsAndPrisonersSpawned?.ToList();
					if (pawns == null || pawns.Count == 0)
					{
						continue;
					}

					foreach (var pawn in pawns)
					{
						if (pawn?.jobs?.curJob?.def != null)
						{
							var jobDef = pawn.jobs.curJob.def;
							if (jobDef.defName is "HaulToInventory" or "UnloadYourHauledInventory")
							{
								Log.Warning($"Clearing mod-specific job from {pawn.NameShortColored}");

								try
								{
									pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, false, false);
								}
								catch (Exception ex)
								{
									Log.Warning($"Error clearing job from {pawn.NameShortColored}: {ex.Message}");
								}
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				Log.Error($"Error during safety cleanup: {ex.Message}");
			}
		}
	}
}

/// <summary>
/// Helper class to store job information during suspension
/// </summary>
public class JobInfo
{
	public Pawn Pawn { get; set; }
	public Job Job { get; set; }
	public List<QueuedJob> JobQueue { get; set; }
	public JobDef JobDef { get; set; }
}