namespace PickUpAndHaul;

internal static class Extensions
{
	private static readonly object _lockObject = new();
	public static void CheckIfShouldUnloadInventory(this Pawn pawn) =>
		Task.Run(() =>
		{
			if ((pawn.jobs.curJob != null && pawn.jobs.curJob.def.defName is "UnloadYourHauledInventory")
				|| pawn.jobs.jobQueue.Any(x => x.job.def.defName is "UnloadYourHauledInventory"))
				return;

			if (!pawn.IsModStateValidAndActive() || pawn.inventory.innerContainer.Count == 0)
				return;
			lock (_lockObject)
			{
				var job = new Job(PUAHJobDefOf.UnloadYourHauledInventory);
				pawn.inventory.innerContainer.RemoveWhere(x => x == null);
				foreach (var thing in pawn.inventory.innerContainer)
					pawn.FindBestBetterStorageFor(thing, job);

				if (job.targetQueueA.Count == 0)
					return;
				job.workGiverDef = PUAHWorkGiverDefOf.HaulToInventory;
				pawn.jobs.jobQueue.EnqueueFirst(job);
				return;
			}
		});

	public static void RemoveCorruptedDefs(this ModContentPack content)
	{
		var corruptedDef = content.defs.FirstOrDefault(x => x.defName == "CompHauledToInventory");
		if (corruptedDef != null)
			content.defs.Remove(corruptedDef);
	}

	public static Job HaulToInventory(this Pawn pawn)
	{
		var job = new Job(PUAHJobDefOf.HaulToInventory);
		pawn.GetClosestAndEnqueue(job);
		return job.targetQueueA.Count > 0 ? job : null;
	}

	public static void CheckUrgentHaul(this Pawn pawn) =>
		Task.Run(() =>
		{
			if (WorkCache.UrgentCache[pawn.Map].IsEmpty)
				return;
			var job = new Job(PUAHJobDefOf.HaulToInventory);
			pawn.GetUrgentAndEnqueue(job);
			if (job.targetQueueA.Count > 0)
				pawn.jobs.jobQueue.EnqueueFirst(job);
		});

	public static bool IsModStateValidAndActive(this Pawn pawn) =>
		PUAHJobDefOf.HaulToInventory != null
		&& PUAHJobDefOf.UnloadYourHauledInventory != null
		&& Settings.IsAllowedRace(pawn.RaceProps)
		&& pawn.Faction == Faction.OfPlayerSilentFail
		&& !pawn.IsQuestLodger();

	public static bool IsUrgent(this Thing thing, Map map) =>
		ModCompatibilityCheck.AllowToolIsActive && map.designationManager.DesignationOn(thing)?.def == PUAHDesignationDefOf.HaulUrgently;

	public static bool IsCorrupted(this Thing thing, Pawn pawn) =>
		thing is null or Corpse || !thing.Spawned || thing.Destroyed || !HaulAIUtility.PawnCanAutomaticallyHaul(pawn, thing, false);

	private static void FindBestBetterStorageFor(this Pawn pawn, Thing thing, Job job)
	{
		if (pawn == null || pawn.Map == null || pawn.Faction == null || thing == null || job == null)
			return;
		job.targetQueueA ??= [];
		job.targetQueueB ??= [];
		job.countQueue ??= [];
		job.workGiverDef = PUAHWorkGiverDefOf.HaulToInventory;
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
			job.targetQueueB.Add(storeTarget);
			job.countQueue.Add(count);
		}
	}

	private static void GetUrgentAndEnqueue(this Pawn pawn, Job job, Thing previousThing = null)
	{
		job.targetQueueA ??= [];
		job.countQueue ??= [];

		while (!WorkCache.UrgentCache[pawn.Map].IsEmpty)
		{
			if (job.targetQueueA.Count >= 2)
				break;
			if (!WorkCache.UrgentCache[pawn.Map].TryDequeue(out var candidate) || candidate.IsCorrupted(pawn))
				continue;
			if (previousThing != null && (previousThing.Position - candidate.Position).LengthHorizontalSquared > 49f)
				continue;
			var count = Math.Min(candidate.stackCount, MassUtility.CountToPickUpUntilOverEncumbered(pawn, candidate));
			if (count <= 0)
				return;

			job.targetQueueA.Add(candidate);
			job.countQueue.Add(count);
			pawn.GetUrgentAndEnqueue(job);
		}
	}

	private static void GetClosestAndEnqueue(this Pawn pawn, Job job, Thing previousThing = null)
	{
		job.targetQueueA ??= [];
		job.countQueue ??= [];

		while (!WorkCache.Cache[pawn.Map].IsEmpty)
		{
			if (job.targetQueueA.Count >= 50)
				break;
			if (!WorkCache.Cache[pawn.Map].TryDequeue(out var candidate) || candidate.IsCorrupted(pawn))
				continue;
			if (previousThing != null && (previousThing.Position - candidate.Position).LengthHorizontalSquared > 144f)
				continue;
			var count = Math.Min(candidate.stackCount, MassUtility.CountToPickUpUntilOverEncumbered(pawn, candidate));
			if (count <= 0)
				return;

			job.targetQueueA.Add(candidate);
			job.countQueue.Add(count);
			pawn.GetClosestAndEnqueue(job, candidate);
		}
	}
}