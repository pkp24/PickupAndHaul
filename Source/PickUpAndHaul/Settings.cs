namespace PickUpAndHaul;

public class Settings : ModSettings
{
	private static bool _allowCorpses;
	private static bool _allowAnimals = true;
	private static bool _allowMechanoids = true;
	private static bool _enableDebugLogging;

	public static bool AllowCorpses => _allowCorpses;
	public static bool AllowAnimals => _allowAnimals;
	public static bool AllowMechanoids => _allowMechanoids;
	public static bool EnableDebugLogging => _enableDebugLogging;

	public static bool IsAllowedRace(RaceProperties props) => props.Humanlike || (AllowAnimals && props.Animal) || (AllowMechanoids && props.IsMechanoid);

	public static void DoSettingsWindowContents(Rect inRect)
	{
		var ls = new Listing_Standard();
		ls.Begin(inRect);
		ls.CheckboxLabeled("PUAH.allowCorpses".Translate(), ref _allowCorpses, "PUAH.allowCorpsesTooltip".Translate());
		ls.CheckboxLabeled("PUAH.allowAnimals".Translate(), ref _allowAnimals, "PUAH.allowAnimalsTooltip".Translate());
		ls.CheckboxLabeled("PUAH.allowMechanoids".Translate(), ref _allowMechanoids, "PUAH.allowMechanoidsTooltip".Translate());
		ls.CheckboxLabeled("PUAH.enableDebugLogging".Translate(), ref _enableDebugLogging, "PUAH.enableDebugLoggingTooltip".Translate());

		// Add debug log management
		if (_enableDebugLogging)
		{
			ls.Gap();
			if (ls.ButtonText("Clear Debug Log File"))
			{
				Log.ClearDebugLogFile();
			}
		}

		ls.End();
	}

	public override void ExposeData()
	{
		base.ExposeData();
		Scribe_Values.Look(ref _allowCorpses, "allowCorpses");
		Scribe_Values.Look(ref _allowAnimals, "allowAnimals", true);
		Scribe_Values.Look(ref _allowMechanoids, "allowMechanoids", true);
		Scribe_Values.Look(ref _enableDebugLogging, "enableDebugLogging", false);
	}
}