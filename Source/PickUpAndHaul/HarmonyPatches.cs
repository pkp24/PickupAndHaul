namespace PickUpAndHaul;
[StaticConstructorOnStartup]
internal static class HarmonyPatches
{
	static HarmonyPatches()
	{
		try
		{
			Log.Message("Initializing HarmonyPatches");

			var harmony = new Harmony("teemo.rimworld.pickupandhaulforked.main");

			if (Settings.EnableDebugLogging)
			{
				Harmony.DEBUG = true;
				Log.Message("Harmony debug mode enabled");
			}

			if (!ModCompatibilityCheck.CombatExtendedIsActive)
			{
				Log.Message("Combat Extended not detected, applying PawnUtility patches");

				harmony.Patch(original: AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.GetMaxAllowedToPickUp), [typeof(Pawn), typeof(ThingDef)]),
					prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(MaxAllowedToPickUpPrefix)));

				harmony.Patch(original: AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.CanPickUp)),
					prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(CanBeMadeToDropStuff)));
			}
			else
			{
				Log.Message("Combat Extended detected, skipping PawnUtility patches");
			}

			Log.Message("Applying JobDriver patches");
			harmony.Patch(original: AccessTools.Method(typeof(JobDriver_HaulToCell), nameof(JobDriver_HaulToCell.MakeNewToils)),
				postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(JobDriver_HaulToCell_PostFix)));

			harmony.Patch(original: AccessTools.Method(typeof(JobDriver_HaulToContainer), nameof(JobDriver_HaulToContainer.MakeNewToils)),
				postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(JobDriver_HaulToContainer_PostFix)));

			Log.Message("Applying JobGiver patches");
			harmony.Patch(original: AccessTools.Method(typeof(JobGiver_Wander), nameof(JobGiver_WanderAnywhere.TryGiveJob)),
				postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Wander_Postfix)));

			harmony.Patch(AccessTools.Method(typeof(JobGiver_Haul), nameof(JobGiver_Haul.TryGiveJob)),
				transpiler: new(typeof(HarmonyPatches), nameof(JobGiver_Haul_TryGiveJob_Transpiler)));

			Log.Message("Applying Game component patches");
			// Add patch to handle GameComponent loading when mod is missing
			harmony.Patch(original: AccessTools.Method(typeof(Game), nameof(Game.ExposeSmallComponents)),
				prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Game_ExposeSmallComponents_Prefix)));

			Log.Message("Applying error interception patches");
			// Add patch to intercept RimWorld error logging
			harmony.Patch(original: AccessTools.Method(typeof(Verse.Log), nameof(Verse.Log.Error), [typeof(string)]),
				prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Log_Error_Prefix)));

			Log.Message("PickUpAndHaul v1.6.0 welcomes you to RimWorld, thanks for enabling debug logging for pointless logspam.");
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error initializing HarmonyPatches");
		}
	}

	private static void JobDriver_HaulToCell_PostFix(JobDriver_HaulToCell __instance)
	{
		try
		{
			Log.Message($"JobDriver_HaulToCell_PostFix called for pawn {__instance.pawn?.Name.ToStringShort}");
			__instance.pawn.CheckUrgentHaul();
			__instance.pawn.CheckIfShouldUnloadInventory();
		}
		catch (Exception ex)
		{
			Log.Error(ex, $"Error in JobDriver_HaulToCell_PostFix for pawn {__instance.pawn?.Name.ToStringShort}");
		}
	}

	private static void JobDriver_HaulToContainer_PostFix(JobDriver_HaulToCell __instance)
	{
		try
		{
			Log.Message($"JobDriver_HaulToContainer_PostFix called for pawn {__instance.pawn?.Name.ToStringShort}");
			__instance.pawn.CheckUrgentHaul();
			__instance.pawn.CheckIfShouldUnloadInventory();
		}
		catch (Exception ex)
		{
			Log.Error(ex, $"Error in JobDriver_HaulToContainer_PostFix for pawn {__instance.pawn?.Name.ToStringShort}");
		}
	}

	public static void Wander_Postfix(Pawn pawn)
	{
		try
		{
			Log.Message($"Wander_Postfix called for pawn {pawn?.Name.ToStringShort}");
			pawn.CheckIfShouldUnloadInventory();
		}
		catch (Exception ex)
		{
			Log.Error(ex, $"Error in Wander_Postfix for pawn {pawn?.Name.ToStringShort}");
		}
	}

	private static bool MaxAllowedToPickUpPrefix(Pawn pawn, ref int __result)
	{
		try
		{
			var isQuestLodger = pawn.IsQuestLodger();
			Log.Message($"MaxAllowedToPickUpPrefix called for pawn {pawn?.Name.ToStringShort} - IsQuestLodger: {isQuestLodger}");

			if (isQuestLodger)
			{
				__result = int.MaxValue;
				return true;
			}

			return false;
		}
		catch (Exception ex)
		{
			Log.Error(ex, $"Error in MaxAllowedToPickUpPrefix for pawn {pawn?.Name.ToStringShort}");
			return false;
		}
	}

	private static bool CanBeMadeToDropStuff(Pawn pawn, ref bool __result)
	{
		try
		{
			var isQuestLodger = pawn.IsQuestLodger();
			Log.Message($"CanBeMadeToDropStuff called for pawn {pawn?.Name.ToStringShort} - IsQuestLodger: {isQuestLodger}");

			__result = !isQuestLodger;
			return false;
		}
		catch (Exception ex)
		{
			Log.Error(ex, $"Error in CanBeMadeToDropStuff for pawn {pawn?.Name.ToStringShort}");
			__result = false;
			return false;
		}
	}

	private static IEnumerable<CodeInstruction> JobGiver_Haul_TryGiveJob_Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		Log.Message("JobGiver_Haul_TryGiveJob_Transpiler called");
		var originalMethod = AccessTools.Method(typeof(HaulAIUtility), nameof(HaulAIUtility.HaulToStorageJob), [typeof(Pawn), typeof(Thing), typeof(bool)]);
		var replacementMethod = AccessTools.Method(typeof(HarmonyPatches), nameof(HaulToStorageJobByRace));

		foreach (var instruction in instructions)
		{
			yield return instruction.Calls(originalMethod) ? new CodeInstruction(OpCodes.Call, replacementMethod) : instruction;
		}
	}

	private static Job HaulToStorageJobByRace(Pawn p, Thing t, bool forced)
	{
		try
		{
			var isAllowedRace = Settings.IsAllowedRace(p.RaceProps);
			Log.Message($"HaulToStorageJobByRace called for pawn {p?.Name.ToStringShort} - IsAllowedRace: {isAllowedRace}");

			if (isAllowedRace)
			{
				var job = p.HaulToInventory();
				Log.Message($"Created HaulToInventory job for pawn {p.Name.ToStringShort}");
				return job;
			}
			else
			{
				Log.Message($"Using vanilla HaulToStorageJob for pawn {p.Name.ToStringShort}");
				return HaulAIUtility.HaulToStorageJob(p, t, forced);
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, $"Error in HaulToStorageJobByRace for pawn {p?.Name.ToStringShort}");
			return HaulAIUtility.HaulToStorageJob(p, t, forced);
		}
	}

	private static void Game_ExposeSmallComponents_Prefix(Game __instance)
	{
		try
		{
			Log.Message($"Game_ExposeSmallComponents_Prefix called");

			if (__instance == null || __instance.Maps == null || __instance.Maps.Count == 0)
			{
				Log.Message("Game instance or maps are null, skipping cache cleanup");
				return;
			}

			Log.Message($"Triggering cache cleanup for {__instance.Maps.Count} maps");
			CacheManager.CheckForGameChanges(__instance.Maps);
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error in Game_ExposeSmallComponents_Prefix");
		}
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