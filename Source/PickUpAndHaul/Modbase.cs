global using RimWorld;
global using System;
global using System.Collections.Generic;
global using UnityEngine;
global using Verse;
global using Verse.AI;

namespace PickUpAndHaul;

public class Modbase : Mod
{
	public Modbase(ModContentPack content) : base(content)
	{
		Instance = this;
		Settings = GetSettings<Settings>();
		
		// Initialize performance logging
		InitializePerformanceLogging();
	}
	
	private void InitializePerformanceLogging()
	{
		try
		{
			// The PerformanceLogger is initialized via static constructor
			// This just ensures it's properly set up
			Log.Message("PickUpAndHaul performance logging initialized");
		}
		catch (Exception ex)
		{
			Log.Error($"Failed to initialize performance logging: {ex.Message}");
		}
	}
	


	public override void DoSettingsWindowContents(Rect inRect) => Settings.DoSettingsWindowContents(inRect);
	public override string SettingsCategory() => "Pick Up And Haul";
	public static Modbase Instance { get; private set; }
	public static Settings Settings { get; private set; }
}