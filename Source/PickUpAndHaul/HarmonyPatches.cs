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

		harmony.Patch(original: AccessTools.Method(typeof(JobDriver_HaulToContainer), nameof(JobDriver_HaulToContainer.MakeNewToils)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(JobDriver_HaulToContainer_PostFix)));

		harmony.Patch(AccessTools.Method(typeof(JobGiver_Haul), nameof(JobGiver_Haul.TryGiveJob)),
			transpiler: new(typeof(HarmonyPatches), nameof(JobGiver_Haul_TryGiveJob_Transpiler)));

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
		__instance.pawn.CheckUrgentHaul();
		__instance.pawn.CheckIfShouldUnloadInventory();
	}

	private static void JobDriver_HaulToContainer_PostFix(JobDriver_HaulToCell __instance)
	{
		__instance.pawn.CheckUrgentHaul();
		__instance.pawn.CheckIfShouldUnloadInventory();
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

	private static IEnumerable<CodeInstruction> JobGiver_Haul_TryGiveJob_Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		var originalMethod = AccessTools.Method(typeof(HaulAIUtility), nameof(HaulAIUtility.HaulToStorageJob), [typeof(Pawn), typeof(Thing), typeof(bool)]);
		var replacementMethod = AccessTools.Method(typeof(HarmonyPatches), nameof(HaulToStorageJobByRace));
		foreach (var instruction in instructions)
			yield return instruction.Calls(originalMethod) ? new CodeInstruction(OpCodes.Call, replacementMethod) : instruction;
	}

	private static Job HaulToStorageJobByRace(Pawn p, Thing t, bool forced) =>
		Settings.IsAllowedRace(p.RaceProps)
		? p.HaulToInventory()
		: HaulAIUtility.HaulToStorageJob(p, t, forced);

	private static void Game_ExposeSmallComponents_Prefix(Game __instance)
	{
		if (__instance == null || __instance.Maps == null || __instance.Maps.Count == 0)
			return;
		CacheManager.CheckForGameChanges(__instance.Maps);
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