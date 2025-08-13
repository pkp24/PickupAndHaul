using Verse;

namespace PartialReservationSystem;

public class Settings : ModSettings
{
	private static bool _enableDebugLogging = true; // ENABLE by default for diagnostics

	public static bool EnableDebugLogging => _enableDebugLogging;

	public static void DoSettingsWindowContents(UnityEngine.Rect inRect)
	{
		var ls = new Listing_Standard();
		ls.Begin(inRect);
		ls.CheckboxLabeled("PRS.enableDebugLogging".Translate(), ref _enableDebugLogging, "PRS.enableDebugLoggingTooltip".Translate());
		ls.End();
	}

	public override void ExposeData()
	{
		base.ExposeData();
		Scribe_Values.Look(ref _enableDebugLogging, "enableDebugLogging", true); // persist default true
	}
}


