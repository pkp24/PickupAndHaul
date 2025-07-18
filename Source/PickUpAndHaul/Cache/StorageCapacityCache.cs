namespace PickUpAndHaul.Cache;

public class StorageCapacityCache : ICache
{
	private static int _lastStorageCapacityCacheCleanupTick;

	static StorageCapacityCache() =>
		CacheManager.RegisterCache(Instance);

	public static StorageCapacityCache Instance { get; } = new();

	public static Dictionary<(Thing thing, IntVec3 cell, int tick), int> Cache { get; } = [];

	public static int CapacityAt(Thing thing, IntVec3 storeCell, Map map)
	{
		// Use cached capacity to avoid repeated expensive calculations
		var currentTick = Find.TickManager.TicksGame;
		var cacheKey = (thing, storeCell, currentTick);

		// Clean up old cache entries periodically
		if (currentTick - _lastStorageCapacityCacheCleanupTick > 50) // Clean up every 50 ticks
		{
			var keysToRemove = Cache.Keys.Where(k => currentTick - k.tick > 3).ToList();
			foreach (var key in keysToRemove)
				Cache.Remove(key);
			_lastStorageCapacityCacheCleanupTick = currentTick;
		}

		if (Cache.TryGetValue(cacheKey, out var cachedCapacity))
			return cachedCapacity;

		if (thing.CapacityAt(storeCell, map, out var capacity))
		{
			Log.Message($"Found external capacity of {capacity}");
			Cache[cacheKey] = capacity;
			return capacity;
		}

		// Check if there's a container at this cell that can hold multiple items
		var thingsAtCell = map.thingGrid.ThingsListAt(storeCell);
		for (var i = 0; i < thingsAtCell.Count; i++)
		{
			var thingAtCell = thingsAtCell[i];
			var thingOwner = thingAtCell.TryGetInnerInteractableThingOwner();
			if (thingOwner != null)
			{
				// This is a container (like a crate) - use its proper capacity calculation
				var containerCapacity = thingOwner.GetCountCanAccept(thing);
				Cache[cacheKey] = containerCapacity;
				return containerCapacity;
			}
		}

		// Fallback to original logic for simple storage cells
		capacity = thing.def.stackLimit;

		var preExistingThing = map.thingGrid.ThingAt(storeCell, thing.def);
		if (preExistingThing != null)
			capacity = thing.def.stackLimit - preExistingThing.stackCount;

		Log.Message($"Using fallback capacity calculation for {thing} at {storeCell}: {capacity}");
		Cache[cacheKey] = capacity;
		return capacity;
	}

	public void ForceCleanup()
	{
		Cache.Clear();
		_lastStorageCapacityCacheCleanupTick = 0;
	}

	public string GetDebugInfo() => $"Number of items in cache: {Cache.Count}";
}