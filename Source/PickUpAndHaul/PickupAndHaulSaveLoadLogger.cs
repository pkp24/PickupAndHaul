using Verse;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse.AI;

namespace PickUpAndHaul
{
    public class PickupAndHaulSaveLoadLogger : GameComponent
    {
        private static readonly object _jobLock = new object();
        private static List<JobInfo> _suspendedJobs = new List<JobInfo>();
        private static bool _isSaving = false;
        private static bool _modRemoved = false;

        public PickupAndHaulSaveLoadLogger() : base() { }
        public PickupAndHaulSaveLoadLogger(Game game) : base() { }

        public override void ExposeData()
        {
            // NEVER save any data for this component
            // This ensures the component is never written to save files
            // and prevents any issues when the mod is removed
            
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                Log.Message("[PickUpAndHaul] Skipping save of GameComponent data");
                return;
            }
            
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                Log.Message("[PickUpAndHaul] Skipping load of GameComponent data");
                return;
            }
            
            // Only perform operations during normal gameplay, not during save/load
            if (Scribe.mode == LoadSaveMode.Inactive)
            {
                // Perform safety check
                PerformSafetyCheck();
                
                // Don't save any mod-specific data if the mod is being removed
                if (_modRemoved)
                {
                    Verse.Log.Warning("[PickUpAndHaul] Mod removed, skipping operations");
                    return;
                }
            }
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            Log.Message("[PickUpAndHaul] GameComponent: On Load (FinalizeInit)");
            
