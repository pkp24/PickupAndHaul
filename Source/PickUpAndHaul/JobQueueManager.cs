using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI;

namespace PickUpAndHaul
{
    /// <summary>
    /// Manages job queue operations atomically to prevent desync issues
    /// </summary>
    public static class JobQueueManager
    {
        private static readonly object _lockObject = new object();
        
        /// <summary>
        /// Atomically adds items to a job's queues
        /// </summary>
        public static bool AddItemsToJob(Job job, List<Thing> things, List<int> counts, List<LocalTargetInfo> targets, Pawn pawn)
        {
            if (job == null || things == null || counts == null || targets == null)
            {
                Log.Error("[JobQueueManager] AddItemsToJob: Null parameters provided");
                return false;
            }
            
            if (things.Count != counts.Count || things.Count != targets.Count)
            {
                Log.Error($"[JobQueueManager] AddItemsToJob: Count mismatch - things: {things.Count}, counts: {counts.Count}, targets: {targets.Count}");
                return false;
            }
            
            lock (_lockObject)
            {
                try
                {
                    // Initialize queues if they don't exist
                    if (job.targetQueueA == null)
                        job.targetQueueA = new List<LocalTargetInfo>();
                    if (job.countQueue == null)
                        job.countQueue = new List<int>();
                    if (job.targetQueueB == null)
                        job.targetQueueB = new List<LocalTargetInfo>();
                    
                    // Add all items atomically
                    for (int i = 0; i < things.Count; i++)
                    {
                        var thing = things[i];
                        var count = counts[i];
                        var target = targets[i];
                        
                        if (thing == null)
                        {
                            Log.Warning($"[JobQueueManager] AddItemsToJob: Skipping null thing at index {i}");
                            continue;
                        }
                        
                        if (count <= 0)
                        {
                            Log.Warning($"[JobQueueManager] AddItemsToJob: Skipping non-positive count {count} at index {i}");
                            continue;
                        }
                        
                        if (target == null)
                        {
                            Log.Warning($"[JobQueueManager] AddItemsToJob: Skipping null target at index {i}");
                            continue;
                        }
                        
                        job.targetQueueA.Add(new LocalTargetInfo(thing));
                        job.countQueue.Add(count);
                        job.targetQueueB.Add(target);
                    }
                    
                    // Validate synchronization after adding
                    if (job.targetQueueA.Count != job.countQueue.Count)
                    {
                        Log.Error($"[JobQueueManager] AddItemsToJob: Queue synchronization failed after adding items for {pawn}");
                        RollbackJobQueues(job, things.Count);
                        return false;
                    }
                    
                    if (Settings.EnableDebugLogging)
                    {
                        Log.Message($"[JobQueueManager] Successfully added {things.Count} items to job for {pawn}");
                    }
                    
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error($"[JobQueueManager] AddItemsToJob: Exception occurred: {ex.Message}");
                    RollbackJobQueues(job, things.Count);
                    return false;
                }
            }
        }
        
        /// <summary>
        /// Atomically removes items from a job's queues
        /// </summary>
        public static bool RemoveItemsFromJob(Job job, int count, Pawn pawn)
        {
            if (job == null || count <= 0)
            {
                Log.Error("[JobQueueManager] RemoveItemsFromJob: Invalid parameters");
                return false;
            }
            
            lock (_lockObject)
            {
                try
                {
                    if (job.targetQueueA == null || job.countQueue == null || job.targetQueueB == null)
                    {
                        Log.Error("[JobQueueManager] RemoveItemsFromJob: Job queues are null");
                        return false;
                    }
                    
                    var actualCount = Math.Min(count, job.targetQueueA.Count);
                    
                    // Remove items from the end (most recently added)
                    for (int i = 0; i < actualCount; i++)
                    {
                        if (job.targetQueueA.Count > 0)
                            job.targetQueueA.RemoveAt(job.targetQueueA.Count - 1);
                        if (job.countQueue.Count > 0)
                            job.countQueue.RemoveAt(job.countQueue.Count - 1);
                        if (job.targetQueueB.Count > 0)
                            job.targetQueueB.RemoveAt(job.targetQueueB.Count - 1);
                    }
                    
                    // Validate synchronization after removing
                    if (job.targetQueueA.Count != job.countQueue.Count)
                    {
                        Log.Error($"[JobQueueManager] RemoveItemsFromJob: Queue synchronization failed after removing items for {pawn}");
                        return false;
                    }
                    
                    if (Settings.EnableDebugLogging)
                    {
                        Log.Message($"[JobQueueManager] Successfully removed {actualCount} items from job for {pawn}");
                    }
                    
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error($"[JobQueueManager] RemoveItemsFromJob: Exception occurred: {ex.Message}");
                    return false;
                }
            }
        }
        
