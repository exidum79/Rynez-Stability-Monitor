@echo off
chcp 65001 >nul
title Rynez Stability Monitor - transient (boost-cycling)

rem ============================================================
rem  OPTIONAL: edit the duty cycle below (milliseconds).
rem    BURST = load-burst length, IDLE = idle-gap length.
rem    Lower = more idle->load boost swings per second, but
rem    Windows timer granularity is ~0.5-2ms so very small
rem    values get jittery. 5 / 5 is a sane default.
rem  OPTIONAL: set CORES to soak only specific core(s).
rem    one core      ->  set "CORES=0"
rem    several cores ->  set "CORES=0,2,5"   (comma, NO spaces)
rem    every core    ->  set "CORES="        (blank = sweep all)
rem ============================================================
set "BURST=5"
set "IDLE=5"
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
echo  Rynez Stability Monitor - TRANSIENT  (boost-cycling)
echo  Testing: %WHAT%   duty: %BURST%ms load / %IDLE%ms idle
echo  Pins y-cruncher to ONE core AND duty-cycles its worker
echo  (suspend/resume) so the core ramps idle-^>load over and
echo  over. Those rapid boost swings expose Curve Optimizer
echo  faults that a STEADY 100%% load never triggers - while
echo  y-cruncher's own math self-check still catches silent
echo  compute errors. Any error/micro-freeze on a pinned core
echo  is blamed on it, so it STOPS and shows that core.
echo.
echo  HONEST LIMIT: Windows timer granularity is ~0.5-2ms, NOT
echo  sub-ms. This adds real transient exposure over steady
echo  load, but does NOT match a Linux sub-ms tool. Treat it as
echo  a complement to the steady single-core / all-core runs.
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

"%EXE%" --transient --burst-ms %BURST% --idle-ms %IDLE% %CORESEL% --seconds 120 --cycles 0

echo.
echo Finished. Logs (and lastalive.txt) are next to the exe, in its logs folder.
pause
