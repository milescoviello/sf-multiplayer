@echo off
:: Stick Fight Online - uninstall (puts Stick Fight back the way it was).
title Stick Fight Online - uninstall
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator permission...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall-sf-multiplayer.ps1"
echo.
pause
