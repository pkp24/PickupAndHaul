namespace PickUpAndHaul;

internal static class Log
{
	private static readonly string DEBUG_LOG_FILE_PATH = Path.Combine(GenFilePaths.SaveDataFolderPath, "PickUpAndHaulForked.log");
	private static readonly object _fileLock = new();
	private static readonly object _queueLock = new();
	private static readonly Queue<LogEntry> _logQueue = new();
	private static readonly Thread _logThread;
	private static readonly AutoResetEvent _logEvent = new(false);
	private static StreamWriter _sw;
	private static bool _shutdownRequested;
	private static bool _initialized;
	private static readonly object _jobErrorLock = new();
	private static int _jobErrorCount;
	private static readonly Dictionary<string, int> _jobErrorTypes = [];

	static Log()
	{
		InitStreamWriter();

		// Start dedicated logging thread
		_logThread = new Thread(LogWorkerThread)
		{
			IsBackground = true,
			Name = "PickUpAndHaul Debug Logger"
		};
		_logThread.Start();

		// Set up unhandled exception logging
		AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
		TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

		// Set up job system monitoring
		SetupJobSystemMonitoring();

		_initialized = true;
	}

	private static void SetupJobSystemMonitoring()
	{
		try
		{
			// Monitor for job-related errors by intercepting common job failure points
			// This will help catch errors that occur in RimWorld's job system
			Message("Job system monitoring initialized");

			// Set up additional monitoring for cross-mod compatibility
			SetupCrossModMonitoring();
		}
		catch (Exception ex)
		{
			Verse.Log.Warning($"Failed to setup job system monitoring: {ex.Message}");
		}
	}

	private static void SetupCrossModMonitoring()
	{
		try
		{
			// Monitor for mod interaction errors
			Message("Cross-mod monitoring initialized");

			// Log loaded mods for debugging compatibility issues
			var loadedMods = LoadedModManager.RunningModsListForReading
				.Where(m => m != null && m.PackageId != null)
				.Select(m => m.PackageId)
				.ToList();

			Message($"Loaded mods: {string.Join(", ", loadedMods)}");

			// Set up RimWorld error interception
			SetupRimWorldErrorInterception();
		}
		catch (Exception ex)
		{
			Verse.Log.Warning($"Failed to setup cross-mod monitoring: {ex.Message}");
		}
	}

	private static void SetupRimWorldErrorInterception()
	{
		try
		{
			// Monitor for RimWorld errors that might be related to our mod
			Message("RimWorld error interception initialized");

			// We'll use Harmony to patch Verse.Log.Error to capture relevant errors
			// This will be set up in the HarmonyPatches.cs file
		}
		catch (Exception ex)
		{
			Verse.Log.Warning($"Failed to setup RimWorld error interception: {ex.Message}");
		}
	}

	/// <summary>
	/// Intercept RimWorld errors and log them to our debug file
	/// </summary>
	public static void InterceptRimWorldError(string errorMessage, string stackTrace = null)
	{
		try
		{
			var context = new List<string>();

			if (!string.IsNullOrEmpty(stackTrace))
			{
				context.Add($"Stack: {stackTrace}");
			}

			var fullMessage = $"[RIMWORLD_ERROR] {errorMessage}";
			if (context.Count > 0)
			{
				fullMessage += $" | Context: {string.Join(" | ", context)}";
			}

			QueueLogEntry(fullMessage, "InterceptRimWorldError", "DebugLog.cs", 0);

			// Track as RimWorld error
			TrackJobError($"RIMWORLD: {errorMessage}", "InterceptRimWorldError");
		}
		catch (Exception ex)
		{
			Verse.Log.Warning($"Failed to intercept RimWorld error: {ex.Message}");
		}
	}

	[Conditional("DEBUG")]
	public static void Message(string x, [CallerMemberName] string memberName = "", [CallerFilePath] string sourceFilePath = "", [CallerLineNumber] int sourceLineNumber = 0)
	{
		if (Settings.EnableDebugLogging)
			QueueLogEntry($"[DEBUG] {x}", memberName, sourceFilePath, sourceLineNumber);
	}

