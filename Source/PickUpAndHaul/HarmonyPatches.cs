using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace PickUpAndHaul;
[StaticConstructorOnStartup]
static class HarmonyPatches
{
	static HarmonyPatches()
	{
		var harmony = new Harmony("mehni.rimworld.pickupandhaul.main");
		
		if (Settings.EnableDebugLogging)
		{
			Harmony.DEBUG = true;
		}

		if (!ModCompatibilityCheck.CombatExtendedIsActive)
		{
			harmony.Patch(original: AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.GetMaxAllowedToPickUp), new[] { typeof(Pawn), typeof(ThingDef) }),
				prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(MaxAllowedToPickUpPrefix)));

			harmony.Patch(original: AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.CanPickUp)),
				prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(CanBeMadeToDropStuff)));
		}

		harmony.Patch(original: AccessTools.Method(typeof(JobGiver_DropUnusedInventory), nameof(JobGiver_DropUnusedInventory.TryGiveJob)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(DropUnusedInventory_PostFix)));

		harmony.Patch(original: AccessTools.Method(typeof(JobDriver_HaulToCell), nameof(JobDriver_HaulToCell.MakeNewToils)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(JobDriver_HaulToCell_PostFix)));

		harmony.Patch(original: AccessTools.Method(typeof(Pawn_InventoryTracker), nameof(Pawn_InventoryTracker.Notify_ItemRemoved)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Pawn_InventoryTracker_PostFix)));

		harmony.Patch(original: AccessTools.Method(typeof(JobGiver_DropUnusedInventory), nameof(JobGiver_DropUnusedInventory.Drop)),
			prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Drop_Prefix)));

		harmony.Patch(original: AccessTools.Method(typeof(JobGiver_Idle), nameof(JobGiver_Idle.TryGiveJob)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(IdleJoy_Postfix)));

		// Add performance profiler update to the main tick
		harmony.Patch(original: AccessTools.Method(typeof(TickManager), nameof(TickManager.TickManagerUpdate)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(TickManagerUpdate_Postfix)));

		harmony.Patch(original: AccessTools.Method(typeof(ITab_Pawn_Gear), nameof(ITab_Pawn_Gear.DrawThingRow)),
			transpiler: new HarmonyMethod(typeof(HarmonyPatches), nameof(GearTabHighlightTranspiler)));

		harmony.Patch(original: AccessTools.Method(typeof(WorkGiver_Haul), nameof(WorkGiver_Haul.ShouldSkip)),
			prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(SkipCorpses_Prefix)));

		harmony.Patch(AccessTools.Method(typeof(JobGiver_Haul), nameof(JobGiver_Haul.TryGiveJob)),
			transpiler: new(typeof(HarmonyPatches), nameof(JobGiver_Haul_TryGiveJob_Transpiler)));

		// Add patch to handle missing mod scenarios
		harmony.Patch(original: AccessTools.Method(typeof(Job), nameof(Job.ExposeData)),
			prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Job_ExposeData_Prefix)));

		harmony.Patch(original: AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.ExposeData)),
			prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Pawn_JobTracker_ExposeData_Prefix)));

		// Add patch to handle GameComponent loading when mod is missing
		harmony.Patch(original: AccessTools.Method(typeof(Game), nameof(Game.ExposeSmallComponents)),
			prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Game_ExposeSmallComponents_Prefix)));

		// Add patches to handle save/load events for job suspension
		harmony.Patch(original: AccessTools.Method(typeof(Game), nameof(Game.ExposeData)),
			prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Game_ExposeData_Prefix)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Game_ExposeData_Postfix)));

		// Add patch to handle pawn death and job interruption for storage allocation cleanup
		harmony.Patch(original: AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Pawn_JobTracker_EndCurrentJob_Postfix)));

		// Add patch to handle pawn death for storage allocation cleanup
		harmony.Patch(original: AccessTools.Method(typeof(Pawn), nameof(Pawn.Kill)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Pawn_Kill_Postfix)));



		Log.Message("PickUpAndHaul v1.6.0 welcomes you to RimWorld, thanks for enabling debug logging for pointless logspam.");
	}

	/// <summary>
	/// Prevents saving of mod-specific job data that could cause issues when mod is removed
	/// </summary>
	private static bool Job_ExposeData_Prefix(Job __instance)
	{
		// Check if this is a mod-specific job that shouldn't be saved
		if (__instance?.def != null && IsModSpecificJob(__instance.def))
		{
			// Don't save mod-specific job data to prevent corruption when mod is removed
			                Log.Warning($"[PickUpAndHaul] Preventing save of mod-specific job: {__instance.def.defName}");
			return false; // Skip the original method entirely
		}
		return true;
	}

	/// <summary>
	/// Handles job tracker data to prevent issues with missing mod jobs
	/// </summary>
	private static bool Pawn_JobTracker_ExposeData_Prefix(Pawn_JobTracker __instance)
	{
		try
		{
			// Always clear mod-specific jobs during save/load to prevent corruption
			// Check if current job is mod-specific and should be cleared
			if (__instance.curJob?.def != null && IsModSpecificJob(__instance.curJob.def))
			{
				                Log.Warning($"[PickUpAndHaul] Clearing mod-specific job from pawn {__instance.pawn?.NameShortColored}: {__instance.curJob.def.defName}");
				__instance.EndCurrentJob(JobCondition.InterruptForced, false, false);
			}

			// Check job queue for mod-specific jobs
			var jobQueue = __instance.jobQueue;
			if (jobQueue != null)
			{
				for (int i = jobQueue.Count - 1; i >= 0; i--)
				{
					if (jobQueue[i]?.job?.def != null && IsModSpecificJob(jobQueue[i].job.def))
					{
						                                        Log.Warning($"[PickUpAndHaul] Removing mod-specific job from queue: {jobQueue[i].job.def.defName}");
                        jobQueue.Extract(jobQueue[i].job);
					}
				}
			}

			// Also clear any mod-specific job drivers
			if (__instance.curDriver != null && IsModSpecificJobDriver(__instance.curDriver))
			{
				                Log.Warning($"[PickUpAndHaul] Clearing mod-specific job driver from pawn {__instance.pawn?.NameShortColored}");
				__instance.EndCurrentJob(JobCondition.InterruptForced, false, false);
			}

			// Clear any mod-specific job references in the job itself
			if (__instance.curJob != null)
			{
				var job = __instance.curJob;
				if (job.def != null && IsModSpecificJob(job.def))
				{
					                Log.Warning($"[PickUpAndHaul] Clearing mod-specific job reference: {job.def.defName}");
					__instance.EndCurrentJob(JobCondition.InterruptForced, false, false);
				}
			}
		}
		catch (Exception ex)
		{
			                Log.Error($"[PickUpAndHaul] Error in Pawn_JobTracker_ExposeData_Prefix: {ex.Message}");
		}
		return true;
	}

	/// <summary>
	/// Checks if a job definition is mod-specific
	/// </summary>
	private static bool IsModSpecificJob(JobDef jobDef)
	{
		if (jobDef == null) return false;
		
		return jobDef.defName == "HaulToInventory" || 
			   jobDef.defName == "UnloadYourHauledInventory" ||
			   jobDef.driverClass == typeof(JobDriver_HaulToInventory) ||
			   jobDef.driverClass == typeof(JobDriver_UnloadYourHauledInventory);
	}

	/// <summary>
	/// Checks if a job driver is mod-specific
	/// </summary>
	private static bool IsModSpecificJobDriver(JobDriver jobDriver)
	{
		if (jobDriver == null) return false;
		
		return jobDriver.GetType() == typeof(JobDriver_HaulToInventory) ||
			   jobDriver.GetType() == typeof(JobDriver_UnloadYourHauledInventory);
	}

	private static bool Drop_Prefix(Pawn pawn, Thing thing)
	{
		// Check if save operation is in progress
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			return true; // Allow normal drop behavior during save
		}

		var takenToInventory = pawn.GetComp<CompHauledToInventory>();
		if (takenToInventory == null)
		{
			return true;
		}

		var carriedThing = takenToInventory.GetHashSet();
		return !carriedThing.Contains(thing);
	}

	private static void Pawn_InventoryTracker_PostFix(Pawn_InventoryTracker __instance, Thing item)
	{
		// Check if save operation is in progress
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			return; // Skip inventory tracking during save
		}

		var takenToInventory = __instance.pawn?.GetComp<CompHauledToInventory>();
		if (takenToInventory == null)
		{
			return;
		}

		var carriedThing = takenToInventory.GetHashSet();
		if (carriedThing?.Count > 0)
		{
			carriedThing.Remove(item);
		}
	}

	private static void JobDriver_HaulToCell_PostFix(JobDriver_HaulToCell __instance)
	{
		// Check if save operation is in progress
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			return; // Skip job driver postfix during save
		}

		var pawn = __instance.pawn;
		var takenToInventory = pawn?.GetComp<CompHauledToInventory>();
		if (takenToInventory == null)
		{
			return;
		}

		var carriedThing = takenToInventory.GetHashSet();

		if (__instance.job.haulMode == HaulMode.ToCellStorage
			&& pawn.Faction == Faction.OfPlayerSilentFail
			&& Settings.IsAllowedRace(pawn.RaceProps)
			&& (Settings.AllowCorpses || pawn.carryTracker.CarriedThing is not Corpse)
			&& carriedThing != null
			&& carriedThing.Count != 0) //deliberate hauling job. Should unload.
		{
			PawnUnloadChecker.CheckIfPawnShouldUnloadInventory(pawn, true);
		}
	}

	public static void IdleJoy_Postfix(Pawn pawn) 
	{
		// Check if save operation is in progress
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			return; // Skip idle joy postfix during save
		}
		PawnUnloadChecker.CheckIfPawnShouldUnloadInventory(pawn, true); 
	}

	public static void TickManagerUpdate_Postfix()
	{
		//PerformanceProfiler.Update();
	}

	public static void DropUnusedInventory_PostFix(Pawn pawn) 
	{
		// Check if save operation is in progress
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			return; // Skip drop unused inventory postfix during save
		}
		PawnUnloadChecker.CheckIfPawnShouldUnloadInventory(pawn); 
	}

	public static bool MaxAllowedToPickUpPrefix(Pawn pawn, ref int __result)
	{
		__result = int.MaxValue;
		return pawn.IsQuestLodger();
	}

	public static bool CanBeMadeToDropStuff(Pawn pawn, ref bool __result)
	{
		__result = !pawn.IsQuestLodger();
		return false;
	}

	public static bool SkipCorpses_Prefix(WorkGiver_Haul __instance, ref bool __result, Pawn pawn)
	{
		if (__instance is not WorkGiver_HaulCorpses)
		{
			return true;
		}

		if (Settings.AllowCorpses //Don't use the vanilla HaulCorpses WorkGiver if PUAH is allowed to haul those
			|| pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).Count < 1) //...or if there are no corpses to begin with. Indeed Tynan did not foresee this situation
		{
			__result = true;
			return false;
		}

		return true;
	}

	/// <summary>
	/// For animal hauling
	/// </summary>
	public static IEnumerable<CodeInstruction> JobGiver_Haul_TryGiveJob_Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		var originalMethod = AccessTools.Method(typeof(HaulAIUtility), nameof(HaulAIUtility.HaulToStorageJob), new[] { typeof(Pawn), typeof(Thing), typeof(bool) });
		var replacementMethod = AccessTools.Method(typeof(HarmonyPatches), nameof(HaulToStorageJobByRace));
		foreach (var instruction in instructions)
		{
			if (instruction.Calls(originalMethod))
			{
				yield return new CodeInstruction(OpCodes.Call, replacementMethod);
			}
			else
			{
				yield return instruction;
			}
		}
	}

	public static Job HaulToStorageJobByRace(Pawn p, Thing t, bool forced) => Settings.IsAllowedRace(p.RaceProps) ? HaulToInventoryJob(p, t, forced) : HaulAIUtility.HaulToStorageJob(p, t, forced);
	private static Func<Pawn, Thing, bool, Job> HaulToInventoryJob => _haulToInventoryJob ??= new(((WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed("HaulToInventory").Worker).JobOnThing);
	private static Func<Pawn, Thing, bool, Job> _haulToInventoryJob;

	//ITab_Pawn_Gear
	//private void DrawThingRow(ref float y, float width, Thing thing, bool inventory = false)
	public static IEnumerable<CodeInstruction> GearTabHighlightTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase method)
	{
		var ColorWhite = AccessTools.PropertyGetter(typeof(Color), nameof(Color.white));

		var done = false;
		foreach (var i in instructions)
		{
			//// Color color = flag ? Color.grey : Color.white;
			if (!done && i.Calls(ColorWhite))
			{
				yield return FishTranspiler.This;
				yield return FishTranspiler.CallPropertyGetter(typeof(ITab_Pawn_Gear), nameof(ITab_Pawn_Gear.SelPawnForGear));
				yield return FishTranspiler.Argument(method, "thing");
				yield return FishTranspiler.Call(GetColorForHauled);
				done = true;
			}
			else
			{
				yield return i;
			}
		}

		if (!done)
		{
			Log.Warning("Pick Up And Haul failed to patch ITab_Pawn_Gear.DrawThingRow. This is only used for coloring and totally harmless, but you might wanna know anyway");
		}
	}

	private static Color GetColorForHauled(Pawn pawn, Thing thing)
		=> pawn.GetComp<CompHauledToInventory>()?.GetHashSet().Contains(thing) ?? false
		? Color.Lerp(Color.grey, Color.red, 0.5f)
		: Color.white;

	// Add patch to handle GameComponent loading when mod is missing
	private static bool Game_ExposeSmallComponents_Prefix(Game __instance)
	{
		try
		{
			// Filter out mod-specific components during save/load to prevent corruption
			var components = __instance.components;
			if (components != null)
			{
				for (int i = components.Count - 1; i >= 0; i--)
				{
					var component = components[i];
					if (component != null && IsModSpecificComponent(component))
					{
						                Log.Warning($"[PickUpAndHaul] Removing mod-specific component during save/load: {component.GetType().Name}");
						components.RemoveAt(i);
					}
				}
			}
		}
		catch (Exception ex)
		{
			                Log.Error($"[PickUpAndHaul] Error in Game_ExposeSmallComponents_Prefix: {ex.Message}");
		}
		return true;
	}

	/// <summary>
	/// Checks if a component is mod-specific
	/// </summary>
	private static bool IsModSpecificComponent(GameComponent component)
	{
		if (component == null) return false;
		
		var componentType = component.GetType();
		return componentType == typeof(PickupAndHaulSaveLoadLogger) ||
			   componentType.Namespace?.StartsWith("PickUpAndHaul") == true;
	}

	/// <summary>
	/// Suspends pickup and haul jobs before saving
	/// </summary>
	private static void Game_ExposeData_Prefix(Game __instance)
	{
		try
		{
			// Only suspend jobs during saving, not loading
			if (Scribe.mode == LoadSaveMode.Saving)
			{
				Log.Message("[PickUpAndHaul] Save operation starting - suspending pickup and haul jobs");
				PickupAndHaulSaveLoadLogger.SuspendPickupAndHaulJobs();
			}
		}
		catch (Exception ex)
		{
			Log.Error($"[PickUpAndHaul] Error in Game_ExposeData_Prefix: {ex.Message}");
		}
	}

	/// <summary>
	/// Restores pickup and haul jobs after saving is complete
	/// </summary>
	private static void Game_ExposeData_Postfix(Game __instance)
	{
		try
		{
			// Only restore jobs after saving is complete
			if (Scribe.mode == LoadSaveMode.Saving)
			{
				Log.Message("[PickUpAndHaul] Save operation complete - restoring pickup and haul jobs");
				PickupAndHaulSaveLoadLogger.RestorePickupAndHaulJobs();
			}
		}
		catch (Exception ex)
		{
			Log.Error($"[PickUpAndHaul] Error in Game_ExposeData_Postfix: {ex.Message}");
		}
	}

	/// <summary>
	/// Clean up storage allocations when a job ends
	/// </summary>
	private static void Pawn_JobTracker_EndCurrentJob_Postfix(Pawn_JobTracker __instance, JobCondition condition, bool startNewJob = true, bool canReturnToPool = true)
	{
		// Check if save operation is in progress
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			return; // Skip cleanup during save
		}

        // Clean up storage allocations for the pawn if relevant
        if (__instance.pawn != null && !__instance.pawn.RaceProps.Animal)
        {
                if (StorageAllocationTracker.HasAllocations(__instance.pawn))
                {
                        StorageAllocationTracker.CleanupPawnAllocations(__instance.pawn);
                        Log.Message($"[PickUpAndHaul] DEBUG: Cleaned up storage allocations for {__instance.pawn} after job ended with condition {condition}");
                }
        }
	}

	/// <summary>
	/// Clean up storage allocations when a pawn dies
	/// </summary>
	private static void Pawn_Kill_Postfix(Pawn __instance, DamageInfo? dinfo, Hediff exactCulprit = null)
	{
		// Check if save operation is in progress
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			return; // Skip cleanup during save
		}

        // Clean up storage allocations for the dead pawn if relevant
        if (!__instance.RaceProps.Animal && StorageAllocationTracker.HasAllocations(__instance))
        {
                StorageAllocationTracker.CleanupPawnAllocations(__instance);
                Log.Message($"[PickUpAndHaul] DEBUG: Cleaned up storage allocations for dead pawn {__instance}");
        }
	}
}
