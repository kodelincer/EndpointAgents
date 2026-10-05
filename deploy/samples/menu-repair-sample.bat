@echo off
rem SIMULATION ONLY. A harmless mock "Office repair tool" for developing and testing
rem playbooks. It prints text and changes nothing on the machine.
rem Test switches: SAMPLE_DIALOG=1 shows a message box that closes itself after 3 s,
rem SAMPLE_FAIL=1 makes the simulated repair report an error.
setlocal EnableExtensions
title Office repair tool (simulation)
rem ANSI escape for the green result line; the runner strips colour codes.
for /f %%a in ('echo prompt $E^| cmd') do set "ESC=%%a"

echo Office repair tool (simulation) is starting...
>nul ping -n 2 127.0.0.1
echo Extracting files, please wait...
>nul ping -n 2 127.0.0.1
echo Tool version 1.0 (simulation) on %OS% %PROCESSOR_ARCHITECTURE%

:main
echo.
echo ============== MAIN MENU ==============
echo  [1] Office repair
echo  [2] Show information
echo  [3] Check disk space
echo  [4] Settings
echo  [5] Help
echo  [6] Exit
echo =======================================
set "choice="
set /p "choice=Select an option [1-6]: "
if "%choice%"=="1" goto repair
if "%choice%"=="6" echo Goodbye.& exit /b 0
if "%choice%"=="" goto main
echo Option not available in the simulation.
goto main

:repair
echo.
echo ============ OFFICE REPAIR ============
echo  [1] Reset Office cache
echo  [2] Check Office settings
echo  [3] Clear recent file list
echo  [4] Rebuild templates
echo  [5] Show repair log
echo  [6] Advanced
echo  [7] Exit
echo  [8] Back to main menu
echo =======================================
set "item="
set /p "item=Select an option [1-8]: "
if "%item%"=="1" goto area
if "%item%"=="7" echo Goodbye.& exit /b 0
if "%item%"=="8" goto main
if "%item%"=="" goto repair
echo Option not available in the simulation.
goto repair

:area
echo.
choice /c ASB /n /m "Which area? [A]pps [S]ettings [B]oth : "
if errorlevel 3 goto run
if errorlevel 2 goto run
:run
if "%SAMPLE_DIALOG%"=="1" powershell -NoProfile -Command "(New-Object -ComObject WScript.Shell).Popup('Cache reset (simulation)', 3, 'Information', 64) | Out-Null"
cls
echo Please wait...
>nul ping -n 2 127.0.0.1
echo Cache reset started, please wait...
>nul ping -n 2 127.0.0.1
echo Resetting cache (simulated, nothing is changed)...
>nul ping -n 2 127.0.0.1
if "%SAMPLE_FAIL%"=="1" (
    echo Repair failed. Error 0x80070005 [simulated]
) else (
    echo %ESC%[42;97m Repair completed successfully [simulated] %ESC%[0m
)
pause
goto repair
