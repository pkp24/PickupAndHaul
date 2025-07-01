namespace PickUpAndHaul
{
	static class Log
	{
		[System.Diagnostics.Conditional("DEBUG")]
		public static void Message(string x)
		{
			if (Settings.EnableDebugLogging)
			{
				Verse.Log.Message(x);
			}
		}

		[System.Diagnostics.Conditional("DEBUG")]
		public static void Warning(string x)
		{
			if (Settings.EnableDebugLogging)
			{
				Verse.Log.Warning(x);
			}
		}

		[System.Diagnostics.Conditional("DEBUG")]
		public static void Error(string x)
		{
			if (Settings.EnableDebugLogging)
			{
				Verse.Log.Error(x);
			}
		}
	}
}
