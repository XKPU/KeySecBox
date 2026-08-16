@echo off
setlocal
set CFG=%1
if "%CFG%"=="" set CFG=Debug

call "C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\Tools\VsDevCmd.bat" -arch=amd64
if errorlevel 1 (echo VsDevCmd failed & exit /b 1)

rem x64 makepri.exe (arm64 build errors with exit 216; x64 is required)
set MAKEPRI=
for /f "delims=" %%m in ('dir /b /s "%WindowsSdkBinPath%makepri.exe" 2^>nul ^| findstr /i "\\x64\\"') do if not defined MAKEPRI set MAKEPRI=%%m

rem Clean outputs for the requested configuration only (Debug/Release kept separate)
rd /s /q "%~dp0bin\%CFG%" 2>nul
rd /s /q "%~dp0build\KeySecBox.DLL.dir\%CFG%" 2>nul
rd /s /q "%~dp0build\ALL_BUILD.dir\%CFG%" 2>nul
rd /s /q "%~dp0build\ZERO_CHECK.dir\%CFG%" 2>nul
rd /s /q "%~dp0build\x64\%CFG%" 2>nul
rd /s /q "%~dp0src\ui\obj\x64\%CFG%" 2>nul

rem --fresh forces a full regen so stale ZERO_CHECK/.slnx rules cannot spawn
rem cmd during cmake --build and break the build with stray console output
cmake --fresh -S "%~dp0." -B "%~dp0build" -G "Visual Studio 18 2026" -A x64 -DCMAKE_SUPPRESS_REGENERATION=ON
if errorlevel 1 (echo cmake configure failed & exit /b 1)
rem Build the C++ target directly, skipping the ALL_BUILD/ZERO_CHECK regen rule
cmake --build "%~dp0build" --target KeySecBox.DLL --config %CFG%
if errorlevel 1 (echo cmake build failed & exit /b 1)

if /i "%CFG%"=="Release" (
    rem Two publish modes:
    rem  framework      requires .NET + Windows App SDK installed on target
    rem  selfcontained  bundles .NET + Windows App SDK (single-file disabled:
    rem                  PublishSingleFile extracts fully to %TEMP%\.net - slow
    rem                  start and occasional error 0x8013134b)
    dotnet publish "%~dp0src\ui\KeySecBox.UI.csproj" -c Release -r win-x64 --self-contained false -p:Platform=x64 -p:WindowsAppSDKSelfContained=false -o "%~dp0bin\Release\x64\framework"
    if errorlevel 1 (echo dotnet publish framework failed & exit /b 1)
    dotnet publish "%~dp0src\ui\KeySecBox.UI.csproj" -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -o "%~dp0bin\Release\x64\selfcontained"
    if errorlevel 1 (echo dotnet publish selfcontained failed & exit /b 1)
    if not defined MAKEPRI (echo makepri.exe not found & exit /b 1)
    call :MergeSelfcontainedPri "%~dp0bin\Release\x64\selfcontained" "%MAKEPRI%"
    if errorlevel 1 (echo merge selfcontained PRI failed & exit /b 1)
    rem Keep only en-us, zh-CN and the Microsoft.UI.Xaml payload needed by the
    rem merged PRI; remove any other subdirectories
    call :TrimSelfcontainedDirs "%~dp0bin\Release\x64\selfcontained"
    if errorlevel 1 (echo trim selfcontained dirs failed & exit /b 1)
    echo Publish framework done: bin\Release\x64\framework\KeySecBox.UI.exe
    echo Publish selfcontained done: bin\Release\x64\selfcontained\KeySecBox.UI.exe
) else (
    dotnet build "%~dp0src\ui\KeySecBox.UI.csproj" -c %CFG% -p:Platform=x64
    if errorlevel 1 (echo dotnet build failed & exit /b 1)
    echo BUILD_DONE
    echo UI output: bin\%CFG%\x64\exe\KeySecBox.UI.exe
)
exit /b 0

:TrimSelfcontainedDirs
rem Keep only en-us, Microsoft.UI.Xaml, zh-CN; delete all other subdirectories
setlocal
set SC=%~1
for /d %%d in ("%SC%\*") do (
    if /i not "%%~nxd"=="en-us" if /i not "%%~nxd"=="Microsoft.UI.Xaml" if /i not "%%~nxd"=="zh-CN" (
        rd /s /q "%%d"
        if errorlevel 1 (echo failed to remove %%d & endlocal & exit /b 1)
    )
)
echo Trim selfcontained done
endlocal
exit /b 0

:MergeSelfcontainedPri
rem Merge WinAppSDK framework .pri files into KeySecBox.UI.pri: self-contained
rem has no registered framework package, so MRT Core must resolve
rem with "Cannot locate resource ... themeresources.xaml".
setlocal
set SC=%~1
set MAKEPRI=%~2

rem Prefer the passed-in makepri; fall back to Windows Kits if missing
if exist "%MAKEPRI%" goto pri_found
for /f "delims=" %%m in ('dir /b /s "C:\Program Files (x86)\Windows Kits\10\bin\makepri.exe" 2^>nul ^| findstr /i "\\x64\\"') do set MAKEPRI=%%m
:pri_found
if not exist "%MAKEPRI%" (echo makepri.exe not found & exit /b 1)

rem List the input .pri files (app + all framework pris)
del /q "%SC%\merged.pri" 2>nul
dir /b "%SC%\*.pri" > "%SC%\pri.resfiles"

rem Minimal priconfig that indexes the listed .pri files (framework maps end up
rem under the app's "Files" subtree once merged)
(
    echo ^<?xml version="1.0" encoding="utf-8"?^>
    echo ^<resources targetOsVersion="10.0.0" majorVersion="1"^>
    echo   ^<index root="\" startIndexAt="pri.resfiles"^>
    echo     ^<default^>
    echo       ^<qualifier name="Language" value="en-US" /^>
    echo       ^<qualifier name="Contrast" value="standard" /^>
    echo       ^<qualifier name="Scale" value="200" /^>
    echo       ^<qualifier name="HomeRegion" value="001" /^>
    echo       ^<qualifier name="TargetSize" value="256" /^>
    echo       ^<qualifier name="LayoutDirection" value="LTR" /^>
    echo       ^<qualifier name="DXFeatureLevel" value="DX9" /^>
    echo       ^<qualifier name="Configuration" value="" /^>
    echo       ^<qualifier name="AlternateForm" value="" /^>
    echo       ^<qualifier name="Platform" value="UAP" /^>
    echo     ^</default^>
    echo     ^<indexer-config type="PRI" /^>
    echo     ^<indexer-config type="RESFILES" qualifierDelimiter="." /^>
    echo   ^</index^>
    echo ^</resources^>
) > "%SC%\priconfig.xml"

"%MAKEPRI%" new /pr "%SC%" /cf "%SC%\priconfig.xml" /of "%SC%\merged.pri" /in KeySecBox.UI /o >nul 2>&1
if errorlevel 1 (echo makepri merge failed & exit /b 1)

rem Replace the app PRI with the merged copy and clean up temp files
copy /y "%SC%\merged.pri" "%SC%\KeySecBox.UI.pri" >nul
if errorlevel 1 (echo copying merged pri failed & exit /b 1)
del /q "%SC%\merged.pri" "%SC%\pri.resfiles" "%SC%\priconfig.xml" 2>nul
echo Merged framework PRI into KeySecBox.UI.pri
endlocal
exit /b 0
