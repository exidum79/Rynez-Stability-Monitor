@echo off
chcp 65001 >nul
title All-core 100%% load - 2 hour limit

rem PERSONAL launcher - all-core steady 100%% load, auto-stops after 2 hours.

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator privileges... Click "Yes" on the UAC prompt.
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ============================================================
echo  ALL-CORE  steady 100%% load  -  auto-stop after 2 hours
echo  Stresses load-line / Vdroop / thermal across every core.
echo  Press Ctrl + C to stop early.
echo ============================================================
echo.

rem Find the exe: next to this .bat, in dist\, or one level up (this file lives in my-launchers\).
set "EXE=%~dp0dist\ycruncher-monitor.exe"
if not exist "%EXE%" set "EXE=%~dp0ycruncher-monitor.exe"
if not exist "%EXE%" set "EXE=%~dp0..\dist\ycruncher-monitor.exe"
if not exist "%EXE%" set "EXE=%~dp0..\ycruncher-monitor.exe"
if not exist "%EXE%" (
    echo [ERROR] ycruncher-monitor.exe not found near this .bat.
    echo Keep this file in my-launchers\ inside the repo, or next to the exe.
    echo.
    pause
    exit /b 1
)

"%EXE%" --minutes 120 --seconds 120 --cycles 0

echo.
echo Finished. Logs are next to the exe, in its logs folder.
pause
