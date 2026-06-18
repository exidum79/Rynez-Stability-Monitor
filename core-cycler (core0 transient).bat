@echo off
chcp 65001 >nul
title Rynez Stability Monitor - core 0 transient (boost-cycling)

rem ============================================================
rem  Hard-pinned to CORE 0, transient (boost-cycling) mode.
rem  OPTIONAL: edit the duty cycle below (milliseconds).
rem    BURST = load-burst length, IDLE = idle-gap length.
rem    Lower = more idle->load boost swings per second, but
rem    Windows timer granularity is ~0.5-2ms so very small
rem    values get jittery. 5 / 5 is a sane default.
rem ============================================================
set "BURST=5"
set "IDLE=5"
rem ============================================================


net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator privileges... Click "Yes" on the UAC prompt.
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ============================================================
echo  Rynez Stability Monitor - CORE 0  TRANSIENT (boost-cycling)
echo  Testing: core 0 only   duty: %BURST%ms load / %IDLE%ms idle
echo  Pins y-cruncher to CORE 0 AND duty-cycles its worker
echo  (suspend/resume) so the core ramps idle-^>load over and
echo  over. Those rapid boost swings shake the clock up and down
echo  and expose Curve Optimizer faults that a STEADY 100%% load
echo  never triggers - while y-cruncher's own math self-check
echo  still catches silent compute errors. Any error/micro-freeze
echo  on core 0 is blamed on it, so it STOPS and shows that core.
echo.
echo  HONEST LIMIT: Windows timer granularity is ~0.5-2ms, NOT
echo  sub-ms. This adds real transient exposure over steady
echo  load, but does NOT match a Linux sub-ms tool. Treat it as
echo  a complement to the steady single-core / all-core runs.
echo  Needs tools\y-cruncher.exe next to the exe (see the .txt there).
echo  TIP: after it starts, open Task Manager once and confirm only
echo       CORE 0 is loaded (per-core affinity working).
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

"%EXE%" --transient --burst-ms %BURST% --idle-ms %IDLE% --cores 0 --seconds 120 --cycles 0

echo.
echo Finished. Logs (and lastalive.txt) are next to the exe, in its logs folder.
pause
