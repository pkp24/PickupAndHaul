namespace PickUpAndHaul;
internal static class Extensions
{
	public static bool IsModSpecificJob(this JobDef jobDef) => jobDef != null &&
		  (jobDef.defName == nameof(PickUpAndHaulJobDefOf.HaulToInventory) ||
		   jobDef.defName == nameof(PickUpAndHaulJobDefOf.UnloadYourHauledInventory) ||
		   jobDef.driverClass == typeof(JobDriver_HaulToInventory) ||
		   jobDef.driverClass == typeof(JobDriver_UnloadYourHauledInventory));
}
