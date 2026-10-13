@echo off
setlocal
rem Restores Google Chrome's settings to their defaults for the user who asked, like
rem Chrome's Settings > Reset settings: startup pages, new tab page, search engine,
rem pinned tabs, theme, site settings and extensions go back to defaults. Bookmarks,
rem history, saved passwords and cookies stay. Every Chrome profile of the user is reset;
rem the old settings are kept as "Preferences.before-reset" next to the new ones.
rem Runs as LocalSystem; the endpoint service passes the requesting user's profile in
rem ENDPOINTAGENTS_USER_SID, ENDPOINTAGENTS_USER_PROFILE and ENDPOINTAGENTS_USER_LOCALAPPDATA.
rem Exit codes: 0 reset, 20 Chrome is not installed, 21 Chrome has no settings for this
rem user yet, 1 failed. Every success prints "Chrome reset completed."

if not defined ENDPOINTAGENTS_USER_SID goto no_user
if not defined ENDPOINTAGENTS_USER_LOCALAPPDATA goto no_user
set "USERDATA=%ENDPOINTAGENTS_USER_LOCALAPPDATA%\Google\Chrome\User Data"

set "CHROME="
for %%P in ("%ProgramW6432%" "%ProgramFiles%" "%ProgramFiles(x86)%" "%ENDPOINTAGENTS_USER_LOCALAPPDATA%") do if exist "%%~P\Google\Chrome\Application\chrome.exe" set "CHROME=1"
if not defined CHROME goto not_installed
if not exist "%USERDATA%\" goto no_data

echo Closing Chrome for this user...
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command "Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'chrome.exe' -and (Invoke-CimMethod -InputObject $_ -MethodName GetOwnerSid).Sid -eq $env:ENDPOINTAGENTS_USER_SID } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
rem Give Chrome a moment to release its files.
ping -n 3 127.0.0.1 >nul

set /a PROFILES=0, FAILED=0
for /d %%D in ("%USERDATA%\*") do if exist "%%~fD\Preferences" call :reset_profile "%%~fD"

if %PROFILES%==0 goto no_data
if not %FAILED%==0 goto failed
echo Reset %PROFILES% Chrome profile(s).
echo Chrome reset completed.
exit /b 0

:no_user
echo ERROR: The endpoint did not say which user to reset Chrome for.
exit /b 1

:not_installed
echo Chrome is not installed for this user.
echo Chrome reset completed.
exit /b 20

:no_data
echo Chrome has no settings for this user yet.
echo Chrome reset completed.
exit /b 21

:failed
echo ERROR: %FAILED% Chrome profile(s) could not be reset.
exit /b 1

rem %1 is one Chrome profile folder, such as ...\User Data\Default.
:reset_profile
set /a PROFILES+=1
call :set_aside "%~1" "Preferences"
call :set_aside "%~1" "Secure Preferences"
exit /b 0

rem Keeps one previous copy: "<name>.before-reset".
:set_aside
if not exist "%~1\%~2" exit /b 0
if exist "%~1\%~2.before-reset" del /f /q "%~1\%~2.before-reset"
ren "%~1\%~2" "%~2.before-reset"
if errorlevel 1 (
    echo Could not reset %~nx1.
    set /a FAILED+=1
)
exit /b 0
