@echo off
chcp 65001 >nul
title Rynez Stability Monitor - RAM / IMC test

rem ============================================================
rem  RAM / IMC test. All-core, memory-coupled + heavy tests with a
rem  large memory footprint, looping - to stress the DRAM and the
rem  memory controller (IMC). If WHEA logs a memory error it is
rem  tagged [WHEA -> RAM/IMC]. (Reportable only with ECC memory;
rem  consumer non-ECC DDR5 corrects on-die silently - see README.)
rem
rem  EDIT these if you want:
set "SECONDS=120"
set "CYCLES=0"
set "MEM="
rem    SECONDS = seconds per individual test
rem    CYCLES  = number of all-core runs (0 = loop forever until error / you stop)
rem    MEM     = memory size, e.g. set "MEM=24G" - BIGGER = more RAM coverage.
rem              Set it to most of your FREE RAM for the best memory stress.
rem              Blank = let y-cruncher auto-size (lighter on memory).
rem
rem  NOTE: y-cruncher is NOT a substitute for a dedicated memory tester.
rem        For deep RAM coverage also run TM5 (anta777) / Karhu / MemTest86.
rem  Press Ctrl + C to stop.
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
echo  Rynez Stability Monitor - RAM / IMC test (all-core, memory)
if defined MEMARG (echo  Memory size: %MEM%) else (echo  Memory size: auto ^(set MEM for heavier RAM stress^))
echo  Tests: VT3 N63 FFTv4  ^(memory-coupled + heavy^)
echo  WHEA classifies any hardware error as RAM/IMC vs CPU-core.
echo  Press Ctrl + C to stop.
echo ============================================================
echo.

"%EXE%" --yc-tests "VT3 N63 FFTv4" --seconds %SECONDS% --cycles %CYCLES% --stop-on 1 %MEMARG%

echo.
echo Finished. Logs (and lastalive.txt) are next to the exe, in its logs folder.
echo Reminder: confirm RAM separately with TM5 / Karhu / MemTest86.
pause
