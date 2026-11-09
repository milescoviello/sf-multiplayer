@echo off
:: Stick Fight Online - installer. Double-click it (one admin prompt).
:: Run it again any time to repair or update.
title Stick Fight Online - installer
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator permission...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-sf-multiplayer.ps1"
echo.
pause
