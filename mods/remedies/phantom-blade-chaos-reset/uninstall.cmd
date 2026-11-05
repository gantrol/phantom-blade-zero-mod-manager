@echo off
cd /d "%~dp0"
node "%~dp0install.mjs" --uninstall %*
pause
