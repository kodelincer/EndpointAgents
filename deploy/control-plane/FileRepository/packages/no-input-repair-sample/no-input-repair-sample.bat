@echo off
rem SIMULATION ONLY. A harmless mock "print spooler reset" for testing no-input
rem packages. It prints progress, never asks for input on its own, and changes
rem nothing on the machine. Optional first argument /quiet skips the banner.
rem Test switches: SAMPLE_FAIL=1 reports an error, SAMPLE_RESTART=1 exits with 3010
rem ("succeeded, restart needed").
setlocal EnableExtensions

if /i not "%~1"=="/quiet" echo Print spooler reset (simulation)
echo Stopping the print spooler...
>nul ping -n 2 127.0.0.1
echo Clearing stuck print jobs...
>nul ping -n 2 127.0.0.1
if "%SAMPLE_FAIL%"=="1" (
    echo ERROR: the spooler did not start.
    exit /b 1
)
echo Starting the print spooler...
>nul ping -n 2 127.0.0.1
echo Spooler reset completed.
rem A vendor BAT often ends with pause. Unattended runs close input at start, so
rem this returns at once instead of waiting for a key.
pause
if "%SAMPLE_RESTART%"=="1" exit /b 3010
exit /b 0