        /// <summary>
        /// Atomically clears all items from a job's queues
        /// </summary>
        public static void ClearJobQueues(Job job, Pawn pawn)
        {
            if (job == null) return;
            
            lock (_lockObject)
            {
                try
                {
                    job.targetQueueA?.Clear();
                    job.countQueue?.Clear();
                    job.targetQueueB?.Clear();
                    
                    if (Settings.EnableDebugLogging)
                    {
                        Log.Message($"[JobQueueManager] Cleared all job queues for {pawn}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[JobQueueManager] ClearJobQueues: Exception occurred: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Validates job queue synchronization
        /// </summary>
        public static bool ValidateJobQueues(Job job, Pawn pawn)
        {
            if (job == null) return false;
            
            lock (_lockObject)
            {
                try
                {
                    if (job.targetQueueA == null || job.countQueue == null)
                    {
                        Log.Error($"[JobQueueManager] ValidateJobQueues: Job queues are null for {pawn}");
                        return false;
                    }
                    
                    if (job.targetQueueA.Count != job.countQueue.Count)
                    {
                        Log.Error($"[JobQueueManager] ValidateJobQueues: Queue synchronization failure for {pawn} - targetQueueA.Count ({job.targetQueueA.Count}) != countQueue.Count ({job.countQueue.Count})");
                        return false;
                    }
                    
                    // Check for null or invalid targets
                    for (int i = 0; i < job.targetQueueA.Count; i++)
                    {
                        var target = job.targetQueueA[i];
                        if (target == null || target.Thing == null)
                        {
                            Log.Error($"[JobQueueManager] ValidateJobQueues: Found null target at index {i} for {pawn}");
                            return false;
                        }
                        
                        if (target.Thing.Destroyed || !target.Thing.Spawned)
                        {
                            Log.Warning($"[JobQueueManager] ValidateJobQueues: Found destroyed/unspawned target {target.Thing} at index {i} for {pawn}");
                        }
                    }
                    
                    // Check for non-positive counts
                    for (int i = 0; i < job.countQueue.Count; i++)
                    {
                        if (job.countQueue[i] <= 0)
                        {
                            Log.Error($"[JobQueueManager] ValidateJobQueues: Found non-positive count {job.countQueue[i]} at index {i} for {pawn}");
                            return false;
                        }
                    }
                    
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error($"[JobQueueManager] ValidateJobQueues: Exception occurred: {ex.Message}");
                    return false;
                }
            }
        }
        
        /// <summary>
        /// Rolls back job queues by removing the specified number of items
        /// </summary>
        private static void RollbackJobQueues(Job job, int count)
        {
            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (job.targetQueueA?.Count > 0)
                        job.targetQueueA.RemoveAt(job.targetQueueA.Count - 1);
                    if (job.countQueue?.Count > 0)
                        job.countQueue.RemoveAt(job.countQueue.Count - 1);
                    if (job.targetQueueB?.Count > 0)
                        job.targetQueueB.RemoveAt(job.targetQueueB.Count - 1);
                }
                
                Log.Message($"[JobQueueManager] Rolled back {count} items from job queues");
            }
            catch (Exception ex)
            {
                Log.Error($"[JobQueueManager] RollbackJobQueues: Exception occurred: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Gets debug information about job queues
        /// </summary>
        public static string GetJobQueueDebugInfo(Job job, Pawn pawn)
        {
            if (job == null) return "Job is null";
            
            lock (_lockObject)
            {
                var info = new System.Text.StringBuilder();
                info.AppendLine($"[JobQueueManager] Job queue debug info for {pawn}:");
                info.AppendLine($"  targetQueueA: {job.targetQueueA?.Count ?? 0} items");
                info.AppendLine($"  countQueue: {job.countQueue?.Count ?? 0} items");
                info.AppendLine($"  targetQueueB: {job.targetQueueB?.Count ?? 0} items");
                
                if (job.targetQueueA != null && job.countQueue != null)
                {
                    info.AppendLine($"  Synchronized: {job.targetQueueA.Count == job.countQueue.Count}");
                    
                    if (job.targetQueueA.Count > 0)
                    {
                        info.AppendLine("  targetQueueA contents:");
                        for (int i = 0; i < Math.Min(job.targetQueueA.Count, 5); i++)
                        {
                            info.AppendLine($"    [{i}]: {job.targetQueueA[i]}");
                        }
                        
                        info.AppendLine("  countQueue contents:");
                        for (int i = 0; i < Math.Min(job.countQueue.Count, 5); i++)
                        {
                            info.AppendLine($"    [{i}]: {job.countQueue[i]}");
                        }
                    }
                }
                
                return info.ToString();
            }
        }
    }
} 