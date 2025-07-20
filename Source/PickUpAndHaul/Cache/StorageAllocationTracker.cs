using PickUpAndHaul.Structs;

namespace PickUpAndHaul.Cache;

/// <summary>
/// Tracks pending hauling jobs and their storage allocations to prevent race conditions
/// between multiple pawns hauling to the same storage location.
/// </summary>
public class StorageAllocationTracker : ICache
{
	/// <summary>
	/// Gets the singleton instance
	/// </summary>
	public static StorageAllocationTracker Instance { get; } = new();

	/// <summary>
	/// Tracks storage allocations by storage location and item type
	/// Key: Storage location identifier (cell position or container thing)
	/// Value: Dictionary of item def to allocated count
	/// </summary>
	private readonly ConcurrentDictionary<StorageLocation, Dictionary<ThingDef, int>> _pendingAllocations = [];

	/// <summary>
	/// Tracks which pawns have pending allocations to clean up when they die or jobs fail
	/// </summary>
	private readonly ConcurrentDictionary<Pawn, HashSet<StorageLocation>> _pawnAllocations = [];

	/// <summary>
	/// Lock object for thread safety
	/// </summary>
	private readonly object _lockObject = new();

	static StorageAllocationTracker() =>
		// Register with cache manager for automatic cleanup
		CacheManager.RegisterCache(Instance);

	/// <summary>
	/// Reserve storage capacity for a pending hauling job (backward compatibility)
	/// </summary>
	public bool ReserveCapacity(StorageLocation location, ThingDef itemDef, int amount, Pawn pawn)
	{
		lock (_lockObject)
		{
			// Check if we can reserve this amount
			if (GetActualCapacity(location, itemDef, pawn.Map) - GetPendingAllocation(location, itemDef) < amount)
			{
				Log.Message($"Cannot reserve {amount} of {itemDef} at {location} - insufficient capacity");
				return false;
			}

			// Add to pending allocations
			if (!_pendingAllocations.TryGetValue(location, out var items))
				_pendingAllocations.TryAdd(location, new Dictionary<ThingDef, int> { { itemDef, amount } });
			else if (items.ContainsKey(itemDef))
			{
				items[itemDef] += amount;
				_pendingAllocations.AddOrUpdate(location, items, (key, oldValue) => items);
			}

			if (!_pawnAllocations.TryGetValue(pawn, out var locations))
				_pawnAllocations.TryAdd(pawn, [location]);
			else
			{
				locations.Add(location);
				_pawnAllocations.AddOrUpdate(pawn, locations, (key, oldValue) => locations);
			}

			Log.Message($"Reserved {amount} of {itemDef} at {location} for {pawn}");
			return true;
		}
	}

	/// <summary>
	/// Check if a pawn currently has any pending allocations
	/// </summary>
	public bool HasAllocations(Pawn pawn) => _pawnAllocations.TryGetValue(pawn, out var storageLocations) && storageLocations.Count > 0;

	/// <summary>
	/// Release storage capacity when a hauling job is completed or cancelled
	/// </summary>
	public void ReleaseCapacity(StorageLocation location, ThingDef itemDef, int amount, Pawn pawn)
	{
		lock (_lockObject)
		{
			if (_pendingAllocations.TryGetValue(location, out var items) && items.TryGetValue(itemDef, out var currentAmount))
			{
				items[itemDef] += Math.Max(0, currentAmount - amount);

				// Remove location from pawn's allocations if no more pending allocations
				if (items[itemDef] == 0)
					items.Remove(itemDef);

				if (items.Count == 0)
					_pendingAllocations.Remove(location, out var _);
				else
					_pendingAllocations.AddOrUpdate(location, items, (key, oldValue) => items);
			}

			// Remove from pawn's allocation tracking
			if (_pawnAllocations.TryGetValue(pawn, out var locations))
			{
				locations.Remove(location);
				if (locations.Count == 0)
					_pawnAllocations.Remove(pawn, out var _);
				else
					_pawnAllocations.AddOrUpdate(pawn, locations, (key, oldValue) => locations);
			}

			Log.Message($"Released {amount} of {itemDef} at {location} for {pawn}");
		}
	}

	/// <summary>
	/// Clean up all allocations for a pawn (when they die or job fails)
	/// </summary>
	public void CleanupPawnAllocations(Pawn pawn)
	{
		if (_pawnAllocations.TryGetValue(pawn, out var locations))
		{
			foreach (var location in locations)
				if (_pendingAllocations.TryGetValue(location, out var items))
					foreach (var itemDef in items)
						ReleaseCapacity(location, itemDef.Key, itemDef.Value, pawn);

			Log.Message($"Cleaned up all allocations for {pawn}");
		}
	}

