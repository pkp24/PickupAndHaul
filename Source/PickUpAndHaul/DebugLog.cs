using System.Threading;

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
	}

	private static void QueueLogEntry(string message, string memberName, string sourceFilePath, int sourceLineNumber)
	{
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
			Verse.Log.Warning($"Failed to write to debug log file: {ex.Message}");
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

	private class LogEntry
	{
		public string Message { get; set; } = "";
		public string MemberName { get; set; } = "";
		public string SourceFilePath { get; set; } = "";
		public int SourceLineNumber { get; set; }
		public DateTime Timestamp { get; set; }
	}
}