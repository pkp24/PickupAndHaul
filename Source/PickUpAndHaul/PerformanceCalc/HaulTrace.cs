
// HaulTrace.cs  -- performance trace helper for PUAH (legacy compatibility)
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using RimWorld;
using Verse;

namespace PickUpAndHaul.Performance
{
    /// <summary>
    /// Thread‑safe, high‑resolution logger for comparing hauling algorithms.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class HaulTrace
    {
        // ------------------------------------------------------------------
        // CONFIG
        // ------------------------------------------------------------------
        private const string FileName      = "HaulTrace.csv";
        private const int    FlushInterval = 10;              // ms
        // ------------------------------------------------------------------

        private static readonly Stopwatch               _sw    = Stopwatch.StartNew();
        private static readonly ConcurrentQueue<string> _queue = new();
        private static readonly Thread                  _bg;

        // ---- STATIC CONSTRUCTOR -----------------------------------------
        static HaulTrace()
        {
            const string header = "elapsed_ms,event,count,pawn,thing,map,tick\n";
            if (!File.Exists(FileName))
                File.AppendAllText(FileName, header);

            _bg = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name         = "PUAH‑HaulTrace"
            };
            _bg.Start();
        }

        /// <summary>
        /// Add a line to the log. Thread‑safe: single enqueue, no locks.
        /// Legacy method - now delegates to PerformanceLogger
        /// </summary>
        public static void Log(string evt,
                               int    count = -1,
                               Pawn   pawn  = null,
                               Thing  thing = null)
        {
            // Delegate to the new PerformanceLogger for better integration
            PerformanceLogger.LogPerformance(evt, count, pawn, thing);
        }

        // ---------------- BACKGROUND WRITER ------------------------------
        private static void WriterLoop()
        {
            using var fs = new FileStream(FileName,
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
    }
}
