@echo off
rem Harmless menu sample for testing playbook scenarios. It changes nothing.
rem Test switches: SAMPLE_PAUSE=1 shows an optional "Press any key" screen,
rem SAMPLE_PAGES=n shows n notice pages, SAMPLE_CONFIGURED=1 shows a different
rem settings prompt.
setlocal EnableExtensions
if not defined SAMPLE_PAGES set "SAMPLE_PAGES=0"

:pages
if %SAMPLE_PAGES% LEQ 0 goto main
echo Notice page %SAMPLE_PAGES%
<nul set /p "=Press N for the next page: "
set "page="
set /p "page="
if /i not "%page%"=="N" echo Unexpected page answer [%page%]& exit /b 20
set /a SAMPLE_PAGES-=1
goto pages

:main
echo.
echo === Sample maintenance menu ===
echo 1. Settings
echo 2. Run task
echo 3. Run task that reports a failure
echo 4. Run task that stops with exit code 7
echo 5. Run task that asks for a server name
echo 9. Exit
set "choice="
set /p "choice=Main menu choice: "
if "%choice%"=="1" goto settings
if "%choice%"=="2" goto task
if "%choice%"=="3" goto failing
if "%choice%"=="4" goto stopped
if "%choice%"=="5" goto server
if "%choice%"=="9" echo Bye.& exit /b 0
echo Invalid choice [%choice%]
goto main

:settings
if "%SAMPLE_CONFIGURED%"=="1" goto configured
echo.
echo Settings menu: A. Enable logging  B. Back
<nul set /p "=Settings choice: "
set "setting="
set /p "setting="
if /i "%setting%"=="A" echo Logging enabled.
goto main

:configured
choice /c YN /n /m "Already configured. Reconfigure? [Y/N]: "
if errorlevel 2 echo Kept current settings.& goto main
echo Settings reset.
goto main

:task
echo Running sample task...
if "%SAMPLE_PAUSE%"=="1" pause
echo Task completed successfully.
exit /b 0

:failing
echo Running sample task...
echo Task FAILED: sample error 0x80070005
exit /b 0

:stopped
echo Task stopped.
exit /b 7

:server
set "server="
set /p "server=Enter the server name: "
echo Using server [%server%]
exit /b 0
