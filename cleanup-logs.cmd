@echo off
rem AoGPN Logs bakim betisi - tek tikla calistirir.
rem Arguer: cleanup-logs.ps1 'a aynen iletilir (orn. -DryRun).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0cleanup-logs.ps1" %*
echo.
pause
