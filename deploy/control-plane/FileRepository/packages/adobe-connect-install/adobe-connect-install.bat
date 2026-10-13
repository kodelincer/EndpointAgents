@echo off
setlocal
rem Installs Adobe Connect for all users from the vendor MSI shipped next to this file,
rem unless it is already installed. Runs unattended as LocalSystem.
rem Exit codes: 0 installed now, 10 already installed, 3010 installed and a restart
rem is needed, 1 failed. Every success prints "Adobe Connect install completed."

set "APP=Adobe Connect"
set "MSI=%~dp0AdobeConnect.msi"
rem findstr patterns for the product's DisplayName in the Uninstall registry keys.
set "DETECT=REG_SZ *Adobe Connect *[0-9.]*$"
set "DETECT_ALT=REG_SZ *Adobe Connect *[0-9.]*$"
rem Fixed MSI properties chosen by the administrator, for example ALLUSERS=1.
set "MSIPROPS="
set "LOGDIR=%ProgramData%\EndpointAgents\Logs"
set "LOG=%LOGDIR%\adobe-connect-install.log"

call :find_app
if defined FOUND goto already_installed

if not exist "%MSI%" goto no_msi
if not exist "%LOGDIR%" mkdir "%LOGDIR%"

echo Installing %APP% for all users...
"%SystemRoot%\System32\msiexec.exe" /i "%MSI%" /qn /norestart /l*v "%LOG%" %MSIPROPS%
set "RESULT=%ERRORLEVEL%"
echo msiexec exit code %RESULT%.
if "%RESULT%"=="1618" goto busy
if "%RESULT%"=="3010" goto verify
if not "%RESULT%"=="0" goto msi_failed

:verify
call :find_app
if not defined FOUND goto missing_after_install
echo %APP% install completed.
exit /b %RESULT%

:already_installed
echo %APP% is already installed.
echo %APP% install completed.
exit /b 10

:no_msi
echo ERROR: The %APP% installer is missing from the package.
exit /b 1

:busy
echo ERROR: Another installation is running on this PC. Try again later.
exit /b 1

:msi_failed
echo ERROR: The %APP% installer failed with code %RESULT%. See %LOG%.
exit /b 1

:missing_after_install
echo ERROR: The installer finished but %APP% was not found. See %LOG%.
exit /b 1

rem Sets FOUND when an Uninstall entry, 64- or 32-bit, has a matching DisplayName.
:find_app
set "FOUND="
for %%K in ("HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" "HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall") do reg query %%K /s /v DisplayName 2>nul | findstr /i /r /c:"%DETECT%" /c:"%DETECT_ALT%" >nul && set "FOUND=1"
exit /b 0
