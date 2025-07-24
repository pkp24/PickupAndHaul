namespace PickUpAndHaul.Defs;

[DefOf]
public static class PUAHJobDefOf
{
	public static JobDef UnloadYourHauledInventory = DefDatabase<JobDef>.GetNamed("UnloadYourHauledInventory");
	public static JobDef HaulToInventory = DefDatabase<JobDef>.GetNamed("HaulToInventory");
}