using HarmonyLib;
using System.Linq;
using System.Reflection;
using PickUpAndHaul.Cache;

namespace PickUpAndHaul;

[StaticConstructorOnStartup]
internal static class HarmonyPatches
{
	static HarmonyPatches()
	{
		var harmony = new Harmony("mehni.rimworld.pickupandhaul.main");
#if DEBUG
		Harmony.DEBUG = true;
#endif

		if (!ModCompatibilityCheck.CombatExtendedIsActive)
		{
			harmony.Patch(original: AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.GetMaxAllowedToPickUp), [typeof(Pawn), typeof(ThingDef)]),
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

		harmony.Patch(original: AccessTools.Method(typeof(ITab_Pawn_Gear), nameof(ITab_Pawn_Gear.DrawThingRow)),
			transpiler: new HarmonyMethod(typeof(HarmonyPatches), nameof(GearTabHighlightTranspiler)));

		harmony.Patch(original: AccessTools.Method(typeof(WorkGiver_Haul), nameof(WorkGiver_Haul.ShouldSkip)),
			prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(SkipCorpses_Prefix)));

		harmony.Patch(AccessTools.Method(typeof(JobGiver_Haul), nameof(JobGiver_Haul.TryGiveJob)),
			transpiler: new(typeof(HarmonyPatches), nameof(JobGiver_Haul_TryGiveJob_Transpiler)));

