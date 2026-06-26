@echo off
chcp 65001 >nul
title Core 2 pick 100% load - 2 hour limit

rem PERSONAL launcher - core 2 ONLY, steady 100% single-core load,
rem auto-stops after 2 hours. Tests sustained high-boost Vmin on core 2.

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator privileges... Click "Yes" on the UAC prompt.
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ============================================================
echo  CORE 2  pick 100%% load  -  auto-stop after 2 hours
echo  Steady high-boost single-core soak on core 2 only.
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

"%EXE%" --single --core 2 --minutes 120 --seconds 120 --cycles 0

echo.
echo Finished. Logs are next to the exe, in its logs folder.
pause
