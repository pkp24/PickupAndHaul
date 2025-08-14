using System.Linq;

namespace PickUpAndHaul;

internal static class HaulUtils
{
	public static bool PawnIsUsable(Pawn pawn)
		=> pawn != null && !pawn.Dead && !pawn.Downed;

	public static bool ThingIsValid(Thing thing)
		=> thing != null && thing.Spawned && !thing.Destroyed;

	public static bool CanPawnCarryThing(Pawn pawn, Thing thing)
	{
		if (pawn == null || thing == null) return false;
		var thingMass = thing.GetStatValue(StatDefOf.Mass);
		var maxCarryMass = pawn.GetStatValue(StatDefOf.CarryingCapacity);
		return thingMass <= maxCarryMass;
	}

	public static bool IsTooHeavyForAnyPawn(Map map, Thing thing)
	{
		if (map == null || thing == null) return false;
		foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
		{
			if (!PawnIsUsable(pawn)) continue;
			if (CanPawnCarryThing(pawn, thing)) return false;
		}
		return true;
	}

	public static bool AnyUsablePawnCanReach(Map map, Thing thing)
	{
		if (map == null || thing == null) return false;
		return map.mapPawns.FreeColonistsSpawned.Any(p => PawnIsUsable(p) && p.CanReach(thing, PathEndMode.Touch, Danger.Deadly));
	}
}


