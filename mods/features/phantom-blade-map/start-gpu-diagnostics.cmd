@echo off
powershell.exe -NoProfile -File "%~dp0tools\start-gpu-diagnostics.ps1"
if errorlevel 1 pause
