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
	private readonly Dictionary<StorageLocation, Dictionary<ThingDef, int>> _pendingAllocations = [];

	/// <summary>
	/// Tracks which pawns have pending allocations to clean up when they die or jobs fail
	/// </summary>
	private readonly Dictionary<Pawn, HashSet<StorageLocation>> _pawnAllocations = [];

	/// <summary>
	/// Lock object for thread safety
	/// </summary>
	private readonly object _lockObject = new();

	static StorageAllocationTracker() =>
		// Register with cache manager for automatic cleanup
		CacheManager.RegisterCache(Instance);

	/// <summary>
	/// Check if there's enough available capacity at a storage location for a given item
	/// </summary>
	private bool HasAvailableCapacity(StorageLocation location, ThingDef itemDef, int requestedAmount, Map map)
	{
		lock (_lockObject)
		{
			// Get actual storage capacity
			var actualCapacity = GetActualCapacity(location, itemDef, map);

			// Subtract pending allocations
			var pendingAmount = GetPendingAllocation(location, itemDef);

			var availableCapacity = actualCapacity - pendingAmount;

			Log.Message($"Location {location}: actual={actualCapacity}, pending={pendingAmount}, available={availableCapacity}, requested={requestedAmount}");

			return availableCapacity >= requestedAmount;
		}
	}

	/// <summary>
	/// Reserve storage capacity for a pending hauling job (backward compatibility)
	/// </summary>
	public bool ReserveCapacity(StorageLocation location, ThingDef itemDef, int amount, Pawn pawn)
	{
		lock (_lockObject)
		{
			// Check if we can reserve this amount
			if (!HasAvailableCapacity(location, itemDef, amount, pawn.Map))
			{
				Log.Message($"Cannot reserve {amount} of {itemDef} at {location} - insufficient capacity");
				return false;
			}

			// Add to pending allocations
			if (!_pendingAllocations.ContainsKey(location))
				_pendingAllocations[location] = [];

			if (!_pendingAllocations[location].ContainsKey(itemDef))
				_pendingAllocations[location][itemDef] = 0;

			_pendingAllocations[location][itemDef] += amount;

			// Track this allocation for the pawn
			if (!_pawnAllocations.ContainsKey(pawn))
				_pawnAllocations[pawn] = [];
			_pawnAllocations[pawn].Add(location);

			Log.Message($"Reserved {amount} of {itemDef} at {location} for {pawn}");
			return true;
		}
	}

	/// <summary>
	/// Check if a pawn currently has any pending allocations
	/// </summary>
	public bool HasAllocations(Pawn pawn)
	{
		lock (_lockObject)
			return _pawnAllocations.ContainsKey(pawn) && _pawnAllocations[pawn].Count > 0;
	}

	/// <summary>
	/// Release storage capacity when a hauling job is completed or cancelled
	/// </summary>
	public void ReleaseCapacity(StorageLocation location, ThingDef itemDef, int amount, Pawn pawn)
	{
		lock (_lockObject)
		{
			if (_pendingAllocations.ContainsKey(location) && _pendingAllocations[location].ContainsKey(itemDef))
			{
				_pendingAllocations[location][itemDef] = Math.Max(0, _pendingAllocations[location][itemDef] - amount);

				// Remove location from pawn's allocations if no more pending allocations
				if (_pendingAllocations[location][itemDef] == 0)
					_pendingAllocations[location].Remove(itemDef);

				if (_pendingAllocations[location].Count == 0)
					_pendingAllocations.Remove(location);
			}

			// Remove from pawn's allocation tracking
			if (_pawnAllocations.ContainsKey(pawn))
			{
				_pawnAllocations[pawn].Remove(location);
				if (_pawnAllocations[pawn].Count == 0)
					_pawnAllocations.Remove(pawn);
			}

			Log.Message($"Released {amount} of {itemDef} at {location} for {pawn}");
		}
	}

	/// <summary>
	/// Clean up all allocations for a pawn (when they die or job fails)
	/// </summary>
	public void CleanupPawnAllocations(Pawn pawn)
	{
		lock (_lockObject)
		{
			if (!_pawnAllocations.ContainsKey(pawn))
				return;

			var locations = new List<StorageLocation>(_pawnAllocations[pawn]);
			foreach (var location in locations)
				if (_pendingAllocations.ContainsKey(location))
				{
					var itemDefs = new List<ThingDef>(_pendingAllocations[location].Keys);
					foreach (var itemDef in itemDefs)
					{
						var amount = _pendingAllocations[location][itemDef];
						ReleaseCapacity(location, itemDef, amount, pawn);
					}
				}

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
	private int GetPendingAllocation(StorageLocation location, ThingDef itemDef) => _pendingAllocations.ContainsKey(location) && _pendingAllocations[location].ContainsKey(itemDef)
			? _pendingAllocations[location][itemDef]
			: 0;

	/// <summary>
	/// Forces a cleanup of the storage allocation tracker
	/// </summary>
	public void ForceCleanup()
	{
		lock (_lockObject)
		{
			// Clean up allocations for dead pawns
			var deadPawns = new List<Pawn>();

			foreach (var kvp in _pawnAllocations)
			{
				var pawn = kvp.Key;
				if (pawn == null || pawn.Destroyed || !pawn.Spawned)
					deadPawns.Add(pawn);
			}

			foreach (var deadPawn in deadPawns)
				CleanupPawnAllocations(deadPawn);

			if (deadPawns.Count > 0 && Settings.EnableDebugLogging)
				Log.Message($"Cleaned up allocations for {deadPawns.Count} dead pawns");
		}
	}

	/// <summary>
	/// Gets debug information about the storage allocation tracker
	/// </summary>
	public string GetDebugInfo()
	{
		lock (_lockObject)
			return $"StorageAllocationTracker: {_pendingAllocations.Count} pending allocations, {_pawnAllocations.Count} pawn allocations";
	}
}