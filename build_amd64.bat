@echo off
setlocal
set CFG=%1
if "%CFG%"=="" set CFG=Debug

call "C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\Tools\VsDevCmd.bat" -arch=amd64
if errorlevel 1 (echo VsDevCmd failed & exit /b 1)

rem Clean outputs for the requested configuration only (Debug/Release kept separate)
rd /s /q "%~dp0bin\%CFG%" 2>nul
rd /s /q "%~dp0build\KeySecBox.DLL.dir\%CFG%" 2>nul
rd /s /q "%~dp0build\ALL_BUILD.dir\%CFG%" 2>nul
rd /s /q "%~dp0build\ZERO_CHECK.dir\%CFG%" 2>nul
rd /s /q "%~dp0build\x64\%CFG%" 2>nul
rd /s /q "%~dp0src\ui\obj\x64\%CFG%" 2>nul

cmake -S "%~dp0." -B "%~dp0build" -G "Visual Studio 18 2026" -A x64
if errorlevel 1 (echo cmake configure failed & exit /b 1)
cmake --build "%~dp0build" --config %CFG%
if errorlevel 1 (echo cmake build failed & exit /b 1)
dotnet build "%~dp0src\ui\KeySecBox.UI.csproj" -c %CFG% -p:Platform=x64
if errorlevel 1 (echo dotnet build failed & exit /b 1)
echo BUILD_DONE
echo UI output: bin\%CFG%\exe\KeySecBox.UI.exe
