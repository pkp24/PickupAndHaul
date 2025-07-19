namespace PickUpAndHaul;
internal static class Extensions
{
	private const float MAX_SAFE_CAPACITY = 0.9f; // How much pawns can safely carry

	public static bool IsOverAllowedGearCapacity(this Pawn pawn)
	{
		var totalMass = MassUtility.GearAndInventoryMass(pawn);
		var capacity = MassUtility.Capacity(pawn);
		var ratio = totalMass / capacity;
		var isOverCapacity = ratio >= MAX_SAFE_CAPACITY;

		if (Settings.EnableDebugLogging && isOverCapacity)
			Log.Message($"IsOverAllowedGearCapacity for {pawn} - mass: {totalMass}, capacity: {capacity}, ratio: {ratio:F2}");

		return isOverCapacity;
	}

	public static bool CapacityAt(this Thing thing, IntVec3 storeCell, Map map, out int capacity)
	{
		capacity = 0;

		if ((map.haulDestinationManager.SlotGroupParentAt(storeCell) as ThingWithComps)?
		   .AllComps.FirstOrDefault(x => x is IHoldMultipleThings)
		   is IHoldMultipleThings compOfHolding)
			return compOfHolding.CapacityAt(thing, storeCell, map, out capacity);

		foreach (var t in storeCell.GetThingList(map))
			if (t is IHoldMultipleThings holderOfMultipleThings)
				return holderOfMultipleThings.CapacityAt(thing, storeCell, map, out capacity);

		return false;
	}

	public static bool StackableAt(this Thing thing, IntVec3 storeCell, Map map)
	{
		if ((map.haulDestinationManager.SlotGroupParentAt(storeCell) as ThingWithComps)?
		   .AllComps.FirstOrDefault(x => x is IHoldMultipleThings)
		   is IHoldMultipleThings compOfHolding)
			return compOfHolding.StackableAt(thing, storeCell, map);

		foreach (var t in storeCell.GetThingList(map))
			if (t is IHoldMultipleThings holderOfMultipleThings)
				return holderOfMultipleThings.StackableAt(thing, storeCell, map);

		return false;
	}

	/// <summary>
	/// Validates job queue integrity to help debug ArgumentOutOfRangeException
	/// </summary>
	public static void ValidateJobQueues(this Job job, Pawn pawn, string context)
	{
		if (job == null)
		{
			Log.Error($"Job is null in {context} for {pawn}");
			return;
		}

		var targetQueueACount = job.targetQueueA?.Count ?? 0;
		var targetQueueBCount = job.targetQueueB?.Count ?? 0;
		var countQueueCount = job.countQueue?.Count ?? 0;

		Log.Message($"[{context}]: {pawn} - targetQueueA: {targetQueueACount}, targetQueueB: {targetQueueBCount}, countQueue: {countQueueCount}");

		// Check for null queues
		if (job.targetQueueA == null)
			Log.Error($"targetQueueA is null in {context} for {pawn}");
		if (job.targetQueueB == null)
			Log.Error($"targetQueueB is null in {context} for {pawn}");
		if (job.countQueue == null)
			Log.Error($"countQueue is null in {context} for {pawn}");

		// Check for queue synchronization issues
		if (targetQueueACount != countQueueCount)
			Log.Error($"Queue synchronization issue in {context} for {pawn} - targetQueueA.Count ({targetQueueACount}) != countQueue.Count ({countQueueCount})");

		// Check for empty targetQueueA (this would cause the ArgumentOutOfRangeException)
		// But only flag as error if it's not during job creation (where empty is expected)
		if (targetQueueACount == 0 && context != "Job Creation")
			Log.Error($"targetQueueA is empty in {context} for {pawn} - this will cause ArgumentOutOfRangeException!");

		// Check for negative or zero counts
		if (job.countQueue != null)
			for (var i = 0; i < job.countQueue.Count; i++)
				if (job.countQueue[i] <= 0)
					Log.Error($"Found negative/zero count {job.countQueue[i]} at index {i} in {context} for {pawn}");

		// Check if any targets in targetQueueA are null or invalid
		if (job.targetQueueA != null && job.countQueue != null)
			for (var i = 0; i < job.targetQueueA.Count; i++)
			{
				var target = job.targetQueueA[i];
				if (target == null || target.Thing == null)
					Log.Error($"Found null target at index {i} in targetQueueA in {context} for {pawn}");
				else if (target.Thing.Destroyed || !target.Thing.Spawned)
					Log.Warning($"Found destroyed/unspawned target {target.Thing} at index {i} in targetQueueA in {context} for {pawn}");
			}
	}
}
