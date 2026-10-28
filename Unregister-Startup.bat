@echo off
setlocal
set "APP_NAME=NssStartupGuard"
set "RUN_KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"

reg query "%RUN_KEY%" /v "%APP_NAME%" >nul 2>&1
if errorlevel 1 (
    echo [COMPLETED] NssStartupGuard was not registered; nothing to remove.
    echo.
    pause
    exit /b 0
)

reg delete "%RUN_KEY%" /v "%APP_NAME%" /f >nul
if errorlevel 1 (
    echo [FAILED] Could not remove the current-user startup entry.
    echo.
    pause
    exit /b 1
)

echo [SUCCESS] Startup unregistration completed.
echo.
pause
exit /b 0
