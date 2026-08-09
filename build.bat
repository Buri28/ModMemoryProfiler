@echo off
setlocal

rem NOTE: keep this file ASCII-only. .bat is parsed with the console OEM codepage,
rem so non-ASCII comments get mangled and break parsing.

set "SCRIPT_DIR=%~dp0"
set "PROJECT_FILE=%SCRIPT_DIR%ModMemoryProfiler\ModMemoryProfiler.csproj"
set "CONFIGURATION=%~1"

if "%CONFIGURATION%"=="" set "CONFIGURATION=Debug"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo dotnet command was not found.
    exit /b 1
)

dotnet build "%PROJECT_FILE%" -c %CONFIGURATION% /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary
if errorlevel 1 exit /b %ERRORLEVEL%

set "OUTPUT_DLL=%SCRIPT_DIR%ModMemoryProfiler\bin\%CONFIGURATION%\net48\net48\ModMemoryProfiler.dll"

rem Deploy target. Defaults to the dev machine install; override with BEATSABER_PATH.
set "DEPLOY_ROOT=%BEATSABER_PATH%"
if "%DEPLOY_ROOT%"=="" set "DEPLOY_ROOT=D:\BSManager\BSInstances\1.40.8"

set "PLUGINS_DIR=%DEPLOY_ROOT%\Plugins"

if not exist "%PLUGINS_DIR%" (
    echo.
    echo Plugins folder not found: %PLUGINS_DIR%
    echo Set BEATSABER_PATH to your Beat Saber install. Skipping deploy.
    echo Built: %OUTPUT_DLL%
    exit /b 0
)

echo Copying to %PLUGINS_DIR%...
copy /y "%OUTPUT_DLL%" "%PLUGINS_DIR%\"
if errorlevel 1 (
    echo Failed to copy DLL.
    exit /b 1
)
echo Done.
exit /b 0
