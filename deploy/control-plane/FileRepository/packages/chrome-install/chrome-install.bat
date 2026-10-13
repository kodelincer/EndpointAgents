@echo off
setlocal
rem Installs Google Chrome for all users from the enterprise MSI shipped next to this
rem file, unless Chrome is already installed. Runs unattended as LocalSystem.
rem Exit codes: 0 installed now, 10 already installed, 3010 installed and a restart
rem is needed, 1 failed. Every success prints "Chrome install completed."

set "MSI=%~dp0GoogleChromeStandaloneEnterprise64.msi"
set "LOGDIR=%ProgramData%\EndpointAgents\Logs"
set "LOG=%LOGDIR%\chrome-install.log"

call :find_chrome
if defined CHROME goto already_installed

if /i "%PROCESSOR_ARCHITECTURE%"=="x86" if not defined PROCESSOR_ARCHITEW6432 goto x86
if not exist "%MSI%" goto no_msi
if not exist "%LOGDIR%" mkdir "%LOGDIR%"

echo Installing Google Chrome for all users...
"%SystemRoot%\System32\msiexec.exe" /i "%MSI%" /qn /norestart /l*v "%LOG%"
set "RESULT=%ERRORLEVEL%"
echo msiexec exit code %RESULT%.
if "%RESULT%"=="1618" goto busy
if "%RESULT%"=="3010" goto verify
if not "%RESULT%"=="0" goto msi_failed

:verify
call :find_chrome
if not defined CHROME goto missing_after_install
echo Chrome is installed at "%CHROME%".
echo Chrome install completed.
exit /b %RESULT%

:already_installed
echo Chrome is already installed at "%CHROME%".
echo Chrome install completed.
exit /b 10

:x86
echo ERROR: This PC runs 32-bit Windows and the package installs 64-bit Chrome.
exit /b 1

:no_msi
echo ERROR: The Chrome installer is missing from the package.
exit /b 1

:busy
echo ERROR: Another installation is running on this PC. Try again later.
exit /b 1

:msi_failed
echo ERROR: The Chrome installer failed with code %RESULT%. See %LOG%.
exit /b 1

:missing_after_install
echo ERROR: The installer finished but Chrome was not found. See %LOG%.
exit /b 1

rem Sets CHROME to the system-wide chrome.exe, or leaves it empty.
:find_chrome
set "CHROME="
for %%P in ("%ProgramW6432%" "%ProgramFiles%" "%ProgramFiles(x86)%") do if exist "%%~P\Google\Chrome\Application\chrome.exe" set "CHROME=%%~P\Google\Chrome\Application\chrome.exe"
if defined CHROME exit /b 0
for /f "tokens=2,*" %%A in ('reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe" /ve 2^>nul ^| findstr /i "REG_SZ"') do if exist "%%~B" set "CHROME=%%~B"
exit /b 0
