@echo off
echo Extracting all .cs files from Source directories...
echo.

powershell -ExecutionPolicy Bypass -File extract_cs_files.ps1

echo.
echo Press any key to exit...
pause >nul 