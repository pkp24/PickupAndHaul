global using RimWorld;
global using System;
global using System.Collections.Generic;
global using UnityEngine;
global using Verse;
global using Verse.AI;
global using PickUpAndHaul.Cache;

namespace PickUpAndHaul;

public class Modbase : Mod
{
	public Modbase(ModContentPack content) : base(content)
	{
		Instance = this;
		Settings = GetSettings<Settings>();
		
		// Clear debug log on mod initialization
		ClearDebugLogOnStartup();
		
		// Initialize performance logging
		InitializePerformanceLogging();
		
		// Initialize cache system
		InitializeCacheSystem();
	}
	
	private void ClearDebugLogOnStartup()
	{
		try
		{
			Log.ClearDebugLogFile();
			Log.Message("PickUpAndHaul debug log cleared on startup");
		}
		catch (Exception ex)
		{
			// Use Verse.Log as fallback since our Log might not be initialized yet
			Verse.Log.Warning($"Failed to clear debug log on startup: {ex.Message}");
		}
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
	
	private void InitializeCacheSystem()
	{
		try
		{
			// Initialize caches for all existing maps
			CacheInitializer.InitializeAllCaches();
			Log.Message("PickUpAndHaul cache system initialized");
		}
		catch (Exception ex)
		{
			Log.Error($"Failed to initialize cache system: {ex.Message}");
		}
	}
	


	public override void DoSettingsWindowContents(Rect inRect) => Settings.DoSettingsWindowContents(inRect);
	public override string SettingsCategory() => "Pick Up And Haul";
	public static Modbase Instance { get; private set; }
	public static Settings Settings { get; private set; }
}