	/// <summary>
	/// Get the actual storage capacity at a location
	/// </summary>
	private static int GetActualCapacity(StorageLocation location, ThingDef itemDef, Map map)
	{
		if (location.Container != null)
		{
			var thingOwner = location.Container.TryGetInnerInteractableThingOwner();
			if (thingOwner != null)
			{
				// Create a temporary thing to check capacity
				var tempThing = CreateTempThing(itemDef);
				if (tempThing != null)
					try
					{
						var capacity = thingOwner.GetCountCanAccept(tempThing);
						return capacity;
					}
					catch (Exception ex)
					{
						Log.Error($"Error getting capacity for {itemDef} at container {location.Container}: {ex.Message}");
						// Return safe stack count as fallback
						return GetSafeStackCount(itemDef);
					}
					finally
					{
						// Properly dispose of the temporary thing to prevent memory leaks
						DisposeTempThing(tempThing);
					}
				else
					// Fallback: use safe stack count as approximation
					return GetSafeStackCount(itemDef);
			}
		}
		else if (location.Cell.IsValid)
		{
			// Use the existing CapacityAt method
			var tempThing = CreateTempThing(itemDef);
			if (tempThing != null)
				try
				{
					return StorageCapacityCache.CapacityAt(tempThing, location.Cell, map);
				}
				catch (Exception ex)
				{
					Log.Error($"Error getting capacity for {itemDef} at cell {location.Cell}: {ex.Message}");
					// Fallback: check if there's already an item at the location and calculate remaining capacity
					var existingThing = map.thingGrid.ThingAt(location.Cell, itemDef);
					return existingThing != null ? GetSafeStackCount(itemDef) - existingThing.stackCount : GetSafeStackCount(itemDef);
				}
				finally
				{
					// Properly dispose of the temporary thing to prevent memory leaks
					DisposeTempThing(tempThing);
				}
			else
			{
				// Fallback: check if there's already an item at the location and calculate remaining capacity
				var existingThing = map.thingGrid.ThingAt(location.Cell, itemDef);
				return existingThing != null ? GetSafeStackCount(itemDef) - existingThing.stackCount : GetSafeStackCount(itemDef);
			}
		}

		return 0;
	}

	/// <summary>
	/// Safely create a temporary thing for capacity checking
	/// </summary>
	private static Thing CreateTempThing(ThingDef itemDef)
	{
		try
		{
			// Validate stack limit and use a safe default if invalid
			var safeStackCount = GetSafeStackCount(itemDef);

			if (itemDef.MadeFromStuff)
			{
				// For stuff-based items, use the default stuff or find a suitable one
				var stuff = itemDef.defaultStuff ?? GenStuff.DefaultStuffFor(itemDef);
				if (stuff != null)
				{
					var thing = ThingMaker.MakeThing(itemDef, stuff);
					thing.stackCount = safeStackCount;
					return thing;
				}
				else
				{
					Log.Warning($"Could not find suitable stuff for {itemDef}, using stackCount for capacity estimation");
					return null;
				}
			}
			else
			{
				var thing = ThingMaker.MakeThing(itemDef);
				thing.stackCount = safeStackCount;
				return thing;
			}
		}
		catch (Exception ex)
		{
			Log.Error($"Error creating temp thing for {itemDef}: {ex.Message}");
			return null;
		}
	}

	/// <summary>
	/// Properly dispose of a temporary thing to prevent memory leaks
	/// </summary>
	private static void DisposeTempThing(Thing tempThing)
	{
		if (tempThing == null)
			return;

		try
		{
			// If the thing is spawned, destroy it properly
			if (tempThing.Spawned)
				tempThing.Destroy();
			else
			{
				// For unspawned things, we need to clean up any references they might hold
				// Clear any potential references that might cause memory leaks
				tempThing.stackCount = 0;

				// Note: We cannot directly clear the Stuff reference as SetStuffDirect doesn't exist
				// The garbage collector will handle cleanup of unspawned objects
				tempThing = null;
			}
		}
		catch (Exception ex)
		{
			Log.Error($"Error disposing temp thing {tempThing}: {ex.Message}");
		}
	}

	/// <summary>
	/// Get a safe stack count for capacity calculations, validating against itemDef.stackLimit
	/// </summary>
	private static int GetSafeStackCount(ThingDef itemDef)
	{
		// Validate stack limit - must be positive
		if (itemDef.stackLimit <= 0)
		{
			Log.Warning($"Invalid stackLimit ({itemDef.stackLimit}) for {itemDef}, using default of 1");
			return 1;
		}

		// For items that can't be stacked, use 1
		if (itemDef.stackLimit == 1)
			return 1;

		// For items with very large stack limits, cap at a reasonable value to prevent issues
		const int maxReasonableStackLimit = 10000;
		if (itemDef.stackLimit > maxReasonableStackLimit)
		{
			Log.Warning($"Very large stackLimit ({itemDef.stackLimit}) for {itemDef}, capping at {maxReasonableStackLimit}");
			return maxReasonableStackLimit;
		}

		return itemDef.stackLimit;
	}

	/// <summary>
	/// Get the amount of pending allocations for a specific item at a location
	/// </summary>
	private int GetPendingAllocation(StorageLocation location, ThingDef itemDef) =>
		_pendingAllocations.TryGetValue(location, out var items) && items.TryGetValue(itemDef, out var currentAmount)
			? currentAmount
			: 0;

	/// <summary>
	/// Forces a cleanup of the storage allocation tracker
	/// </summary>
	public void ForceCleanup()
	{
		foreach (var kvp in _pawnAllocations)
		{
			var pawn = kvp.Key;
			if (pawn == null || pawn.Destroyed || !pawn.Spawned)
			{
				CleanupPawnAllocations(pawn);
				Log.Message($"Removed {pawn} from allocations");
			}
		}
	}

	/// <summary>
	/// Gets debug information about the storage allocation tracker
	/// </summary>
	public string GetDebugInfo() => $"StorageAllocationTracker: {_pendingAllocations.Count} pending allocations, {_pawnAllocations.Count} pawn allocations";
}