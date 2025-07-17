namespace PickUpAndHaul;

public class CompHauledToInventory : ThingComp
{
	private HashSet<Thing> takenToInventory;

	// Don't modify the collection here - this causes concurrent modification exceptions
	// Instead, handle null removal at specific safe points
	public HashSet<Thing> GetHashSet()
		=> takenToInventory ??= [];

	// Separate method to clean nulls that can be called at safe points
	public void CleanupNulls() =>
		takenToInventory?.RemoveWhere(x => x == null);

	public void RegisterHauledItem(Thing thing) => takenToInventory.Add(thing);

	public override void PostExposeData()
	{
		// Don't save any data for this component to prevent save corruption
		// when the mod is removed
		if (Scribe.mode == LoadSaveMode.Saving)
		{
			Log.Message("[PickUpAndHaul] Skipping save data for CompHauledToInventory");
			return;
		}

		// Handle loading by properly handling the scribing but ignoring data
		if (Scribe.mode == LoadSaveMode.LoadingVars)
		{
			// Initialize with empty collection to prevent null reference errors
			takenToInventory ??= [];
			// Properly handle the scribing to avoid errors
			Scribe_Collections.Look(ref takenToInventory, "ThingsHauledToInventory", LookMode.Reference);
			return;
		}

		// Only expose data if we're in a different mode (like copying)
		base.PostExposeData();
		Scribe_Collections.Look(ref takenToInventory, "ThingsHauledToInventory", LookMode.Reference);
	}
}