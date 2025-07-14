using System;
using System.IO;
using Verse;

namespace PickUpAndHaul
{
	static class Log
	{
		private static readonly string DEBUG_LOG_FILE_PATH = Path.Combine(GenFilePaths.SaveDataFolderPath, "PickUpAndHaul_Debug.txt");
		private static readonly object _fileLock = new object();

		[System.Diagnostics.Conditional("DEBUG")]
		public static void Message(string x)
		{
			if (Settings.EnableDebugLogging)
			{
				Verse.Log.Message(x);
				WriteToFile(x);
			}
		}

		[System.Diagnostics.Conditional("DEBUG")]
		public static void Warning(string x)
		{
			if (Settings.EnableDebugLogging)
			{
				Verse.Log.Warning(x);
				WriteToFile($"[WARNING] {x}");
			}
		}

		[System.Diagnostics.Conditional("DEBUG")]
		public static void Error(string x)
		{
			if (Settings.EnableDebugLogging)
			{
				Verse.Log.Error(x);
				WriteToFile($"[ERROR] {x}");
			}
		}

		[System.Diagnostics.Conditional("DEBUG")]
		public static void MessageToFile(string x)
		{
			if (Settings.EnableDebugLogging)
			{
				WriteToFile(x);
			}
		}

		private static void WriteToFile(string message)
		{
			try
			{
				lock (_fileLock)
				{
					// Ensure the directory exists
					var directory = Path.GetDirectoryName(DEBUG_LOG_FILE_PATH);
					if (!Directory.Exists(directory))
					{
						Directory.CreateDirectory(directory);
					}

					// Append to file with timestamp
					var timestampedMessage = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
					File.AppendAllText(DEBUG_LOG_FILE_PATH, timestampedMessage + Environment.NewLine);
				}
			}
			catch (Exception ex)
			{
				// Don't use our own logging to avoid infinite recursion
				Verse.Log.Warning($"[PickUpAndHaul] Failed to write to debug log file: {ex.Message}");
			}
		}

		public static void ClearDebugLogFile()
		{
			try
			{
				lock (_fileLock)
				{
					if (File.Exists(DEBUG_LOG_FILE_PATH))
					{
						File.Delete(DEBUG_LOG_FILE_PATH);
						Verse.Log.Message("[PickUpAndHaul] Debug log file cleared.");
					}
				}
			}
			catch (Exception ex)
			{
				Verse.Log.Warning($"[PickUpAndHaul] Failed to clear debug log file: {ex.Message}");
			}
		}
	}
}