		// Add patch to intercept RimWorld error logging
		harmony.Patch(original: AccessTools.Method(typeof(Verse.Log), nameof(Verse.Log.Error), [typeof(string)]),
			prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Log_Error_Prefix)));

		// Add patch to register the periodic performance tracker when a game starts
		harmony.Patch(original: AccessTools.Method(typeof(Game), nameof(Game.InitNewGame)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(InitNewGame_PostFix)));

		// Add patch to ensure cache updater is added to maps when they're created
		harmony.Patch(original: AccessTools.Method(typeof(Map), nameof(Map.ConstructComponents)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Map_ConstructComponents_PostFix)));

		// Add patch to initialize caches when a game is loaded
		harmony.Patch(original: AccessTools.Method(typeof(Game), nameof(Game.LoadGame)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(LoadGame_PostFix)));

		// Add patch to prevent null reference exceptions in HaulAIUtility.PawnCanAutomaticallyHaulFast
		harmony.Patch(original: AccessTools.Method(typeof(HaulAIUtility), nameof(HaulAIUtility.PawnCanAutomaticallyHaulFast)),
			prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(PawnCanAutomaticallyHaulFast_Prefix)));

		// Add patch to prevent null reference exceptions in GridsUtility.Fogged
		// There are two overloads of GridsUtility.Fogged; explicitly target the Thing extension overload
		harmony.Patch(original: AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.Fogged), [typeof(Thing)]),
			prefix: new HarmonyMethod(typeof(HarmonyPatches), nameof(GridsUtility_Fogged_Prefix)));

		// Add patch to filter out null things from ListerHaulables.ThingsPotentiallyNeedingHauling
		// Return type is ICollection<Thing>
		harmony.Patch(original: AccessTools.Method(typeof(ListerHaulables), nameof(ListerHaulables.ThingsPotentiallyNeedingHauling)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(ThingsPotentiallyNeedingHauling_Postfix)));

		Log.Message("PickUpAndHaul v1.6.0 welcomes you to RimWorld, thanks for enabling debug logging for pointless logspam.");
	}

	private static bool Drop_Prefix(Pawn pawn, Thing thing)
	{
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

	public static void IdleJoy_Postfix(Pawn pawn) => PawnUnloadChecker.CheckIfPawnShouldUnloadInventory(pawn, true);

	public static void DropUnusedInventory_PostFix(Pawn pawn) => PawnUnloadChecker.CheckIfPawnShouldUnloadInventory(pawn);

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
		=> instructions.MethodReplacer(HaulAIUtility.HaulToStorageJob, HaulToStorageJobByRace);

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
			Verse.Log.Warning("Pick Up And Haul failed to patch ITab_Pawn_Gear.DrawThingRow. This is only used for coloring and totally harmless, but you might wanna know anyway");
		}
	}

	private static Color GetColorForHauled(Pawn pawn, Thing thing)
		=> pawn.GetComp<CompHauledToInventory>()?.GetHashSet().Contains(thing) ?? false
		? Color.Lerp(Color.grey, Color.red, 0.5f)
		: Color.white;

	/// <summary>
	/// Intercept RimWorld error logging to capture all errors in our debug log
	/// </summary>
	private static void Log_Error_Prefix(string text)
	{
		try
		{
			// Get the current stack trace
			var stackTrace = Environment.StackTrace;

			// Check if this is the specific AllowTool compatibility error we're seeing
			if (text.Contains("System.NullReferenceException") && stackTrace.Contains("AllowTool.WorkGiver_HaulUrgently"))
			{
				Log.ModCompatibilityError("AllowTool compatibility issue detected - null Thing passed to GridsUtility.Fogged", "AllowTool");
			}

			Log.InterceptRimWorldError(text, stackTrace);
		}
		catch (Exception ex)
		{
			// Don't let our error interception cause more errors
			Verse.Log.Warning($"Failed to intercept RimWorld error: {ex.Message}");
		}
	}

	/// <summary>
	/// Register the periodic performance tracker when a new game starts
	/// </summary>
	private static void InitNewGame_PostFix(Game __instance)
	{
		try
		{
			// Register the periodic performance tracker when a new game starts
			__instance.components.Add(new Performance.PeriodicPerformanceTracker(__instance));
			Log.Message("PeriodicPerformanceTracker registered for new game");
			
			// Initialize caches for all existing maps
			CacheInitializer.InitializeAllCaches();
		}
		catch (Exception ex)
		{
			Log.Error($"Failed to register PeriodicPerformanceTracker: {ex.Message}");
		}
	}

	/// <summary>
	/// Ensure cache updater is added to maps when they're created
	/// </summary>
	private static void Map_ConstructComponents_PostFix(Map __instance)
	{
		try
		{
			// Ensure the cache updater is added to the map
			CacheUpdaterHelper.EnsureCacheUpdater(__instance);
		}
		catch (Exception ex)
		{
			Log.Error($"Failed to add cache updater to map: {ex.Message}");
		}
	}

	/// <summary>
	/// Initialize caches when a game is loaded
	/// </summary>
	private static void LoadGame_PostFix(Game __instance)
	{
		try
		{
			// Initialize caches for all existing maps when loading a game
			CacheInitializer.InitializeAllCaches();
			Log.Message("PUAH caches initialized for loaded game");
		}
		catch (Exception ex)
		{
			Log.Error($"Failed to initialize PUAH caches: {ex.Message}");
		}
	}

	/// <summary>
	/// Prevent null reference exceptions in HaulAIUtility.PawnCanAutomaticallyHaulFast
	/// This is a safety net to catch any null things that might slip through
	/// </summary>
	private static bool PawnCanAutomaticallyHaulFast_Prefix(Pawn p, Thing t, bool forced, ref bool __result)
	{
		try
		{
			// Check if the thing is null or invalid
			if (t == null || !t.Spawned || t.Destroyed)
			{
				// Suppress noisy error spam – only log in debug mode
				if (Settings.EnableDebugLogging)
				{
					//Log.Message($"Prevented null reference in PawnCanAutomaticallyHaulFast - Thing was null or invalid");
				}
				__result = false;
				return false; // Skip the original method
			}

			// Check if the pawn is null or invalid
			if (p == null || p.Destroyed)
			{
				if (Settings.EnableDebugLogging)
				{
					//Log.Message($"Prevented null reference in PawnCanAutomaticallyHaulFast - Pawn was null or invalid");
				}
				__result = false;
				return false; // Skip the original method
			}

			// Let the original method run
			return true;
		}
		catch (Exception ex)
		{
			if (Settings.EnableDebugLogging)
			{
				Log.Error($"Exception in PawnCanAutomaticallyHaulFast_Prefix: {ex.Message}");
			}
			__result = false;
			return false; // Skip the original method on exception
		}
	}

	/// <summary>
	/// Prevent null reference exceptions in GridsUtility.Fogged
	/// This is the final safety net to catch any null things that reach this method
	/// </summary>
	private static bool GridsUtility_Fogged_Prefix(Thing t, ref bool __result)
	{
		try
		{
			// Check if the thing is null or invalid
			if (t == null || !t.Spawned || t.Destroyed)
			{
				Log.JobError($"Prevented null reference in GridsUtility.Fogged - Thing was null or invalid", null, null, t);
				__result = false;
				return false; // Skip the original method
			}

			// Let the original method run
			return true;
		}
		catch (Exception ex)
		{
			Log.JobError($"Exception in GridsUtility_Fogged_Prefix: {ex.Message}", null, null, t);
			__result = false;
			return false; // Skip the original method on exception
		}
	}

	/// <summary>
	/// Filter out null and invalid things from the haulables list
	/// This ensures that no null things are returned to any mod that uses this method
	/// </summary>
	private static void ThingsPotentiallyNeedingHauling_Postfix(ref ICollection<Thing> __result)
	{
		try
		{
			if (__result == null)
			{
				return;
			}

			// If it's a List<Thing>, remove in-place (avoids allocations and preserves expected type)
			if (__result is List<Thing> list)
			{
				list.RemoveAll(t => t == null || !t.Spawned || t.Destroyed);
				return; // done
			}

			// Fallback: create a new list with only valid entries and assign
			var validThings = __result.Where(t => t != null && t.Spawned && !t.Destroyed).ToList();
			__result = validThings;
		}
		catch (Exception ex)
		{
			Log.JobError($"Exception in ThingsPotentiallyNeedingHauling_Postfix: {ex.Message}", null, null, null);
			// On error, clear the collection to avoid downstream issues
			__result?.Clear();
		}
	}
}