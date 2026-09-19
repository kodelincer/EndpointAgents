@echo off
setlocal
title Administrator: office-activation-repair.bat

echo EndpointAgents sample Office activation repair
echo.
echo 1. Office activation problem
echo 2. Sample unused action
echo 3. Exit
echo.
set /p "selection=Select an item: "

if "%selection%"=="1" goto office_activation
if "%selection%"=="2" goto unused
exit /b 2

:office_activation
echo SAMPLE ONLY: no Office settings will be changed.
choice /c YN /n /m "Continue with harmless sample? [Y/N]: "
if errorlevel 2 exit /b 4
echo Harmless sample completed.
exit /b 0

:unused
echo No action was performed.
exit /b 3
