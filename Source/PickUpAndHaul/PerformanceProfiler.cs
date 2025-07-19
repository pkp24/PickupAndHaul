namespace PickUpAndHaul;

public static class PerformanceProfiler
{
	private static readonly Dictionary<string, PerformanceMetric> _metrics = [];
	private static readonly Dictionary<string, Stopwatch> _activeTimers = [];
	private static int _lastReportTick;
	private const int REPORT_INTERVAL_TICKS = 6000; // Report every 10 seconds at 60 TPS
	private static readonly string LOG_FILE_PATH = Path.Combine(GenFilePaths.SaveDataFolderPath, "PickUpAndHaulForkedPerformance.log");
	private static int _reportStagger; // Stagger performance reports

	public static void StartTimer(string operationName)
	{
		if (!Settings.EnableDebugLogging)
			return;

		if (_activeTimers.ContainsKey(operationName))
		{
			Log.Warning($"[PerformanceProfiler] Timer '{operationName}' was already running, restarting");
			_activeTimers.Remove(operationName);
		}

		_activeTimers[operationName] = Stopwatch.StartNew();
	}

	public static void EndTimer(string operationName)
	{
		if (!Settings.EnableDebugLogging || !_activeTimers.TryGetValue(operationName, out var timer))
			return;

		timer.Stop();
		var elapsedTicks = timer.ElapsedTicks;
		_activeTimers.Remove(operationName);

		if (!_metrics.ContainsKey(operationName))
		{
			_metrics[operationName] = new PerformanceMetric();
		}

		var metric = _metrics[operationName];
		metric.AddMeasurement(elapsedTicks);
	}

	public static void RecordOperation(string operationName, int tickCount = 1)
	{
		if (!Settings.EnableDebugLogging)
			return;

		if (!_metrics.ContainsKey(operationName))
		{
			_metrics[operationName] = new PerformanceMetric();
		}

		_metrics[operationName].AddMeasurement(tickCount);
	}

	public static void Update()
	{
		if (!Settings.EnableDebugLogging)
			return;

		var currentTick = Find.TickManager?.TicksGame ?? 0;

		// Stagger performance reports to prevent simultaneous report generation
		var reportOffset = _reportStagger++ % 200; // Spread reports over 200 ticks
		if (currentTick - _lastReportTick >= REPORT_INTERVAL_TICKS + reportOffset)
		{
			GenerateReport();
			_lastReportTick = currentTick;
		}
	}

	private static string GenerateReportText()
	{
		var report = "=== PickUpAndHaul Performance Report ===\n";
		report += $"Generated at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
		report += $"Game Tick: {Find.TickManager?.TicksGame ?? 0}\n\n";

		var sortedMetrics = _metrics.OrderByDescending(kvp => kvp.Value.AverageTicks).ToList();

		foreach (var kvp in sortedMetrics)
		{
			var metric = kvp.Value;
			var operationName = kvp.Key;

			report += $"{operationName}:\n";
			report += $"  Calls: {metric.CallCount}\n";
			report += $"  Total Ticks: {metric.TotalTicks}\n";
			report += $"  Average Ticks: {metric.AverageTicks:F2}\n";
			report += $"  Max Ticks: {metric.MaxTicks}\n";
			report += $"  Min Ticks: {metric.MinTicks}\n";
			report += $"  Calls per 10s: {metric.CallsPerSecond:F2}\n";
			report += $"  Ticks per 10s: {metric.TicksPerSecond:F2}\n";
			report += "\n";
		}

		return report;
	}

	private static void WriteReportToFile(string report)
	{
		try
		{
			// Stagger file write operations to prevent I/O spikes
			var currentTick = Find.TickManager?.TicksGame ?? 0;
			var writeDelay = currentTick % 30; // Spread file writes over 30 ticks

			if (writeDelay != 0)
			{
				// Skip this write to stagger I/O operations
				return;
			}

			// Ensure the directory exists
			var directory = Path.GetDirectoryName(LOG_FILE_PATH);
			if (!Directory.Exists(directory))
			{
				Directory.CreateDirectory(directory);
			}

			// Append to file with timestamp
			var timestampedReport = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{report}\n{new string('=', 80)}\n\n";
			File.AppendAllText(LOG_FILE_PATH, timestampedReport);
		}
		catch (Exception ex)
		{
			Log.Warning($"[PerformanceProfiler] Failed to write to performance log file: {ex.Message}");
		}
	}

	private static void GenerateReport()
	{
		if (_metrics.Count == 0)
			return;

		var report = GenerateReportText();
		Log.Message(report);

		// Also write to file
		WriteReportToFile(report);

		// Clear metrics for next reporting period
		_metrics.Clear();
	}

	public static void ClearMetrics()
	{
		_metrics.Clear();
		_activeTimers.Clear();
	}

	public static void GenerateManualReport()
	{
		if (_metrics.Count == 0)
		{
			Log.Message("No performance metrics collected yet.");
			return;
		}

		var report = GenerateReportText();
		Log.Message(report);
		WriteReportToFile(report);
	}

	public static void ClearPerformanceLogFile()
	{
		try
		{
			if (File.Exists(LOG_FILE_PATH))
			{
				File.Delete(LOG_FILE_PATH);
				Log.Message("[PerformanceProfiler] Performance log file cleared.");
			}
		}
		catch (Exception ex)
		{
			Log.Warning($"[PerformanceProfiler] Failed to clear performance log file: {ex.Message}");
		}
	}

	private class PerformanceMetric
	{
		public long TotalTicks { get; private set; }
		public int CallCount { get; private set; }
		public long MaxTicks { get; private set; }
		public long MinTicks { get; private set; } = long.MaxValue;
		public float AverageTicks => CallCount > 0 ? (float)TotalTicks / CallCount : 0f;
		public float CallsPerSecond => CallCount / 10f; // Assuming 10 second reporting interval
		public float TicksPerSecond => TotalTicks / 10f;

		public void AddMeasurement(long ticks)
		{
			TotalTicks += ticks;
			CallCount++;
			MaxTicks = Math.Max(MaxTicks, ticks);
			MinTicks = Math.Min(MinTicks, ticks);
		}
	}
}