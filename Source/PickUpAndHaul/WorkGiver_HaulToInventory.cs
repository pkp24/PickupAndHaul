namespace PickUpAndHaul;
public class WorkGiver_HaulToInventory : WorkGiver_HaulGeneral
{
	public override bool ShouldSkip(Pawn pawn, bool forced = false)
	{
		try
		{
			var baseSkip = base.ShouldSkip(pawn, forced);
			var modStateValid = pawn.IsModStateValidAndActive();

			Log.Message($"ShouldSkip called for pawn {pawn?.Name.ToStringShort} - Base skip: {baseSkip}, Mod state valid: {modStateValid}");

			var shouldSkip = baseSkip || !modStateValid;

			if (shouldSkip)
			{
				Log.Message($"Skipping work for pawn {pawn.Name.ToStringShort} - Base skip: {baseSkip}, Mod state valid: {modStateValid}");
			}

			return shouldSkip;
		}
		catch (Exception ex)
		{
			Log.Error(ex, $"Error in ShouldSkip for pawn {pawn?.Name.ToStringShort}");
			return true; // Skip on error to prevent issues
		}
	}

	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
	{
		try
		{
			Log.Message($"PotentialWorkThingsGlobal called for pawn {pawn?.Name.ToStringShort}");
			var workThings = WorkCache.CalculatePotentialWork(pawn);
			Log.Message($"Returning {workThings.Count} potential work things for pawn {pawn.Name.ToStringShort}");
			return workThings;
		}
		catch (Exception ex)
		{
			Log.Error(ex, $"Error in PotentialWorkThingsGlobal for pawn {pawn?.Name.ToStringShort}");
			return [];
		}
	}

	public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
	{
		try
		{
			Log.Message($"HasJobOnThing called for pawn {pawn?.Name.ToStringShort} on thing {t?.def.defName} (Stack: {t?.stackCount})");

			// Validate that we can actually create a job for this thing
			if (t == null || !t.Spawned || t.Destroyed || !HaulAIUtility.PawnCanAutomaticallyHaul(pawn, t, forced))
			{
				Log.Message($"Cannot haul thing {t?.def.defName} for pawn {pawn?.Name.ToStringShort} - Invalid thing or cannot haul");
				return false;
			}

			// Check if pawn can carry this item
			var countToPickUp = MassUtility.CountToPickUpUntilOverEncumbered(pawn, t);
			if (countToPickUp <= 0)
			{
				Log.Message($"Cannot haul thing {t.def.defName} for pawn {pawn?.Name.ToStringShort} - Would be over encumbered");
				return false;
			}

			// Check if the pawn already has a job
			if (pawn.jobs?.curJob != null)
			{
				Log.Message($"Pawn {pawn?.Name.ToStringShort} already has a job, skipping");
				return false;
			}

			// Check if pawn's inventory is full or nearly full
			var carryingCapacity = pawn.GetStatValue(StatDefOf.CarryingCapacity, true);
			var currentMass = MassUtility.GearAndInventoryMass(pawn);
			if (currentMass >= carryingCapacity * 0.9f) // 90% capacity threshold
			{
				Log.Message($"Pawn {pawn?.Name.ToStringShort} inventory is nearly full ({currentMass:F2}/{carryingCapacity:F2}), skipping");
				return false;
			}

			// Check cooldown to prevent infinite loops
			var currentTick = Find.TickManager.TicksGame;
			if (Extensions._lastHaulTick.TryGetValue(pawn, out var lastTick) &&
				currentTick - lastTick < Extensions.HAUL_COOLDOWN_TICKS)
			{
				Log.Message($"Pawn {pawn?.Name.ToStringShort} is in cooldown, skipping");
				return false;
			}

			// Check if there are any valid haul targets in the cache
			if (WorkCache.Cache[pawn.Map].IsEmpty && WorkCache.UrgentCache[pawn.Map].IsEmpty)
			{
				Log.Message($"No haul targets available for pawn {pawn?.Name.ToStringShort}");
				return false;
			}

			// Check if this specific thing is in the work cache
			if (!WorkCache.Cache[pawn.Map].Contains(t) && !WorkCache.UrgentCache[pawn.Map].Contains(t))
			{
				Log.Message($"Thing {t.def.defName} is not in work cache for pawn {pawn?.Name.ToStringShort}");
				return false;
			}

			return true;
		}
		catch (Exception ex)
		{
			Log.Error(ex, $"Error in HasJobOnThing for pawn {pawn?.Name.ToStringShort} and thing {t?.def.defName}");
			return false;
		}
	}

	public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
	{
		try
		{
			Log.Message($"JobOnThing called for pawn {pawn?.Name.ToStringShort} on thing {thing?.def.defName} (Stack: {thing?.stackCount})");

			// Validate the thing parameter
			if (thing == null || !thing.Spawned || thing.Destroyed)
			{
				Log.Message($"Invalid thing {thing?.def.defName} for pawn {pawn?.Name.ToStringShort}");
				return null;
			}

			var job = pawn.HaulToInventory();

			if (job != null)
			{
				// Add the target thing to the job
				job.targetQueueA.Add(thing);
				job.targetQueueB.Add(thing.Position);
				job.countQueue.Add(thing.stackCount);

				// Update cooldown to prevent infinite loops
				Extensions._lastHaulTick[pawn] = Find.TickManager.TicksGame;

				Log.Message($"Created job for pawn {pawn.Name.ToStringShort} with {job.targetQueueA?.Count ?? 0} targets");
			}
			else
			{
				Log.Message($"No job created for pawn {pawn.Name.ToStringShort}");
			}

			return job;
		}
		catch (Exception ex)
		{
			Log.Error(ex, $"Error in JobOnThing for pawn {pawn?.Name.ToStringShort} and thing {thing?.def.defName}");
			return null;
		}
	}
}