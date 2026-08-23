@echo off
rem ============================================================================
rem  KidControl — uninstall.bat
rem  Fully removes an installed KidControl instance. Self-contained: no source,
rem  no build, no network. Just run it (it self-elevates).
rem
rem  It mirrors InstallOrchestrator.Uninstall:
rem    unprotect registry -> stop+delete service -> kill UI -> relax ACLs ->
rem    delete install + data directories.
rem
rem  SAFETY: the service is stopped ONLY via "sc stop" (a graceful SCM stop), which
rem  lets the process clear its "critical process" flag in its finally block. It is
rem  never force-killed, so uninstall can never blue-screen the machine even if the
rem  CriticalProcess option was enabled.
rem ============================================================================

rem ---------------------------------------------------------------------------
rem  CONFIG (defaults match a standard install; edit only if you customised them)
rem ---------------------------------------------------------------------------
set "KC_SERVICE=KidControlService"
set "KC_TASK=KidControl.UiHost.Launch"
set "KC_INSTALL_DIR=%ProgramFiles%\KidControl"
set "KC_DATA_DIR=%ProgramData%\KidControl"
set "KC_UI_PROC=KidControl.UiHost.exe"
set "KC_UNINSTALL_KEY=HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\KidControl"
rem  Keep runtime data (appsettings.json with the bot token, session_state.json,
rem  logs)? 0 = delete everything (default), 1 = keep %KC_DATA_DIR%.
set "KC_KEEP_DATA=0"
rem ---------------------------------------------------------------------------

setlocal EnableExtensions

rem --- Require administrator --------------------------------------------------
net session >nul 2>&1
if %errorlevel% NEQ 0 (
    echo Requesting administrator privileges...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo(
echo === KidControl uninstall ===
echo Service      : %KC_SERVICE%
echo Install dir  : %KC_INSTALL_DIR%
echo Data dir     : %KC_DATA_DIR%  (keep=%KC_KEEP_DATA%)
echo(
choice /C YN /M "Proceed with uninstall"
if errorlevel 2 (
    echo Cancelled.
    goto :end
)
echo(

rem --- 1. Remove the scheduled task (so the UI is not relaunched) -------------
echo [1/7] Removing scheduled task...
schtasks /Delete /TN "%KC_TASK%" /F >nul 2>&1
if %errorlevel%==0 (echo       task removed) else (echo       no task ^(ok^))

rem --- 2. Stop the service gracefully (clears the critical flag safely) -------
echo [2/7] Stopping service...
sc stop "%KC_SERVICE%" >nul 2>&1
sc config "%KC_SERVICE%" start= disabled >nul 2>&1

rem  Wait up to ~30s for STOPPED before deleting.
set /a _tries=0
:waitstop
sc query "%KC_SERVICE%" 2>nul | find "STOPPED" >nul
if %errorlevel%==0 goto stopped
sc query "%KC_SERVICE%" 2>nul | find "1060" >nul
if %errorlevel%==0 goto stopped
set /a _tries+=1
if %_tries% GEQ 15 goto stopped
timeout /t 2 /nobreak >nul
goto waitstop
:stopped
echo       stopped

rem --- 3. Kill the UI process (never the service — see SAFETY note) -----------
echo [3/7] Terminating UI process...
taskkill /F /IM "%KC_UI_PROC%" /T >nul 2>&1
if %errorlevel%==0 (echo       UI terminated) else (echo       UI not running ^(ok^))

rem --- 4. Delete the service (SCM runs as SYSTEM, so the protected key is ok) --
echo [4/7] Deleting service...
sc delete "%KC_SERVICE%" >nul 2>&1
if %errorlevel%==0 (echo       service deleted) else (echo       service already gone ^(ok^))

rem --- 5. Remove the Add/Remove-Programs hardening key ------------------------
echo [5/7] Cleaning registry...
reg delete "%KC_UNINSTALL_KEY%" /f >nul 2>&1
echo       done

rem --- 6. Delete the install directory (reset ACL + take ownership first) -----
echo [6/7] Removing install directory...
if exist "%KC_INSTALL_DIR%" (
    takeown /F "%KC_INSTALL_DIR%" /R /D Y >nul 2>&1
    icacls "%KC_INSTALL_DIR%" /reset /T /C /Q >nul 2>&1
    icacls "%KC_INSTALL_DIR%" /grant *S-1-5-32-544:(OI)(CI)F /T /C /Q >nul 2>&1
    rmdir /S /Q "%KC_INSTALL_DIR%" >nul 2>&1
    if exist "%KC_INSTALL_DIR%" (echo       WARNING: could not fully remove %KC_INSTALL_DIR%) else (echo       removed)
) else (
    echo       not present ^(ok^)
)

rem --- 7. Delete the data directory (config/state/logs) unless kept -----------
echo [7/7] Removing data directory...
if "%KC_KEEP_DATA%"=="1" (
    echo       kept by request ^(KC_KEEP_DATA=1^)
) else (
    if exist "%KC_DATA_DIR%" (
        takeown /F "%KC_DATA_DIR%" /R /D Y >nul 2>&1
        icacls "%KC_DATA_DIR%" /reset /T /C /Q >nul 2>&1
        icacls "%KC_DATA_DIR%" /grant *S-1-5-32-544:(OI)(CI)F /T /C /Q >nul 2>&1
        rmdir /S /Q "%KC_DATA_DIR%" >nul 2>&1
        if exist "%KC_DATA_DIR%" (echo       WARNING: could not fully remove %KC_DATA_DIR%) else (echo       removed)
    ) else (
        echo       not present ^(ok^)
    )
)

echo(
echo === KidControl uninstall complete. ===
echo If a directory could not be removed, reboot and re-run this file.

:end
echo(
pause
exit /b 0