	public static void Warning(string x,
		[CallerMemberName] string memberName = "",
		[CallerFilePath] string sourceFilePath = "",
		[CallerLineNumber] int sourceLineNumber = 0)
	{
		Verse.Log.Warning($"[WARNING] {x}");
		QueueLogEntry($"[WARNING] {x}", memberName, sourceFilePath, sourceLineNumber);
	}

	public static void Error(string x,
		[CallerMemberName] string memberName = "",
		[CallerFilePath] string sourceFilePath = "",
		[CallerLineNumber] int sourceLineNumber = 0)
	{
		Verse.Log.Error($"[ERROR] {x}");
		QueueLogEntry($"[ERROR] {x}", memberName, sourceFilePath, sourceLineNumber);

		// Track job-related errors
		TrackJobError(x, memberName);
	}

	public static void Error(Exception ex, string context = "",
		[CallerMemberName] string memberName = "",
		[CallerFilePath] string sourceFilePath = "",
		[CallerLineNumber] int sourceLineNumber = 0)
	{
		var errorMessage = string.IsNullOrEmpty(context) ? ex.ToString() : $"{context}: {ex}";
		Verse.Log.Error($"[ERROR] {errorMessage}");
		QueueLogEntry($"[ERROR] {errorMessage}", memberName, sourceFilePath, sourceLineNumber);

		// Also log the full stack trace
		if (ex.StackTrace != null)
		{
			QueueLogEntry($"[STACK_TRACE] {ex.StackTrace}", memberName, sourceFilePath, sourceLineNumber);
		}

		// Track job-related errors
		TrackJobError(errorMessage, memberName);
	}

	/// <summary>
	/// Specifically for logging job-related errors with additional context
	/// </summary>
	public static void JobError(string message, Pawn pawn = null, Job job = null, Thing target = null,
		[CallerMemberName] string memberName = "",
		[CallerFilePath] string sourceFilePath = "",
		[CallerLineNumber] int sourceLineNumber = 0)
	{
		var context = new List<string>();

		if (pawn != null)
		{
			context.Add($"Pawn: {pawn.Name?.ToStringShort ?? "Unknown"} ({pawn.ThingID})");
			context.Add($"Pawn Position: {pawn.Position}");
			context.Add($"Pawn Faction: {pawn.Faction?.Name ?? "None"}");
		}

		if (job != null)
		{
			context.Add($"Job: {job.def?.defName ?? "Unknown"}");
			context.Add($"Job Status: {job.def?.label ?? "Unknown"}");
		}

		if (target != null)
		{
			context.Add($"Target: {target.def?.defName ?? "Unknown"} ({target.ThingID})");
			context.Add($"Target Position: {target.Position}");
		}

		var fullMessage = $"[JOB_ERROR] {message}";
		if (context.Count > 0)
		{
			fullMessage += $" | Context: {string.Join(" | ", context)}";
		}

		Verse.Log.Error(fullMessage);
		QueueLogEntry(fullMessage, memberName, sourceFilePath, sourceLineNumber);

		// Track job error statistics
		TrackJobError(message, memberName);
	}

	/// <summary>
	/// Log cross-mod compatibility errors
	/// </summary>
	public static void ModCompatibilityError(string message, string modName = null, Exception ex = null,
		[CallerMemberName] string memberName = "",
		[CallerFilePath] string sourceFilePath = "",
		[CallerLineNumber] int sourceLineNumber = 0)
	{
		var context = new List<string>();

		if (!string.IsNullOrEmpty(modName))
		{
			context.Add($"Mod: {modName}");
		}

		if (ex != null)
		{
			context.Add($"Exception: {ex.GetType().Name}");
			// Check if exception is from a different mod
			if (ex.StackTrace != null)
			{
				var stackFrames = ex.StackTrace.Split('\n');
				foreach (var frame in stackFrames.Take(5)) // Check first 5 frames
				{
					if (frame.Contains("PickUpAndHaul", StringComparison.OrdinalIgnoreCase))
					{
						context.Add("Stack: Contains PickUpAndHaul");
						break;
					}
				}
			}
		}

		var fullMessage = $"[MOD_COMPATIBILITY_ERROR] {message}";
		if (context.Count > 0)
		{
			fullMessage += $" | Context: {string.Join(" | ", context)}";
		}

		Verse.Log.Error(fullMessage);
		QueueLogEntry(fullMessage, memberName, sourceFilePath, sourceLineNumber);

		// Track as mod compatibility error
		TrackJobError($"MOD_COMPAT: {message}", memberName);

		// Also log the full exception if provided
		if (ex != null && ex.StackTrace != null)
		{
			QueueLogEntry($"[MOD_STACK_TRACE] {ex.StackTrace}", memberName, sourceFilePath, sourceLineNumber);
		}
	}

