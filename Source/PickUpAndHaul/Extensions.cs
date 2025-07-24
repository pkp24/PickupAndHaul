using PickUpAndHaul.Cache;

namespace PickUpAndHaul;
internal static class Extensions
{
	public static void CheckIfShouldUnloadInventory(this Pawn pawn, bool forced = false)
	{
		var currentTick = Find.TickManager?.TicksGame ?? 0;
		if (currentTick % 360 != 0) // Values below 360 ticks heavily impact performance
			return;

		if (pawn.IsModStateValidAndActive()
			&& pawn.inventory.innerContainer is { } inventoryContainer
			&& inventoryContainer.Count != 0
			&& (forced || (MassUtility.EncumbrancePercent(pawn) >= 0.90f)))
		{
			var job = new Job(PickUpAndHaulJobDefOf.UnloadYourHauledInventory, pawn);
			pawn.inventory.innerContainer.RemoveWhere(x => x == null);
			foreach (var thing in pawn.inventory.innerContainer)
				pawn.FindBestBetterStorageFor(thing, job);

			if (job.targetQueueA.Count == 0)
				return;

			pawn.jobs.jobQueue.EnqueueFirst(job, JobTag.Misc);
			return;
		}
	}

	public static void FindBestBetterStorageFor(this Pawn pawn, Thing thing, Job job)
	{
		if (pawn == null || pawn.Map == null || pawn.Faction == null || thing == null || job == null)
			return;
		job.targetQueueA ??= [];
		job.targetQueueB ??= [];
		job.countQueue ??= [];
		if (StoreUtility.TryFindBestBetterStorageFor(thing, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out var storeCell, out var dest))
		{
			LocalTargetInfo storeTarget = null;
			var count = 99999;
			if (dest is ISlotGroupParent)
			{
				storeTarget = storeCell;
				var slotGroup = pawn.Map.haulDestinationManager.SlotGroupAt(storeCell);
				if (slotGroup != null)
				{
					var num = 0;
					var statValue = pawn.GetStatValue(StatDefOf.CarryingCapacity, true);
					foreach (var cell in slotGroup.CellsList)
					{
						if (StoreUtility.IsGoodStoreCell(cell, pawn.Map, thing, pawn, pawn.Faction))
						{
							var thing2 = pawn.Map.thingGrid.ThingAt(cell, thing.def);
							if (thing2 != null && thing2 != thing)
								num += Math.Max(thing.def.stackLimit - thing2.stackCount, 0);
							else
								num += thing.def.stackLimit;
							if (num >= statValue)
								break;
						}
					}
					count = num;
				}
			}
			else if (dest is Thing container && container.TryGetInnerInteractableThingOwner() != null)
			{
				storeTarget = container;
				count = Math.Min(thing.stackCount, container.TryGetInnerInteractableThingOwner().GetCountCanAccept(thing, true));
			}
			if (storeTarget == null)
				return;
			job.targetQueueA.Add(thing);
			job.targetQueueA.Add(storeTarget);
			job.countQueue.Add(count);
		}
	}

	public static bool IsModStateValidAndActive(this Pawn pawn)
	{
		var modActive = PickupAndHaulSaveLoadLogger.IsModActive();
		var allowedRace = Settings.IsAllowedRace(pawn.RaceProps);
		var playerFaction = pawn.Faction == Faction.OfPlayerSilentFail;
		var notQuestLodger = !pawn.IsQuestLodger();

		var result = modActive && allowedRace && playerFaction && notQuestLodger;

		return result;
	}

	public static Job HaulToInventory(this Pawn pawn, Thing thing)
	{
		if (thing == null)
		{
			Log.Warning($"thing is null for pawn {pawn.Name?.ToStringShort ?? "Unknown"}");
			return null;
		}

		var job = new Job(PickUpAndHaulJobDefOf.HaulToInventory);
		var currentMass = MassUtility.GearAndInventoryMass(pawn);

		// Initialize job target queues
		job.targetQueueA ??= [];
		job.countQueue ??= [];

		// First, check if we can carry the initial thing
		var initialCount = Math.Min(thing.stackCount, thing.CalculateActualCarriableAmount(currentMass, MassUtility.Capacity(pawn)));
		if (initialCount > 0 && thing.Spawned && !thing.Destroyed && thing.Map != null && HaulAIUtility.PawnCanAutomaticallyHaul(pawn, thing, false))
		{
			currentMass += thing.GetStatValue(StatDefOf.Mass) * initialCount;
			job.targetQueueA.Add(thing);
			job.countQueue.Add(initialCount);
		}

		// Then look for additional things to haul
		GetClosestAndEnqueue(thing, pawn, job, currentMass);

		if (job.targetQueueA.Count > 0)
		{
			Log.Message($"Created job with {job.targetQueueA.Count} targets for {pawn.Name?.ToStringShort ?? "Unknown"}");
			return job;
		}
		else
		{
			Log.Warning($"Returning null - no targets added to job for pawn {pawn.Name?.ToStringShort ?? "Unknown"} on thing {thing.LabelShort}");
			return null;
		}
	}

