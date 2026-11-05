@echo off
cd /d "%~dp0"
if not exist "dist\PhantomBladeModManager.exe" (
  dotnet publish gui\PhantomBladeModManager.csproj -c Release -o dist
  if errorlevel 1 (
    pause
    exit /b 1
  )
)
if "%~1"=="" (
  "%~dp0dist\PhantomBladeModManager.exe" status
  pause
) else (
  "%~dp0dist\PhantomBladeModManager.exe" %*
)
