@echo off
chcp 65001 >nul
title Rynez Stability Monitor - single-core

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator privileges... Click "Yes" on the UAC prompt.
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ============================================================
echo  Rynez Stability Monitor - SINGLE-CORE  (Core Cycler style)
echo  Pins y-cruncher to ONE core at a time so that core boosts
echo  HIGH - this exposes Curve Optimizer instability in the
echo  single-core / idle-boost regime that all-core testing misses.
echo  Any error or micro-freeze on a pinned core is blamed on it,
echo  so it STOPS and shows that core. You then adjust that core's
echo  Curve Optimizer yourself and re-test.
echo  Needs dist\tools\y-cruncher.exe (see the .txt in that folder).
echo  TIP: after it starts, open Task Manager once and confirm only
echo       ONE core is loaded (per-core affinity working).
echo  Press Ctrl + C to stop.
echo ============================================================
echo.

"%~dp0dist\ycruncher-monitor.exe" --single --seconds 120 --cycles 0

echo.
echo Finished. Logs (and lastalive.txt) are in dist\logs.
pause
