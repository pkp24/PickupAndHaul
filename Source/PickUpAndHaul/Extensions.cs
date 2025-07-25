namespace PickUpAndHaul;

internal static class Extensions
{
	private static readonly object _lockObject = new();
	public static void CheckIfShouldUnloadInventory(this Pawn pawn) =>
		Task.Run(() =>
		{
			try
			{
				Log.Message($"CheckIfShouldUnloadInventory called for pawn {pawn?.Name.ToStringShort}");

				if ((pawn.jobs.curJob != null && pawn.jobs.curJob.def.defName is "UnloadYourHauledInventory")
					|| pawn.jobs.jobQueue.Any(x => x.job.def.defName is "UnloadYourHauledInventory"))
				{
					Log.Message($"Pawn {pawn.Name.ToStringShort} already has unload job, skipping");
					return;
				}

				if (!pawn.IsModStateValidAndActive() || pawn.inventory.innerContainer.Count == 0)
				{
					Log.Message($"Pawn {pawn.Name.ToStringShort} not valid or inventory empty, skipping");
					return;
				}

				lock (_lockObject)
				{
					var job = new Job(PUAHJobDefOf.UnloadYourHauledInventory);
					pawn.inventory.innerContainer.RemoveWhere(x => x == null);

					Log.Message($"Pawn {pawn.Name.ToStringShort} inventory contains {pawn.inventory.innerContainer.Count} items");

					foreach (var thing in pawn.inventory.innerContainer)
					{
						Log.Message($"Processing inventory item: {thing.def.defName} (Stack: {thing.stackCount}) for pawn {pawn.Name.ToStringShort}");
						pawn.FindBestBetterStorageFor(thing, job);
					}

					if (job.targetQueueA.Count == 0)
					{
						Log.Message($"No storage targets found for pawn {pawn.Name.ToStringShort}");
						return;
					}

					Log.Message($"Created unload job with {job.targetQueueA.Count} targets for pawn {pawn.Name.ToStringShort}");
					job.workGiverDef = PUAHWorkGiverDefOf.HaulToInventory;
					pawn.jobs.jobQueue.EnqueueFirst(job);
					return;
				}
			}
			catch (Exception ex)
			{
				Log.Error(ex, $"Error in CheckIfShouldUnloadInventory for pawn {pawn?.Name.ToStringShort}");
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
		try
		{
			Log.Message($"HaulToInventory called for pawn {pawn?.Name.ToStringShort}");

			var job = new Job(PUAHJobDefOf.HaulToInventory);
			pawn.GetClosestAndEnqueue(job);

			if (job.targetQueueA.Count > 0)
			{
				Log.Message($"Created haul job with {job.targetQueueA.Count} targets for pawn {pawn.Name.ToStringShort}");
				return job;
			}
			else
			{
				Log.Message($"No haul targets found for pawn {pawn.Name.ToStringShort}");
				return null;
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, $"Error in HaulToInventory for pawn {pawn?.Name.ToStringShort}");
			return null;
		}
	}

	public static void CheckUrgentHaul(this Pawn pawn) =>
		Task.Run(() =>
		{
			try
			{
				Log.Message($"CheckUrgentHaul called for pawn {pawn?.Name.ToStringShort}");

				if (WorkCache.UrgentCache[pawn.Map].IsEmpty)
				{
					Log.Message($"No urgent items for pawn {pawn.Name.ToStringShort}");
					return;
				}

				var job = new Job(PUAHJobDefOf.HaulToInventory);
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
				Log.Error(ex, $"Error in CheckUrgentHaul for pawn {pawn?.Name.ToStringShort}");
			}
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
		try
		{
			if (pawn == null || pawn.Map == null || pawn.Faction == null || thing == null || job == null)
			{
				Log.Warning($"FindBestBetterStorageFor called with null parameters - Pawn: {pawn != null}, Map: {pawn?.Map != null}, Faction: {pawn?.Faction != null}, Thing: {thing != null}, Job: {job != null}");
				return;
			}

			Log.Message($"Finding storage for {thing.def.defName} (Stack: {thing.stackCount}) for pawn {pawn.Name.ToStringShort}");

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

						Log.Message($"Pawn {pawn.Name.ToStringShort} carrying capacity: {statValue:F2}, processing slot group");

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
				{
					Log.Warning($"No store target found for {thing.def.defName} for pawn {pawn.Name.ToStringShort}");
					return;
				}

				Log.Message($"Found storage target for {thing.def.defName}, count: {count} for pawn {pawn.Name.ToStringShort}");
				job.targetQueueA.Add(thing);
				job.targetQueueB.Add(storeTarget);
				job.countQueue.Add(count);
			}
			else
			{
				Log.Warning($"Could not find storage for {thing.def.defName} for pawn {pawn.Name.ToStringShort}");
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, $"Error in FindBestBetterStorageFor for pawn {pawn?.Name.ToStringShort} and thing {thing?.def.defName}");
		}
	}

	private static void GetUrgentAndEnqueue(this Pawn pawn, Job job, Thing previousThing = null)
	{
		try
		{
			Log.Message($"GetUrgentAndEnqueue called for pawn {pawn?.Name.ToStringShort}");

			job.targetQueueA ??= [];
			job.countQueue ??= [];

			while (!WorkCache.UrgentCache[pawn.Map].IsEmpty)
			{
				if (job.targetQueueA.Count >= 2)
				{
					Log.Message($"Job queue limit reached for pawn {pawn.Name.ToStringShort}");
					break;
				}

				if (!WorkCache.UrgentCache[pawn.Map].TryDequeue(out var candidate) || candidate.IsCorrupted(pawn))
				{
					Log.Message($"Skipping corrupted or null candidate for pawn {pawn.Name.ToStringShort}");
					continue;
				}

				if (previousThing != null && (previousThing.Position - candidate.Position).LengthHorizontalSquared > 49f)
				{
					Log.Message($"Candidate too far from previous thing for pawn {pawn.Name.ToStringShort}");
					continue;
				}

				var count = Math.Min(candidate.stackCount, MassUtility.CountToPickUpUntilOverEncumbered(pawn, candidate));

				Log.Message($"Urgent candidate: {candidate.def.defName} (Stack: {candidate.stackCount}), count to pick up: {count} for pawn {pawn.Name.ToStringShort}");

				if (count <= 0)
				{
					Log.Warning($"Count to pick up is {count} for {candidate.def.defName}, skipping for pawn {pawn.Name.ToStringShort}");
					return;
				}

				job.targetQueueA.Add(candidate);
				job.countQueue.Add(count);
				pawn.GetUrgentAndEnqueue(job);
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, $"Error in GetUrgentAndEnqueue for pawn {pawn?.Name.ToStringShort}");
		}
	}

	private static void GetClosestAndEnqueue(this Pawn pawn, Job job, Thing previousThing = null)
	{
		try
		{
			Log.Message($"GetClosestAndEnqueue called for pawn {pawn?.Name.ToStringShort}");

			job.targetQueueA ??= [];
			job.countQueue ??= [];

			while (!WorkCache.Cache[pawn.Map].IsEmpty)
			{
				if (job.targetQueueA.Count >= 50)
				{
					Log.Message($"Job queue limit reached for pawn {pawn.Name.ToStringShort}");
					break;
				}

				if (!WorkCache.Cache[pawn.Map].TryDequeue(out var candidate) || candidate.IsCorrupted(pawn))
				{
					Log.Message($"Skipping corrupted or null candidate for pawn {pawn.Name.ToStringShort}");
					continue;
				}

				if (previousThing != null && (previousThing.Position - candidate.Position).LengthHorizontalSquared > 144f)
				{
					Log.Message($"Candidate too far from previous thing for pawn {pawn.Name.ToStringShort}");
					continue;
				}

				var count = Math.Min(candidate.stackCount, MassUtility.CountToPickUpUntilOverEncumbered(pawn, candidate));

				Log.Message($"Candidate: {candidate.def.defName} (Stack: {candidate.stackCount}), count to pick up: {count} for pawn {pawn.Name.ToStringShort}");

				if (count <= 0)
				{
					Log.Warning($"Count to pick up is {count} for {candidate.def.defName}, skipping for pawn {pawn.Name.ToStringShort}");
					return;
				}

				job.targetQueueA.Add(candidate);
				job.countQueue.Add(count);
				pawn.GetClosestAndEnqueue(job, candidate);
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, $"Error in GetClosestAndEnqueue for pawn {pawn?.Name.ToStringShort}");
		}
	}
}