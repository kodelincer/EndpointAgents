@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem EndpointAgents sample Windows activation repair.
rem Uses only the license already installed on Windows. It never installs keys,
rem changes KMS configuration, or bypasses Microsoft activation.

set "POWERSHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
set "SLMGR=%SystemRoot%\System32\slmgr.vbs"

if /i "%~1"=="--self-test" goto self_test

if not exist "%POWERSHELL%" exit /b 10
if not exist "%SLMGR%" exit /b 10

call :is_licensed
if not errorlevel 1 exit /b 0

call :ensure_licensing_service
if errorlevel 1 exit /b 11

cscript.exe //Nologo "%SLMGR%" /ato >nul 2>&1
set "activation_exit=%errorlevel%"
timeout.exe /t 5 /nobreak >nul 2>&1

call :is_licensed
if not errorlevel 1 exit /b 0
if not "%activation_exit%"=="0" exit /b 12
exit /b 13

:is_licensed
"%POWERSHELL%" -NoLogo -NoProfile -NonInteractive -Command ^
  "$p=Get-CimInstance SoftwareLicensingProduct -Filter ""ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f'"" ^| Where-Object {$_.PartialProductKey -and $_.LicenseStatus -eq 1}; if($p){exit 0}else{exit 1}" ^
  >nul 2>&1
exit /b %errorlevel%

:ensure_licensing_service
sc.exe query sppsvc 2>nul | findstr.exe /i "RUNNING" >nul
if not errorlevel 1 exit /b 0
sc.exe start sppsvc >nul 2>&1
if errorlevel 1 (
  timeout.exe /t 2 /nobreak >nul 2>&1
  sc.exe query sppsvc 2>nul | findstr.exe /i "RUNNING" >nul
  if errorlevel 1 exit /b 1
)
exit /b 0

:self_test
if not exist "%POWERSHELL%" exit /b 10
if not exist "%SLMGR%" exit /b 10
echo windowsbugfixer.bat prerequisites are available.
exit /b 0
