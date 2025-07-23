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

	public static bool IsModStateValidAndActive(this Pawn pawn) =>
		PickupAndHaulSaveLoadLogger.IsModActive()
		&& Settings.IsAllowedRace(pawn.RaceProps)
		&& pawn.Faction == Faction.OfPlayerSilentFail
		&& !pawn.IsQuestLodger();

	public static Job HaulToInventory(this Pawn pawn, Thing thing)
	{
		var job = new Job(PickUpAndHaulJobDefOf.HaulToInventory);
		GetClosestAndEnqueue(thing, pawn, job, MassUtility.GearAndInventoryMass(pawn));
		return job.targetQueueA.Count > 0 ? job : null;
	}

	public static void GetClosestAndEnqueue(this Thing previousThing, Pawn pawn, Job job, float currentMass)
	{
		job.targetQueueA ??= [];
		job.countQueue ??= [];

		while (!WorkCache.Cache.IsEmpty)
		{
			if (job.targetQueueA.Count >= 20)
				break;

			if (!WorkCache.Cache.TryDequeue(out var candidate))
				break;
			var count = Math.Min(candidate.stackCount, candidate.CalculateActualCarriableAmount(currentMass, MassUtility.Capacity(pawn)));
			if (count <= 0)
				break;

			if (!candidate.Spawned || candidate.Destroyed || !HaulAIUtility.PawnCanAutomaticallyHaul(pawn, candidate, false))
				continue;
			var thingInSight = (previousThing.Position - candidate.Position).LengthHorizontalSquared <= 49f;
			if (!thingInSight)
				continue;

			currentMass += candidate.GetStatValue(StatDefOf.Mass) * count;
			job.targetQueueA.Add(candidate);
			job.countQueue.Add(count);
			GetClosestAndEnqueue(candidate, pawn, job, currentMass);
		}
	}

	public static int CalculateActualCarriableAmount(this Thing thing, float currentMass, float capacity)
	{
		if (currentMass >= capacity)
			return 0;

		var thingMass = thing.GetStatValue(StatDefOf.Mass);

		if (thingMass <= 0)
			return thing.stackCount;

		var remainingCapacity = capacity - currentMass;
		var maxCarriable = (int)Math.Floor(remainingCapacity / thingMass);

		return Math.Min(maxCarriable, thing.stackCount);
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
