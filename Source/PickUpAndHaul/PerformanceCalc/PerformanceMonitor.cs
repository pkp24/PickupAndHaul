using System;
using System.Collections.Generic;
using System.Diagnostics;
using RimWorld;
using Verse;

namespace PickUpAndHaul.Performance
{
    /// <summary>
    /// Utility class for monitoring performance metrics in PickUpAndHaul
    /// </summary>
    public static class PerformanceMonitor
    {
        private static readonly Dictionary<string, Stopwatch> _timers = new();
        private static readonly Dictionary<string, int> _counters = new();
        private static readonly Dictionary<string, long> _totalTimes = new();

        /// <summary>
        /// Check if the game is currently paused
        /// </summary>
        private static bool IsGamePaused()
        {
            return Find.TickManager?.Paused ?? true;
        }

        /// <summary>
        /// Trigger periodic performance logging
        /// This should be called regularly (e.g., every tick or every few ticks)
        /// </summary>
        public static void UpdatePeriodicLogging()
        {
            if (IsGamePaused()) return;
            
            try
            {
                PerformanceLogger.LogPeriodicMetrics();
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to update periodic logging: {ex.Message}");
            }
        }

        /// <summary>
        /// Start timing a performance operation
        /// </summary>
        public static void StartTimer(string operationName)
        {
            if (IsGamePaused()) return;
            
            if (!_timers.ContainsKey(operationName))
            {
                _timers[operationName] = Stopwatch.StartNew();
            }
            else
            {
                _timers[operationName].Restart();
            }
        }

        /// <summary>
        /// Stop timing and log the performance metrics
        /// </summary>
        public static void StopTimer(string operationName, Pawn pawn = null, Thing thing = null, string additionalInfo = "")
        {
            if (IsGamePaused()) return;
            
            if (_timers.TryGetValue(operationName, out var timer))
            {
                timer.Stop();
                var elapsedMs = timer.ElapsedMilliseconds;
                
                // Update total time
                if (!_totalTimes.ContainsKey(operationName))
                    _totalTimes[operationName] = 0;
                _totalTimes[operationName] += elapsedMs;
                
                // Log the performance metric
                PerformanceLogger.LogPerformance($"TIMER_{operationName}", (int)elapsedMs, pawn, thing, additionalInfo);
                
                // Log if operation takes too long
                if (elapsedMs > 100) // Log operations taking more than 100ms
                {
                    Log.Warning($"Performance warning: {operationName} took {elapsedMs}ms {additionalInfo}");
                }
            }
        }

        /// <summary>
        /// Increment a counter and optionally log it
        /// </summary>
        public static void IncrementCounter(string counterName, int amount = 1, bool log = false, Pawn pawn = null, string additionalInfo = "")
        {
            if (IsGamePaused()) return;
            
            if (!_counters.ContainsKey(counterName))
                _counters[counterName] = 0;
            
            _counters[counterName] += amount;
            
            if (log)
            {
                PerformanceLogger.LogPerformance($"COUNTER_{counterName}", _counters[counterName], pawn, null, additionalInfo);
            }
        }

        /// <summary>
        /// Get the current value of a counter
        /// </summary>
        public static int GetCounter(string counterName)
        {
            return _counters.TryGetValue(counterName, out var value) ? value : 0;
        }

        /// <summary>
        /// Reset a counter
        /// </summary>
        public static void ResetCounter(string counterName)
        {
            if (_counters.ContainsKey(counterName))
                _counters[counterName] = 0;
        }

        /// <summary>
        /// Get total time for an operation
        /// </summary>
        public static long GetTotalTime(string operationName)
        {
            return _totalTimes.TryGetValue(operationName, out var time) ? time : 0;
        }

        /// <summary>
        /// Log a summary of all performance metrics
        /// </summary>
        public static void LogPerformanceSummary()
        {
            if (IsGamePaused()) return;
            
            Log.Message("=== PickUpAndHaul Performance Summary ===");
            
            foreach (var counter in _counters)
            {
                Log.Message($"Counter {counter.Key}: {counter.Value}");
            }
            
            foreach (var timer in _totalTimes)
            {
                Log.Message($"Total time for {timer.Key}: {timer.Value}ms");
            }
            
            Log.Message("=== End Performance Summary ===");
        }

        /// <summary>
        /// Clear all performance data
        /// </summary>
        public static void ClearAllData()
        {
            _timers.Clear();
            _counters.Clear();
            _totalTimes.Clear();
        }

        /// <summary>
        /// Monitor a specific operation with automatic timing
        /// </summary>
        public static IDisposable MonitorOperation(string operationName, Pawn pawn = null, Thing thing = null, string additionalInfo = "")
        {
            if (IsGamePaused()) 
                return new DummyOperationMonitor();
                
            return new OperationMonitor(operationName, pawn, thing, additionalInfo);
        }

        private class OperationMonitor : IDisposable
        {
            private readonly string _operationName;
            private readonly Pawn _pawn;
            private readonly Thing _thing;
            private readonly string _additionalInfo;
            private readonly Stopwatch _stopwatch;

            public OperationMonitor(string operationName, Pawn pawn, Thing thing, string additionalInfo)
            {
                _operationName = operationName;
                _pawn = pawn;
                _thing = thing;
                _additionalInfo = additionalInfo;
                _stopwatch = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                _stopwatch.Stop();
                PerformanceLogger.LogPerformance($"MONITOR_{_operationName}", (int)_stopwatch.ElapsedMilliseconds, _pawn, _thing, _additionalInfo);
            }
        }

        private class DummyOperationMonitor : IDisposable
        {
            public void Dispose() { }
        }
    }
} 