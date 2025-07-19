namespace PickUpAndHaul;

internal static class Log
{
	private static readonly string DEBUG_LOG_FILE_PATH = Path.Combine(GenFilePaths.SaveDataFolderPath, "PickUpAndHaulForked.log");
	private static readonly object _fileLock = new();
	private static StreamWriter _sw;

	static Log() => InitStreamWriter();

	[Conditional("DEBUG")]
	public static void Message(string x,
		[CallerMemberName] string memberName = "",
		[CallerFilePath] string sourceFilePath = "",
		[CallerLineNumber] int sourceLineNumber = 0)
	{
		if (Settings.EnableDebugLogging)
			Task.Run(() => WriteToFile($"[DEBUG] {x}", memberName, sourceFilePath, sourceLineNumber));
	}

	public static void Warning(string x,
		[CallerMemberName] string memberName = "",
		[CallerFilePath] string sourceFilePath = "",
		[CallerLineNumber] int sourceLineNumber = 0)
	{
		Verse.Log.Warning($"[WARNING] {x}");
		Task.Run(() => WriteToFile($"[WARNING] {x}", memberName, sourceFilePath, sourceLineNumber));
	}

	public static void Error(string x,
		[CallerMemberName] string memberName = "",
		[CallerFilePath] string sourceFilePath = "",
		[CallerLineNumber] int sourceLineNumber = 0)
	{
		Verse.Log.Error($"[ERROR] {x}");
		Task.Run(() => WriteToFile($"[ERROR] {x}", memberName, sourceFilePath, sourceLineNumber));
	}

	public static void ClearDebugLogFile()
	{
		var timestampedMessage = $"[{DateTime.Now:HH:mm:ss.fff}] [PUAHForked]";

		try
		{
			lock (_fileLock)
			{
				if (File.Exists(DEBUG_LOG_FILE_PATH))
				{
					_sw.Close();
					File.Delete(DEBUG_LOG_FILE_PATH);
					InitStreamWriter();
					Verse.Log.Message($"{timestampedMessage} Debug log file cleared.");
				}
			}
		}
		catch (Exception ex)
		{
			Verse.Log.Warning($"{timestampedMessage} Failed to clear debug log file: {ex.Message}");
		}
	}

	private static void WriteToFile(string message, string memberName, string sourceFilePath, int sourceLineNumber)
	{
		var timestampedMessage = $"[{DateTime.Now:HH:mm:ss.fff}] [PUAHForked] [{Path.GetFileNameWithoutExtension(sourceFilePath)}] [{memberName}:{sourceLineNumber}] {message}";
		try
		{
			if (_sw == null)
				InitStreamWriter();
            lock (_fileLock)
				_sw.Write(timestampedMessage + Environment.NewLine);
		}
		catch (Exception ex)
		{
			// Don't use our own logging to avoid infinite recursion
			Verse.Log.Warning($"{timestampedMessage} Failed to write to debug log file: {ex.Message}");
		}
	}

	private static void InitStreamWriter() =>
		_sw = new StreamWriter(DEBUG_LOG_FILE_PATH, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
}