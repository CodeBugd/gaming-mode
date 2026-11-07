@echo off
title Gaming Mode
rem Already set up? Just toggle (no admin prompt).
schtasks /query /tn "AceBot Gaming Mode" >nul 2>&1
if not errorlevel 1 (
    schtasks /run /tn "AceBot Gaming Mode" >nul
    exit /b
)

rem First run: one-time setup. Asks for admin once, never again after this.
net session >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0GamingMode.ps1" -Install
if errorlevel 1 ( pause & exit /b )
schtasks /run /tn "AceBot Gaming Mode" >nul
echo.
echo   Gaming Mode is turning on - look for the green G in your tray.
timeout /t 5
