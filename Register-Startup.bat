@echo off
setlocal
set "APP_NAME=NssStartupGuard"
set "APP_PATH=%~dp0NssStartupGuard.exe"
set "RUN_KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"

if not exist "%APP_PATH%" (
    echo [FAILED] NssStartupGuard.exe was not found next to this batch file.
    echo.
    pause
    exit /b 1
)

reg add "%RUN_KEY%" /v "%APP_NAME%" /t REG_SZ /d "\"%APP_PATH%\"" /f >nul
if errorlevel 1 (
    echo [FAILED] Could not register the current-user startup entry.
    echo.
    pause
    exit /b 1
)

echo [SUCCESS] Startup registration completed.
echo NssStartupGuard will run at sign-in for the current user.
echo Remove any old NssStartupGuard shortcut from the Startup folder to avoid duplicate entries.
echo.
pause
exit /b 0
