@echo off
chcp 65001 >nul
title Rynez Stability Monitor - core 0 MAX-SHAKE (boost-cycling)

rem ============================================================
rem  Hard-pinned to CORE 0, transient (boost-cycling) mode tuned
rem  for the BIGGEST clock swings (deepest idle -> full boost).
rem    BURST = load-burst length, IDLE = idle-gap length (ms).
rem  2 / 3 makes each idle gap long enough that the clock+voltage
rem  actually drop, then the load burst yanks it back to full
rem  boost - the deep "low-volt idle -> sudden full load" swing
rem  that pops Curve Optimizer undervolts. Going below ~1-2ms is
rem  pointless: Windows Sleep floors at ~1ms and the clock/volt
rem  ramp itself takes ~1-2ms, so shorter just blurs into a
rem  mid-clock instead of a full swing.
rem  POWER PLAN MATTERS: for the biggest swing, use Balanced with
rem  Minimum processor state ~5%% so the clock really drops in the
rem  idle gap. High performance / min-state 100%% kills the swing.
rem ============================================================
set "BURST=2"
set "IDLE=3"
rem ============================================================


net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator privileges... Click "Yes" on the UAC prompt.
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ============================================================
echo  Rynez Stability Monitor - CORE 0  MAX-SHAKE (boost-cycling)
echo  Testing: core 0 only   duty: %BURST%ms load / %IDLE%ms idle
echo  Pins y-cruncher to CORE 0 AND duty-cycles its worker
echo  (suspend/resume) so the core slams idle-^>full-boost over
echo  and over. This tuning maximises the clock SWING - the deep
echo  low-volt idle -^> sudden full load transition that pops
echo  Curve Optimizer undervolts that a STEADY 100%% load never
echo  triggers - while y-cruncher's own math self-check still
echo  catches silent compute errors. Any error/micro-freeze on
echo  core 0 is blamed on it, so it STOPS and shows that core.
echo.
echo  POWER PLAN: for the biggest swing use Balanced + Minimum
echo  processor state ~5%%. High performance / min-state 100%%
echo  keeps the clock pinned high and kills the swing.
echo  HONEST LIMIT: Windows timer granularity is ~0.5-2ms, NOT
echo  sub-ms. This maximises transient exposure on Windows, but
echo  still does NOT match a Linux sub-ms tool. Treat it as a
echo  complement to the steady single-core / all-core runs.
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
