@echo off
setlocal
rem Installs every .ttf and .otf font shipped next to this file for all users,
rem skipping fonts that are already installed. Runs unattended as LocalSystem.
rem Exit codes: 3010 fonts added (they appear after the user signs out and back in),
rem 10 every font was already installed, 1 failed.
rem Every success prints "Persian fonts install completed."

set "FONTS=%SystemRoot%\Fonts"
set "KEY=HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"
set /a TOTAL=0, ADDED=0, FAILED=0

for %%F in ("%~dp0*.ttf" "%~dp0*.otf") do call :install "%%~fF" "%%~nF" "%%~nxF" "%%~xF"

if %TOTAL%==0 goto no_fonts
if not %FAILED%==0 goto failed
if %ADDED%==0 goto already_installed
echo Installed %ADDED% of %TOTAL% fonts.
echo Persian fonts install completed.
exit /b 3010

:already_installed
echo All %TOTAL% fonts are already installed.
echo Persian fonts install completed.
exit /b 10

:no_fonts
echo ERROR: The package contains no fonts.
exit /b 1

:failed
echo ERROR: %FAILED% of %TOTAL% fonts could not be installed.
exit /b 1

rem %1 full path, %2 name, %3 file name, %4 extension.
:install
set /a TOTAL+=1
set "VALUE=%~2 (TrueType)"
if /i "%~4"==".otf" set "VALUE=%~2 (OpenType)"
reg query "%KEY%" /v "%VALUE%" >nul 2>&1
if errorlevel 1 goto install_new
if exist "%FONTS%\%~3" exit /b 0
:install_new
rem A font file that is already there may be in use; only register it.
if exist "%FONTS%\%~3" goto install_register
copy /y "%~1" "%FONTS%\%~3" >nul
if errorlevel 1 goto install_failed
:install_register
reg add "%KEY%" /v "%VALUE%" /t REG_SZ /d "%~3" /f >nul
if errorlevel 1 goto install_failed
echo Installed %~3.
set /a ADDED+=1
exit /b 0
:install_failed
echo Could not install %~3.
set /a FAILED+=1
exit /b 0
