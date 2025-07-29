using HarmonyLib;
using System.Linq;
using System.Reflection;
using PickUpAndHaul.Cache;
using RimWorld;
using System.IO;

namespace PickUpAndHaul;

[StaticConstructorOnStartup]
internal static class HarmonyPatches
{
	private static bool _isInErrorInterception = false;

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

		// Apply reservation system patches
		harmony.PatchAll(typeof(HarmonyPatches_ReservationSystem).Assembly);

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

		// Add patch to update cache when things are spawned
		harmony.Patch(original: AccessTools.Method(typeof(ListerHaulables), nameof(ListerHaulables.Notify_Spawned)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(ListerHaulables_Notify_Spawned_Postfix)));

		// Add patch to update cache when things are despawned
		harmony.Patch(original: AccessTools.Method(typeof(ListerHaulables), nameof(ListerHaulables.Notify_DeSpawned)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(ListerHaulables_Notify_DeSpawned_Postfix)));

		// Add patch to update cache when haul designations are added
		harmony.Patch(original: AccessTools.Method(typeof(ListerHaulables), nameof(ListerHaulables.HaulDesignationAdded)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(ListerHaulables_HaulDesignationAdded_Postfix)));

		// Add patch to update cache when haul designations are removed
		harmony.Patch(original: AccessTools.Method(typeof(ListerHaulables), nameof(ListerHaulables.HaulDesignationRemoved)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(ListerHaulables_HaulDesignationRemoved_Postfix)));

