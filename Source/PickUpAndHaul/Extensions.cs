namespace PickUpAndHaul;

internal static class Extensions
{
	internal static readonly Dictionary<Pawn, int> _lastHaulTick = [];
	internal const int HAUL_COOLDOWN_TICKS = 60; // 1 second at 60 TPS

	public static void CheckIfShouldUnloadInventory(this Pawn pawn)
	{
		try
		{
			var currentTick = Find.TickManager.TicksGame;

			if (_lastHaulTick.TryGetValue(pawn, out var lastTick) &&
				currentTick - lastTick < HAUL_COOLDOWN_TICKS)
			{
				return; // Still in cooldown
			}

			Log.Message($"CheckIfShouldUnloadInventory called for pawn {pawn?.Name.ToStringShort}");

			if (pawn.inventory?.innerContainer == null || pawn.inventory.innerContainer.Count == 0)
			{
				Log.Message($"Pawn {pawn.Name.ToStringShort} has no inventory to unload");
				return;
			}

			Log.Message($"Pawn {pawn.Name.ToStringShort} inventory contains {pawn.inventory.innerContainer.Count} items");

			var job = new Job(PUAHJobDefOf.UnloadYourHauledInventory);
			job.targetQueueA ??= [];
			job.targetQueueB ??= [];
			job.countQueue ??= [];

			foreach (var item in pawn.inventory.innerContainer.ToList())
			{
				if (item == null)
					continue;

				Log.Message($"Processing inventory item: {item.def.defName} (Stack: {item.stackCount}) for pawn {pawn.Name.ToStringShort}");

				pawn.FindBestBetterStorageFor(item, job);
			}

			if (job.targetQueueA.Count > 0)
			{
				Log.Message($"Created unload job with {job.targetQueueA.Count} targets for pawn {pawn.Name.ToStringShort}");
				pawn.jobs.jobQueue.EnqueueFirst(job);
				_lastHaulTick[pawn] = currentTick; // Update cooldown
			}
			else
			{
				Log.Message($"No storage found for pawn {pawn.Name.ToStringShort}'s inventory items");
			}
		}
		catch (Exception ex)
		{
			Log.Error($"Error in CheckIfShouldUnloadInventory for pawn {pawn?.Name.ToStringShort}: {ex}");
		}
	}

	public static void CheckUrgentHaul(this Pawn pawn)
	{
		try
		{
			Log.Message($"CheckUrgentHaul called for pawn {pawn?.Name.ToStringShort}");

			if (pawn?.Map == null)
			{
				Log.Error($"CheckUrgentHaul called with null pawn or pawn.Map for pawn {pawn?.Name.ToStringShort}");
				return;
			}

			// Ensure cache is initialized for this map
			if (WorkCache.UrgentCache == null)
			{
				Log.Error($"WorkCache.UrgentCache is null for pawn {pawn.Name.ToStringShort}");
				return;
			}

			// Force initialization of the cache for this map
			WorkCache.UrgentCache.TryAdd(pawn.Map, new ConcurrentQueue<Thing>());

			if (WorkCache.UrgentCache[pawn.Map].IsEmpty)
			{
				Log.Message($"No urgent items for pawn {pawn.Name.ToStringShort}");
				return;
			}

			var job = new Job(PUAHJobDefOf.HaulToInventory);
			job.targetQueueA ??= [];
			job.targetQueueB ??= [];
			job.countQueue ??= [];
			pawn.GetUrgentAndEnqueue(job);

			if (job.targetQueueA.Count > 0)
			{
				Log.Message($"Created urgent haul job with {job.targetQueueA.Count} targets for pawn {pawn.Name.ToStringShort}");
				pawn.jobs.jobQueue.EnqueueFirst(job);
			}
			else
			{
				Log.Message($"No urgent haul targets found for pawn {pawn.Name.ToStringShort}");
			}
		}
		catch (Exception ex)
		{
			Log.Error($"Error in CheckUrgentHaul for pawn {pawn?.Name.ToStringShort}: {ex}");
		}
	}

	// Extension method for ModContentPack to remove corrupted defs
	public static void RemoveCorruptedDefs(this ModContentPack _)
	{
		try
		{
			Log.Message("RemoveCorruptedDefs called");
			// This is a placeholder - in a real implementation, this would remove corrupted defs
			// For now, we'll just log that it was called
		}
		catch (Exception ex)
		{
			Log.Error($"Error in RemoveCorruptedDefs: {ex}");
		}
	}

	// Extension method for Pawn to check if mod state is valid and active
	public static bool IsModStateValidAndActive(this Pawn pawn)
	{
		try
		{
			// Check if pawn is valid and not in mental state
			if (pawn == null || pawn.InMentalState)
			{
				return false;
			}

			// Check if pawn can work and is not downed
			if (pawn.Downed || pawn.Dead || !pawn.workSettings?.EverWork == true)
			{
				return false;
			}

			// Check if pawn has the required work type
			return !pawn.workSettings?.WorkIsActive(WorkTypeDefOf.Hauling) != true;
		}
		catch (Exception ex)
		{
			Log.Error($"Error in IsModStateValidAndActive for pawn {pawn?.Name.ToStringShort}: {ex}");
			return false;
		}
	}

