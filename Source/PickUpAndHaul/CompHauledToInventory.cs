namespace PickUpAndHaul;

public class CompHauledToInventory : ThingComp
{
	private HashSet<Thing> takenToInventory = new();

	public HashSet<Thing> GetHashSet()
	{
		if (takenToInventory == null)
			takenToInventory = new HashSet<Thing>();
		// Don't modify the collection here - this causes concurrent modification exceptions
		// Instead, handle null removal at specific safe points
		return takenToInventory;
	}
	
	public void CleanupNulls()
	{
		// Separate method to clean nulls that can be called at safe points
		takenToInventory?.RemoveWhere(x => x == null);
	}

	public void RegisterHauledItem(Thing thing) => takenToInventory.Add(thing);

	public override void PostExposeData()
	{
		// Don't save any data for this component to prevent save corruption when the mod is removed
		if (Scribe.mode == LoadSaveMode.Saving)
		{
			Log.Message("[PickUpAndHaul] Skipping save data for CompHauledToInventory");
			return;
		}
		
		// Don't load any data for this component to prevent save corruption when the mod is removed
		if (Scribe.mode == LoadSaveMode.LoadingVars)
		{
			Log.Message("[PickUpAndHaul] Skipping load data for CompHauledToInventory");
			// Initialize with empty collection to prevent null reference errors
			takenToInventory = new HashSet<Thing>();
			return;
		}
		
		// Only expose data if we're in a different mode (like copying)
		base.PostExposeData();
		Scribe_Collections.Look(ref takenToInventory, "ThingsHauledToInventory", LookMode.Reference);
	}
}