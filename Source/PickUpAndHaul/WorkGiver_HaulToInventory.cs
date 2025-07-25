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

			var job = pawn.HaulToInventory();

			if (job != null)
			{
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