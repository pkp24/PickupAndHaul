namespace PickUpAndHaul;

internal class CompatHelper
{
	//var ceCompInventory = pawn.GetComp<CombatExtended.CompInventory>();
	//return (ceCompInventory.currentWeight / ceCompInventory.capacityWeight) >= Settings.MaximumOccupiedCapacityToConsiderHauling;
	public static bool CeOverweight(Pawn _) => false;

	//pawn.GetComp<CombatExtended.CompInventory>().CanFitInInventory(thing, out var countToPickUp);
	//return countToPickUp;
	public static int CanFitInInventory(Pawn _, Thing thing) => thing.stackCount;

	internal static void UpdateInventory(Pawn _)
	{
		//pawn.GetComp<CombatExtended.CompInventory>().UpdateInventory();
	}
}