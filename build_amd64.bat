: SPDX-FileCopyrightText: 2026 K_PU
: SPDX-License-Identifier: AGPL-3.0-or-later
@echo off
setlocal
rem KeySecBox build script (Windows x64).
rem
rem   build_amd64.bat            Debug in-place build (C++ core + WinUI 3 UI)
rem   build_amd64.bat Release    Release build, then publish framework + selfcontained
rem   build_amd64.bat Test       Configure, build and run the core C API tests (ctest)
rem
rem The toolchain is discovered from the installed Visual Studio: vswhere finds the
rem installation, VsDevCmd provides MSVC / Windows SDK / CMake. Nothing is pinned to
rem a specific install path or VS version (2019 and newer are supported).

set "ROOT=%~dp0"
set "CFG=%~1"
if "%CFG%"=="" set "CFG=Debug"

rem --- locate Visual Studio --------------------------------------------------
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo [error] vswhere.exe not found: install Visual Studio with the C++ desktop workload.
    exit /b 1
)

set "VSARGS=-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64"
set "VSINSTALL="
for /f "usebackq delims=" %%i in (`"%VSWHERE%" %VSARGS% -property installationPath`) do set "VSINSTALL=%%i"
if not defined VSINSTALL (
    echo [error] No Visual Studio installation with the C++ desktop workload was found.
    exit /b 1
)

set "VSMAJOR="
for /f "usebackq tokens=1 delims=." %%v in (`"%VSWHERE%" %VSARGS% -property installationVersion`) do set "VSMAJOR=%%v"

set "GENERATOR="
if "%VSMAJOR%"=="18" set "GENERATOR=Visual Studio 18 2026"
if "%VSMAJOR%"=="17" set "GENERATOR=Visual Studio 17 2022"
if "%VSMAJOR%"=="16" set "GENERATOR=Visual Studio 16 2019"
if not defined GENERATOR (
    echo [error] Unsupported Visual Studio major version: %VSMAJOR%
    exit /b 1
)

rem --- MSVC / Windows SDK environment ---------------------------------------
call "%VSINSTALL%\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64
if errorlevel 1 (
    echo [error] VsDevCmd.bat failed: %VSINSTALL%
    exit /b 1
)

rem --- cmake / ctest: PATH first, then the copy shipped with Visual Studio ---
set "CMAKE="
for /f "delims=" %%c in ('where cmake 2^>nul') do if not defined CMAKE set "CMAKE=%%c"
set "CTEST="
for /f "delims=" %%c in ('where ctest 2^>nul') do if not defined CTEST set "CTEST=%%c"
if not defined CMAKE if exist "%VSINSTALL%\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" set "CMAKE=%VSINSTALL%\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
if not defined CMAKE (
    echo [error] cmake.exe not found: install the CMake component of Visual Studio.
    exit /b 1
)
if not defined CTEST for %%c in ("%CMAKE%") do if exist "%%~dpcctest.exe" set "CTEST=%%~dpcctest.exe"
if not defined CTEST set "CTEST=ctest"

set "DOTNET="
for /f "delims=" %%d in ('where dotnet 2^>nul') do if not defined DOTNET set "DOTNET=%%d"

rem --- clean outputs for the requested configuration only --------------------
if /i not "%CFG%"=="Test" (
    rd /s /q "%ROOT%bin\%CFG%" 2>nul
    rd /s /q "%ROOT%build\KeySecBox.DLL.dir\%CFG%" 2>nul
    rd /s /q "%ROOT%build\ALL_BUILD.dir\%CFG%" 2>nul
    rd /s /q "%ROOT%build\ZERO_CHECK.dir\%CFG%" 2>nul
    rd /s /q "%ROOT%build\x64\%CFG%" 2>nul
    rd /s /q "%ROOT%src\ui\obj\x64\%CFG%" 2>nul
)

rem --- configure ------------------------------------------------------------
rem CMAKE_SUPPRESS_REGENERATION: skip the ALL_BUILD/ZERO_CHECK regen rule, which
rem can spawn cmd during the build and break it with stray console output.
rem The cache is dropped on every run so a stale generator/toolset can never
rem survive a Visual Studio update (the per-config outputs are cleaned above).
if exist "%ROOT%build\CMakeCache.txt" del /q "%ROOT%build\CMakeCache.txt"
if exist "%ROOT%build\CMakeFiles" rd /s /q "%ROOT%build\CMakeFiles" 2>nul
"%CMAKE%" -S "%ROOT%." -B "%ROOT%build" -G "%GENERATOR%" -A x64 -DCMAKE_SUPPRESS_REGENERATION=ON
if errorlevel 1 (
    echo [error] cmake configure failed.
    exit /b 1
)

