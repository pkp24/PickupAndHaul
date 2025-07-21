using PickUpAndHaul.Cache;

namespace PickUpAndHaul;
internal static class Extensions
{
	private const float MAX_SAFE_CAPACITY = 0.9f;

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

	public static bool OkThingToHaul(this Thing t, Pawn pawn) => t != null
		&& !MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, t, 1)
		&& t.Spawned
		&& pawn.CanReserve(t)
		&& !t.IsForbidden(pawn)
		&& !t.IsInValidBestStorage();

	public static void UnloadInventory(this Pawn pawn)
	{
		var itemsTakenToInventory = pawn.GetComp<CompHauledToInventory>();
		if (itemsTakenToInventory == null)
			return;

		if (pawn.Faction != Faction.OfPlayerSilentFail
			|| !Settings.IsAllowedRace(pawn.RaceProps)
			|| itemsTakenToInventory.HashSet.Count == 0
			|| pawn.inventory.innerContainer is not { } inventoryContainer
			|| inventoryContainer.Count == 0)
			return;

		var job = JobMaker.MakeJob(PickUpAndHaulJobDefOf.UnloadYourHauledInventory, pawn);
		if (pawn.IsOverAllowedGearCapacity())
			pawn.jobs.jobQueue.EnqueueFirst(job, JobTag.Misc);
		else if (WorkCache.Cache.Count == 0)
			pawn.jobs.jobQueue.EnqueueLast(job, JobTag.Misc);
	}
}