		// Add patch to remove things from cache when they are despawned
		harmony.Patch(original: AccessTools.Method(typeof(Thing), nameof(Thing.DeSpawn)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Thing_DeSpawn_Postfix)));

		// Add patch to reclassify items when pawns are spawned (affects what's too heavy)
		harmony.Patch(original: AccessTools.Method(typeof(Pawn), nameof(Pawn.SpawnSetup)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Pawn_SpawnSetup_Postfix)));

		// Add patch to reclassify items when pawns are despawned (affects what's too heavy)
		harmony.Patch(original: AccessTools.Method(typeof(Pawn), nameof(Pawn.DeSpawn)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Pawn_DeSpawn_Postfix)));

		// Add patch to clean up cache when a map is destroyed
		harmony.Patch(original: AccessTools.Method(typeof(Map), nameof(Map.ExposeData)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Map_ExposeData_Postfix)));

		// Add patch to invalidate storage location cache when storage priorities change
		harmony.Patch(original: AccessTools.PropertySetter(typeof(StorageSettings), nameof(StorageSettings.Priority)),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(StorageSettings_Priority_Postfix)));

		// Add patch to log when a pawn is undrafted
		harmony.Patch(original: AccessTools.Method(typeof(Pawn_DraftController), "set_Drafted", new[] { typeof(bool) }),
			postfix: new HarmonyMethod(typeof(HarmonyPatches), nameof(Pawn_DraftController_SetDrafted_Postfix)));

		// Only show the welcome message if debug logging is enabled
		if (Settings.EnableDebugLogging)
		{
			Log.Message("PickUpAndHaul v1.6.0 welcomes you to RimWorld, debug logging is enabled.");
		}
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
		// If this thread is already handling an error, abort to avoid recursion
		if (_isInErrorInterception)
		{
			return;
		}
		try
		{
			_isInErrorInterception = true;
			var stackTrace = Environment.StackTrace;
			// Detect known AllowTool compatibility issue and emit warning
			if (text != null && stackTrace != null && text.Contains("System.NullReferenceException") && stackTrace.Contains("AllowTool.WorkGiver_HaulUrgently"))
			{
				Verse.Log.Warning("AllowTool compatibility issue detected - null Thing passed to GridsUtility.Fogged");
			}
			// Forward intercepted error to the mod's debug logger
			Log.InterceptRimWorldError(text, stackTrace);
		}
		catch (Exception ex)
		{
			// Ensure we never crash due to error interception
			Verse.Log.Warning($"Failed to intercept RimWorld error: {ex.Message}");
		}
		finally
		{
			_isInErrorInterception = false;
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
		
		// Initialize PUAH reservation manager for all maps
		foreach (var map in Find.Maps)
		{
			PUAHReservationManager.InitializeMap(map);
		}
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

	/// <summary>
	/// Update cache when a thing is spawned
	/// </summary>
	private static void ListerHaulables_Notify_Spawned_Postfix(ListerHaulables __instance, Thing t)
	{
		try
		{
			if (t?.Map == null || t.Destroyed) return;

			// Check if the thing is too heavy for any pawn
			bool isTooHeavy = true;
			foreach (var pawn in t.Map.mapPawns.FreeColonistsSpawned)
			{
				if (pawn == null || pawn.Dead || pawn.Downed) continue;

				if (CanPawnCarryThing(pawn, t))
				{
					isTooHeavy = false;
					break;
				}
			}

			// Add to appropriate cache
			if (isTooHeavy)
			{
				PUAHHaulCaches.AddToTooHeavyCache(t.Map, t);
			}
			else
			{
				PUAHHaulCaches.AddToHaulableCache(t.Map, t);
			}
		}
		catch (Exception ex)
		{
			if (Settings.EnableDebugLogging)
			{
				Log.Error($"Exception in ListerHaulables_Notify_Spawned_Postfix: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// Update cache when a thing is despawned
	/// </summary>
	private static void ListerHaulables_Notify_DeSpawned_Postfix(ListerHaulables __instance, Thing t)
	{
		try
		{
			if (t?.Map == null) return;

			// Remove from all caches
			PUAHHaulCaches.RemoveFromHaulableCache(t.Map, t);
			PUAHHaulCaches.RemoveFromTooHeavyCache(t.Map, t);
			PUAHHaulCaches.RemoveFromUnreachableCache(t.Map, t);
			PUAHHaulCaches.RemoveFromStorageLocationCache(t.Map, t);
		}
		catch (Exception ex)
		{
			if (Settings.EnableDebugLogging)
			{
				Log.Error($"Exception in ListerHaulables_Notify_DeSpawned_Postfix: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// Update cache when a haul designation is added
	/// </summary>
	private static void ListerHaulables_HaulDesignationAdded_Postfix(ListerHaulables __instance, Thing t)
	{
		try
		{
			if (t?.Map == null || t.Destroyed) return;

			// Check if the thing is too heavy for any pawn
			bool isTooHeavy = true;
			foreach (var pawn in t.Map.mapPawns.FreeColonistsSpawned)
			{
				if (pawn == null || pawn.Dead || pawn.Downed) continue;

				if (CanPawnCarryThing(pawn, t))
				{
					isTooHeavy = false;
					break;
				}
			}

			// Add to appropriate cache
			if (isTooHeavy)
			{
				PUAHHaulCaches.AddToTooHeavyCache(t.Map, t);
			}
			else
			{
				PUAHHaulCaches.AddToHaulableCache(t.Map, t);
			}
		}
		catch (Exception ex)
		{
			if (Settings.EnableDebugLogging)
			{
				Log.Error($"Exception in ListerHaulables_HaulDesignationAdded_Postfix: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// Update cache when a haul designation is removed
	/// </summary>
	private static void ListerHaulables_HaulDesignationRemoved_Postfix(ListerHaulables __instance, Thing t)
	{
		try
		{
			if (t?.Map == null) return;

			// Remove from all caches
			PUAHHaulCaches.RemoveFromHaulableCache(t.Map, t);
			PUAHHaulCaches.RemoveFromTooHeavyCache(t.Map, t);
			PUAHHaulCaches.RemoveFromUnreachableCache(t.Map, t);
			PUAHHaulCaches.RemoveFromStorageLocationCache(t.Map, t);
		}
		catch (Exception ex)
		{
			if (Settings.EnableDebugLogging)
			{
				Log.Error($"Exception in ListerHaulables_HaulDesignationRemoved_Postfix: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// Remove thing from cache when it is despawned
	/// </summary>
	private static void Thing_DeSpawn_Postfix(Thing __instance)
	{
		try
		{
			if (__instance?.Map == null) return;

			// Remove from all caches
			PUAHHaulCaches.RemoveFromHaulableCache(__instance.Map, __instance);
			PUAHHaulCaches.RemoveFromTooHeavyCache(__instance.Map, __instance);
			PUAHHaulCaches.RemoveFromUnreachableCache(__instance.Map, __instance);
			PUAHHaulCaches.RemoveFromStorageLocationCache(__instance.Map, __instance);
		}
		catch (Exception ex)
		{
			if (Settings.EnableDebugLogging)
			{
				Log.Error($"Exception in Thing_DeSpawn_Postfix: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// Reclassify items when a pawn is spawned (affects what's too heavy)
	/// </summary>
	private static void Pawn_SpawnSetup_Postfix(Pawn __instance, Map map)
	{
		try
		{
			if (map == null || __instance == null || __instance.Dead || __instance.Downed) return;

			// Only reclassify if this is a free colonist (player-controlled)
			if (__instance.Faction != Faction.OfPlayerSilentFail) return;

			// Reclassify items between haulable and too heavy caches
			CacheManager.ReclassifyTooHeavyItems(map);
			CacheManager.ReclassifyHaulableItems(map);
		}
		catch (Exception ex)
		{
			if (Settings.EnableDebugLogging)
			{
				Log.Error($"Exception in Pawn_SpawnSetup_Postfix: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// Reclassify items when a pawn is despawned (affects what's too heavy)
	/// </summary>
	private static void Pawn_DeSpawn_Postfix(Pawn __instance)
	{
		try
		{
			if (__instance?.Map == null) return;

			// Only reclassify if this was a free colonist (player-controlled)
			if (__instance.Faction != Faction.OfPlayerSilentFail) return;

			// Reclassify items between haulable and too heavy caches
			CacheManager.ReclassifyTooHeavyItems(__instance.Map);
			CacheManager.ReclassifyHaulableItems(__instance.Map);
		}
		catch (Exception ex)
		{
			if (Settings.EnableDebugLogging)
			{
				Log.Error($"Exception in Pawn_DeSpawn_Postfix: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// Clean up cache when a map is being destroyed
	/// </summary>
	private static void Map_ExposeData_Postfix(Map __instance)
	{
		try
		{
			if (__instance == null) return;

			// Check if the map is being destroyed (when Scribe.mode is Writing and the map is no longer in Current.Game.Maps)
			if (Scribe.mode == LoadSaveMode.Saving && Current.Game?.Maps != null && !Current.Game.Maps.Contains(__instance))
			{
				// Clear all caches for this map
				PUAHHaulCaches.ClearMapCaches(__instance);
				
				// Remove cache updater from the map
				CacheUpdaterHelper.RemoveCacheUpdater(__instance);
			}
		}
		catch (Exception ex)
		{
			if (Settings.EnableDebugLogging)
			{
				Log.Error($"Exception in Map_ExposeData_Postfix: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// Invalidate storage location cache when storage priorities change
	/// </summary>
	private static void StorageSettings_Priority_Postfix(StorageSettings __instance)
	{
		try
		{
			// Find the map this storage setting belongs to
			Map map = null;
			
			// Try to find the map through the parent thing
			if (__instance.owner is Thing thing && thing.Map != null)
			{
				map = thing.Map;
			}
			// Try to find the map through slot groups
			else if (__instance.owner is ISlotGroupParent slotGroupParent)
			{
				// Find a slot group that uses this storage setting
				var slotGroup = slotGroupParent.GetSlotGroup();
				if (slotGroup != null && slotGroup.Map != null)
				{
					map = slotGroup.Map;
				}
			}

			if (map != null)
			{
				// Invalidate all storage location cache entries for this map
				CacheManager.InvalidateAllStorageLocationCache(map);
				
				if (Settings.EnableDebugLogging)
				{
					Log.Message($"Invalidated storage location cache for map {map.uniqueID} due to storage priority change");
				}
			}
		}
		catch (Exception ex)
		{
			if (Settings.EnableDebugLogging)
			{
				Log.Error($"Exception in StorageSettings_Priority_Postfix: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// Check if a pawn can carry a specific thing
	/// </summary>
	private static bool CanPawnCarryThing(Pawn pawn, Thing thing)
	{
		if (pawn == null || thing == null) return false;

		// Check if the thing is too heavy for the pawn
		float thingMass = thing.GetStatValue(StatDefOf.Mass);
		float maxCarryMass = pawn.GetStatValue(StatDefOf.CarryingCapacity);

		return thingMass <= maxCarryMass;
	}

	private static void Pawn_DraftController_SetDrafted_Postfix(Pawn_DraftController __instance, bool value)
	{
		if (!value && __instance.pawn != null)
		{
			Log.Message($"{__instance.pawn} was undrafted. Current job: {__instance.pawn.jobs?.curJob?.def?.defName ?? "null"}, Downed: {__instance.pawn.Downed}, Dead: {__instance.pawn.Dead}, Position: {__instance.pawn.Position}");
		}
	}
}

// Additional patch to clean up PUAH reservations when jobs end
[HarmonyPatch(typeof(Pawn_JobTracker), "EndCurrentJob")]
public static class Pawn_JobTracker_EndCurrentJob_Patch
{
	public static void Prefix(Pawn_JobTracker __instance, JobCondition condition)
	{
		var pawn = __instance.pawn;
		var job = __instance.curJob;
		
		// If this was a PUAH job, clean up our custom reservations
		// Using Prefix to ensure job is still available
		if (job?.def == PickUpAndHaulJobDefOf.HaulToInventory || job?.def == PickUpAndHaulJobDefOf.UnloadYourHauledInventory)
		{
			Log.Message($"[PUAH] Cleaning up PUAH reservations for {pawn} job {job.def.defName}");
			PUAHReservationManager.ReleaseAllForPawn(pawn, job);
		}
	}
}