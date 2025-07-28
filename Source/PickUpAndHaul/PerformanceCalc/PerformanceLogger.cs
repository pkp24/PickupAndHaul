using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using RimWorld;
using Verse;

namespace PickUpAndHaul.Performance
{
    /// <summary>
    /// Thread-safe, high-resolution performance logger for PickUpAndHaul.
    /// Integrates with the existing logging system and clears on startup.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class PerformanceLogger
    {
        // ------------------------------------------------------------------
        // CONFIG
        // ------------------------------------------------------------------
        private static readonly string PerformanceLogPath = Path.Combine(GenFilePaths.SaveDataFolderPath, "PickUpAndHaul.performance.log");
        private const int FlushInterval = 10; // ms
        private const int PeriodicLogInterval = 250; // ticks - log every 250 ticks (about 4 seconds)
        // ------------------------------------------------------------------

        private static readonly Stopwatch _sw = Stopwatch.StartNew();
        private static readonly ConcurrentQueue<string> _queue = new();
        private static readonly Thread _bg;
        private static bool _initialized = false;
        private static int _lastPeriodicLogTick = 0;

        // ---- STATIC CONSTRUCTOR -----------------------------------------
        static PerformanceLogger()
        {
            try
            {
                // Clear the performance log on startup
                ClearPerformanceLog();

                const string header = "timestamp,elapsed_ms,event,count,pawn,thing,map,tick,additional_info\n";
                if (!File.Exists(PerformanceLogPath))
                    File.AppendAllText(PerformanceLogPath, header);

                _bg = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "PUAH-PerformanceLogger"
                };
                _bg.Start();

                _initialized = true;
                Log.Message("Performance logger initialized");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to initialize performance logger: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if the game is currently paused
        /// </summary>
        private static bool IsGamePaused()
        {
            return Find.TickManager?.Paused ?? true;
        }

        /// <summary>
        /// Clear the performance log file on startup
        /// </summary>
        public static void ClearPerformanceLog()
        {
            try
            {
                if (File.Exists(PerformanceLogPath))
                {
                    File.Delete(PerformanceLogPath);
                    Log.Message("Performance log cleared on startup");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to clear performance log: {ex.Message}");
            }
        }

        /// <summary>
        /// Add a performance event to the log. Thread-safe: single enqueue, no locks.
        /// Only logs if game is not paused.
        /// </summary>
        public static void LogPerformance(string evt,
                                         int count = -1,
                                         Pawn pawn = null,
                                         Thing thing = null,
                                         string additionalInfo = "")
        {
            if (!_initialized || IsGamePaused()) return;

            try
            {
                double t = _sw.Elapsed.TotalMilliseconds;
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                string pawnID = pawn?.GetUniqueLoadID() ?? string.Empty;
                string thingID = thing?.GetUniqueLoadID() ?? string.Empty;
                int mapID = pawn?.Map?.uniqueID ??
                           thing?.Map?.uniqueID ??
                           -1;
                int tick = Find.TickManager?.TicksGame ?? -1;

                string line = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1:0.000},{2},{3},{4},{5},{6},{7},{8}\n",
                    timestamp, t, evt, count, pawnID, thingID, mapID, tick, additionalInfo);

                _queue.Enqueue(line);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to log performance event: {ex.Message}");
            }
        }

        /// <summary>
        /// Log periodic haulable counts and inactive pawn counts
        /// Only logs periodically and when game is not paused
        /// </summary>
        public static void LogPeriodicMetrics()
        {
            if (!_initialized || IsGamePaused()) return;

            int currentTick = Find.TickManager?.TicksGame ?? 0;
            if (currentTick - _lastPeriodicLogTick < PeriodicLogInterval) return;

            _lastPeriodicLogTick = currentTick;

            try
            {
                // Count haulable items across all maps
                int totalHaulables = 0;
                foreach (var map in Find.Maps)
                {
                    if (map?.listerHaulables != null)
                    {
                        totalHaulables += map.listerHaulables.ThingsPotentiallyNeedingHauling().Count();
                    }
                }

                // Count inactive pawns
                int inactivePawns = 0;
                if (Find.CurrentMap != null)
                {
                    inactivePawns = Find.CurrentMap.mapPawns.FreeColonistsSpawned
                        .Where(p => !p.Drafted && 
                                   (p.jobs?.curJob == null || 
                                    // Specific inactive job types
                                    p.jobs.curJob.def == JobDefOf.Wait ||
                                    p.jobs.curJob.def == JobDefOf.Wait_Combat ||
                                    p.jobs.curJob.def == JobDefOf.Wait_MaintainPosture ||
                                    p.jobs.curJob.def == JobDefOf.Wait_Wander ||
                                    p.jobs.curJob.def == JobDefOf.Wait_Downed ||
                                    p.jobs.curJob.def == JobDefOf.GotoWander ||
                                    p.jobs.curJob.def == JobDefOf.IdleWhileDespawned ||
                                    p.jobs.curJob.def == JobDefOf.LayDown ||
                                    p.jobs.curJob.def == JobDefOf.LayDownAwake ||
                                    p.jobs.curJob.def == JobDefOf.LayDownResting ||
                                    p.jobs.curJob.def == JobDefOf.Meditate ||
                                    // Jobs that are easily interruptible (likely inactive)
                                    (p.jobs.curJob.def.suspendable && p.jobs.curJob.def.casualInterruptible && 
                                     !p.jobs.curJob.playerForced && p.jobs.curJob.def != JobDefOf.HaulToCell &&
                                     p.jobs.curJob.def != JobDefOf.HaulToContainer)))
                        .Count();
                }

                // Log the periodic metrics
                LogPerformance("PERIODIC_HAULABLES", totalHaulables, null, null, $"Total haulable items across all maps");
                LogPerformance("PERIODIC_INACTIVE_PAWNS", inactivePawns, null, null, $"Inactive colonists on current map");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to log periodic metrics: {ex.Message}");
            }
        }

        /// <summary>
        /// Log hauling performance metrics (legacy - now delegates to periodic logging)
        /// </summary>
        public static void LogHaulPerformance(string operation, int haulableCount, Pawn pawn = null, Thing thing = null, string details = "")
        {
            // Only log non-individual haul operations
            if (operation.Contains("INDIVIDUAL") || operation.Contains("HAUL_TO_INVENTORY"))
                return;
                
            LogPerformance($"HAUL_{operation}", haulableCount, pawn, thing, details);
        }

        /// <summary>
        /// Log cache performance metrics
        /// </summary>
        public static void LogCachePerformance(string operation, int count, Map map = null, string details = "")
        {
            string mapInfo = map != null ? $"Map:{map.uniqueID}" : "";
            string additionalInfo = string.IsNullOrEmpty(details) ? mapInfo : $"{mapInfo};{details}";
            LogPerformance($"CACHE_{operation}", count, null, null, additionalInfo);
        }

        /// <summary>
        /// Log job performance metrics (legacy - now delegates to periodic logging)
        /// </summary>
        public static void LogJobPerformance(string operation, Pawn pawn = null, Job job = null, string details = "")
        {
            // Only log job start/end events, not individual haul operations
            if (operation.Contains("JOB_ON_THING_ATTEMPT") || operation.Contains("JOB_ON_THING_REJECTED"))
                return;
                
            string jobInfo = job != null ? $"Job:{job.def.defName}" : "";
            string additionalInfo = string.IsNullOrEmpty(details) ? jobInfo : $"{jobInfo};{details}";
            LogPerformance($"JOB_{operation}", 1, pawn, null, additionalInfo);
        }

        // ---------------- BACKGROUND WRITER ------------------------------
        private static void WriterLoop()
        {
            try
            {
                using var fs = new FileStream(PerformanceLogPath,
                                            FileMode.Append,
                                            FileAccess.Write,
                                            FileShare.Read);
                using var sw = new StreamWriter(fs) { AutoFlush = false };

                while (true)
                {
                    while (_queue.TryDequeue(out var l))
                        sw.Write(l);

                    sw.Flush();
                    Thread.Sleep(FlushInterval);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Performance logger writer loop failed: {ex.Message}");
            }
        }
    }
} 