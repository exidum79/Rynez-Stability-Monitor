@echo off
chcp 65001 >nul
title Core 1 transient RANDOM - 2 hour limit

rem PERSONAL launcher - core 1 ONLY, real-world RANDOM transient
rem (random 80-2000ms phases at random 0-100% load), auto-stops after 2h.
rem NEEDS Balanced power plan (Minimum processor state ~5%).

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator privileges... Click "Yes" on the UAC prompt.
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ============================================================
echo  CORE 1  transient RANDOM (real-world)  -  stop after 2h
echo  Utilisation wanders 0-^>100%% like real use on core 1 only.
echo  Power plan must be Balanced (min processor state ~5%%).
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

"%EXE%" --transient --random --core 1 --minutes 120 --seconds 120 --cycles 0

echo.
echo Finished. Logs are next to the exe, in its logs folder.
pause
