namespace PickUpAndHaul;
internal static class Extensions
{
	private const float MAX_SAFE_CAPACITY = 0.9f; // How much pawns can safely carry

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
}
