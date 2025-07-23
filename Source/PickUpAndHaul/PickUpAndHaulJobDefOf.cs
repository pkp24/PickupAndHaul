namespace PickUpAndHaul;

[DefOf]
public static class PickUpAndHaulJobDefOf
{
	public static JobDef UnloadYourHauledInventory;
	public static JobDef HaulToInventory;

	static PickUpAndHaulJobDefOf() =>
		DefOfHelper.EnsureInitializedInCtor(typeof(PickUpAndHaulJobDefOf));
}