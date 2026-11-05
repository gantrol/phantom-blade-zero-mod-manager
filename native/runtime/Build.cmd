@echo off
setlocal
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath`) do set "PBZ_VS=%%i"
if not defined PBZ_VS exit /b 1
call "%PBZ_VS%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
cmake -S "%~dp0." -B "%~dp0..\..\.local\runtime-build" -G Ninja -DCMAKE_BUILD_TYPE=Release
if errorlevel 1 exit /b %errorlevel%
cmake --build "%~dp0..\..\.local\runtime-build" --config Release
exit /b %errorlevel%
