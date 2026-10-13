@echo off
setlocal EnableDelayedExpansion
rem Provisions the Microsoft Photos app for every user from the Store bundle shipped
rem next to this file. Every other .appx or .msix file in the package is passed as a
rem dependency (VCLibs, UI.Xaml, Windows App Runtime). Runs unattended as LocalSystem.
rem Windows adds a provisioned app to each user at their next sign-in.
rem Exit codes: 3010 provisioned (the user signs out and back in), 10 already
rem provisioned, 1 failed. Every success prints "Photos install completed."

set "BUNDLE=%~dp0Microsoft.Windows.Photos.msixbundle"
set "LICENSE=%~dp0Microsoft.Windows.Photos-license.xml"
set "DISM=%SystemRoot%\System32\dism.exe"
set "LOGDIR=%ProgramData%\EndpointAgents\Logs"
set "LOG=%LOGDIR%\photos-install.log"

"%DISM%" /English /Online /Get-ProvisionedAppxPackages 2>nul | findstr /i /c:"PackageName : Microsoft.Windows.Photos_" >nul
if not errorlevel 1 goto already_provisioned

if not exist "%BUNDLE%" goto no_bundle
if not exist "%LOGDIR%" mkdir "%LOGDIR%"

set "DEPS="
for %%F in ("%~dp0*.appx" "%~dp0*.msix") do set DEPS=!DEPS! /DependencyPackagePath:"%%~fF"
set "LICENSING=/SkipLicense"
if exist "%LICENSE%" set LICENSING=/LicensePath:"%LICENSE%"

echo Provisioning Microsoft Photos for all users...
"%DISM%" /English /Online /NoRestart /Add-ProvisionedAppxPackage /PackagePath:"%BUNDLE%" !DEPS! !LICENSING! /LogPath:"%LOG%" >nul
set "RESULT=%ERRORLEVEL%"
echo DISM exit code %RESULT%.
if not "%RESULT%"=="0" if not "%RESULT%"=="3010" goto dism_failed
echo Photos install completed.
exit /b 3010

:already_provisioned
echo Microsoft Photos is already provisioned on this PC.
echo Photos install completed.
exit /b 10

:no_bundle
echo ERROR: The Photos bundle is missing from the package.
exit /b 1

:dism_failed
echo ERROR: DISM could not provision Photos, code %RESULT%. See %LOG%.
exit /b 1