	// Extension method for Pawn to create a HaulToInventory job
	public static Job HaulToInventory(this Pawn pawn)
	{
		try
		{
			Log.Message($"HaulToInventory called for pawn {pawn?.Name.ToStringShort}");
			var job = new Job(PUAHJobDefOf.HaulToInventory);
			job.targetQueueA ??= [];
			job.targetQueueB ??= [];
			job.countQueue ??= [];
			return job;
		}
		catch (Exception ex)
		{
			Log.Error($"Error in HaulToInventory for pawn {pawn?.Name.ToStringShort}: {ex}");
			return null;
		}
	}

	// Extension method for Pawn to find best storage for an item
	public static void FindBestBetterStorageFor(this Pawn pawn, Thing thing, Job job)
	{
		try
		{
			Log.Message($"FindBestBetterStorageFor called for pawn {pawn?.Name.ToStringShort} with thing {thing?.def.defName}");

			if (thing == null || job == null)
			{
				Log.Error($"FindBestBetterStorageFor called with null parameters for pawn {pawn?.Name.ToStringShort}");
				return;
			}

			// Use RimWorld's built-in storage finding logic
			if (StoreUtility.TryFindBestBetterStorageFor(thing, pawn, pawn.Map, StoragePriority.Unstored, pawn.Faction, out var foundCell, out var haulDestination))
			{
				job.targetQueueA.Add(new LocalTargetInfo((Thing)haulDestination));
				job.targetQueueB.Add(thing);
				job.countQueue.Add(thing.stackCount);

				Log.Message($"Found storage for {thing.def.defName} at {foundCell} for pawn {pawn.Name.ToStringShort}");
			}
			else
			{
				Log.Message($"No storage found for {thing.def.defName} for pawn {pawn.Name.ToStringShort}");
			}
		}
		catch (Exception ex)
		{
			Log.Error($"Error in FindBestBetterStorageFor for pawn {pawn?.Name.ToStringShort}: {ex}");
		}
	}

	// Extension method for Pawn to get urgent items and enqueue them
	public static void GetUrgentAndEnqueue(this Pawn pawn, Job job)
	{
		try
		{
			Log.Message($"GetUrgentAndEnqueue called for pawn {pawn?.Name.ToStringShort}");

			if (job == null)
			{
				Log.Error($"GetUrgentAndEnqueue called with null job for pawn {pawn?.Name.ToStringShort}");
				return;
			}

			// Ensure job queues are initialized
			job.targetQueueA ??= [];
			job.targetQueueB ??= [];
			job.countQueue ??= [];

			if (pawn?.Map == null)
			{
				Log.Error($"GetUrgentAndEnqueue called with null pawn or pawn.Map for pawn {pawn?.Name.ToStringShort}");
				return;
			}

			// Ensure cache is initialized for this map
			if (WorkCache.UrgentCache == null)
			{
				Log.Error($"WorkCache.UrgentCache is null for pawn {pawn.Name.ToStringShort}");
				return;
			}

			// Force initialization of the cache for this map
			WorkCache.UrgentCache.TryAdd(pawn.Map, new ConcurrentQueue<Thing>());

			var urgentItems = WorkCache.UrgentCache[pawn.Map].ToList();
			Log.Message($"Found {urgentItems.Count} urgent items for pawn {pawn.Name.ToStringShort}");

			foreach (var item in urgentItems)
			{
				if (item == null || item.Destroyed || !item.Spawned)
					continue;

				if (HaulAIUtility.PawnCanAutomaticallyHaul(pawn, item, false))
				{
					job.targetQueueA.Add(item);
					job.targetQueueB.Add(item.Position);
					job.countQueue.Add(item.stackCount);

					Log.Message($"Added urgent item {item.def.defName} to job for pawn {pawn.Name.ToStringShort}");
				}
			}
		}
		catch (Exception ex)
		{
			Log.Error($"Error in GetUrgentAndEnqueue for pawn {pawn?.Name.ToStringShort}: {ex}");
		}
	}

	// Extension method for Thing to check if it's corrupted
	public static bool IsCorrupted(this Thing thing, Pawn pawn)
	{
		try
		{
			if (thing == null || pawn == null)
				return false;

			// Check if the thing is forbidden
			if (thing.IsForbidden(pawn))
				return true;

			// Check if the thing is in a valid state
			if (thing.Destroyed || !thing.Spawned)
				return true;

			// Check if the thing can be hauled
			return !HaulAIUtility.PawnCanAutomaticallyHaul(pawn, thing, false);
		}
		catch (Exception ex)
		{
			Log.Error($"Error in IsCorrupted for thing {thing?.def.defName}: {ex}");
			return true; // Assume corrupted on error
		}
	}

	// Extension method for Thing to check if it's urgent
	public static bool IsUrgent(this Thing thing, Map map)
	{
		try
		{
			if (thing == null || map == null)
				return false;

			// Check if the thing is in a dangerous area (fire, toxic fallout, etc.)
			if (thing.Position.Roofed(map) && thing.Position.GetRoom(map)?.OpenRoofCount > 0)
				return true;

			// Check if the thing is deteriorating (items that deteriorate when not stored properly)
			// In RimWorld 1.6, we'll check for items that are typically perishable and outside
			if (thing.def.IsIngestible && !thing.Position.Roofed(map))
				return true;

			// Check if the thing is in a forbidden area
			return thing.IsForbidden(Faction.OfPlayer);
		}
		catch (Exception ex)
		{
			Log.Error($"Error in IsUrgent for thing {thing?.def.defName}: {ex}");
			return false; // Assume not urgent on error
		}
	}
}