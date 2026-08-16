@echo off
setlocal
set CFG=%1
if "%CFG%"=="" set CFG=Debug

call "C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\Tools\VsDevCmd.bat" -arch=amd64
if errorlevel 1 (echo VsDevCmd failed & exit /b 1)

rem Locate the x64 makepri.exe (needed by the Release self-contained PRI merge step).
rem Note: must pin the x64 build -- the arm64 build prints an error and exits 216.
set MAKEPRI=
for /f "delims=" %%m in ('dir /b /s "%WindowsSdkBinPath%makepri.exe" 2^>nul ^| findstr /i "\\x64\\"') do if not defined MAKEPRI set MAKEPRI=%%m

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
    if not defined MAKEPRI (echo makepri.exe not found & exit /b 1)
    call :MergeSelfcontainedPri "%~dp0bin\Release\x64\selfcontained" "%MAKEPRI%"
    if errorlevel 1 (echo merge selfcontained PRI failed & exit /b 1)
    echo Publish framework done: bin\Release\x64\framework\KeySecBox.UI.exe
    echo Publish selfcontained done: bin\Release\x64\selfcontained\KeySecBox.UI.exe
) else (
    dotnet build "%~dp0src\ui\KeySecBox.UI.csproj" -c %CFG% -p:Platform=x64
    if errorlevel 1 (echo dotnet build failed & exit /b 1)
    echo BUILD_DONE
    echo UI output: bin\%CFG%\x64\exe\KeySecBox.UI.exe
)
exit /b 0

:MergeSelfcontainedPri
rem Merge the Windows App SDK framework .pri files into KeySecBox.UI.pri so MRT
rem Core can resolve ms-appx:///Microsoft.UI.Xaml/... resources. In self-contained
rem mode there is no registered framework package, so the framework resource maps
rem must live in the app's own PRI file; otherwise startup crashes with
rem "Cannot locate resource from 'ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml'".
setlocal
set SC=%~1
set MAKEPRI=%~2

rem Locate makepri.exe next to build tools first, then fall back to Windows Kits.
if exist "%MAKEPRI%" goto pri_found
for /f "delims=" %%m in ('dir /b /s "C:\Program Files (x86)\Windows Kits\10\bin\makepri.exe" 2^>nul ^| findstr /i "\\x64\\"') do set MAKEPRI=%%m
:pri_found
if not exist "%MAKEPRI%" (echo makepri.exe not found & exit /b 1)

rem Build the list of input .pri files (app + all framework pris).
del /q "%SC%\merged.pri" 2>nul
dir /b "%SC%\*.pri" > "%SC%\pri.resfiles"

rem Minimal priconfig that indexes the listed .pri files (framework maps become
rem reachable under the app's "Files" subtree once merged).
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

rem Replace the app PRI with the merged copy and clean up temp files.
copy /y "%SC%\merged.pri" "%SC%\KeySecBox.UI.pri" >nul
if errorlevel 1 (echo copying merged pri failed & exit /b 1)
del /q "%SC%\merged.pri" "%SC%\pri.resfiles" "%SC%\priconfig.xml" 2>nul
echo Merged framework PRI into KeySecBox.UI.pri
endlocal
exit /b 0