	/// <summary>
	/// Log general mod interaction errors
	/// </summary>
	public static void ModInteractionError(string message, string interactionType = null,
		[CallerMemberName] string memberName = "",
		[CallerFilePath] string sourceFilePath = "",
		[CallerLineNumber] int sourceLineNumber = 0)
	{
		var context = new List<string>();

		if (!string.IsNullOrEmpty(interactionType))
		{
			context.Add($"Interaction: {interactionType}");
		}

		// Add current mod load order context
		try
		{
			var currentMod = LoadedModManager.GetMod(typeof(Modbase));
			if (currentMod != null)
			{
				// Find our mod in the running mods list
				var modIndex = -1;
				for (var i = 0; i < LoadedModManager.RunningModsListForReading.Count; i++)
				{
					var mod = LoadedModManager.RunningModsListForReading[i];
					if (mod.PackageId == currentMod.Content.PackageId)
					{
						modIndex = i;
						break;
					}
				}
				if (modIndex >= 0)
				{
					context.Add($"Our Mod Load Order: {modIndex}");
				}
			}
		}
		catch
		{
			// Ignore errors in getting mod context
		}

		var fullMessage = $"[MOD_INTERACTION_ERROR] {message}";
		if (context.Count > 0)
		{
			fullMessage += $" | Context: {string.Join(" | ", context)}";
		}

		Verse.Log.Error(fullMessage);
		QueueLogEntry(fullMessage, memberName, sourceFilePath, sourceLineNumber);

		// Track as mod interaction error
		TrackJobError($"MOD_INTERACTION: {message}", memberName);
	}

	/// <summary>
	/// Log job execution with error tracking
	/// </summary>
	public static void JobExecution(string action, Pawn pawn = null, Job job = null,
		[CallerMemberName] string memberName = "",
		[CallerFilePath] string sourceFilePath = "",
		[CallerLineNumber] int sourceLineNumber = 0)
	{
		if (!Settings.EnableDebugLogging)
			return;

		var context = new List<string>();

		if (pawn != null)
		{
			context.Add($"Pawn: {pawn.Name?.ToStringShort ?? "Unknown"}");
		}

		if (job != null)
		{
			context.Add($"Job: {job.def?.defName ?? "Unknown"}");
		}

		var message = $"[JOB_EXECUTION] {action}";
		if (context.Count > 0)
		{
			message += $" | {string.Join(" | ", context)}";
		}

		QueueLogEntry(message, memberName, sourceFilePath, sourceLineNumber);
	}

	private static void TrackJobError(string errorMessage, string memberName)
	{
		try
		{
			lock (_jobErrorLock)
			{
				_jobErrorCount++;

				// Categorize error types
				var errorType = "Unknown";
				if (errorMessage.Contains("job", StringComparison.OrdinalIgnoreCase))
					errorType = "Job";
				else if (errorMessage.Contains("haul", StringComparison.OrdinalIgnoreCase))
					errorType = "Haul";
				else if (errorMessage.Contains("inventory", StringComparison.OrdinalIgnoreCase))
					errorType = "Inventory";
				else if (errorMessage.Contains("reservation", StringComparison.OrdinalIgnoreCase))
					errorType = "Reservation";
				else if (errorMessage.Contains("path", StringComparison.OrdinalIgnoreCase))
					errorType = "Pathfinding";
				else if (errorMessage.Contains("MOD_COMPAT", StringComparison.OrdinalIgnoreCase))
					errorType = "ModCompatibility";
				else if (errorMessage.Contains("MOD_INTERACTION", StringComparison.OrdinalIgnoreCase))
					errorType = "ModInteraction";
				else if (errorMessage.Contains("harmony", StringComparison.OrdinalIgnoreCase))
					errorType = "Harmony";
				else if (errorMessage.Contains("patch", StringComparison.OrdinalIgnoreCase))
					errorType = "Patch";

				if (_jobErrorTypes.ContainsKey(errorType))
					_jobErrorTypes[errorType]++;
				else
					_jobErrorTypes[errorType] = 1;

				// Log error statistics periodically
				if (_jobErrorCount % 10 == 0)
				{
					var stats = string.Join(", ", _jobErrorTypes.Select(kvp => $"{kvp.Key}: {kvp.Value}"));
					QueueLogEntry($"[ERROR_STATS] Total errors: {_jobErrorCount} | Types: {stats}", memberName, "DebugLog.cs", 0);
				}
			}
		}
		catch (Exception ex)
		{
			Verse.Log.Warning($"Failed to track job error: {ex.Message}");
		}
	}

