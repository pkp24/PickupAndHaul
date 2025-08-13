using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace PartialReservationSystem;

public class PRSMod_Integrator
{
	public static void Initialize()
	{
		try
		{
			// Force-clear the PRS debug log at RimWorld startup so we don't append across runs
			Log.ClearDebugLogFile();
		}
		catch (Exception ex)
		{
			Verse.Log.Warning($"[PRS] Failed to clear debug log at startup: {ex.Message}");
		}

		try
		{
			var harmony = new Harmony("pkp.PartialReservationSystem.inPUAH");
			// Patch all PRS attributes
			harmony.PatchAll(typeof(PRSReservationSystem).Assembly);
		}
		catch (Exception ex)
		{
			Verse.Log.Error($"[PRS] Harmony initialization failed: {ex}");
		}
	}
}


