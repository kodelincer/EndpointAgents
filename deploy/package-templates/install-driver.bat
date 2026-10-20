@echo off
setlocal
rem Installs the Windows driver for @@MODEL@@ from the driver files shipped next to this file
rem (the .inf with its .sys, .cat and other files). Runs unattended as LocalSystem.
rem Copied from deploy\package-templates\install-driver.bat; replace every @@...@@ value.
rem Windows itself refuses a driver whose own signature (.cat) is not valid.
rem Exit codes: 0 installed now, 10 already installed or no device needed it, 3010 installed
rem and a restart is needed, 1 failed. Every success prints "@@MODEL@@ driver completed."

set "MODEL=@@MODEL@@"
set "INF=%~dp0@@INF@@"
set "LOGDIR=%ProgramData%\EndpointAgents\Logs"
set "LOG=%LOGDIR%\@@LOG@@"

if not exist "%INF%" goto no_driver
if not exist "%LOGDIR%" mkdir "%LOGDIR%"

echo Installing the %MODEL% driver...
"%SystemRoot%\System32\pnputil.exe" /add-driver "%INF%" /install >"%LOG%" 2>&1
set "RESULT=%ERRORLEVEL%"
echo pnputil exit code %RESULT%.
if "%RESULT%"=="0" goto installed
if "%RESULT%"=="3010" goto restart
rem 259: the driver was added but no device needed it (already up to date, or not plugged in).
if "%RESULT%"=="259" goto already_installed
goto install_failed

:installed
echo %MODEL% driver completed.
exit /b 0

:restart
echo %MODEL% driver completed.
exit /b 3010

:already_installed
echo The %MODEL% driver is already installed, or the device is not connected.
echo %MODEL% driver completed.
exit /b 10

:no_driver
echo ERROR: The %MODEL% driver files are missing from the package.
exit /b 1

:install_failed
echo ERROR: Windows could not install the %MODEL% driver (code %RESULT%). See %LOG%.
exit /b 1
