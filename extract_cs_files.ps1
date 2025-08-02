# Script to extract all .cs files from Source directories into a single text file
# This script will find all .cs files in Source and subdirectories and output them with neat formatting

param(
    [string]$OutputFile = "all_cs_files.txt"
)

Write-Host "Starting extraction of .cs files..." -ForegroundColor Green

# Clear the output file if it exists
if (Test-Path $OutputFile) {
    Remove-Item $OutputFile -Force
    Write-Host "Removed existing output file: $OutputFile" -ForegroundColor Yellow
}

# Function to write a file with header
function Write-FileWithHeader {
    param(
        [string]$FilePath,
        [string]$OutputPath
    )
    
    $relativePath = $FilePath.Replace((Get-Location).Path, "").TrimStart("\")
    $separator = "=" * 80
    
    Add-Content -Path $OutputPath -Value ""
    Add-Content -Path $OutputPath -Value $separator
    Add-Content -Path $OutputPath -Value "FILE: $relativePath"
    Add-Content -Path $OutputPath -Value $separator
    Add-Content -Path $OutputPath -Value ""
    
    # Read and write the file content
    $content = Get-Content -Path $FilePath -Raw
    Add-Content -Path $OutputPath -Value $content
    
    Add-Content -Path $OutputPath -Value ""
    Add-Content -Path $OutputPath -Value ""
}

# Find all .cs files in Source directory and subdirectories
$csFiles = Get-ChildItem -Path "Source" -Filter "*.cs" -Recurse | Sort-Object FullName

Write-Host "Found $($csFiles.Count) .cs files" -ForegroundColor Cyan

# Create header for the output file
$header = @"
PICKUP AND HAUL MOD - ALL C# SOURCE FILES
==========================================
Extracted on: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Total files: $($csFiles.Count)

"@

Add-Content -Path $OutputFile -Value $header

# Process each .cs file
$fileCount = 0
foreach ($file in $csFiles) {
    $fileCount++
    Write-Host "Processing ($fileCount/$($csFiles.Count)): $($file.Name)" -ForegroundColor White
    
    try {
        Write-FileWithHeader -FilePath $file.FullName -OutputPath $OutputFile
    }
    catch {
        Write-Host "Error processing $($file.FullName): $($_.Exception.Message)" -ForegroundColor Red
    }
}

# Add footer
$footer = @"

==========================================
END OF FILES
==========================================
Extraction completed on: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Total files processed: $fileCount
"@

Add-Content -Path $OutputFile -Value $footer

Write-Host ""
Write-Host "Extraction completed successfully!" -ForegroundColor Green
Write-Host "Output file: $OutputFile" -ForegroundColor Cyan
Write-Host "Total files processed: $fileCount" -ForegroundColor Cyan

# Get file size
$fileSize = (Get-Item $OutputFile).Length
$fileSizeKB = [math]::Round($fileSize / 1KB, 2)
Write-Host "Output file size: $fileSizeKB KB" -ForegroundColor Cyan 