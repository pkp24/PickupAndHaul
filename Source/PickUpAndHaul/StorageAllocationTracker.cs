using System.Collections.Concurrent;

namespace PickUpAndHaul;

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
	private readonly ConcurrentDictionary<StorageLocation, Dictionary<ThingDef, int>> _pendingAllocations = new();

	/// <summary>
	/// Tracks which pawns have pending allocations to clean up when they die or jobs fail
	/// </summary>
	private readonly ConcurrentDictionary<Pawn, HashSet<StorageLocation>> _pawnAllocations = new();

	static StorageAllocationTracker() =>
		CacheManager.RegisterCache(Instance);

	/// <summary>
	/// Represents a storage location (either a cell or a container thing)
	/// </summary>
	public readonly struct StorageLocation : IEquatable<StorageLocation>
	{
		public readonly IntVec3 Cell;
		public readonly Thing Container;

		public StorageLocation(IntVec3 cell)
		{
			Cell = cell;
			Container = null;
		}

		public StorageLocation(Thing container)
		{
			Cell = IntVec3.Invalid;
			Container = container;
		}

		public bool Equals(StorageLocation other) => Container != null ? Container == other.Container : Cell == other.Cell;

		public override bool Equals(object obj) => obj is StorageLocation other && Equals(other);

		public override int GetHashCode() => Container?.GetHashCode() ?? Cell.GetHashCode();

		public override string ToString() => Container?.ToString() ?? Cell.ToString();
		public static bool operator ==(StorageLocation left, StorageLocation right) => left.Equals(right);

		public static bool operator !=(StorageLocation left, StorageLocation right) => !(left == right);
	}

	/// <summary>
	/// Reserve storage capacity for a pending hauling job (backward compatibility)
	/// </summary>
	public bool ReserveCapacity(StorageLocation location, ThingDef itemDef, int amount, Pawn pawn)
	{
		var actualCapacity = GetActualCapacity(location, itemDef, pawn.Map);
		// Subtract pending allocations
		var pendingAmount = GetPendingAllocation(location, itemDef);
		var availableCapacity = actualCapacity - pendingAmount;
		Log.Message($"[StorageAllocationTracker] Location {location}: actual={actualCapacity}, pending={pendingAmount}, available={availableCapacity}, requested={amount}");

		// Check if we can reserve this amount
		if (availableCapacity < amount)
		{
			Log.Message($"[StorageAllocationTracker] Cannot reserve {amount} of {itemDef} at {location} - insufficient capacity");
			return false;
		}

		// Add to pending allocations
		if (!_pendingAllocations.TryGetValue(location, out var resultLocation))
		{
			resultLocation = [];
			_pendingAllocations.TryAdd(location, resultLocation);
		}
		var oldLocation = resultLocation;
		if (!resultLocation.ContainsKey(itemDef))
			resultLocation[itemDef] = 0;

		resultLocation[itemDef] += amount;
		var isAllocationUpdated = _pendingAllocations.TryUpdate(location, resultLocation, oldLocation);

		if (!isAllocationUpdated)
			return false;
		// Track this allocation for the pawn
		if (!_pawnAllocations.TryGetValue(pawn, out var resultPawn))
		{
			resultPawn = [];
			_pawnAllocations.TryAdd(pawn, resultPawn);

		}
		var oldResultPawn = resultPawn;
		resultPawn.Add(location);

		var isPawnAllocated = _pawnAllocations.TryUpdate(pawn, resultPawn, oldResultPawn);

		Log.Message($"[StorageAllocationTracker] Is reserved: {isPawnAllocated} Reserved {amount} of {itemDef} at {location} for {pawn}");
		return isPawnAllocated;
	}

	/// <summary>
	/// Check if a pawn currently has any pending allocations
	/// </summary>
	public bool HasAllocations(Pawn pawn) =>
		_pawnAllocations.TryGetValue(pawn, out var result) && result.Count > 0;

	/// <summary>
	/// Release storage capacity when a hauling job is completed or cancelled
	/// </summary>
	public void ReleaseCapacity(StorageLocation location, ThingDef itemDef, int amount, Pawn pawn)
	{
		if (_pendingAllocations.TryGetValue(location, out var result) && result.TryGetValue(itemDef, out var value))
		{
			var oldItemDef = result;
			result[itemDef] = Math.Max(0, value - amount);

			// Remove location from pawn's allocations if no more pending allocations
			if (result[itemDef] == 0)
				result.Remove(itemDef);
			if (result.Count == 0)
				_pendingAllocations.TryRemove(location, out _);
			else
				_pendingAllocations.TryUpdate(location, result, oldItemDef);
		}

		// Remove from pawn's allocation tracking
		if (_pawnAllocations.TryGetValue(pawn, out var pawnResult))
		{
			var oldResult = pawnResult;
			pawnResult.Remove(location);
			if (pawnResult.Count == 0)
				_pawnAllocations.TryRemove(pawn, out _);
			else
				_pawnAllocations.TryUpdate(pawn, pawnResult, oldResult);

		}

		Log.Message($"[StorageAllocationTracker] Released {amount} of {itemDef} at {location} for {pawn}");
	}

	/// <summary>
	/// Clean up all allocations for a pawn (when they die or job fails)
	/// </summary>
	public void CleanupPawnAllocations(Pawn pawn)
	{
		if (!_pawnAllocations.TryGetValue(pawn, out var resultPawn))
			return;

		var locations = new List<StorageLocation>(resultPawn);
		foreach (var location in locations)
		{
			if (_pendingAllocations.TryGetValue(location, out var resultLocation))
			{
				var itemDefs = new List<ThingDef>(resultLocation.Keys);
				foreach (var itemDef in itemDefs)
				{
					var amount = resultLocation[itemDef];
					ReleaseCapacity(location, itemDef, amount, pawn);
				}
			}
		}

		Log.Message($"[StorageAllocationTracker] Cleaned up all allocations for {pawn}");
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
				{
					try
					{
						var capacity = thingOwner.GetCountCanAccept(tempThing);
						return capacity;
					}
					catch (Exception ex)
					{
						Log.Error($"[StorageAllocationTracker] Error getting capacity for {itemDef} at container {location.Container}: {ex.Message}");
						// Return safe stack count as fallback
						return GetSafeStackCount(itemDef);
					}
					finally
					{
						// Properly dispose of the temporary thing to prevent memory leaks
						DisposeTempThing(tempThing);
					}
				}
				else
				{
					// Fallback: use safe stack count as approximation
					return GetSafeStackCount(itemDef);
				}
			}
		}
		else if (location.Cell.IsValid)
		{
			// Use the existing CapacityAt method
			var tempThing = CreateTempThing(itemDef);
			if (tempThing != null)
			{
				try
				{
					var capacity = WorkGiver_HaulToInventory.CapacityAt(tempThing, location.Cell, map);
					return capacity;
				}
				catch (Exception ex)
				{
					Log.Error($"[StorageAllocationTracker] Error getting capacity for {itemDef} at cell {location.Cell}: {ex.Message}");
					// Fallback: check if there's already an item at the location and calculate remaining capacity
					var existingThing = map.thingGrid.ThingAt(location.Cell, itemDef);
					return existingThing != null ? GetSafeStackCount(itemDef) - existingThing.stackCount : GetSafeStackCount(itemDef);
				}
				finally
				{
					// Properly dispose of the temporary thing to prevent memory leaks
					DisposeTempThing(tempThing);
				}
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
					Log.Warning($"[StorageAllocationTracker] Could not find suitable stuff for {itemDef}, using stackCount for capacity estimation");
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
			Log.Error($"[StorageAllocationTracker] Error creating temp thing for {itemDef}: {ex.Message}");
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
				tempThing = null;
		}
		catch (Exception ex)
		{
			Log.Error($"[StorageAllocationTracker] Error disposing temp thing {tempThing}: {ex.Message}");
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
			Log.Warning($"[StorageAllocationTracker] Invalid stackLimit ({itemDef.stackLimit}) for {itemDef}, using default of 1");
			return 1;
		}

		// For items that can't be stacked, use 1
		if (itemDef.stackLimit == 1)
		{
			return 1;
		}

		// For items with very large stack limits, cap at a reasonable value to prevent issues
		const int maxReasonableStackLimit = 10000;
		if (itemDef.stackLimit > maxReasonableStackLimit)
		{
			Log.Warning($"[StorageAllocationTracker] Very large stackLimit ({itemDef.stackLimit}) for {itemDef}, capping at {maxReasonableStackLimit}");
			return maxReasonableStackLimit;
		}

		return itemDef.stackLimit;
	}

	/// <summary>
	/// Get the amount of pending allocations for a specific item at a location
	/// </summary>
	private int GetPendingAllocation(StorageLocation location, ThingDef itemDef)
		=> _pendingAllocations.TryGetValue(location, out var result) && result.TryGetValue(itemDef, out var value)
			? value : 0;

	/// <summary>
	/// Forces a cleanup of the storage allocation tracker
	/// </summary>
	public void ForceCleanup()
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
			Log.Message($"[StorageAllocationTracker] Cleaned up allocations for {deadPawns.Count} dead pawns");
	}
}