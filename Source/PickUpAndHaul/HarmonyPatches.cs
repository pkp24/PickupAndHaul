using PickUpAndHaul.Cache;

namespace PickUpAndHaul;
[StaticConstructorOnStartup]
internal static class HarmonyPatches
{
	static HarmonyPatches()
	{
		var harmony = new Harmony("teemo.rimworld.pickupandhaulforked.main");

		if (Settings.EnableDebugLogging)
			Harmony.DEBUG = true;

		if (!ModCompatibilityCheck.CombatExtendedIsActive)
		{
			harmony.Patch(original: AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.GetMaxAllowedToPickUp), [typeof(Pawn), typeof(ThingDef)]),
				prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(MaxAllowedToPickUpPrefix)));

			harmony.Patch(original: AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.CanPickUp)),
				prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(CanBeMadeToDropStuff)));
		}

		// Add cache management to the main tick
		harmony.Patch(original: AccessTools.Method(typeof(TickManager), nameof(TickManager.TickManagerUpdate)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(CacheManagement_Postfix)));

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
			Log.Warning($"Preventing save of mod-specific job: {__instance.def.defName}");
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
				Log.Warning($"Clearing mod-specific job from pawn {__instance.pawn?.NameShortColored}: {__instance.curJob.def.defName}");
				__instance.EndCurrentJob(JobCondition.InterruptForced, false, false);
			}

			// Check job queue for mod-specific jobs
			var jobQueue = __instance.jobQueue;
			if (jobQueue != null)
			{
				for (var i = jobQueue.Count - 1; i >= 0; i--)
				{
					if (jobQueue[i]?.job?.def != null && IsModSpecificJob(jobQueue[i].job.def))
					{
						Log.Warning($"Removing mod-specific job from queue: {jobQueue[i].job.def.defName}");
						jobQueue.Extract(jobQueue[i].job);
					}
				}
			}

			// Also clear any mod-specific job drivers
			if (__instance.curDriver != null && IsModSpecificJobDriver(__instance.curDriver))
			{
				Log.Warning($"Clearing mod-specific job driver from pawn {__instance.pawn?.NameShortColored}");
				__instance.EndCurrentJob(JobCondition.InterruptForced, false, false);
			}

			// Clear any mod-specific job references in the job itself
			if (__instance.curJob != null)
			{
				var job = __instance.curJob;
				if (job.def != null && IsModSpecificJob(job.def))
				{
					Log.Warning($"Clearing mod-specific job reference: {job.def.defName}");
					__instance.EndCurrentJob(JobCondition.InterruptForced, false, false);
				}
			}
		}
		catch (Exception ex)
		{
			Log.Error($"Pawn_JobTracker_ExposeData_Prefix: {ex.Message}");
		}
		return true;
	}

	/// <summary>
	/// Checks if a job definition is mod-specific
	/// </summary>
	private static bool IsModSpecificJob(JobDef jobDef) => jobDef != null && (jobDef.defName == "HaulToInventory" ||
			   jobDef.defName == "UnloadYourHauledInventory" ||
			   jobDef.driverClass == typeof(JobDriver_HaulToInventory) ||
			   jobDef.driverClass == typeof(JobDriver_UnloadYourHauledInventory));

	/// <summary>
	/// Checks if a job driver is mod-specific
	/// </summary>
	private static bool IsModSpecificJobDriver(JobDriver jobDriver) => jobDriver != null && (jobDriver.GetType() == typeof(JobDriver_HaulToInventory) ||
			   jobDriver.GetType() == typeof(JobDriver_UnloadYourHauledInventory));

	private static void CacheManagement_Postfix()
	{
		try
		{
			// PERFORMANCE OPTIMIZATION: Only run cache management every 60 ticks (1 second at 60 TPS)
			// This reduces the performance impact from 33ms to <1ms per frame
			var currentTick = Find.TickManager?.TicksGame ?? 0;
			if (currentTick % 60 != 0)
				return; // Skip this tick to reduce performance impact

			// Check for map changes and game resets
			CacheManager.CheckForMapChange();
			CacheManager.CheckForGameReset();
		}
		catch (Exception ex)
		{
			Log.Warning($"cache management: {ex.Message}");
		}
	}

	private static bool MaxAllowedToPickUpPrefix(Pawn pawn, ref int __result)
	{
		__result = int.MaxValue;
		return pawn.IsQuestLodger();
	}

	private static bool CanBeMadeToDropStuff(Pawn pawn, ref bool __result)
	{
		__result = !pawn.IsQuestLodger();
		return false;
	}

	[SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Reflection")]
	private static bool SkipCorpses_Prefix(WorkGiver_Haul __instance, ref bool __result, Pawn pawn)
	{
		if (__instance is not WorkGiver_HaulCorpses)
			return true;

		var takenToInventory = pawn.GetComp<CompHauledToInventory>();
		if (takenToInventory.HashSet.Count == 0 && pawn.inventory.GetDirectlyHeldThings().Count != 0)
			foreach (var item in pawn.inventory.GetDirectlyHeldThings())
				takenToInventory.RegisterHauledItem(item);

		__result = true;
		return false;
	}

	/// <summary>
	/// For animal hauling
	/// </summary>
	private static IEnumerable<CodeInstruction> JobGiver_Haul_TryGiveJob_Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		var originalMethod = AccessTools.Method(typeof(HaulAIUtility), nameof(HaulAIUtility.HaulToStorageJob), [typeof(Pawn), typeof(Thing), typeof(bool)]);
		var replacementMethod = AccessTools.Method(typeof(HarmonyPatches), nameof(HaulToStorageJobByRace));
		foreach (var instruction in instructions)
		{
			yield return instruction.Calls(originalMethod) ? new CodeInstruction(OpCodes.Call, replacementMethod) : instruction;
		}
	}

	private static Job HaulToStorageJobByRace(Pawn p, Thing t, bool forced) => Settings.IsAllowedRace(p.RaceProps) ? HaulToInventoryJob(p, t, forced) : HaulAIUtility.HaulToStorageJob(p, t, forced);
	private static Func<Pawn, Thing, bool, Job> HaulToInventoryJob => _haulToInventoryJob ??= new(((WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed("HaulToInventory").Worker).JobOnThing);
	private static Func<Pawn, Thing, bool, Job> _haulToInventoryJob;

	//ITab_Pawn_Gear
	//private void DrawThingRow(ref float y, float width, Thing thing, bool inventory = false)
	private static IEnumerable<CodeInstruction> GearTabHighlightTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase method)
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
		=> pawn.GetComp<CompHauledToInventory>()?.HashSet.Contains(thing) ?? false
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
				for (var i = components.Count - 1; i >= 0; i--)
				{
					var component = components[i];
					if (component != null && IsModSpecificComponent(component))
					{
						Log.Warning($"Removing mod-specific component during save/load: {component.GetType().Name}");
						components.RemoveAt(i);
					}
				}
			}
		}
		catch (Exception ex)
		{
			Log.Error($"Game_ExposeSmallComponents_Prefix: {ex.Message}");
		}
		return true;
	}

	/// <summary>
	/// Checks if a component is mod-specific
	/// </summary>
	private static bool IsModSpecificComponent(GameComponent component)
	{
		if (component == null)
			return false;

		var componentType = component.GetType();
		return componentType == typeof(PickupAndHaulSaveLoadLogger) ||
			   componentType.Namespace?.StartsWith("PickUpAndHaul", StringComparison.InvariantCultureIgnoreCase) == true;
	}
}