rem --- tests ----------------------------------------------------------------
if /i "%CFG%"=="Test" (
    "%CMAKE%" --build "%ROOT%build" --config Debug --target ksbox_tests
    if errorlevel 1 (echo [error] test build failed. & exit /b 1)
    "%CTEST%" --test-dir "%ROOT%build" -C Debug --output-on-failure
    if errorlevel 1 (echo [error] tests failed. & exit /b 1)
    echo TESTS_DONE
    exit /b 0
)

rem --- core DLL (built directly, skipping the ALL_BUILD regen target) -------
"%CMAKE%" --build "%ROOT%build" --target KeySecBox.DLL --config %CFG%
if errorlevel 1 (
    echo [error] cmake build failed.
    exit /b 1
)

if not defined DOTNET (
    echo [error] dotnet not found: install the .NET 8 SDK.
    exit /b 1
)

if /i "%CFG%"=="Release" (
    rem Two publish modes:
    rem  framework      requires .NET + Windows App SDK installed on target
    rem  selfcontained  bundles .NET + Windows App SDK (single-file disabled:
    rem                  PublishSingleFile extracts fully to %TEMP%\.net - slow
    rem                  start and occasional error 0x8013134b)
    "%DOTNET%" publish "%ROOT%src\ui\KeySecBox.UI.csproj" -c Release -r win-x64 --self-contained false -p:Platform=x64 -p:WindowsAppSDKSelfContained=false -o "%ROOT%bin\Release\x64\framework"
    if errorlevel 1 (echo [error] dotnet publish framework failed. & exit /b 1)
    "%DOTNET%" publish "%ROOT%src\ui\KeySecBox.UI.csproj" -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -o "%ROOT%bin\Release\x64\selfcontained"
    if errorlevel 1 (echo [error] dotnet publish selfcontained failed. & exit /b 1)

    rem x64 makepri.exe (arm64 build errors with exit 216; x64 is required)
    set "MAKEPRI="
    for /f "delims=" %%m in ('dir /b /s "%WindowsSdkBinPath%makepri.exe" 2^>nul ^| findstr /i "\\x64\\"') do if not defined MAKEPRI set "MAKEPRI=%%m"
    if not defined MAKEPRI (echo [error] makepri.exe not found. & exit /b 1)
    call :MergeSelfcontainedPri "%ROOT%bin\Release\x64\selfcontained" "%MAKEPRI%"
    if errorlevel 1 (echo [error] merge selfcontained PRI failed. & exit /b 1)
    rem Keep only en-us, zh-CN and the Microsoft.UI.Xaml payload needed by the
    rem merged PRI; remove any other subdirectories
    call :TrimSelfcontainedDirs "%ROOT%bin\Release\x64\selfcontained"
    if errorlevel 1 (echo [error] trim selfcontained dirs failed. & exit /b 1)
    echo Publish framework done: bin\Release\x64\framework\KeySecBox.UI.exe
    echo Publish selfcontained done: bin\Release\x64\selfcontained\KeySecBox.UI.exe
) else (
    "%DOTNET%" build "%ROOT%src\ui\KeySecBox.UI.csproj" -c %CFG% -p:Platform=x64
    if errorlevel 1 (echo [error] dotnet build failed. & exit /b 1)
    echo BUILD_DONE
    echo UI output: bin\%CFG%\x64\exe\KeySecBox.UI.exe
)
exit /b 0

:TrimSelfcontainedDirs
rem Keep only en-us, Microsoft.UI.Xaml, zh-CN; delete all other subdirectories
setlocal EnableDelayedExpansion
set "SC=%~1"
for /d %%d in ("%SC%\*") do (
    if /i not "%%~nxd"=="en-us" if /i not "%%~nxd"=="Microsoft.UI.Xaml" if /i not "%%~nxd"=="zh-CN" (
        rd /s /q "%%d"
        if errorlevel 1 (echo [error] failed to remove %%d & endlocal & exit /b 1)
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
set "SC=%~1"
set "MAKEPRI=%~2"

rem Prefer the passed-in makepri; fall back to Windows Kits if missing
if exist "%MAKEPRI%" goto pri_found
for /f "delims=" %%m in ('dir /b /s "C:\Program Files (x86)\Windows Kits\10\bin\makepri.exe" 2^>nul ^| findstr /i "\\x64\\"') do set "MAKEPRI=%%m"
:pri_found
if not exist "%MAKEPRI%" (echo [error] makepri.exe not found & exit /b 1)

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
if errorlevel 1 (echo [error] makepri merge failed & exit /b 1)

rem Replace the app PRI with the merged copy and clean up temp files
copy /y "%SC%\merged.pri" "%SC%\KeySecBox.UI.pri" >nul
if errorlevel 1 (echo [error] copying merged pri failed & exit /b 1)
del /q "%SC%\merged.pri" "%SC%\pri.resfiles" "%SC%\priconfig.xml" 2>nul
echo Merged framework PRI into KeySecBox.UI.pri
endlocal
exit /b 0
