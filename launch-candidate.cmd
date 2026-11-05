@echo off
cd /d "%~dp0"
if not exist ".local\settings-candidate\PhantomBladeModManager.exe" (
  dotnet publish gui\PhantomBladeModManager.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishTrimmed=false -p:EnableCompressionInSingleFile=false -o .local\settings-candidate
  if errorlevel 1 exit /b 1
)
start "" "%~dp0.local\settings-candidate\PhantomBladeModManager.exe" "%~dp0."
