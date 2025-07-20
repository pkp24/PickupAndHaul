namespace PickUpAndHaul;

public class PickupAndHaulSaveLoadLogger : GameComponent
{
	private static readonly object _jobLock = new();
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
	/// Provides a public method to check if a save operation is in progress
	/// </summary>
	public static bool IsSaveInProgress() => _isSaving;

	/// <summary>
	/// Marks the mod as removed to prevent save data corruption
	/// </summary>
	public static void MarkModAsRemoved()
	{
		lock (_jobLock)
		{
			Log.Warning("Mod marked as removed, preventing save data corruption");
			_modRemoved = true;
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
					continue;

				foreach (var pawn in pawns)
				{
					if (pawn?.jobs?.curJob?.def != null)
					{
						var jobDef = pawn.jobs.curJob.def;
						if (jobDef.defName is "HaulToInventory" or "UnloadYourHauledInventory")
						{
							Log.Warning($"Clearing mod-specific job from {pawn.NameShortColored}");
							pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, false, false);
						}
					}
				}
			}
		}
	}
}