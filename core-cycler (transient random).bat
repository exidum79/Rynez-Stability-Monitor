@echo off
chcp 65001 >nul
title Rynez Stability Monitor - transient RANDOM (real-world load)

rem ============================================================
rem  REAL-WORLD random transient. Instead of a fixed metronome,
rem  the worker runs random PHASES - each a random 80-2000ms
rem  stretch at a random 0-100%% target load (delivered by fast
rem  ~10ms micro-duty) - so the core's utilisation WANDERS the
rem  whole 0->100%% range like real use: idle stretches, full-load
rem  stretches, and partial stretches with fast idle->boost edges.
rem  This exposes Curve Optimizer faults across many load levels
rem  and transition timings in one run, not just one fixed swing.
rem  OPTIONAL: set CORES to soak only specific core(s).
rem    one core      ->  set "CORES=0"
rem    several cores ->  set "CORES=0,2,5"   (comma, NO spaces)
rem    every core    ->  set "CORES="        (blank = sweep all)
rem ============================================================
set "CORES="
rem ============================================================


net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator privileges... Click "Yes" on the UAC prompt.
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

if defined CORES (
    set "CORESEL=--cores %CORES%"
    set "WHAT=core(s) %CORES%"
) else (
    set "CORESEL="
    set "WHAT=every core"
)

echo ============================================================
echo  Rynez Stability Monitor - TRANSIENT RANDOM (real-world)
echo  Testing: %WHAT%
echo  Pins y-cruncher to ONE core at a time AND duty-cycles its
echo  worker in RANDOM phases (random 80-2000ms at random 0-100%%
echo  load) so the core's utilisation wanders the full 0-^>100%%
echo  range like real-world use - exposing Curve Optimizer faults
echo  across many load levels and idle-^>boost transition timings
echo  that a STEADY 100%% load or a fixed metronome never hits.
echo  y-cruncher's own math self-check still catches silent compute
echo  errors. Any error/micro-freeze on a pinned core STOPS it.
echo.
echo  POWER PLAN: use Balanced + Minimum processor state ~5%% so
echo  the clock can actually drop during the idle stretches.
echo  HONEST LIMIT: Windows timer granularity is ~0.5-2ms, NOT
echo  sub-ms - this adds real transient exposure but does not match
echo  a Linux sub-ms tool. A complement to the steady runs.
echo  Needs tools\y-cruncher.exe next to the exe (see the .txt there).
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

"%EXE%" --transient --random %CORESEL% --seconds 120 --cycles 0

echo.
echo Finished. Logs (and lastalive.txt) are next to the exe, in its logs folder.
pause
