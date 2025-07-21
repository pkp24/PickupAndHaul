using PickUpAndHaul.Cache;

namespace PickUpAndHaul;
internal static class Extensions
{
	private const float MAX_SAFE_CAPACITY = 0.8f; // safe threshold to start unload inventory; otherwise, mod will fallback to core game hauling.

	public static bool IsOverAllowedGearCapacity(this Pawn pawn)
	{
		var totalMass = MassUtility.GearAndInventoryMass(pawn);
		var capacity = MassUtility.Capacity(pawn);
		var ratio = totalMass / capacity;
		var isOverCapacity = ratio >= MAX_SAFE_CAPACITY;

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
		if (pawn.Faction != Faction.OfPlayerSilentFail || !Settings.IsAllowedRace(pawn.RaceProps) || pawn.inventory.GetDirectlyHeldThings().Count == 0)
			return;

		if (pawn.IsOverAllowedGearCapacity() || pawn.InMentalState || pawn.IsInAnyStorage() || pawn.IsActivityDormant())
			pawn.jobs.jobQueue.EnqueueFirst(JobMaker.MakeJob(PickUpAndHaulJobDefOf.UnloadYourHauledInventory, pawn), JobTag.Misc);
		else if (WorkCache.Cache.Count == 0)
			pawn.jobs.jobQueue.EnqueueLast(JobMaker.MakeJob(PickUpAndHaulJobDefOf.UnloadYourHauledInventory, pawn), JobTag.Misc);
	}

	public static bool IsModSpecificJob(this JobDef jobDef) => jobDef != null &&
		  (jobDef.defName == "HaulToInventory" ||
		   jobDef.defName == "UnloadYourHauledInventory" ||
		   jobDef.driverClass == typeof(JobDriver_HaulToInventory) ||
		   jobDef.driverClass == typeof(JobDriver_UnloadYourHauledInventory));
}
