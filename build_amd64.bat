@echo off
setlocal
set CFG=%1
if "%CFG%"=="" set CFG=Debug

call "C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\Tools\VsDevCmd.bat" -arch=amd64
if errorlevel 1 (echo VsDevCmd failed & goto :failrestore)

rem Clean outputs for the requested configuration only (Debug/Release kept separate)
rem For Debug, preserve bin\Debug\x64\exe\data so user vault data survives rebuilds
if /i "%CFG%"=="Debug" (
    if exist "%~dp0bin\Debug\x64\exe\data" (
        move /y "%~dp0bin\Debug\x64\exe\data" "%~dp0bin\Debug_data_tmp" >nul 2>nul
    )
    rd /s /q "%~dp0bin\Debug" 2>nul
    if exist "%~dp0bin\Debug_data_tmp" (
        md "%~dp0bin\Debug\x64\exe" 2>nul
        move /y "%~dp0bin\Debug_data_tmp" "%~dp0bin\Debug\x64\exe\data" >nul 2>nul
    )
) else (
    rd /s /q "%~dp0bin\%CFG%" 2>nul
)
rd /s /q "%~dp0build\KeySecBox.DLL.dir\%CFG%" 2>nul
rd /s /q "%~dp0build\ALL_BUILD.dir\%CFG%" 2>nul
rd /s /q "%~dp0build\ZERO_CHECK.dir\%CFG%" 2>nul
rd /s /q "%~dp0build\x64\%CFG%" 2>nul
rd /s /q "%~dp0KeySecBox.Wpf\obj\x64\%CFG%" 2>nul

rem --fresh forces a full regen so stale ZERO_CHECK/.slnx rules cannot spawn
rem cmd during cmake --build and break the build with stray console output
cmake --fresh -S "%~dp0KeySecBox.Core" -B "%~dp0build" -G "Visual Studio 18 2026" -A x64 -DCMAKE_SUPPRESS_REGENERATION=ON
if errorlevel 1 (echo cmake configure failed & goto :failrestore)
rem Build the C++ target directly, skipping the ALL_BUILD/ZERO_CHECK regen rule
cmake --build "%~dp0build" --target KeySecBox.DLL --config %CFG%
if errorlevel 1 (echo cmake build failed & goto :failrestore)

if /i "%CFG%"=="Release" (
    rem Two publish modes:
    rem  framework      requires .NET Desktop Runtime installed on target
    rem  selfcontained  bundles .NET runtime
    dotnet publish "%~dp0KeySecBox.Wpf\KeySecBox.Wpf.csproj" -c Release -r win-x64 --self-contained false -p:Platform=x64 -o "%~dp0bin\Release\x64\framework"
    if errorlevel 1 (echo dotnet publish framework failed & goto :failrestore)
    dotnet publish "%~dp0KeySecBox.Wpf\KeySecBox.Wpf.csproj" -c Release -r win-x64 --self-contained true -p:Platform=x64 -o "%~dp0bin\Release\x64\selfcontained"
    if errorlevel 1 (echo dotnet publish selfcontained failed & goto :failrestore)
    echo Publish framework done: bin\Release\x64\framework\KeySecBox.exe
    echo Publish selfcontained done: bin\Release\x64\selfcontained\KeySecBox.exe
) else (
    dotnet build "%~dp0KeySecBox.Wpf\KeySecBox.Wpf.csproj" -c %CFG% -p:Platform=x64
    if errorlevel 1 (echo dotnet build failed & goto :failrestore)
    echo BUILD_DONE
    echo UI output: bin\%CFG%\x64\exe\KeySecBox.exe
)
exit /b 0

:failrestore
rem Restore preserved user data if the build failed after we moved it aside
if exist "%~dp0bin\Debug_data_tmp" (
    md "%~dp0bin\Debug\x64\exe" 2>nul
    move /y "%~dp0bin\Debug_data_tmp" "%~dp0bin\Debug\x64\exe\data" >nul 2>nul
)
exit /b 1
