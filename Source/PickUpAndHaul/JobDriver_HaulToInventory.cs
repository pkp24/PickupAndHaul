namespace PickUpAndHaul;
public class JobDriver_HaulToInventory : JobDriver
{
	private readonly object _lockObject = new();

	public override void ExposeData()
	{
		// Don't save any data for this job driver to prevent save corruption
		// when the mod is removed
		if (Scribe.mode == LoadSaveMode.Saving)
		{
			Log.Message("Skipping save data for HaulToInventory job driver");
			return;
		}

		// Only load data if we're in loading mode and the mod is active
		if (Scribe.mode == LoadSaveMode.LoadingVars)
		{
			Log.Message("Skipping load data for HaulToInventory job driver");
			return;
		}
	}

	public override bool TryMakePreToilReservations(bool errorOnFailed)
	{
		lock (_lockObject)
		{
			// Reserve as many as possible from queues
			if (job.targetQueueA != null && job.targetQueueA.Count > 0)
				pawn.ReserveAsManyAsPossible(job.targetQueueA, job);
			else
			{
				Log.Warning($"targetQueueA is null or empty for {pawn}");
				return false;
			}
			return true;
		}
	}

	//get next, goto, take, check for more. Branches off to "all over the place"
	public override IEnumerable<Toil> MakeNewToils()
	{
		var nextTarget = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.A); //also does count
		yield return nextTarget;
		yield return new Toil() { initAction = pawn.UnloadInventory };
		var gotoThing = Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.ClosestTouch);
		gotoThing.FailOnDespawnedNullOrForbidden(TargetIndex.A);
		yield return gotoThing;
		yield return LoadInventory(pawn);
		yield return Toils_Jump.JumpIf(nextTarget, () => !job.targetQueueA.NullOrEmpty());
	}

	private Toil LoadInventory(Pawn pawn) => new()
	{
		initAction = () =>
		{
			var takenToInventory = pawn.GetComp<CompHauledToInventory>();

			if (takenToInventory == null)
				return;

			var thing = TargetThingA;
			Toils_Haul.ErrorCheckForCarry(pawn, thing);

			//get max we can pick up
			var countToPickUp = Mathf.Min(job.count, MassUtility.CountToPickUpUntilOverEncumbered(pawn, thing));

			if (countToPickUp > 0)
			{
				var splitThing = thing.SplitOff(countToPickUp);
				var shouldMerge = takenToInventory.HashSet.Any(x => x.def == thing.def);
				pawn.inventory.GetDirectlyHeldThings().TryAdd(splitThing, shouldMerge);
				takenToInventory.RegisterHauledItem(splitThing);
			}
		}
	};
}