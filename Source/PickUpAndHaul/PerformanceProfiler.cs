using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace PickUpAndHaul
{
    public static class PerformanceProfiler
    {
        private static readonly Dictionary<string, PerformanceMetric> _metrics = new();
        private static readonly Dictionary<string, Stopwatch> _activeTimers = new();
        private static int _lastReportTick = 0;
        private static readonly int REPORT_INTERVAL_TICKS = 6000; // Report every 10 seconds at 60 TPS

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
            
            if (currentTick - _lastReportTick >= REPORT_INTERVAL_TICKS)
            {
                GenerateReport();
                _lastReportTick = currentTick;
            }
        }

        private static void GenerateReport()
        {
            if (_metrics.Count == 0)
                return;

            Log.Message("=== PickUpAndHaul Performance Report ===");
            
            var sortedMetrics = _metrics.OrderByDescending(kvp => kvp.Value.AverageTicks).ToList();
            
            foreach (var kvp in sortedMetrics)
            {
                var metric = kvp.Value;
                var operationName = kvp.Key;
                
                Log.Message($"{operationName}:");
                Log.Message($"  Calls: {metric.CallCount}");
                Log.Message($"  Total Ticks: {metric.TotalTicks}");
                Log.Message($"  Average Ticks: {metric.AverageTicks:F2}");
                Log.Message($"  Max Ticks: {metric.MaxTicks}");
                Log.Message($"  Min Ticks: {metric.MinTicks}");
                Log.Message($"  Calls per 10s: {metric.CallsPerSecond:F2}");
                Log.Message($"  Ticks per 10s: {metric.TicksPerSecond:F2}");
                Log.Message("");
            }

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
            
            GenerateReport();
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
} 