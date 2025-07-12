#!/usr/bin/env python3
"""
Script to create a clean summary of Jaundice's hauling activities from PickUpAndHaul logs.
Always analyzes 'logs.txt' in the current directory and writes output to 'jaundice_complete_summary.txt'.
"""

import re
import sys
from pathlib import Path
from collections import defaultdict

def analyze_jaundice_logs(log_file_path, output_file_path):
    output_lines = []
    jaundice_activities = []
    hauling_items = []
    job_stats = defaultdict(int)
    hauled_item_ids = set()  # Track the specific item IDs that Jaundice hauled
    all_lines = []  # Store all lines for second pass
    
    # Patterns to match
    jaundice_pattern = re.compile(r'.*Jaundice.*', re.IGNORECASE)
    hauling_pattern = re.compile(r'Jaundice is hauling to inventory (.+?):(\d+)', re.IGNORECASE)
    job_found_pattern = re.compile(r'Jaundice job found to haul: (.+?) to \(.+?\):(\d+)', re.IGNORECASE)
    validation_pattern = re.compile(r'VALIDATION \[Job Creation\]: Jaundice - targetQueueA: (\d+), targetQueueB: (\d+), countQueue: (\d+)', re.IGNORECASE)
    potential_items_pattern = re.compile(r'PotentialWorkThingsGlobal for Jaundice at \(.+?\) found (\d+) items', re.IGNORECASE)
    
    # First pass: collect all item IDs that Jaundice hauls, and gather stats
    try:
        with open(log_file_path, 'r', encoding='utf-8') as file:
            for line_num, line in enumerate(file, 1):
                line = line.strip()
                all_lines.append((line_num, line))
                if not line:
                    continue
                
                # Check if line contains Jaundice
                if jaundice_pattern.search(line):
                    jaundice_activities.append((line_num, line))
                    
                    # Extract hauling items
                    hauling_match = hauling_pattern.search(line)
                    if hauling_match:
                        item_name = hauling_match.group(1)
                        quantity = int(hauling_match.group(2))
                        hauling_items.append((item_name, quantity))
                        hauled_item_ids.add(item_name)  # Add to set of hauled items
                    
                    # Extract job found info
                    job_match = job_found_pattern.search(line)
                    if job_match:
                        job_stats['jobs_found'] += 1
                    
                    # Extract validation stats
                    validation_match = validation_pattern.search(line)
                    if validation_match:
                        job_stats['target_queue_a'] = max(job_stats['target_queue_a'], int(validation_match.group(1)))
                        job_stats['target_queue_b'] = max(job_stats['target_queue_b'], int(validation_match.group(2)))
                        job_stats['count_queue'] = max(job_stats['count_queue'], int(validation_match.group(3)))
                        job_stats['validation_entries'] += 1
                    
                    # Extract potential items found
                    potential_match = potential_items_pattern.search(line)
                    if potential_match:
                        items_found = int(potential_match.group(1))
                        job_stats['potential_items_found'] = max(job_stats['potential_items_found'], items_found)
    except FileNotFoundError:
        print(f"Error: Log file '{log_file_path}' not found.")
        return
    except Exception as e:
        print(f"Error reading log file: {e}")
        return

    # Second pass: collect all log lines mentioning any of the hauled item IDs
    item_related_logs = []
    if hauled_item_ids:
        for line_num, line in all_lines:
            for item_id in hauled_item_ids:
                if item_id in line:
                    item_related_logs.append((line_num, line))
                    break  # Avoid duplicate entries for the same line

    # Generate summary
    output_lines.append("=" * 80)
    output_lines.append("JAUNDICE HAULING SUMMARY")
    output_lines.append("=" * 80)
    
    output_lines.append(f"\nSTATISTICS:")
    output_lines.append(f"   * Total Jaundice-related log entries: {len(jaundice_activities)}")
    output_lines.append(f"   * Jobs found: {job_stats['jobs_found']}")
    output_lines.append(f"   * Validation entries: {job_stats['validation_entries']}")
    output_lines.append(f"   * Maximum potential items found: {job_stats['potential_items_found']}")
    output_lines.append(f"   * Max targetQueueA: {job_stats['target_queue_a']}")
    output_lines.append(f"   * Max targetQueueB: {job_stats['target_queue_b']}")
    output_lines.append(f"   * Max countQueue: {job_stats['count_queue']}")
    
    output_lines.append(f"\nITEMS HAULED ({len(hauling_items)} total):")
    item_summary = defaultdict(int)
    for item_name, quantity in hauling_items:
        item_summary[item_name] += quantity
        output_lines.append(f"   * {item_name}: {quantity}")
    
    output_lines.append(f"\nITEM SUMMARY:")
    for item_name, total_quantity in item_summary.items():
        output_lines.append(f"   * {item_name}: {total_quantity} total")
    
    output_lines.append(f"\nKEY JAUNDICE ACTIVITIES:")
    for line_num, line in jaundice_activities:
        if any(keyword in line.lower() for keyword in ['job found to haul', 'is hauling to inventory', 'validation [job creation]', 'potentialworkthingsglobal']):
            output_lines.append(f"   [Line {line_num:4d}] {line}")
    
    output_lines.append(f"\nALL LOGS RELATED TO HAULED ITEMS ({len(item_related_logs)} entries):")
    output_lines.append("   (Includes all interactions with the specific items Jaundice hauled)")
    for line_num, line in item_related_logs:
        output_lines.append(f"   [Line {line_num:4d}] {line}")

    # Print to terminal
    for line in output_lines:
        print(line)
    # Write to file
    try:
        with open(output_file_path, 'w', encoding='utf-8') as f:
            for line in output_lines:
                f.write(line + '\n')
        print(f"\nOutput also written to: {output_file_path}")
    except Exception as e:
        print(f"Error writing to output file: {e}")

if __name__ == "__main__":
    log_file_path = "logs.txt"
    output_file_path = "jaundice_complete_summary.txt"
    if not Path(log_file_path).exists():
        print(f"Error: File '{log_file_path}' does not exist in the current directory.")
        sys.exit(1)
    analyze_jaundice_logs(log_file_path, output_file_path) 