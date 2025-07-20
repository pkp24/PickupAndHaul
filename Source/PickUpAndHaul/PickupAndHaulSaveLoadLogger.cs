namespace PickUpAndHaul;

public class PickupAndHaulSaveLoadLogger : GameComponent
{
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
				return;
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
			Log.Warning("Mod appears to be inactive, performing safety cleanup. Mod marked as removed.");
			_modRemoved = true;

			// Clear any remaining mod-specific jobs from all pawns
			var maps = Find.Maps;
			if (maps == null || maps.Count == 0)
				return;

			foreach (var map in maps)
			{
				if (map?.mapPawns == null)
					continue;

				var pawns = map.mapPawns.FreeColonistsAndPrisonersSpawned?.ToList();
				if (pawns == null || pawns.Count == 0)
					continue;

				foreach (var pawn in pawns)
					if (pawn?.jobs?.curJob?.def != null)
						if (pawn.jobs.curJob.def.defName is "HaulToInventory" or "UnloadYourHauledInventory")
							pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, false, false);
			}
		}
	}
}