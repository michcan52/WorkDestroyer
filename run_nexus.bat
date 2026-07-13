@echo off
setlocal enabledelayedexpansion

echo ====================================================
echo NexusLive - Local Initialization Script
echo ====================================================
echo.
echo [1/2] Checking if LM Studio is running on localhost:1234...

:: Run inline PowerShell to test connection on port 1234
powershell -Command "try { $client = New-Object System.Net.Sockets.TcpClient('localhost', 1234); $client.Close(); exit 0 } catch { exit 1 }"

if %errorlevel% neq 0 (
    echo [WARNING] LM Studio does not appear to be running on port 1234.
    echo Make sure LM Studio is open and the local server is active.
    echo.
    set /p choice="Do you want to launch the application anyway in mock/offline mode? (Y/N): "
    if /i "!choice!" neq "Y" (
        echo Exiting launch script.
        exit /b 1
    )
) else (
    echo [SUCCESS] LM Studio is online on port 1234.
)

echo.
echo [2/2] Launching NexusLive.App with Hot Reload Watcher...
echo Press Ctrl+C in this terminal to stop watching and terminate the application.
echo.

dotnet watch run --project NexusLive.App
