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

rem --fresh forces a full regeneration so stale ZERO_CHECK/.slnx rules cannot
rem spawn cmd during cmake --build and print "'dows'/'eFile' is not recognized".
rem Together with CMAKE_SUPPRESS_REGENERATION=ON the ZERO_CHECK regen is disabled.
cmake --fresh -S "%~dp0." -B "%~dp0build" -G "Visual Studio 18 2026" -A x64 -DCMAKE_SUPPRESS_REGENERATION=ON
if errorlevel 1 (echo cmake configure failed & exit /b 1)
rem Build the C++ target directly; the ALL_BUILD/ZERO_CHECK .slnx regen rule is skipped.
cmake --build "%~dp0build" --target KeySecBox.DLL --config %CFG%
if errorlevel 1 (echo cmake build failed & exit /b 1)

if /i "%CFG%"=="Release" (
    rem Two publish modes:
    rem  1) framework      depends on .NET 8 runtime + Windows App SDK installed on target.
    rem  2) selfcontained  bundles .NET + Windows App SDK as a folder deploy (no single-file:
    rem      PublishSingleFile forces full extraction to %TEMP%\.net -- slow start and
    rem      occasional error 0x8013134b).
    dotnet publish "%~dp0src\ui\KeySecBox.UI.csproj" -c Release -r win-x64 --self-contained false -p:Platform=x64 -p:WindowsAppSDKSelfContained=false -o "%~dp0bin\Release\x64\framework"
    if errorlevel 1 (echo dotnet publish framework failed & exit /b 1)
    dotnet publish "%~dp0src\ui\KeySecBox.UI.csproj" -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -o "%~dp0bin\Release\x64\selfcontained"
    if errorlevel 1 (echo dotnet publish selfcontained failed & exit /b 1)
    echo Publish framework done: bin\Release\x64\framework\KeySecBox.UI.exe
    echo Publish selfcontained done: bin\Release\x64\selfcontained\KeySecBox.UI.exe
) else (
    dotnet build "%~dp0src\ui\KeySecBox.UI.csproj" -c %CFG% -p:Platform=x64
    if errorlevel 1 (echo dotnet build failed & exit /b 1)
    echo BUILD_DONE
    echo UI output: bin\%CFG%\x64\exe\KeySecBox.UI.exe
)
