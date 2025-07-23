namespace PickUpAndHaul;

public class PickupAndHaulSaveLoadLogger : GameComponent
{
	private static bool _modRemoved;

	public PickupAndHaulSaveLoadLogger() : base() { }

	[SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Reflection")]
	public PickupAndHaulSaveLoadLogger(Game game) : base() { }

	public override void ExposeData()
	{
		if (Scribe.mode is LoadSaveMode.Saving or LoadSaveMode.LoadingVars)
		{
			Log.Message($"Skipping {Enum.GetName(typeof(LoadSaveMode), Scribe.mode)} for GameComponent");
			return;
		}

		if (Scribe.mode == LoadSaveMode.Inactive) // Only perform operations during normal gameplay, not during save/load
		{
			PerformSafetyCheck();
			if (_modRemoved) // Don't save any mod-specific data if the mod is being removed
				return;
		}
	}

	public override void FinalizeInit()
	{
		base.FinalizeInit();
		Log.Message("GameComponent: On Load (FinalizeInit)");
		PerformSafetyCheck(); // Perform safety check to ensure mod is active
	}

	public static bool IsModActive()
	{
		try
		{
			// Check if our key types are available
			var haulToInventoryDef = DefDatabase<JobDef>.GetNamedSilentFail(nameof(PickUpAndHaulJobDefOf.HaulToInventory));
			var unloadDef = DefDatabase<JobDef>.GetNamedSilentFail(nameof(PickUpAndHaulJobDefOf.UnloadYourHauledInventory));

			return haulToInventoryDef != null && unloadDef != null;
		}
		catch (Exception ex)
		{
			Log.Error(ex.ToString());
			return false;
		}
	}

	private static void PerformSafetyCheck()
	{
		if (!IsModActive())
		{
			Log.Warning("Mod appears to be inactive, performing safety cleanup. Mod marked as removed.");
			_modRemoved = true;
		}
	}
}