using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace PickUpAndHaul;

internal static class Log
{
	private static readonly string DEBUG_LOG_FILE_PATH = Path.Combine(GenFilePaths.SaveDataFolderPath, "PickUpAndHaul_Debug.txt");
	private static readonly string ERROR_LOG_FILE_PATH = Path.Combine(GenFilePaths.SaveDataFolderPath, "PickUpAndHaul_Errors.txt");
	private static readonly object _fileLock = new();
	private static readonly object _errorFileLock = new();
	private static bool _globalErrorHandlerInitialized = false;

	[Conditional("DEBUG")]
	public static void Message(string x)
	{
		if (Settings.EnableDebugLogging)
		{
			// Verse.Log.Message(x);
			Task.Run(() => WriteToFile(x));
		}
	}

	[Conditional("DEBUG")]
	public static void Warning(string x)
	{
		if (Settings.EnableDebugLogging)
		{
			// Verse.Log.Warning(x);
			Task.Run(() => WriteToFile($"[WARNING] {x}"));
		}
	}

	[Conditional("DEBUG")]
	public static void Error(string x)
	{
		if (Settings.EnableDebugLogging)
		{
			Verse.Log.Error(x);
			Task.Run(() => WriteToFile($"[ERROR] {x}"));
		}
	}

	[Conditional("DEBUG")]
	public static void MessageToFile(string x)
	{
		if (Settings.EnableDebugLogging)
			Task.Run(() => WriteToFile(x));
	}

	/// <summary>
	/// Initialize global error handling to capture all errors to the error log file
	/// </summary>
	public static void InitializeGlobalErrorHandling()
	{
		if (_globalErrorHandlerInitialized)
			return;

		try
		{
			// Set up global exception handler for unhandled exceptions
			AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

			_globalErrorHandlerInitialized = true;
			WriteToErrorFile("[PickUpAndHaul] Global error handling initialized");
		}
		catch (Exception ex)
		{
			// Fallback to Verse.Log if our error handling fails
			Verse.Log.Warning($"[PickUpAndHaul] Failed to initialize global error handling: {ex.Message}");
		}
	}

	/// <summary>
	/// Handle unhandled exceptions from any source
	/// </summary>
	private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		try
		{
			var exception = e.ExceptionObject as Exception;
			var message = exception != null
				? $"[UNHANDLED EXCEPTION] {exception.Message}\nStackTrace: {exception.StackTrace}"
				: $"[UNHANDLED EXCEPTION] Unknown exception: {e.ExceptionObject}";

			WriteToErrorFile(message);
			// Also write to debug file for context
			WriteToFile(message);
		}
		catch
		{
			// If our error logging fails, fall back to Verse.Log
			Verse.Log.Error("PickUpAndHaul: Failed to log unhandled exception");
		}
	}

	/// <summary>
	/// Write any error to the error log file (for non-PUAH errors)
	/// </summary>
	public static void LogAnyError(string errorMessage, Exception exception = null)
	{
		try
		{
			var message = exception != null
				? $"[EXTERNAL ERROR] {errorMessage}\nException: {exception.Message}\nStackTrace: {exception.StackTrace}"
				: $"[EXTERNAL ERROR] {errorMessage}";

			WriteToErrorFile(message);
			// Also write to debug file for context
			WriteToFile(message);
		}
		catch
		{
			// If our error logging fails, fall back to Verse.Log
			Verse.Log.Error("PickUpAndHaul: Failed to log external error");
		}
	}

	/// <summary>
	/// Capture Verse.Log errors and warnings to our error log file
	/// </summary>
	public static void CaptureVerseLogError(string message)
	{
		try
		{
			WriteToErrorFile($"[VERSE.LOG ERROR] {message}");
			// Also write to debug file for context
			WriteToFile($"[VERSE.LOG ERROR] {message}");
		}
		catch
		{
			// If our error logging fails, don't try to log again to avoid infinite recursion
		}
	}

	/// <summary>
	/// Capture Verse.Log warnings to our error log file
	/// </summary>
	public static void CaptureVerseLogWarning(string message)
	{
		try
		{
			WriteToErrorFile($"[VERSE.LOG WARNING] {message}");
			// Also write to debug file for context
			WriteToFile($"[VERSE.LOG WARNING] {message}");
		}
		catch
		{
			// If our error logging fails, don't try to log again to avoid infinite recursion
		}
	}

	private static void WriteToFile(string message)
	{
		try
		{
			PerformanceProfiler.Update();
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

	private static void WriteToErrorFile(string message)
	{
		try
		{
			lock (_errorFileLock)
			{
				// Ensure the directory exists
				var directory = Path.GetDirectoryName(ERROR_LOG_FILE_PATH);
				if (!Directory.Exists(directory))
				{
					Directory.CreateDirectory(directory);
				}

				// Append to file with timestamp
				var timestampedMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
				File.AppendAllText(ERROR_LOG_FILE_PATH, timestampedMessage + Environment.NewLine);
			}
		}
		catch (Exception ex)
		{
			// Don't use our own logging to avoid infinite recursion
			Verse.Log.Warning($"[PickUpAndHaul] Failed to write to error log file: {ex.Message}");
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

	public static void ClearErrorLogFile()
	{
		try
		{
			lock (_errorFileLock)
			{
				if (File.Exists(ERROR_LOG_FILE_PATH))
				{
					File.Delete(ERROR_LOG_FILE_PATH);
					Verse.Log.Message("[PickUpAndHaul] Error log file cleared.");
				}
			}
		}
		catch (Exception ex)
		{
			Verse.Log.Warning($"[PickUpAndHaul] Failed to clear error log file: {ex.Message}");
		}
	}
}