	public static void GetClosestAndEnqueue(this Thing previousThing, Pawn pawn, Job job, float currentMass)
	{
		// Add null checks for job and pawn
		if (job == null)
		{
			Log.Warning($"job is null for pawn {pawn.Name?.ToStringShort ?? "Unknown"}");
			return;
		}

		if (pawn == null)
		{
			Log.Warning($"pawn is null");
			return;
		}

		job.targetQueueA ??= [];
		job.countQueue ??= [];

		// Check if previousThing is null and handle it gracefully
		if (previousThing == null)
		{
			Log.Warning($"previousThing is null for pawn {pawn.Name?.ToStringShort ?? "Unknown"}, skipping distance checks");
			// Add null check for WorkCache.Cache
			if (WorkCache.Cache == null)
			{
				Log.Warning($"WorkCache.Cache is null for pawn {pawn.Name?.ToStringShort ?? "Unknown"} (previousThing null path)");
				return;
			}

			// If previousThing is null, we can't do distance checks, so just add candidates without distance validation
			while (!WorkCache.Cache.IsEmpty)
			{
				if (job.targetQueueA.Count >= 20)
				{
					break;
				}

				if (!WorkCache.Cache.TryDequeue(out var candidate))
				{
					break;
				}

				// Add null check for candidate
				if (candidate == null)
				{
					Log.Warning($"Dequeued candidate is null for pawn {pawn.Name?.ToStringShort ?? "Unknown"} (previousThing null path), skipping");
					continue;
				}

				var count = Math.Min(candidate.stackCount, candidate.CalculateActualCarriableAmount(currentMass, MassUtility.Capacity(pawn)));

				if (count <= 0)
				{
					break;
				}

				// Add null check for candidate's map before calling HaulAIUtility.PawnCanAutomaticallyHaul
				if (!candidate.Spawned || candidate.Destroyed || candidate.Map == null || !HaulAIUtility.PawnCanAutomaticallyHaul(pawn, candidate, false))
				{
					continue;
				}

				currentMass += candidate.GetStatValue(StatDefOf.Mass) * count;

				// Add null checks for collections before adding
				if (job.targetQueueA != null && job.countQueue != null)
				{
					job.targetQueueA.Add(candidate);
					job.countQueue.Add(count);
				}
				else
				{
					Log.Warning($"job collections are null, cannot add {candidate.LabelShort} for pawn {pawn.Name?.ToStringShort ?? "Unknown"}");
				}
			}

			return;
		}

		// Add null check for WorkCache.Cache
		if (WorkCache.Cache == null)
		{
			Log.Warning($"WorkCache.Cache is null for pawn {pawn.Name?.ToStringShort ?? "Unknown"}");
			return;
		}

		// Original logic - no iteration limits
		while (!WorkCache.Cache.IsEmpty)
		{
			if (job.targetQueueA.Count >= 20)
			{
				break;
			}

			if (!WorkCache.Cache.TryDequeue(out var candidate))
			{
				break;
			}

			// Add null check for candidate
			if (candidate == null)
			{
				Log.Warning($"Dequeued candidate is null for pawn {pawn.Name?.ToStringShort ?? "Unknown"}, skipping");
				continue;
			}

			var count = Math.Min(candidate.stackCount, candidate.CalculateActualCarriableAmount(currentMass, MassUtility.Capacity(pawn)));

			if (count <= 0)
			{
				break;
			}

			// Add null check for candidate's map before calling HaulAIUtility.PawnCanAutomaticallyHaul
			if (!candidate.Spawned || candidate.Destroyed || candidate.Map == null || !HaulAIUtility.PawnCanAutomaticallyHaul(pawn, candidate, false))
			{
				continue;
			}

			var thingInSight = (previousThing.Position - candidate.Position).LengthHorizontalSquared <= 49f;

			if (!thingInSight)
			{
				continue;
			}

			currentMass += candidate.GetStatValue(StatDefOf.Mass) * count;

			// Add null checks for collections before adding
			if (job.targetQueueA != null && job.countQueue != null)
			{
				job.targetQueueA.Add(candidate);
				job.countQueue.Add(count);
			}
			else
			{
				Log.Warning($"job collections are null, cannot add {candidate.LabelShort} for pawn {pawn.Name?.ToStringShort ?? "Unknown"}");
			}
		}
	}

	public static int CalculateActualCarriableAmount(this Thing thing, float currentMass, float capacity)
	{
		// Add a small tolerance to prevent issues with rounding errors
		const float capacityTolerance = 0.1f;
		var effectiveCapacity = capacity + capacityTolerance;

		// Add a minimum capacity threshold to prevent micro-hauling when almost full
		const float minimumCapacityThreshold = 0.5f;
		var remainingCapacity = effectiveCapacity - currentMass;

		if (currentMass >= effectiveCapacity || remainingCapacity < minimumCapacityThreshold)
		{
			return 0;
		}

		var thingMass = thing.GetStatValue(StatDefOf.Mass);

		if (thingMass <= 0)
		{
			return thing.stackCount;
		}

		var maxCarriable = (int)Math.Floor(remainingCapacity / thingMass);
		var result = Math.Min(maxCarriable, thing.stackCount);

		return result;
	}

	public static bool IsModSpecificNamespace(this string name)
	{
		if (!nameof(PickUpAndHaul).Contains("PickUpAndHaul", StringComparison.InvariantCultureIgnoreCase))
			throw new MissingFieldException("Current namespace isn't backward compatible. Add previous distinct namespace to list to prevent save game corruption.");

		List<string> puahNamespaces = ["PickUpAndHaul"]; // it's forked PUAH mod so for player safety it should also include non-forked namespaces
		foreach (var puahNamespace in puahNamespaces)
			if (name.Contains(puahNamespace, StringComparison.InvariantCultureIgnoreCase))
				return true;
		return false;
	}

	public static bool IsModSpecificType(this Type type) =>
		type != null && type.Namespace.IsModSpecificNamespace();

	public static void RemoveCorruptedDefs(this ModContentPack content)
	{
		List<string> defNames = ["CompHauledToInventory"]; // it's forked PUAH mod so for player safety it should also include non-forked defs
		foreach (var name in defNames)
		{
			var corruptedDef = content.defs.FirstOrDefault(x => x.defName.Contains(name, StringComparison.InvariantCultureIgnoreCase));
			if (corruptedDef != null)
				content.defs.Remove(corruptedDef);
		}
	}
}