	private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		if (e.ExceptionObject is Exception ex)
		{
			Error(ex, "Unhandled Exception");
		}
		else
		{
			Error($"Unhandled Exception: {e.ExceptionObject}");
		}
	}

	private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
	{
		Error(e.Exception, "Unobserved Task Exception");
		e.SetObserved(); // Mark as observed to prevent application crash
	}

	private static void QueueLogEntry(string message, string memberName, string sourceFilePath, int sourceLineNumber)
	{
		if (!_initialized)
		{
			// Fallback to direct writing if not initialized
			WriteToFileDirect(message, memberName, sourceFilePath, sourceLineNumber);
			return;
		}

		try
		{
			var entry = new LogEntry
			{
				Message = message,
				MemberName = memberName,
				SourceFilePath = sourceFilePath,
				SourceLineNumber = sourceLineNumber,
				Timestamp = DateTime.Now
			};

			lock (_queueLock)
			{
				_logQueue.Enqueue(entry);
			}

			_logEvent.Set(); // Signal the worker thread
		}
		catch (Exception ex)
		{
			// Fallback to synchronous logging if queuing fails
			Verse.Log.Warning($"Failed to queue log entry: {ex.Message}");
			WriteToFileDirect($"[FALLBACK] {message}", memberName, sourceFilePath, sourceLineNumber);
		}
	}

	private static void LogWorkerThread()
	{
		while (!_shutdownRequested)
		{
			try
			{
				// Wait for signal or timeout
				_logEvent.WaitOne(1000); // 1 second timeout

				// Process all queued entries
				var entriesToProcess = new List<LogEntry>();
				lock (_queueLock)
				{
					while (_logQueue.Count > 0)
					{
						entriesToProcess.Add(_logQueue.Dequeue());
					}
				}

				// Write all entries in order
				foreach (var entry in entriesToProcess)
				{
					WriteToFileDirect(entry.Message, entry.MemberName, entry.SourceFilePath, entry.SourceLineNumber);
				}
			}
			catch (Exception ex)
			{
				// Log error to Verse.Log when custom logging fails
				Verse.Log.Warning($"Debug logger thread error: {ex.Message}");
			}
		}
	}

	private static void WriteToFileDirect(string message, string memberName, string sourceFilePath, int sourceLineNumber)
	{
		try
		{
			lock (_fileLock)
			{
				if (_sw == null)
				{
					InitStreamWriter();
				}

				var fileName = Path.GetFileName(sourceFilePath);
				var timestamp = DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
				var logEntry = $"[{timestamp}] [PUAHForked] [{memberName}] [{fileName}:{sourceLineNumber}] {message}";
				_sw.WriteLine(logEntry);
				_sw.Flush();
			}
		}
		catch (Exception ex)
		{
			// Use Verse.Log as final fallback
			Verse.Log.Warning($"Failed to write to debug log file: {ex.Message}");
			Verse.Log.Warning($"Original message was: {message}");
		}
	}

	private static void InitStreamWriter()
	{
		try
		{
			var directory = Path.GetDirectoryName(DEBUG_LOG_FILE_PATH);
			if (!Directory.Exists(directory))
			{
				Directory.CreateDirectory(directory);
			}

			_sw = new StreamWriter(DEBUG_LOG_FILE_PATH, true)
			{
				AutoFlush = true
			};

			// Write initialization message
			var timestamp = DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
			var initMessage = $"[{timestamp}] [PUAHForked] [InitStreamWriter] [DebugLog.cs:0] PickUpAndHaul Debug Logger initialized.";
			_sw.WriteLine(initMessage);
			_sw.Flush();
		}
		catch (Exception ex)
		{
			Verse.Log.Warning($"Failed to initialize debug log file: {ex.Message}");
		}
	}

	public static void Dispose()
	{
		try
		{
			// Remove event handlers
			AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
			TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

			// Signal shutdown
			_shutdownRequested = true;
			_logEvent.Set();

			// Wait for thread to finish
			if (_logThread != null && _logThread.IsAlive)
			{
				_logThread.Join(2000); // Wait up to 2 seconds
			}

			// Process any remaining entries
			var remainingEntries = new List<LogEntry>();
			lock (_queueLock)
			{
				while (_logQueue.Count > 0)
				{
					remainingEntries.Add(_logQueue.Dequeue());
				}
			}

			foreach (var entry in remainingEntries)
			{
				WriteToFileDirect(entry.Message, entry.MemberName, entry.SourceFilePath, entry.SourceLineNumber);
			}

			_sw?.Dispose();
			_sw = null;
			_initialized = false;
		}
		catch (Exception ex)
		{
			Verse.Log.Warning($"Failed to dispose debug log file: {ex.Message}");
		}
	}

	public static void ClearDebugLogFile()
	{
		try
		{
			lock (_fileLock)
			{
				// Clear the queue
				lock (_queueLock)
				{
					_logQueue.Clear();
				}

				// Close and delete the file
				_sw?.Dispose();
				_sw = null;

				if (File.Exists(DEBUG_LOG_FILE_PATH))
				{
					File.Delete(DEBUG_LOG_FILE_PATH);
				}

				// Reinitialize the stream writer
				InitStreamWriter();

				var timestamp = DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
				var logEntry = $"[{timestamp}] [PUAHForked] [ClearDebugLogFile] [DebugLog.cs:0] Debug log file cleared.";

				// Check if stream writer was successfully initialized
#pragma warning disable CA1508 //ignore the warning
				if (_sw != null)
				{
					_sw.WriteLine(logEntry);
					_sw.Flush();
				}
#pragma warning restore CA1508
			}
		}
		catch (Exception ex)
		{
			Verse.Log.Warning($"Failed to clear debug log file: {ex.Message}");
		}
	}

	/// <summary>
	/// Get current job error statistics
	/// </summary>
	public static string GetJobErrorStats()
	{
		try
		{
			lock (_jobErrorLock)
			{
				var stats = string.Join(", ", _jobErrorTypes.Select(kvp => $"{kvp.Key}: {kvp.Value}"));
				return $"Total job errors: {_jobErrorCount} | Types: {stats}";
			}
		}
		catch (Exception ex)
		{
			return $"Error getting stats: {ex.Message}";
		}
	}

	/// <summary>
	/// Reset job error statistics
	/// </summary>
	public static void ResetJobErrorStats()
	{
		try
		{
			lock (_jobErrorLock)
			{
				_jobErrorCount = 0;
				_jobErrorTypes.Clear();
				Message("Job error statistics reset");
			}
		}
		catch (Exception ex)
		{
			Warning($"Failed to reset job error stats: {ex.Message}");
		}
	}

	/// <summary>
	/// Log a comprehensive error report with current statistics
	/// </summary>
	public static void LogErrorReport(string context = "")
	{
		try
		{
			var stats = GetJobErrorStats();
			var report = string.IsNullOrEmpty(context) ? $"[ERROR_REPORT] {stats}" : $"[ERROR_REPORT] {context} | {stats}";
			QueueLogEntry(report, "LogErrorReport", "DebugLog.cs", 0);
		}
		catch (Exception ex)
		{
			Verse.Log.Warning($"Failed to log error report: {ex.Message}");
		}
	}

	private class LogEntry
	{
		public string Message { get; set; } = "";
		public string MemberName { get; set; } = "";
		public string SourceFilePath { get; set; } = "";
		public int SourceLineNumber { get; set; }
		public DateTime Timestamp { get; set; }
	}
}