@echo off
chcp 65001 >nul
title Rynez Stability Monitor - all-core

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator privileges... Click "Yes" on the UAC prompt.
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ============================================================
echo  Rynez Stability Monitor - ALL-CORE  (manual diagnosis)
echo  Runs y-cruncher ALL-CORE stress (heavy AVX-512 + memory).
echo  When an error is found it STOPS and shows the suspect core.
echo  You then adjust that core's Curve Optimizer yourself and re-test.
echo  Needs dist\tools\y-cruncher.exe (see the .txt in that folder).
echo  Instability is intermittent - run long / repeat to catch it.
echo  Press Ctrl + C to stop.
echo ============================================================
echo.

rem Locate the exe whether it sits in a dist\ subfolder (repo layout) or next to this .bat (flattened).
set "EXE=%~dp0dist\ycruncher-monitor.exe"
if not exist "%EXE%" set "EXE=%~dp0ycruncher-monitor.exe"
if not exist "%EXE%" (
    echo [ERROR] ycruncher-monitor.exe not found. Looked in:
    echo     %~dp0dist\ycruncher-monitor.exe
    echo     %~dp0ycruncher-monitor.exe
    echo Put this .bat in the SAME folder as ycruncher-monitor.exe
    echo ^(or keep the exe in a "dist" subfolder next to it^).
    echo.
    pause
    exit /b 1
)

"%EXE%" --seconds 180 --cycles 0

echo.
echo Finished. Logs (and lastalive.txt) are next to the exe, in its logs folder.
pause