            // Perform safety check to ensure mod is active
            PerformSafetyCheck();
        }

        /// <summary>
        /// Temporarily suspends all pickup and haul jobs to prevent save conflicts
        /// </summary>
        private static void SuspendPickupAndHaulJobs()
        {
            lock (_jobLock)
            {
                if (_isSaving)
                {
                    Verse.Log.Warning("[PickUpAndHaul] Save operation already in progress, skipping job suspension");
                    return;
                }

                _isSaving = true;
                _suspendedJobs.Clear();

                try
                {
                    // Get all maps and their pawns
                    var maps = Find.Maps;
                    foreach (var map in maps)
                    {
                        var pawns = map.mapPawns.FreeColonistsAndPrisonersSpawned.ToList();
                        
                        foreach (var pawn in pawns)
                        {
                            if (pawn?.jobs?.curJob == null) continue;

                            var currentJob = pawn.jobs.curJob;
                            
                            // Check if this is a pickup and haul job
                            if (IsPickupAndHaulJob(currentJob))
                            {
                                var jobInfo = new JobInfo
                                {
                                    Pawn = pawn,
                                    Job = currentJob,
                                    JobQueue = pawn.jobs.jobQueue.ToList(),
                                    JobDef = currentJob.def
                                };

                                _suspendedJobs.Add(jobInfo);
                                
                                // End the current job safely
                                if (pawn.jobs.curDriver != null)
                                {
                                    pawn.jobs.curDriver.EndJobWith(JobCondition.InterruptForced);
                                }
                                
                                // Clear the job queue
                                pawn.jobs.jobQueue.Clear(pawn, true);
                                
                                Log.Message($"[PickUpAndHaul] Suspended job for {pawn.NameShortColored}: {currentJob.def.defName}");
                            }
                        }
                    }

                    Log.Message($"[PickUpAndHaul] Suspended {_suspendedJobs.Count} pickup and haul jobs for save operation");
                }
                catch (Exception ex)
                {
                    Verse.Log.Error($"[PickUpAndHaul] Error during job suspension: {ex.Message}");
                    _isSaving = false;
                }
            }
        }

        /// <summary>
        /// Restores previously suspended pickup and haul jobs after save completion
        /// </summary>
        private static void RestorePickupAndHaulJobs()
        {
            lock (_jobLock)
            {
                if (!_isSaving)
                {
                    Verse.Log.Warning("[PickUpAndHaul] No save operation in progress, skipping job restoration");
                    return;
                }

                try
                {
                    var restoredCount = 0;
                    var failedCount = 0;

                    foreach (var jobInfo in _suspendedJobs)
                    {
                        if (jobInfo.Pawn?.Spawned == true && jobInfo.Pawn.Map != null)
                        {
                            // Restore the job queue
                            jobInfo.Pawn.jobs.jobQueue.Clear(jobInfo.Pawn, true);
                            foreach (var queuedJob in jobInfo.JobQueue)
                            {
                                jobInfo.Pawn.jobs.jobQueue.EnqueueLast(queuedJob.job, queuedJob.tag);
                            }

                            // Try to restart the main job if the pawn is available
                            if (jobInfo.Pawn.jobs.curJob == null && 
                                jobInfo.Job?.targetA != null && 
                                jobInfo.Pawn.CanReserveAndReach(jobInfo.Job.targetA, PathEndMode.ClosestTouch, Danger.Deadly))
                            {
                                var newJob = JobMaker.MakeJob(jobInfo.JobDef, jobInfo.Job.targetA, jobInfo.Job.targetB);
                                
                                // Copy job properties safely
                                if (jobInfo.Job.targetQueueA != null)
                                    newJob.targetQueueA = new List<LocalTargetInfo>(jobInfo.Job.targetQueueA);
                                if (jobInfo.Job.targetQueueB != null)
                                    newJob.targetQueueB = new List<LocalTargetInfo>(jobInfo.Job.targetQueueB);
                                if (jobInfo.Job.countQueue != null)
                                    newJob.countQueue = new List<int>(jobInfo.Job.countQueue);
                                
                                newJob.count = jobInfo.Job.count;
                                newJob.haulMode = jobInfo.Job.haulMode;

                                if (newJob.TryMakePreToilReservations(jobInfo.Pawn, false))
                                {
                                    jobInfo.Pawn.jobs.StartJob(newJob, JobCondition.InterruptForced);
                                    Log.Message($"[PickUpAndHaul] Restored job for {jobInfo.Pawn.NameShortColored}: {jobInfo.JobDef.defName}");
                                    restoredCount++;
                                }
                                else
                                {
                                    Verse.Log.Warning($"[PickUpAndHaul] Failed to restore job for {jobInfo.Pawn.NameShortColored}: reservation failed");
                                    failedCount++;
                                }
                            }
                            else
                            {
                                Log.Message($"[PickUpAndHaul] Could not restore job for {jobInfo.Pawn.NameShortColored}: pawn unavailable or already has job");
                                failedCount++;
                            }
                        }
                        else
                        {
                            Verse.Log.Warning($"[PickUpAndHaul] Could not restore job: pawn no longer spawned or valid");
                            failedCount++;
                        }
                    }

                    Log.Message($"[PickUpAndHaul] Job restoration complete: {restoredCount} restored, {failedCount} failed");
                }
                catch (Exception ex)
                {
                    Verse.Log.Error($"[PickUpAndHaul] Error during job restoration: {ex.Message}");
                }
                finally
                {
                    _suspendedJobs.Clear();
                    _isSaving = false;
                }
            }
        }

        /// <summary>
        /// Checks if a job is a pickup and haul job that should be suspended during save
        /// </summary>
        private static bool IsPickupAndHaulJob(Job job)
        {
            if (job?.def == null) return false;
            
            return job.def.defName == "HaulToInventory" || 
                   job.def.defName == "UnloadYourHauledInventory";
        }

        /// <summary>
        /// Provides a public method to check if a save operation is in progress
        /// </summary>
        public static bool IsSaveInProgress()
        {
            lock (_jobLock)
            {
                return _isSaving;
            }
        }

        /// <summary>
        /// Provides a public method to manually suspend jobs (for external use)
        /// </summary>
        public static void ManualSuspendJobs()
        {
            SuspendPickupAndHaulJobs();
        }

        /// <summary>
        /// Provides a public method to manually restore jobs (for external use)
        /// </summary>
        public static void ManualRestoreJobs()
        {
            RestorePickupAndHaulJobs();
        }

        /// <summary>
        /// Emergency cleanup method to reset the save state if something goes wrong
        /// </summary>
        public static void EmergencyCleanup()
        {
            lock (_jobLock)
            {
                Verse.Log.Warning("[PickUpAndHaul] Performing emergency cleanup of save state");
                _suspendedJobs.Clear();
                _isSaving = false;
            }
        }

        /// <summary>
        /// Gets the current status of the save system for debugging
        /// </summary>
        public static string GetSaveStatus()
        {
            lock (_jobLock)
            {
                return $"Save in progress: {_isSaving}, Suspended jobs: {_suspendedJobs.Count}, Mod removed: {_modRemoved}";
            }
        }

        /// <summary>
        /// Marks the mod as removed to prevent save data corruption
        /// </summary>
        public static void MarkModAsRemoved()
        {
            lock (_jobLock)
            {
                Verse.Log.Warning("[PickUpAndHaul] Mod marked as removed, preventing save data corruption");
                _modRemoved = true;
                _suspendedJobs.Clear();
                _isSaving = false;
            }
        }

        /// <summary>
        /// Checks if the mod is currently active and available
        /// </summary>
        public static bool IsModActive()
        {
            try
            {
                // Check if our key types are available
                var haulToInventoryDef = DefDatabase<JobDef>.GetNamedSilentFail("HaulToInventory");
                var unloadDef = DefDatabase<JobDef>.GetNamedSilentFail("UnloadYourHauledInventory");
                
                return haulToInventoryDef != null && unloadDef != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Performs a safety check and cleanup if the mod is not active
        /// </summary>
        public static void PerformSafetyCheck()
        {
            if (!IsModActive())
            {
                Verse.Log.Warning("[PickUpAndHaul] Mod appears to be inactive, performing safety cleanup");
                MarkModAsRemoved();
                
                // Clear any remaining mod-specific jobs from all pawns
                try
                {
                    var maps = Find.Maps;
                    foreach (var map in maps)
                    {
                        var pawns = map.mapPawns.FreeColonistsAndPrisonersSpawned.ToList();
                        foreach (var pawn in pawns)
                        {
                            if (pawn?.jobs?.curJob?.def != null)
                            {
                                var jobDef = pawn.jobs.curJob.def;
                                if (jobDef.defName == "HaulToInventory" || jobDef.defName == "UnloadYourHauledInventory")
                                {
                                    Verse.Log.Warning($"[PickUpAndHaul] Clearing mod-specific job from {pawn.NameShortColored}");
                                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, false, false);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Verse.Log.Error($"[PickUpAndHaul] Error during safety cleanup: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Helper class to store job information during suspension
    /// </summary>
    public class JobInfo
    {
        public Pawn Pawn { get; set; }
        public Job Job { get; set; }
        public List<QueuedJob> JobQueue { get; set; }
        public JobDef JobDef { get; set; }
    }
} 