namespace PickUpAndHaul;
public class WorkGiver_HaulToInventory : WorkGiver_HaulGeneral
{
	public override bool ShouldSkip(Pawn pawn, bool forced = false) => base.ShouldSkip(pawn, forced) || !pawn.IsModStateValidAndActive();
	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => WorkCache.CalculatePotentialWork(pawn);
	public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) => true;
	public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false) => pawn.HaulToInventory();
}