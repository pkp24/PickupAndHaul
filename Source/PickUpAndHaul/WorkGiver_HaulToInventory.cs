using PickUpAndHaul.Cache;

namespace PickUpAndHaul;
public class WorkGiver_HaulToInventory : WorkGiver_HaulGeneral
{
	public override bool ShouldSkip(Pawn pawn, bool forced = false)
	{
		var baseSkip = base.ShouldSkip(pawn, forced);
		var modStateValid = pawn.IsModStateValidAndActive();
		var shouldSkip = baseSkip || !modStateValid;

		return shouldSkip;
	}

	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
	{
		var potentialThings = WorkCache.CalculatePotentialWork(pawn);
		return potentialThings;
	}

	public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
	{
		if (t == null)
		{
			return false;
		}

		// Add null check for pawn
		if (pawn == null)
		{
			Log.Warning($"pawn is null, returning false");
			return false;
		}

		// Check if pawn can carry at least one of this item
		var currentMass = MassUtility.GearAndInventoryMass(pawn);
		var capacity = MassUtility.Capacity(pawn);
		var thingMass = t.GetStatValue(StatDefOf.Mass);

		// If thing has no mass, it can always be carried
		if (thingMass <= 0)
		{
			return true;
		}

		// Add a small tolerance to prevent issues with rounding errors (same as in CalculateActualCarriableAmount)
		const float capacityTolerance = 0.1f;
		var effectiveCapacity = capacity + capacityTolerance;

		// Add a minimum capacity threshold to prevent micro-hauling when almost full
		const float minimumCapacityThreshold = 0.5f;
		var remainingCapacity = effectiveCapacity - currentMass;

		// Check if there's enough capacity to carry at least one AND we have meaningful capacity left
		var canCarryOne = currentMass + thingMass <= effectiveCapacity && remainingCapacity >= minimumCapacityThreshold;

		return canCarryOne;
	}

	public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
	{
		if (thing == null)
		{
			Log.Warning($"thing is null for pawn {pawn.Name?.ToStringShort ?? "Unknown"}");
			return null;
		}

		var job = pawn.HaulToInventory(thing);

		if (job == null)
		{
			Log.Warning($"HaulToInventory returned null for pawn {pawn.Name?.ToStringShort ?? "Unknown"} on thing {thing.LabelShort} (type: {thing.GetType().Name})");
		}

		return job;
	}
}