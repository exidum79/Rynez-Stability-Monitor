@echo off
chcp 65001 >nul
title Rynez Stability Monitor - single-core (pick cores)

rem ============================================================
rem  EDIT THE NEXT LINE: which physical core(s) to test.
rem    one core      ->  set "CORES=0"
rem    several cores ->  set "CORES=0,2,5"   (comma-separated, NO spaces)
rem    every core    ->  set "CORES="        (leave blank = sweep all)
rem  Core numbers are 0-based - cross-check with Ryzen Master / BIOS.
rem ============================================================
set "CORES=0"
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
echo  Rynez Stability Monitor - SINGLE-CORE  (pick cores)
echo  Testing: %WHAT%
echo  Pins y-cruncher to ONE core at a time so it boosts HIGH -
echo  this exposes Curve Optimizer instability in the single-core
echo  / idle-boost regime that all-core testing misses.
echo  Pick ONE suspect core above to soak it continuously, or list
echo  a few. Any error/micro-freeze on a pinned core is blamed on it.
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

"%EXE%" --single %CORESEL% --seconds 120 --cycles 0

echo.
echo Finished. Logs (and lastalive.txt) are next to the exe, in its logs folder.
pause
