@echo off
chcp 65001 >nul
title Rynez Stability Monitor - FULL TEST (RAM/IMC + CPU/CO)

rem ============================================================
rem  FULL TEST battery. Runs two phases back to back:
rem    Phase 1 - ALL-CORE, memory-coupled + heavy load  -> stresses RAM / IMC (+ load vdroop)
rem    Phase 2 - SINGLE-CORE sweep, high boost          -> stresses CPU core Curve Optimizer
rem  WHEA tags each error as RAM/IMC vs CPU-CORE while it runs.
rem  It STOPS the whole battery on the first detected problem (so you can read what/where).
rem
rem  EDIT these if you want (cycles bound each phase so it finishes and moves on; 0 = infinite,
rem  but DO NOT set Phase 1 to 0 or it never reaches Phase 2):
set "SECONDS=120"
set "P1_CYCLES=5"
set "P2_CYCLES=3"
set "MEM="
rem    SECONDS   = seconds per individual test
rem    P1_CYCLES = all-core runs (Phase 1)
rem    P2_CYCLES = single-core sweeps over every core (Phase 2)
rem    MEM       = y-cruncher memory size for Phase 1, e.g. set "MEM=8G" (blank = auto)
rem  To abort the whole battery: close this window (X) - the job object kills y-cruncher.
rem  (Ctrl+C only ends the current phase and may advance to the next.)
rem ============================================================


net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator privileges... Click "Yes" on the UAC prompt.
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

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

set "MEMARG="
if defined MEM if not "%MEM%"=="" set "MEMARG=--yc-mem %MEM%"

echo ============================================================
echo  FULL TEST - Phase 1 of 2: ALL-CORE (RAM / IMC + load)
echo  Memory-coupled + heavy tests across every core.
echo  NOTE: y-cruncher is not a substitute for a dedicated RAM tester
echo        (TM5 / Karhu / MemTest86). Use those for deep RAM coverage.
echo ============================================================
echo.
"%EXE%" --yc-tests "VT3 N63 FFTv4" --seconds %SECONDS% --cycles %P1_CYCLES% --stop-on 1 %MEMARG%
if errorlevel 1 goto :problem

echo.
echo ============================================================
echo  FULL TEST - Phase 2 of 2: SINGLE-CORE sweep (CPU / CO)
echo  One core pinned at a time at high boost - exposes Curve
echo  Optimizer instability that all-core testing hides.
echo ============================================================
echo.
"%EXE%" --single --yc-tests "BKT FFTv4 N63 VT3" --seconds %SECONDS% --cycles %P2_CYCLES% --stop-on 1
if errorlevel 1 goto :problem

echo.
echo ============================================================
echo  FULL TEST complete - no problem detected in either phase.
echo  Remember: a clean pass is a strong signal, not a guarantee.
echo  For confidence, raise the cycles / run overnight, and prove
echo  RAM separately with TM5 / Karhu / MemTest86.
echo ============================================================
goto :end

:problem
echo.
echo ############################################################
echo #  A PROBLEM WAS DETECTED - battery stopped.
echo #  Scroll up: the run printed the suspect core, and any
echo #  [WHEA -> RAM/IMC] / [WHEA -> CPU-CORE] hardware tags.
echo #  Logs (and lastalive.txt) are in the exe's logs folder.
echo ############################################################

:end
echo.
pause
