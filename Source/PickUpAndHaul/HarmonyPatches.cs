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

		harmony.Patch(original: AccessTools.Method(typeof(JobDriver_HaulToCell), nameof(JobDriver_HaulToCell.MakeNewToils)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(JobDriver_HaulToCell_PostFix)));

		harmony.Patch(original: AccessTools.Method(typeof(JobGiver_DropUnusedInventory), nameof(JobGiver_DropUnusedInventory.TryGiveJob)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(DropUnusedInventory_PostFix)));

		harmony.Patch(original: AccessTools.Method(typeof(JobGiver_Idle), nameof(JobGiver_Idle.TryGiveJob)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(IdleJoy_Postfix)));

		harmony.Patch(original: AccessTools.Method(typeof(WorkGiver_Haul), nameof(WorkGiver_Haul.ShouldSkip)),
			prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(ShouldSkip_Prefix)));

		harmony.Patch(AccessTools.Method(typeof(JobGiver_Haul), nameof(JobGiver_Haul.TryGiveJob)),
			transpiler: new(typeof(HarmonyPatches), nameof(JobGiver_Haul_TryGiveJob_Transpiler)));

		// Add patch to handle missing mod scenarios
		harmony.Patch(original: AccessTools.Method(typeof(Job), nameof(Job.ExposeData)),
			prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Job_ExposeData_Prefix)));

		// Add patch to handle GameComponent loading when mod is missing
		harmony.Patch(original: AccessTools.Method(typeof(Game), nameof(Game.ExposeSmallComponents)),
			prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Game_ExposeSmallComponents_Prefix)));

		// Add patch to intercept RimWorld error logging
		harmony.Patch(original: AccessTools.Method(typeof(Verse.Log), nameof(Verse.Log.Error), [typeof(string)]),
			prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Log_Error_Prefix)));

		Log.Message("PickUpAndHaul v1.6.0 welcomes you to RimWorld, thanks for enabling debug logging for pointless logspam.");
	}

	private static void JobDriver_HaulToCell_PostFix(JobDriver_HaulToCell __instance)
	{
		if (__instance.job.haulMode == HaulMode.ToCellStorage)
			__instance.pawn.CheckIfShouldUnloadInventory(true);
	}

	public static void IdleJoy_Postfix(Pawn pawn) => pawn.CheckIfShouldUnloadInventory(true);
	public static void DropUnusedInventory_PostFix(Pawn pawn) => pawn.CheckIfShouldUnloadInventory();

	/// <summary>
	/// Prevents saving of mod-specific job data that could cause issues when mod is removed
	/// </summary>
	private static bool Job_ExposeData_Prefix(Job __instance)
	{
		// Check if this is a mod-specific job that shouldn't be saved
		if (__instance?.def != null && __instance.def.GetType().IsModSpecificType())
		{
			// Don't save mod-specific job data to prevent corruption when mod is removed
			Log.Warning($"Preventing save of mod-specific job: {__instance.def.defName}");
			return false; // Skip the original method entirely
		}
		return true;
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
	private static bool ShouldSkip_Prefix(WorkGiver_Haul __instance, ref bool __result, Pawn pawn)
	{
		if (__instance is not WorkGiver_HaulCorpses)
			return true;

		__result = true;
		return false;
	}

	private static IEnumerable<CodeInstruction> JobGiver_Haul_TryGiveJob_Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		var originalMethod = AccessTools.Method(typeof(HaulAIUtility), nameof(HaulAIUtility.HaulToStorageJob), [typeof(Pawn), typeof(Thing), typeof(bool)]);
		var replacementMethod = AccessTools.Method(typeof(HarmonyPatches), nameof(HaulToStorageJobByRace));
		foreach (var instruction in instructions)
			yield return instruction.Calls(originalMethod) ? new CodeInstruction(OpCodes.Call, replacementMethod) : instruction;
	}

	private static Job HaulToStorageJobByRace(Pawn p, Thing t, bool forced) =>
		Settings.IsAllowedRace(p.RaceProps)
		? p.HaulToInventory(t)
		: HaulAIUtility.HaulToStorageJob(p, t, forced);

	private static bool Game_ExposeSmallComponents_Prefix(Game __instance)
	{
		// Filter out mod-specific components during save/load to prevent corruption
		if (__instance.components != null)
		{
			for (var i = __instance.components.Count - 1; i >= 0; i--)
			{
				if (__instance.components[i] != null && __instance.components[i].GetType().IsModSpecificType())
				{
					Log.Warning($"Removing mod-specific component during save/load: {__instance.components[i].GetType().Name}");
					__instance.components.RemoveAt(i);
				}
			}
		}
		CacheManager.CheckForGameChanges(__instance.Maps);

		return true;
	}

	/// <summary>
	/// Intercept RimWorld error logging to capture all errors in our debug log
	/// </summary>
	private static void Log_Error_Prefix(string text)
	{
		try
		{
			// Get the current stack trace
			var stackTrace = Environment.StackTrace;
			Log.InterceptRimWorldError(text, stackTrace);
		}
		catch (Exception ex)
		{
			// Don't let our error interception cause more errors
			Verse.Log.Warning($"Failed to intercept RimWorld error: {ex.Message}");
		}
	}
}