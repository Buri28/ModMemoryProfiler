@echo off
rem Launcher for proc-memlog.ps1.
rem Double-click to start. Optional argument: sampling interval in seconds.
rem   proc-memlog.bat        -> 15 second interval
rem   proc-memlog.bat 10     -> 10 second interval
rem
rem NOTE: keep this file ASCII-only. .bat is parsed with the console OEM codepage,
rem so non-ASCII comments get mangled and break parsing.

setlocal

set "SCRIPT_DIR=%~dp0"
set "PS1=%SCRIPT_DIR%proc-memlog.ps1"

if not exist "%PS1%" (
    echo Cannot find proc-memlog.ps1 next to this file.
    echo Expected: %PS1%
    pause
    exit /b 1
)

set "INTERVAL=%~1"
if "%INTERVAL%"=="" set "INTERVAL=15"

echo ModMemoryProfiler - process / GPU logger
echo   interval : %INTERVAL% sec
echo   output   : %SCRIPT_DIR%
echo.
echo Start Beat Saber. Logging begins automatically and stops when the game exits.
echo Press Ctrl+C to stop early.
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" -IntervalSec %INTERVAL%

echo.
echo Done. Press any key to close.
pause >nul
