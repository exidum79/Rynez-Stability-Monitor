@echo off
chcp 65001 >nul
title Rynez Stability Monitor - single test (pick which y-cruncher test)

rem ============================================================
rem  Runs ONLY the y-cruncher test(s) you pick, single-core.
rem  Use this when your CPU crashes reliably on ONE test and you
rem  want to skip the rest (faster reproduction).
rem    TESTS = which y-cruncher test(s) to run. Default VT3.
rem      one test   ->  set "TESTS=VT3"
rem      a few      ->  set "TESTS=VT3 N63"   (space-separated)
rem      Valid: BKT BBP SFTv4 SNT SVT FFTv4 NTT63 N63 VSTv3 VT3
rem    CORES = which physical core(s) to test.
rem      one core   ->  set "CORES=0"
rem      several    ->  set "CORES=0,2,5"     (comma, NO spaces)
rem      every core ->  set "CORES="          (blank = sweep all)
rem ============================================================
set "TESTS=VT3"
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
echo  Rynez Stability Monitor - SINGLE TEST  ^(%TESTS%^)
echo  Testing: %WHAT%   y-cruncher test(s): %TESTS%
echo  Runs ONLY the test(s) above so a crash that reproduces on a
echo  specific test (e.g. VT3) is hit faster, skipping the others.
echo  A progress tick every ~15s shows y-cruncher's current test.
echo  Any error/micro-freeze on a pinned core is blamed on it.
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

"%EXE%" --single %CORESEL% --yc-tests "%TESTS%" --seconds 120 --cycles 0

echo.
echo Finished. Logs (and lastalive.txt) are next to the exe, in its logs folder.
pause
